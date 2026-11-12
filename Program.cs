using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CapturePlayer;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        bool checkMotion = args.Contains("--check-motion");
        bool checkNeural = args.Contains("--check-dlss5");
        bool checkSuper = args.Contains("--check-super-res");
        int checkMultiplier = 0;
        if (checkMotion)
        {
            int index = Array.IndexOf(args, "--check-motion");
            checkMultiplier = index + 1 < args.Length && int.TryParse(args[index + 1], out int requested)
                ? Math.Clamp(requested, 2, 6) : 2;
        }
        bool checkSensors = args.Contains("--check-sensors");
        bool checkFps = args.Contains("--check-fps") || checkSensors;
        bool check = args.Contains("--check") || checkMotion || checkNeural || checkSuper || checkFps;
        Application.Run(new Player(check, args.Contains("--1080p"), checkMultiplier, check && args.Contains("--synthetic"),
            checkNeural, checkSuper, checkFps, checkSensors));
    }
}

internal sealed class Player : Form
{
    static readonly Color Surface = Color.FromArgb(25, 30, 39);
    static readonly Color Ink = Color.FromArgb(240, 244, 251);
    static readonly Color Soft = Color.FromArgb(163, 178, 197);
    readonly string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CapturePlayer");
    readonly Panel preview = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(9, 12, 17) };
    readonly Label placeholder = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Soft, Text = "正在打开采集卡…" };
    readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Soft, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 8, 0) };
    readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 174, BackColor = Surface, ForeColor = Ink, AccessibleName = "采集分辨率和超分" };
    readonly ComboBox effect = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 134, BackColor = Surface, ForeColor = Ink, AccessibleName = "画面效果" };
    readonly CheckBox neural = new() { Text = "DLSS 5 FP8", AutoSize = true, Margin = new Padding(6, 3, 4, 0), ForeColor = Ink };
    readonly CheckBox showFps = new() { Text = "显示帧数", AutoSize = true, Margin = new Padding(4, 4, 3, 0), ForeColor = Ink };
    readonly ComboBox fpsPosition = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 72, BackColor = Surface, ForeColor = Ink, AccessibleName = "监控显示位置" };
    readonly FrameBadge frameBadge = new();
    readonly Button monitorOptions = Button("监控项 ▾", 85);
    readonly ContextMenuStrip monitorMenu = new();
    readonly Dictionary<MonitorItem, ToolStripMenuItem> monitorItems = new();
    readonly HardwareTelemetry telemetry = new();
    MonitorSample sensors = new();
    MonitorItem selectedMonitors;
    long nextSampleAt;
    bool sampling;
    readonly TrackBar volume = new() { Minimum = 0, Maximum = 100, Value = 70, TickStyle = TickStyle.None, Width = 98, Height = 32, AutoSize = false };
    readonly Button mute = Button("静音", 54), reconnect = Button("重连", 60), fullscreen = Button("全屏", 60);
    readonly Panel topBar = new() { Dock = DockStyle.Top, Height = 70, Padding = new Padding(14, 0, 12, 0), BackColor = Surface };
    readonly Panel bottomBar = new() { Dock = DockStyle.Bottom, Height = 32 };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 180 };
    readonly Stopwatch elapsed = new();
    readonly Stopwatch testTime = Stopwatch.StartNew();
    readonly bool check;
    readonly int checkMultiplier;
    readonly bool synthetic;
    readonly bool checkFps, checkSensors;
    int multiplier = 1;
    bool motionActive => multiplier > 1;
    bool motionReady;
    bool neuralActive, neuralReady, superActive;
    bool enhanced => motionActive || neuralActive || superActive;
    long encodedFrames;
    readonly Queue<(double Time, long Frames)> encoderSamples = new();
    string? effectNotice;
    Process? process, captureProcess;
    IntPtr videoWindow;
    bool busy, closing, muted, full, changingFullscreen, pendingVolume, audioListed, soundReady, videoReady, audioReady, retryAudio;
    int version, dropped;
    string selectedMode = "4K60";
    string? lastError;
    RectState? restore;
    long toolbarHideAt;

    public Player(bool check, bool low, int checkMultiplier, bool synthetic, bool checkNeural, bool checkSuper, bool checkFps, bool checkSensors)
    {
        this.check = check;
        this.checkMultiplier = checkMultiplier;
        this.synthetic = synthetic;
        this.checkFps = checkFps;
        this.checkSensors = checkSensors;
        Text = "采集卡播放器";
        Font = new Font("Microsoft YaHei UI", 10);
        ForeColor = Ink;
        BackColor = Surface;
        ClientSize = new Size(1090, 650);
        MinimumSize = new Size(970, 460);
        StartPosition = FormStartPosition.CenterScreen;
        Directory.CreateDirectory(appData);
        bool super = checkSuper;
        bool fps = checkFps;
        selectedMonitors = checkSensors ? MonitorItem.All : MonitorItem.None;
        int corner = 1;
        try
        {
            var settings = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(appData, "settings.json")));
            if (settings != null)
            {
                volume.Value = Math.Clamp(settings.Volume, 0, 100); muted = settings.Muted;
                if (!check)
                {
                    low = settings.Low; super = settings.SuperRes; fps = settings.ShowFps; corner = settings.FpsCorner;
                    selectedMonitors = settings.Monitors & MonitorItem.All;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        if (check) { muted = true; }
        mode.Items.AddRange(["4K · 60 帧", "1080p · 60 帧", "1080p → 4K 超分"]);
        mode.SelectedIndex = super ? 2 : low ? 1 : 0;
        effect.Items.AddRange(["不补帧", "动作补帧 2×", "动作补帧 3×", "动作补帧 4×", "动作补帧 5×", "动作补帧 6×"]);
        effect.SelectedIndex = checkMultiplier > 0 ? checkMultiplier - 1 : 0;
        neural.Checked = checkNeural;
        fpsPosition.Items.AddRange(["左上", "右上", "左下", "右下"]);
        fpsPosition.SelectedIndex = Math.Clamp(corner, 0, 3);
        showFps.Checked = fps;
        fpsPosition.Enabled = fps || selectedMonitors != MonitorItem.None;
        monitorOptions.Height = 26;
        monitorOptions.Margin = new Padding(6, 2, 4, 0);
        monitorOptions.ContextMenuStrip = monitorMenu;
        monitorOptions.Click += (_, _) => monitorMenu.Show(monitorOptions, new Point(0, 0), ToolStripDropDownDirection.AboveLeft);
        monitorMenu.Closing += (_, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };
        AddMonitor(MonitorItem.GpuLoad, "GPU 占用");
        AddMonitor(MonitorItem.GpuTemp, "GPU 温度");
        AddMonitor(MonitorItem.Vram, "显存占用");
        AddMonitor(MonitorItem.GpuPower, "GPU 功耗");
        monitorMenu.Items.Add(new ToolStripSeparator());
        AddMonitor(MonitorItem.CpuLoad, "CPU 占用");
        AddMonitor(MonitorItem.CpuTemp, "CPU 温度（微星小飞机）");
        mute.Text = muted ? "开声" : "静音";
        var title = new Label { Text = "采集卡播放器", Font = new Font(Font.FontFamily, 13, FontStyle.Bold), ForeColor = Ink, AutoSize = true, Location = new Point(15, 13) };
        var model = new Label { Text = "VC-002Pro · HDMI 预览", ForeColor = Soft, Font = new Font(Font.FontFamily, 9), AutoSize = true, Location = new Point(16, 40) };
        var buttons = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Dock = DockStyle.Right, Padding = new Padding(0, 16, 0, 0) };
        buttons.Controls.AddRange([mode, neural, effect, mute, volume, reconnect, fullscreen]);
        topBar.Controls.AddRange([title, model, buttons]);
        preview.Margin = Padding.Empty;
        preview.Controls.Add(placeholder);
        frameBadge.Font = new Font(Font.FontFamily, 10, FontStyle.Bold);
        preview.Controls.Add(frameBadge);
        var fpsOptions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Right, Padding = new Padding(0, 1, 8, 0), WrapContents = false };
        fpsOptions.Controls.AddRange([showFps, monitorOptions, fpsPosition]);
        bottomBar.Controls.AddRange([status, fpsOptions]);
        Controls.AddRange([preview, bottomBar, topBar]);
        placeholder.DoubleClick += (_, _) => ToggleFullscreen();
        preview.DoubleClick += (_, _) => ToggleFullscreen();
        preview.Resize += (_, _) => FitVideo();
        reconnect.Click += async (_, _) => { effectNotice = null; await StartCapture(); };
        mode.SelectedIndexChanged += async (_, _) => { if (!busy) { effectNotice = null; Save(); await StartCapture(); } };
        effect.SelectedIndexChanged += async (_, _) => { if (!busy) { effectNotice = null; await StartCapture(); } };
        neural.CheckedChanged += async (_, _) => { if (!busy) { effectNotice = null; await StartCapture(); } };
        showFps.CheckedChanged += (_, _) => OverlayOptionsChanged();
        fpsPosition.SelectedIndexChanged += (_, _) => { RefreshFrameBadge(); Save(); };
        volume.ValueChanged += (_, _) => { if (muted) { muted = false; mute.Text = "静音"; } pendingVolume = true; };
        mute.Click += (_, _) => { muted = !muted; mute.Text = muted ? "开声" : "静音"; pendingVolume = true; Save(); };
        fullscreen.Click += (_, _) => ToggleFullscreen();
        timer.Tick += (_, _) =>
        {
            if (full) UpdateFullscreenToolbar(PointToClient(Cursor.Position), ContainsFocus || ActiveForm == this);
            CheckPlayback();
        };
        Shown += async (_, _) => { timer.Start(); await StartCapture(); };
        FormClosed += (_, _) => { closing = true; timer.Stop(); monitorMenu.Dispose(); StopCapture(); Save(); _ = Task.Run(telemetry.Dispose); };
    }

    void AddMonitor(MonitorItem flag, string caption)
    {
        var item = new ToolStripMenuItem(caption) { CheckOnClick = true, Checked = (selectedMonitors & flag) != 0 };
        item.CheckedChanged += (_, _) =>
        {
            selectedMonitors = item.Checked ? selectedMonitors | flag : selectedMonitors & ~flag;
            nextSampleAt = 0;
            if (selectedMonitors == MonitorItem.None) sensors = new();
            OverlayOptionsChanged();
        };
        monitorItems.Add(flag, item);
        monitorMenu.Items.Add(item);
    }

    void OverlayOptionsChanged()
    {
        fpsPosition.Enabled = showFps.Checked || selectedMonitors != MonitorItem.None;
        RefreshFrameBadge();
        Save();
    }

    protected override bool ShowWithoutActivation => check;

    static Button Button(string name, int width) => new() { Text = name, Width = width, Height = 31, BackColor = Color.FromArgb(42, 52, 67), ForeColor = Ink, FlatStyle = FlatStyle.Flat, Margin = new Padding(4, 0, 4, 0), Cursor = Cursors.Hand };

    async Task StartCapture(bool defaultAudio = false)
    {
        if (busy || closing) return;
        string? enhancedStartupError = null;
        busy = true; reconnect.Enabled = mode.Enabled = effect.Enabled = neural.Enabled = false;
        StopCapture();
        placeholder.Visible = true; placeholder.Text = "正在打开采集卡…";
        status.Text = "正在查找 VC-002Pro";
        lastError = null; dropped = 0; encodedFrames = 0; encoderSamples.Clear();
        videoReady = audioReady = soundReady = motionReady = neuralReady = false;
        multiplier = effect.SelectedIndex + 1;
        neuralActive = neural.Checked;
        superActive = mode.SelectedIndex == 2;
        retryAudio = defaultAudio;
        try
        {
            var playerPath = FindFfplay();
            var (video, audio) = synthetic ? ("Test video", "Test audio") : await FindDevice(playerPath);
            if (closing) return;
            if (video is null) throw new InvalidOperationException("没找到 VC-002Pro。请检查采集卡的 USB 连接，再点重连。");
            audioListed = audio != null;
            bool low = mode.SelectedIndex != 0;
            selectedMode = superActive ? "1080p→4K 超分" : low ? "1080p60" : "4K60";
            if (neuralActive) await Task.Run(() => ImageEnhancement.ValidateModel(AppContext.BaseDirectory));
            if (closing) return;
            string log = Path.Combine(appData, "playback.log");
            File.WriteAllText(log, $"{DateTimeOffset.Now:O} {selectedMode} {multiplier}x NR={neuralActive}{Environment.NewLine}");
            var start = new ProcessStartInfo(playerPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
            start.Environment["SDL_AUDIODRIVER"] = "wasapi";
            start.Environment["SDL_WINDOWS_DPI_AWARENESS"] = "system";
            start.Environment["SDL_VIDEO_MINIMIZE_ON_FOCUS_LOSS"] = "0";
            Add(start, "-hide_banner", "-stats", "-loglevel", "info", "-noborder", "-window_title", "VC-002Pro Preview", "-x", "960", "-y", "540", "-left", "-32000", "-top", "-32000", "-sync", audioListed ? "audio" : "video", "-framedrop", "-noinfbuf", "-autoexit", "-volume", check ? "0" : "100",
                "-probesize", enhanced ? "1M" : "512k", "-analyzeduration", enhanced ? "1000000" : "250000", "-f", "nut", "-i", "pipe:0");
            var capture = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(playerPath)!, "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = AppContext.BaseDirectory };
            Add(capture, "-hide_banner", "-nostdin", "-nostats", "-loglevel", "warning");
            Add(capture, "-progress", "pipe:2", "-stats_period", "0.5");
            if (enhanced) ImageEnhancement.Configure(capture, appData, neuralActive, superActive, multiplier);
            string size = low ? "1920x1080" : "3840x2160";
            if (synthetic)
                Add(capture, "-re", "-f", "lavfi", "-i",
                    $"testsrc2=s={size}:r=60[out0];sine=frequency=440:sample_rate=48000[out1]");
            else
            {
                Add(capture, "-rtbufsize", neuralActive ? "128M" : "64M", "-f", "dshow", "-video_size", size, "-framerate", "60", "-vcodec", "mjpeg");
                if (audioListed && !defaultAudio) Add(capture, "-sample_rate", "48000", "-sample_size", "16", "-channels", "2", "-audio_buffer_size", "80");
                Add(capture, "-i", "video=" + video + (audio is null ? "" : ":audio=" + audio));
            }
            if (enhanced) ImageEnhancement.ConfigureOutput(capture, low, neuralActive, superActive, multiplier);
            else Add(capture, "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy");
            Add(capture, "-max_interleave_delta", "100000", "-flush_packets", "1", "-f", "nut", "pipe:1");
            int generation = version;
            process = Process.Start(start) ?? throw new InvalidOperationException("无法启动播放器。");
            captureProcess = Process.Start(capture) ?? throw new InvalidOperationException("无法启动采集组件。");
            elapsed.Restart();
            pendingVolume = true;
            _ = ReadDiagnostics(process, generation, log, true);
            _ = ReadDiagnostics(captureProcess, generation, log, false);
            _ = PipeCapture(captureProcess.StandardOutput.BaseStream, process.StandardInput.BaseStream);
            status.Text = $"正在打开 {selectedMode} · {(audioListed ? "采集卡双声道" : "未检测到采集卡音频")}";
        }
        catch (Exception e)
        {
            if (!closing && enhanced && !check) enhancedStartupError = e.ToString();
            else if (!closing) Fail(e is InvalidOperationException or FileNotFoundException ? e.Message : "无法打开采集卡，检查连接后点重连。", e.ToString());
        }
        finally { if (!closing) { busy = false; reconnect.Enabled = mode.Enabled = effect.Enabled = neural.Enabled = true; } }
        if (enhancedStartupError != null) FallBackToOriginal(enhancedStartupError);
    }

    static void Add(ProcessStartInfo p, params string[] args) { foreach (var a in args) p.ArgumentList.Add(a); }

    string FindFfplay()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "ffplay.exe");
        if (File.Exists(bundled) && File.Exists(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))) return bundled;
        var hint = Path.Combine(AppContext.BaseDirectory, "ffplay-path.txt");
        if (File.Exists(hint)) { var path = File.ReadAllText(hint).Trim(); if (Path.IsPathFullyQualified(path) && File.Exists(path)) return path; }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(dir, "ffplay.exe"); if (Path.IsPathFullyQualified(path) && File.Exists(path)) return path;
        }
        throw new FileNotFoundException("未找到 FFplay 播放组件。请检查本机的 FFmpeg 安装。");
    }

    static async Task<(string? Video, string? Audio)> FindDevice(string ffplay)
    {
        var start = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(ffplay)!, "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
        Add(start, "-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy");
        using var p = Process.Start(start)!;
        var read = p.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!p.HasExited) p.Kill(); throw new InvalidOperationException("采集设备查询超时。请重新插好采集卡，再点重连。"); }
        string? video = null, audio = null;
        foreach (Match m in Regex.Matches(await read, "\\\"([^\\\"\\r\\n]*VC-002Pro[^\\\"\\r\\n]*)\\\"\\s+\\((video|audio)\\)", RegexOptions.IgnoreCase))
        {
            if (m.Groups[2].Value == "video") video ??= m.Groups[1].Value;
            else audio ??= m.Groups[1].Value;
        }
        return (video, audio);
    }

    static async Task PipeCapture(Stream input, Stream output)
    {
        try { await input.CopyToAsync(output, 64 * 1024).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { try { output.Dispose(); } catch (IOException) { } }
    }

    async Task ReadDiagnostics(Process p, int generation, string log, bool playback)
    {
        try
        {
            int count = 0;
            while (await p.StandardError.ReadLineAsync() is { } line && generation == version)
            {
                if (line.Contains("aq=") && line.Contains("vq=")) continue;
                if (!playback && line.StartsWith("frame=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(6), out long frames))
                {
                    encodedFrames = frames;
                    encoderSamples.Enqueue((elapsed.Elapsed.TotalSeconds, frames));
                    if (encoderSamples.Count > 5) encoderSamples.Dequeue();
                }
                if (!playback && !check && IsProgress(line)) continue;
                if (!playback && line.StartsWith("[CaptureNR] Feature 18 evaluated", StringComparison.Ordinal))
                    neuralReady = true;
                if (line.Contains("frame dropped!")) dropped++;
                if (playback && line.Contains("Video: ") && line.Contains(mode.SelectedIndex == 1 ? "1920x1080" : "3840x2160"))
                {
                    videoReady = true;
                    motionReady = motionActive && line.Contains($"{60 * multiplier} fps");
                }
                if (playback && line.Contains("Audio: ")) audioReady = true;
                if (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase) || line.Contains("Could not", StringComparison.OrdinalIgnoreCase)) lastError = line.Trim();
                if (++count <= 100) File.AppendAllText(log, line + Environment.NewLine);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    static bool IsProgress(string line)
    {
        int delimiter = line.IndexOf('=');
        if (delimiter < 0) return false;
        return line[..delimiter] is "frame" or "fps" or "stream_0_0_q" or "bitrate" or "total_size" or
            "out_time_us" or "out_time_ms" or "out_time" or "dup_frames" or "drop_frames" or "speed" or "progress";
    }

    double? RecentOutputFps()
    {
        if (encoderSamples.Count < 2) return null;
        var first = encoderSamples.Peek();
        var last = encoderSamples.Last();
        return elapsed.Elapsed.TotalSeconds - last.Time > 2.0 ? 0 :
            Math.Round((last.Frames - first.Frames) / Math.Max(0.01, last.Time - first.Time), 1);
    }

    async Task RefreshSensors()
    {
        if (sampling || videoWindow == IntPtr.Zero || selectedMonitors == MonitorItem.None ||
            Environment.TickCount64 < nextSampleAt) return;
        sampling = true;
        nextSampleAt = Environment.TickCount64 + 1000;
        MonitorItem requested = selectedMonitors;
        try
        {
            var sample = await Task.Run(() => telemetry.Read(requested));
            if (!closing && selectedMonitors == requested)
            {
                sensors = sample;
                RefreshFrameBadge();
            }
        }
        finally { sampling = false; }
    }

    void RefreshFrameBadge()
    {
        if ((!showFps.Checked && selectedMonitors == MonitorItem.None) || videoWindow == IntPtr.Zero)
        {
            frameBadge.Visible = false;
            return;
        }
        var lines = new List<string>(7);
        if (showFps.Checked) lines.Add($"输出 {Number(RecentOutputFps(), "0")} FPS");
        if (Has(MonitorItem.GpuLoad)) lines.Add($"GPU 占用  {Number(sensors.GpuLoad, "0")}%");
        if (Has(MonitorItem.GpuTemp)) lines.Add($"GPU 温度  {Number(sensors.GpuTemp, "0")} °C");
        if (Has(MonitorItem.Vram)) lines.Add($"显存  {Number(sensors.VramUsed, "0.0")} / {Number(sensors.VramTotal, "0.0")} GiB");
        if (Has(MonitorItem.GpuPower)) lines.Add($"GPU 功耗  {Number(sensors.GpuPower, "0")} W");
        if (Has(MonitorItem.CpuLoad)) lines.Add($"CPU 占用  {Number(sensors.CpuLoad, "0")}%");
        if (Has(MonitorItem.CpuTemp)) lines.Add($"CPU 温度  {Number(sensors.CpuTemp, "0")} °C");
        var caption = string.Join(Environment.NewLine, lines);
        // Give changing readouts a stable width so updates do not move the overlay.
        var measurement = (Has(MonitorItem.Vram) ? "显存  100.0 / 100.0 GiB" : "GPU 温度  100 °C")
            + string.Concat(Enumerable.Repeat("\n0", lines.Count - 1));
        var textSize = TextRenderer.MeasureText(measurement, frameBadge.Font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        var bounds = FrameBadge.PositionIn(preview.ClientSize, fpsPosition.SelectedIndex, preview.DeviceDpi, textSize);
        bool show = !frameBadge.Visible || frameBadge.Bounds != bounds;
        if (show) frameBadge.Bounds = bounds;
        if (frameBadge.Text != caption) frameBadge.Text = caption;
        if (show)
        {
            frameBadge.Visible = true;
            Native.RaiseChild(frameBadge.Handle);
        }
        bool Has(MonitorItem flag) => (selectedMonitors & flag) != 0;
        static string Number(double? value, string format) => value?.ToString(format) ?? "—";
    }

    object? CheckFpsBadge()
    {
        if (!checkFps) return null;
        var placements = new List<Rectangle>();
        bool above = frameBadge.Visible && Native.GetTopWindow(preview.Handle) == frameBadge.Handle;
        var text = frameBadge.Text;
        for (int corner = 0; corner < 4; corner++)
        {
            fpsPosition.SelectedIndex = corner;
            placements.Add(frameBadge.Bounds);
        }
        showFps.Checked = false;
        bool independent = checkSensors && frameBadge.Visible && !frameBadge.Text.Contains("FPS");
        var monitored = checkSensors ? sensors : null;
        if (checkSensors)
        {
            monitorItems[MonitorItem.GpuTemp].Checked = false;
            independent &= !frameBadge.Text.Contains("GPU 温度") && frameBadge.Text.Contains("CPU 温度");
            foreach (var item in monitorItems.Values) item.Checked = false;
        }
        return new { AboveVideo = above, Text = text, DistinctCorners = placements.Distinct().Count(),
            Independent = independent, Sensors = monitored, HiddenWhenOff = !frameBadge.Visible };
    }

    void CheckPlayback()
    {
        if (check && testTime.Elapsed.TotalSeconds >= (synthetic ? 16 : 9) && (videoWindow != IntPtr.Zero || testTime.Elapsed.TotalSeconds >= 20))
        {
            double recentFps = RecentOutputFps() ?? 0;
            File.WriteAllText(Path.Combine(appData, neuralActive || superActive ? "enhancement-check.json" : checkMultiplier > 0 ? "motion-check.json" : "check.json"),
                JsonSerializer.Serialize(new { Mode = selectedMode, Multiplier = multiplier, MotionOutputFps = motionReady ? 60 * multiplier : 0, NeuralRequested = neuralActive, NeuralOutput = neuralReady, SuperResolution = superActive, EncodedFrames = encodedFrames, RecentEncodedFps = recentFps, FpsOverlay = CheckFpsBadge(), Seconds = elapsed.Elapsed.TotalSeconds, Synthetic = synthetic, Embedded = videoWindow != IntPtr.Zero, VideoStream = videoReady, AudioStream = audioReady, AudioSession = soundReady, CaptureBufferDropWarnings = dropped, Error = lastError }, new JsonSerializerOptions { WriteIndented = true }));
            Close(); return;
        }
        if (busy || process == null) return;
        if (process.HasExited || captureProcess?.HasExited == true)
        {
            var error = lastError ?? (process.HasExited ? $"FFplay exited ({process.ExitCode})." : "Capture process exited.");
            if (enhanced && !check) { FallBackToOriginal(error); return; }
            if (audioListed && !retryAudio && videoWindow == IntPtr.Zero) { _ = StartCapture(defaultAudio: true); return; }
            Fail("采集连接中断。请确认采集卡已插好、OBS 已关闭，再点重连。", error); return;
        }
        if (videoWindow == IntPtr.Zero)
        {
            process.Refresh();
            var hwnd = process.MainWindowHandle;
            if (hwnd != IntPtr.Zero)
            {
                if (Native.Embed(hwnd, preview.Handle)) { videoWindow = hwnd; placeholder.Visible = false; FitVideo(); }
                else { Fail("无法显示预览窗口，点重连重试。", "Could not embed SDL window."); return; }
            }
            else if (elapsed.Elapsed.TotalSeconds > (neuralActive ? 20 : 12))
            {
                if (enhanced && !check) { FallBackToOriginal(lastError ?? "Enhanced preview timed out."); return; }
                if (audioListed && !retryAudio) { _ = StartCapture(defaultAudio: true); return; }
                Fail("还没收到画面。请打开底座上的 Switch 2，再点重连。", lastError); return;
            }
        }
        if (audioListed && audioReady && pendingVolume)
        {
            soundReady = Native.Volume(process.Id, volume.Value / 100f, muted);
            pendingVolume = !soundReady;
        }
        RefreshFrameBadge();
        _ = RefreshSensors();
        if (videoWindow != IntPtr.Zero)
            status.Text = effectNotice ?? $"{selectedMode}  ·  {(audioReady ? (muted ? "已静音" : "采集卡音频") : audioListed ? "等待采集卡音频" : "未找到采集卡音频")}"
                + (neuralActive ? (neuralReady ? "  ·  DLSS 5 FP8" : "  ·  DLSS 5 等待有效画面") : "")
                + (motionActive ? $"  ·  补帧 {multiplier}×（目标 {60 * multiplier} 帧）" : "")
                + (enhanced && dropped >= 3 ? "  ·  采集掉帧，建议减少效果" : !enhanced ? "  ·  F11 全屏 / Esc 返回" : "");
    }

    void FallBackToOriginal(string detail)
    {
        try { File.WriteAllText(Path.Combine(appData, "enhancement-error.log"), detail + Environment.NewLine); } catch (IOException) { }
        busy = true;
        effect.SelectedIndex = 0;
        neural.Checked = false;
        if (mode.SelectedIndex == 2) mode.SelectedIndex = 1;
        busy = false;
        Save();
        effectNotice = "画面增强启动失败或中断，已切回原画。";
        _ = StartCapture();
    }

    void Fail(string message, string? detail)
    {
        StopCapture(); lastError = detail ?? message;
        placeholder.Visible = true; placeholder.Text = message;
        status.Text = "未连接";
        try { File.AppendAllText(Path.Combine(appData, "playback.log"), lastError + Environment.NewLine); } catch (IOException) { }
    }

    void StopCapture()
    {
        version++; videoWindow = IntPtr.Zero;
        frameBadge.Visible = false;
        Stop(captureProcess); captureProcess = null;
        Stop(process); process = null;
    }

    static void Stop(Process? p)
    {
        if (p == null) return;
        try { if (!p.HasExited) { p.Kill(); p.WaitForExit(1200); } } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        p.Dispose();
    }

    void FitVideo()
    {
        if (changingFullscreen || videoWindow == IntPtr.Zero) return;
        Native.ResizeSurface(videoWindow, new Rectangle(0, 0, Math.Max(1, preview.ClientSize.Width),
            Math.Max(1, preview.ClientSize.Height)), show: true);
        RefreshFrameBadge();
    }

    void ToggleFullscreen()
    {
        changingFullscreen = true;
        if (videoWindow != IntPtr.Zero) Native.HideSurface(videoWindow);
        frameBadge.Hide();
        SuspendLayout();
        if (!full)
        {
            restore = new RectState(WindowState, WindowState == FormWindowState.Normal ? Bounds : RestoreBounds);
            var screen = Screen.FromControl(this).Bounds;
            full = true;
            fullscreen.Text = "退出全屏";
            topBar.Hide(); bottomBar.Hide();
            topBar.Dock = DockStyle.None;
            topBar.BringToFront();
            WindowState = FormWindowState.Normal; FormBorderStyle = FormBorderStyle.None;
            Native.ResizeSurface(Handle, screen);
        }
        else if (restore != null)
        {
            full = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            Native.ResizeSurface(Handle, restore.Bounds);
            WindowState = restore.State;
            topBar.Dock = DockStyle.Top;
            preview.BringToFront();
            topBar.Show(); bottomBar.Show();
            fullscreen.Text = "全屏";
        }
        toolbarHideAt = 0;
        ResumeLayout(true);
        Native.RepaintSurface(Handle);
        changingFullscreen = false;
        FitVideo();
    }

    void UpdateFullscreenToolbar(Point mouse, bool focused)
    {
        if (!full) return;
        bool overPlayer = ClientRectangle.Contains(mouse);
        int revealBand = Math.Max(1, 7 * DeviceDpi / 96);
        if (!topBar.Visible)
        {
            if (!focused || !overPlayer || mouse.Y >= revealBand) return;
            topBar.SetBounds(0, 0, ClientSize.Width, topBar.Height);
            topBar.Show();
        }
        if (focused && ((overPlayer && topBar.Bounds.Contains(mouse)) || mode.DroppedDown || effect.DroppedDown))
        {
            toolbarHideAt = 0;
            return;
        }
        long now = Environment.TickCount64;
        if (toolbarHideAt == 0) toolbarHideAt = now + 650;
        if (now >= toolbarHideAt)
        {
            // The external SDL child otherwise covers the invalid region while Windows repaints.
            if (videoWindow != IntPtr.Zero) Native.HideSurface(videoWindow);
            topBar.Hide();
            Native.RepaintSurface(Handle);
            FitVideo();
            toolbarHideAt = 0;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11 || (keyData == Keys.Escape && full)) { ToggleFullscreen(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    void Save()
    {
        if (check) return;
        try { File.WriteAllText(Path.Combine(appData, "settings.json"), JsonSerializer.Serialize(new Preferences(volume.Value, muted, mode.SelectedIndex != 0, mode.SelectedIndex == 2, showFps.Checked, fpsPosition.SelectedIndex, selectedMonitors))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    sealed record Preferences(int Volume, bool Muted, bool Low, bool SuperRes = false, bool ShowFps = false, int FpsCorner = 1,
        MonitorItem Monitors = MonitorItem.None);
    sealed record RectState(FormWindowState State, Rectangle Bounds);
}
