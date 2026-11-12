using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace CapturePlayer;

internal static class ImageEnhancement
{
    const string ModelSha256 = "2BC19BC09A7B89DD44F73E2AC0108FCC586E2154BDD0830F242F4F040E28789F";
    const string Shader = "resources/super-resolution/FSRCNNX_x2_8-0-4-1.glsl";

    internal static bool HasModelFiles(string root) =>
        File.Exists(Path.Combine(root, "resources", "frei0r", "capture_nr.dll")) &&
        File.Exists(Path.Combine(root, "resources", "models", "nvngx_dlssnr.dll"));

    internal static void ValidateModel(string root)
    {
        var resources = Path.Combine(root, "resources");
        if (!File.Exists(Path.Combine(resources, "frei0r", "capture_nr.dll")))
            throw new FileNotFoundException("缺少 DLSS 5 处理组件。");
        using var stream = File.OpenRead(Path.Combine(resources, "models", "nvngx_dlssnr.dll"));
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), ModelSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("DLSS 5 模型文件校验失败。");
    }

    internal static void Configure(ProcessStartInfo p, string appData, bool neural, bool superResolution, int multiplier)
    {
        if (neural || multiplier > 1) MotionInterpolation.ConfigureGpu(p);
        else if (superResolution)
            Add(p, "-init_hw_device", "vulkan=vk:NVIDIA", "-filter_hw_device", "vk");
        if (superResolution && !File.Exists(Path.Combine(p.WorkingDirectory, Shader)))
            throw new FileNotFoundException("缺少 FSRCNNX 超分模型。");
        if (!neural) return;
        p.Environment["FREI0R_PATH"] = Path.Combine(p.WorkingDirectory, "resources", "frei0r") + Path.DirectorySeparatorChar;
        p.Environment["CAPTURE_NR_CACHE"] = Directory.CreateDirectory(Path.Combine(appData, "neural-cache")).FullName;
    }

    internal static void ConfigureOutput(ProcessStartInfo p, bool low, bool neural, bool superResolution, int multiplier, double? liveOrigin, bool audio)
    {
        string input = "0:v";
        var filters = new List<string>();
        if (multiplier > 1 && (neural || superResolution))
        {
            // Normalize the timebase without filling gaps with old frames after
            // warmup or after the live relay discards frames it could not deliver.
            filters.Add("[0:v]settb=1/60[clocked]");
            input = "clocked";
        }
        if (neural)
            input = AppendNeuralFilters(filters, input, keepGpu: !superResolution && multiplier > 1);
        if (superResolution)
        {
            filters.Add($"[{input}]libplacebo=w=3840:h=2160:format=nv12:upscaler=spline36:custom_shader_path={Shader}[sr]");
            input = "sr";
        }
        string? liveClock = liveOrigin is double epoch ? $"time(0)-{epoch.ToString("F3", CultureInfo.InvariantCulture)}" : null;
        if (multiplier > 1)
        {
            bool fullOutput = !low || superResolution;
            filters.Add(MotionInterpolation.Filter(fullOutput ? 3840 : 1920, fullOutput ? 2160 : 1080, multiplier, input,
                alreadyOnGpu: neural && !superResolution));
            input = "v";
        }
        string? fresh = null;
        if (liveClock is not null)
        {
            // Motion filters can fill a gap after GPU warmup. These generated
            // frames must never be queued for playback once the gap is past.
            fresh = $"gte(t,{liveClock}-0.45)";
            filters.Add($"[{input}]select='{fresh}'[live]");
            input = "live";
        }
        Add(p, "-filter_complex", string.Join(';', filters), "-map", $"[{input}]");
        if (multiplier == 1) Add(p, "-enc_time_base:v", "1/1000");
        if (audio)
        {
            Add(p, "-map", "0:a:0?");
            if (fresh is not null) Add(p, "-af", $"aselect='{fresh}'", "-c:a", "pcm_s16le");
            else Add(p, "-c:a", "copy");
        }
        MotionInterpolation.ConfigureEncoder(p, multiplier, !low || superResolution);
    }

    internal static void MapDirectNeuralVideo(ProcessStartInfo p)
    {
        var filters = new List<string>();
        string output = AppendNeuralFilters(filters, "0:v", limitedRange: true);
        Add(p, "-filter_complex", string.Join(';', filters), "-map", $"[{output}]");
    }

    static string AppendNeuralFilters(List<string> filters, string input, bool keepGpu = false, bool limitedRange = false)
    {
        string range = limitedRange ? ":in_range=auto:out_range=tv" : "";
        filters.Add($"[{input}]split=2[nrbase][nrin]");
        filters.Add($"[nrbase]scale=iw:ih:flags=fast_bilinear:threads=4{range},format=nv12,hwupload[nrraw]");
        filters.Add("[nrin]scale=512:288:flags=fast_bilinear:threads=2,format=rgba,split=2[nrlow][nreval]");
        filters.Add("[nrlow]format=nv12,hwupload[nrreference]");
        filters.Add("[nreval]frei0r=filter_name=capture_nr,format=nv12,hwupload[nrresult]");
        string transfer = keepGpu ? "" : ",hwdownload,format=nv12";
        filters.Add($"[nrraw][nrreference][nrresult]program_opencl=source=resources/neural_residual.cl:kernel=neural_residual:inputs=3{transfer}[neural]");
        return "neural";
    }

    static void Add(ProcessStartInfo p, params string[] args)
    {
        foreach (var arg in args) p.ArgumentList.Add(arg);
    }
}
