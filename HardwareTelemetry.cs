using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace CapturePlayer;

[Flags]
internal enum MonitorItem
{
    None = 0, GpuLoad = 1, GpuTemp = 2, CpuLoad = 4, CpuTemp = 8, Vram = 16, GpuPower = 32,
    All = GpuLoad | GpuTemp | CpuLoad | CpuTemp | Vram | GpuPower
}

internal sealed record MonitorSample(int? GpuLoad = null, int? GpuTemp = null, double? CpuLoad = null,
    double? CpuTemp = null, double? VramUsed = null, double? VramTotal = null, double? GpuPower = null);

internal sealed class HardwareTelemetry : IDisposable
{
    readonly object gate = new();
    bool disposed;
    (ulong Idle, ulong Total)? lastCpu;
    GpuSensors? gpu;

    internal MonitorSample Read(MonitorItem items)
    {
        lock (gate)
        {
            if (disposed) return new();
            double? cpu = null, cpuTemp = null;
            if ((items & MonitorItem.CpuLoad) != 0 && GetSystemTimes(out ulong idle, out ulong kernel, out ulong user))
            {
                ulong total = kernel + user;
                if (lastCpu is { } previous && total > previous.Total && idle >= previous.Idle)
                    cpu = Math.Clamp(100d * (1d - (double)(idle - previous.Idle) / (total - previous.Total)), 0, 100);
                lastCpu = (idle, total);
            }
            else lastCpu = null;
            if ((items & MonitorItem.CpuTemp) != 0) cpuTemp = ReadCpuTemperature();
            var gpuItems = items & (MonitorItem.GpuLoad | MonitorItem.GpuTemp | MonitorItem.Vram | MonitorItem.GpuPower);
            if (gpuItems != MonitorItem.None) gpu ??= new GpuSensors();
            var sample = gpuItems != MonitorItem.None && gpu != null ? gpu.Read(gpuItems) : new MonitorSample();
            return sample with { CpuLoad = cpu, CpuTemp = cpuTemp };
        }
    }

    static double? ReadCpuTemperature()
    {
        try
        {
            // MSI Afterburner SDK's MAHM v2, read only. Ignore stale or unsupported data.
            using var memory = MemoryMappedFile.OpenExisting("MAHMSharedMemory", MemoryMappedFileRights.Read);
            using var view = memory.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (view.Capacity < 32 || view.ReadUInt32(0) != 0x4D41484D || view.ReadUInt32(4) >> 16 != 2) return null;
            long header = view.ReadUInt32(8), count = view.ReadUInt32(12), entry = view.ReadUInt32(16);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            uint sampleTime = view.ReadUInt32(20);
            if (header < 32 || entry < 1324 || count > 2048 || header + count * entry > view.Capacity ||
                now - sampleTime is < -2 or > 15) return null;
            for (long i = 0; i < count; i++)
            {
                long at = header + i * entry;
                if (view.ReadUInt32(at + 1316) != uint.MaxValue || view.ReadUInt32(at + 1320) != 0x80) continue;
                float celsius = view.ReadSingle(at + 1300);
                if (float.IsFinite(celsius) && celsius is > 0 and < 125) return celsius;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            gpu?.Dispose();
            gpu = null;
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    sealed class GpuSensors : IDisposable
    {
        readonly IntPtr library, device;
        readonly bool initialized;
        readonly QueryUtil? utilization;
        readonly QueryTemperature? temperature;
        readonly QueryMemory? memory;
        readonly QueryUInt? power;

        internal GpuSensors()
        {
            library = LoadNvml();
            if (library == IntPtr.Zero || Export<Call>("nvmlInit_v2")?.Invoke() != 0) return;
            initialized = true;
            if (Export<QueryDevice>("nvmlDeviceGetHandleByIndex_v2")?.Invoke(0, out device) != 0) return;
            utilization = Export<QueryUtil>("nvmlDeviceGetUtilizationRates");
            temperature = Export<QueryTemperature>("nvmlDeviceGetTemperature");
            memory = Export<QueryMemory>("nvmlDeviceGetMemoryInfo");
            power = Export<QueryUInt>("nvmlDeviceGetPowerUsage");
        }

        internal MonitorSample Read(MonitorItem items)
        {
            if (device == IntPtr.Zero) return new();
            int? load = null, temp = null;
            double? used = null, total = null, watts = null;
            if ((items & MonitorItem.GpuLoad) != 0 && utilization != null && utilization(device, out var usage) == 0) load = (int)usage.Gpu;
            if ((items & MonitorItem.GpuTemp) != 0 && temperature != null && temperature(device, 0, out uint celsius) == 0) temp = (int)celsius;
            if ((items & MonitorItem.Vram) != 0 && memory != null && memory(device, out var bytes) == 0)
            {
                used = bytes.Used / 1073741824d;
                total = bytes.Total / 1073741824d;
            }
            if ((items & MonitorItem.GpuPower) != 0 && power != null && power(device, out uint milliwatts) == 0) watts = milliwatts / 1000d;
            return new MonitorSample(GpuLoad: load, GpuTemp: temp, VramUsed: used, VramTotal: total, GpuPower: watts);
        }

        static IntPtr LoadNvml()
        {
            // Only look in Windows or NVIDIA driver locations, never in the working directory.
            var system = Environment.SystemDirectory;
            var paths = new List<string> { Path.Combine(system, "nvml.dll") };
            var nvSmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
            if (File.Exists(nvSmi)) paths.Add(nvSmi);
            try
            {
                paths.AddRange(Directory.EnumerateDirectories(Path.Combine(system, "DriverStore", "FileRepository"), "nv*.inf_amd64_*")
                    .Select(dir => Path.Combine(dir, "nvml.dll")).Where(File.Exists)
                    .OrderByDescending(path => File.GetLastWriteTimeUtc(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            foreach (string path in paths)
                if (NativeLibrary.TryLoad(path, out var handle)) return handle;
            return IntPtr.Zero;
        }

        T? Export<T>(string name) where T : Delegate => NativeLibrary.TryGetExport(library, name, out var p)
            ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;

        public void Dispose()
        {
            if (library == IntPtr.Zero) return;
            if (initialized) Export<Call>("nvmlShutdown")?.Invoke();
            NativeLibrary.Free(library);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Utilization { public uint Gpu, Memory; }
        [StructLayout(LayoutKind.Sequential)]
        struct Memory { public ulong Total, Free, Used; }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Call();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int QueryDevice(uint index, out IntPtr device);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int QueryUtil(IntPtr device, out Utilization utilization);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int QueryTemperature(IntPtr device, uint sensor, out uint temperature);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int QueryMemory(IntPtr device, out Memory memory);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int QueryUInt(IntPtr device, out uint value);
    }
}
