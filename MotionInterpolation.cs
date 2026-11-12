using System.Diagnostics;

namespace CapturePlayer;

internal static class MotionInterpolation
{
    internal static void ConfigureGpu(ProcessStartInfo p)
    {
        if (!File.Exists(Path.Combine(p.WorkingDirectory, "resources", "interpolate.cl")))
            throw new FileNotFoundException("缺少补帧资源文件 resources/interpolate.cl。");
        Add(p, "-init_hw_device", "opencl=oc:,platform_vendor=NVIDIA Corporation,device_type=gpu",
            "-filter_hw_device", "oc");
    }

    internal static string Filter(int width, int height, int multiplier, string input = "0:v", bool alreadyOnGpu = false)
    {
        if (multiplier is < 2 or > 6) throw new ArgumentOutOfRangeException(nameof(multiplier));
        // Round up to even tile counts to accommodate the NV12 chroma plane.
        string tiles = $"{((width + 63) / 64) * 6}x{((height + 63) / 64) * 2}";
        int rate = 60 * multiplier;
        // Track both source frames and one motion field at the same PTS.
        // Only expand to the output rate after the motion search.
        return $"[{input}]fps=60,{(alreadyOnGpu ? "" : "format=nv12,hwupload,")}split=3[a][b][p];"
            + @"[p]setpts=if(eq(N\,0)\,PTS\,PTS+1),split=2[c][d];"
            + $"[a][c]program_opencl=source=resources/interpolate.cl:kernel=flow:inputs=2:size={tiles},fps={rate}[f];"
            + $"[b]fps={rate}[n];[d]fps={rate}[o];"
            + $"[n][o][f]program_opencl=source=resources/interpolate.cl:kernel=phase{multiplier}:inputs=3,hwdownload,format=nv12[v]";
    }

    internal static void ConfigureEncoder(ProcessStartInfo p, int multiplier, bool fullOutput)
    {
        Add(p, "-c:v", "h264_nvenc", "-preset", "p1", "-tune", "ull");
        if (fullOutput || multiplier > 1)
        {
            // A camera pan should not flood the local H.264 decoder with a
            // bitrate spike. The stream is used only for the preview.
            var bitrate = fullOutput ? multiplier > 1 ? "45M" : "32M" : "18M";
            Add(p, "-rc", "cbr", "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", fullOutput ? "2M" : "1M", "-slices", "4");
        }
        else Add(p, "-rc", "constqp", "-qp", "17");
        Add(p,
            // A new low-buffer viewer can skip the frames it probed and join the next keyframe.
            "-g", (12 * multiplier).ToString(System.Globalization.CultureInfo.InvariantCulture), "-bf", "0", "-zerolatency", "1",
            // Pipeline frame transfers and encoding at high preview rates.
            "-surfaces", multiplier >= 4 ? "12" : multiplier == 3 ? "8" : "4",
            "-delay", multiplier >= 4 ? "4" : multiplier == 3 ? "2" : "0", "-pix_fmt", "nv12");
    }

    static void Add(ProcessStartInfo p, params string[] args)
    {
        foreach (var arg in args) p.ArgumentList.Add(arg);
    }
}
