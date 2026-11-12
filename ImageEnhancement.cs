using System.Diagnostics;
using System.Security.Cryptography;

namespace CapturePlayer;

internal static class ImageEnhancement
{
    const string ModelSha256 = "2BC19BC09A7B89DD44F73E2AC0108FCC586E2154BDD0830F242F4F040E28789F";
    const string Shader = "resources/super-resolution/FSRCNNX_x2_8-0-4-1.glsl";

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

    internal static void ConfigureOutput(ProcessStartInfo p, bool low, bool neural, bool superResolution, int multiplier)
    {
        string input = "0:v";
        var filters = new List<string>();
        if (neural || superResolution)
        {
            // DirectShow timestamps use 100 ns units. Normalize before the
            // frame-sync filters so their branches share exactly one timebase.
            filters.Add("[0:v]fps=60:start_time=0[clocked]");
            input = "clocked";
        }
        if (neural)
        {
            filters.Add($"[{input}]split=2[nrbase][nrin]");
            filters.Add("[nrbase]format=nv12,hwupload[nrraw]");
            filters.Add("[nrin]scale=768:432:flags=fast_bilinear,format=rgba,split=2[nrlow][nreval]");
            filters.Add("[nrlow]format=nv12,hwupload[nrreference]");
            filters.Add("[nreval]frei0r=filter_name=capture_nr,format=nv12,hwupload[nrresult]");
            string transfer = !superResolution && multiplier > 1 ? "" : ",hwdownload,format=nv12";
            filters.Add($"[nrraw][nrreference][nrresult]program_opencl=source=resources/neural_residual.cl:kernel=neural_residual:inputs=3{transfer}[neural]");
            input = "neural";
        }
        if (superResolution)
        {
            filters.Add($"[{input}]libplacebo=w=3840:h=2160:format=nv12:upscaler=spline36:custom_shader_path={Shader}[sr]");
            input = "sr";
        }
        if (multiplier > 1)
        {
            bool fullOutput = !low || superResolution;
            filters.Add(MotionInterpolation.Filter(fullOutput ? 3840 : 1920, fullOutput ? 2160 : 1080, multiplier, input,
                alreadyOnGpu: neural && !superResolution));
            input = "v";
        }
        Add(p, "-filter_complex", string.Join(';', filters), "-map", $"[{input}]", "-map", "0:a:0?", "-c:a", "copy");
        MotionInterpolation.ConfigureEncoder(p, multiplier);
    }

    static void Add(ProcessStartInfo p, params string[] args)
    {
        foreach (var arg in args) p.ArgumentList.Add(arg);
    }
}
