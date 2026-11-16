using System.Diagnostics;
using System.Globalization;
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
        int testInputIndex = Array.IndexOf(args, "--test-input");
        string? testInput = check && testInputIndex >= 0 && testInputIndex + 1 < args.Length && File.Exists(args[testInputIndex + 1])
            ? Path.GetFullPath(args[testInputIndex + 1]) : null;
        bool synthetic = args.Contains("--synthetic") || testInput is not null;
        using var player = new Player(check, args.Contains("--1080p"), checkMultiplier, synthetic,
            checkNeural || args.Contains("--nr-preview"), checkSuper, checkFps, checkSensors, testInput,
            check && args.Contains("--test-video-only"), checkMotion && args.Contains("--check-direct-controls"));
        int displayIndex = Array.IndexOf(args, "--display");
        if (displayIndex >= 0 && displayIndex + 1 < args.Length && int.TryParse(args[displayIndex + 1], out int display) &&
            Screen.AllScreens.FirstOrDefault(s => s.DeviceName.EndsWith($"\\DISPLAY{display}", StringComparison.OrdinalIgnoreCase)) is { } screen)
        {
            player.StartPosition = FormStartPosition.Manual;
            player.Location = new Point(screen.WorkingArea.Left + 80, screen.WorkingArea.Top + 80);
        }
        Application.Run(player);
    }
}

internal sealed class Player : Form, IMessageFilter
{
    static readonly Color Surface = Color.FromArgb(25, 30, 39);
    static readonly Color Header = Color.FromArgb(20, 26, 35);
    static readonly Color Border = Color.FromArgb(59, 75, 94);
    static readonly Color Ink = Color.FromArgb(240, 244, 251);
    static readonly Color Soft = Color.FromArgb(163, 178, 197);
    static readonly Color Accent = Color.FromArgb(114, 231, 223);
    const string EnterFullscreen = "进入全屏", ExitFullscreen = "退出全屏";
    const string MuteSound = "静音", UnmuteSound = "恢复声音", ReconnectDevice = "重连采集卡";
    readonly string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CapturePlayer");
    readonly Panel preview = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(9, 12, 17) };
    readonly Label placeholder = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Soft, Text = "正在打开采集卡…" };
    readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Soft, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 8, 0) };
    readonly ComboBox mode = Choice("画质", 176);
    readonly ComboBox effect = Choice("补帧倍率", 112);
    readonly CheckBox neural = new() { Text = "DLSS 5", AutoSize = true, ForeColor = Ink };
    readonly CheckBox showOverlay = new() { Text = "显示监控", AutoSize = true, ForeColor = Ink };
    readonly ToolStripMenuItem showFps = new("帧数 (FPS)") { CheckOnClick = true };
    readonly ComboBox fpsPosition = Choice("监控显示位置", 78);
    readonly FrameBadge frameBadge = new();
    readonly Button monitorOptions = Button("监控设置", 102);
    readonly ContextMenuStrip monitorMenu = new();
    readonly TrackBar badgeOpacity = new() { Minimum = FrameBadge.MinimumOpacity, Maximum = FrameBadge.DefaultOpacity,
        Value = FrameBadge.DefaultOpacity, TickStyle = TickStyle.None, SmallChange = 5, LargeChange = 10,
        AutoSize = false, Width = 104, Height = 24, AccessibleName = "浮层不透明度" };
    readonly ToolTip hints = new() { ShowAlways = true };
    readonly Bitmap? brandImage;
    readonly Dictionary<MonitorItem, ToolStripMenuItem> monitorItems = new();
    readonly HardwareTelemetry telemetry = new();
    MonitorSample sensors = new();
    MonitorItem selectedMonitors;
    long nextSampleAt;
    bool sampling;
    readonly TrackBar volume = new() { Minimum = 0, Maximum = 100, Value = 70, TickStyle = TickStyle.None, AutoSize = false, Width = 100, Height = 26, AccessibleName = "采集音量" };
    readonly Button mute = Button(MuteSound, 102), reconnect = Button(ReconnectDevice, 118), fullscreen = Button(EnterFullscreen, 108, primary: true);
    readonly Panel topBar = new() { Dock = DockStyle.Top, Height = 106, BackColor = Header };
    readonly Panel bottomBar = new() { Dock = DockStyle.Bottom, Height = 48, BackColor = Surface };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 180 };
    readonly Stopwatch elapsed = new();
    readonly Stopwatch testTime = Stopwatch.StartNew();
    readonly bool check;
    readonly int checkMultiplier;
    readonly bool synthetic;
    readonly string? testInput;
    readonly bool testVideoOnly;
    readonly bool checkFps, checkSensors;
    readonly bool checkDirectControls;
    int controlStage, initialCapturePid;
    bool fullscreenResized, windowResized, checkMotionAudio;
    double? checkFullscreenFps, checkWindowedFps;
    Size motionSize, initialMotionSize;
    int multiplier = 1;
    bool motionActive => multiplier > 1;
    bool motionReady;
    bool neuralActive, neuralReady, superActive;
    bool enhanced => motionActive || neuralActive || superActive;
    bool directPreview => motionActive || neuralActive && !superActive;
    long encodedFrames;
    readonly Queue<(double Time, long Frames)> encoderSamples = new();
    readonly List<double> checkRates = [];
    readonly Queue<double> previewDelay = new();
    readonly Queue<(long At, long Count)> previewDrops = new();
    static readonly Regex PlaybackStats = new(@"^\s*(?<clock>-?\d+\.\d+)\s+(?:A-V|M-V):\s*(?<diff>-?\d+\.\d+).*?\bfd=\s*(?<drops>\d+)", RegexOptions.CultureInvariant);
    long nextDelaySample, outputFrameAt, behindSince, nextWarmupReport, visibleSince, slowSince, nextPlaybackReport;
    double? latestPreviewDelay;
    string? effectNotice;
    Process? process, captureProcess, filterProcess, motionAudio;
    LiveCapture? liveCapture, effectRelay;
    Task? captureReader;
    CancellationTokenSource? feedCancellation, audioFeed;
    RetainedPreview? retained;
    bool captureLow, bridging, previewAudio, audioMaster;
    IntPtr videoWindow, motionWindow;
    double? directFps, directGeneratedFps;
    long directReportAt;
    Process? SoundProcess => directPreview ? motionAudio : process;
    IntPtr DisplayWindow => videoWindow != IntPtr.Zero ? videoWindow : retained?.Window ?? IntPtr.Zero;
    bool busy, closing, muted, full, changingFullscreen, pendingVolume, audioListed, soundReady, videoReady, audioReady, retryAudio;
    int version, dropped;
    string selectedMode = "4K60";
    string? lastError;
    RectState? restore;
    long toolbarHideAt;
    string? displayDevice;
    int displayRefresh;

    public Player(bool check, bool low, int checkMultiplier, bool synthetic, bool checkNeural, bool checkSuper,
        bool checkFps, bool checkSensors, string? testInput, bool testVideoOnly, bool checkDirectControls)
    {
        this.check = check;
        this.checkMultiplier = checkMultiplier;
        this.synthetic = synthetic;
        this.testInput = testInput;
        this.testVideoOnly = testVideoOnly;
        this.checkFps = checkFps;
        this.checkSensors = checkSensors;
        this.checkDirectControls = checkDirectControls;
        SuspendLayout();
        Text = synthetic ? "采集卡播放器 · 测试图" : "采集卡播放器";
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        brandImage = Icon?.ToBitmap();
        ForeColor = Ink;
        BackColor = Surface;
        ClientSize = new Size(1090, 650);
        MinimumSize = new Size(970, 460);
        StartPosition = FormStartPosition.CenterScreen;
        Directory.CreateDirectory(appData);
        bool super = checkSuper;
        bool fps = checkFps;
        bool overlayEnabled = true;
        selectedMonitors = checkSensors ? MonitorItem.All : MonitorItem.None;
        int corner = 1;
        try
        {
            var settings = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(appData, "settings.json")));
            if (settings != null && !synthetic)
            {
                volume.Value = Math.Clamp(settings.Volume, 0, 100); muted = settings.Muted;
                if (!check)
                {
                    low = settings.Low; super = settings.SuperRes; fps = settings.ShowFps; corner = settings.FpsCorner;
                    selectedMonitors = settings.Monitors & MonitorItem.All;
                    overlayEnabled = settings.OverlayEnabled;
                    badgeOpacity.Value = Math.Clamp(settings.BadgeOpacity, badgeOpacity.Minimum, badgeOpacity.Maximum);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        if (check) { muted = true; }
        mode.Items.AddRange(["4K · 60 帧", "1080p · 60 帧", "1080p → 4K 超分"]);
        mode.SelectedIndex = super ? 2 : low ? 1 : 0;
        effect.Items.AddRange(["关闭", "2×", "3×", "4×", "5×", "6×"]);
        bool useStartupEffects = !check && !synthetic &&
            File.Exists(Path.Combine(AppContext.BaseDirectory, "resources", "gpu-preview.exe"));
        int startupMultiplier = checkMultiplier > 0 ? checkMultiplier : useStartupEffects && !checkNeural ? 3 : 1;
        effect.SelectedIndex = startupMultiplier - 1;
        neural.Checked = checkNeural || useStartupEffects && ImageEnhancement.HasModelFiles(AppContext.BaseDirectory);
        fpsPosition.Items.AddRange(["左上", "右上", "左下", "右下"]);
        fpsPosition.SelectedIndex = Math.Clamp(corner, 0, 3);
        showFps.Checked = fps;
        showOverlay.Checked = overlayEnabled;
        fpsPosition.Enabled = OverlayVisible;
        monitorOptions.ContextMenuStrip = monitorMenu;
        monitorOptions.Click += (_, _) => monitorMenu.Show(monitorOptions, new Point(0, 0), ToolStripDropDownDirection.AboveLeft);
        monitorMenu.Closing += (_, e) => { if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true; };
        AddOpacitySetting();
        monitorMenu.Items.Add(showFps);
        AddMonitor(MonitorItem.GpuLoad, "GPU 占用");
        AddMonitor(MonitorItem.GpuTemp, "GPU 温度");
        AddMonitor(MonitorItem.Vram, "显存占用");
        AddMonitor(MonitorItem.GpuPower, "GPU 功耗");
        monitorMenu.Items.Add(new ToolStripSeparator());
        AddMonitor(MonitorItem.CpuLoad, "CPU 占用");
        AddMonitor(MonitorItem.CpuTemp, "CPU 温度（微星小飞机）");
        monitorMenu.Items.Add(new ToolStripSeparator());
        monitorMenu.Items.Add("清除已选项目", null, (_, _) => ClearMonitorItems());
        mute.Text = muted ? UnmuteSound : MuteSound;
        BuildToolbar();
        preview.Margin = Padding.Empty;
        preview.Controls.Add(placeholder);
        frameBadge.Font = new Font(Font.FontFamily, 10, FontStyle.Bold);
        preview.Controls.Add(frameBadge);
        Controls.AddRange([preview, bottomBar, topBar]);
        ResumeLayout(true);
        placeholder.DoubleClick += (_, _) => ToggleFullscreen();
        preview.DoubleClick += (_, _) => ToggleFullscreen();
        preview.Resize += (_, _) => FitVideo();
        reconnect.Click += async (_, _) => { effectNotice = null; await StartCapture(reopen: true); };
        mode.SelectionChangeCommitted += (_, _) => ApplySelection();
        effect.SelectionChangeCommitted += (_, _) => ApplySelection();
        neural.CheckedChanged += async (_, _) => await ApplyNeuralSelection();
        showFps.Click += (_, _) => OverlayOptionsChanged();
        showOverlay.CheckedChanged += (_, _) => OverlayOptionsChanged();
        fpsPosition.SelectedIndexChanged += (_, _) => { RefreshFrameBadge(); Save(); };
        volume.ValueChanged += (_, _) => { if (muted) { muted = false; mute.Text = MuteSound; } VolumeChanged(); };
        mute.Click += (_, _) => { muted = !muted; mute.Text = muted ? UnmuteSound : MuteSound; VolumeChanged(); Save(); };
        fullscreen.Click += (_, _) => ToggleFullscreen();
        timer.Tick += (_, _) =>
        {
            if (full) UpdateFullscreenToolbar(PointToClient(Cursor.Position), ContainsFocus || ActiveForm == this);
            CheckPlayback();
        };
        Shown += async (_, _) => { Application.AddMessageFilter(this); timer.Start(); await StartCapture(); };
        FormClosed += (_, _) => { closing = true; Application.RemoveMessageFilter(this); timer.Stop(); monitorMenu.Dispose(); hints.Dispose(); brandImage?.Dispose(); StopCapture(); Save(); _ = Task.Run(telemetry.Dispose); };
    }

    void BuildToolbar()
    {
        var title = new Label { Text = "采集卡播放器", Font = new Font(Font.FontFamily, 14, FontStyle.Bold), ForeColor = Ink, AutoSize = true };
        var mark = new PictureBox { Image = brandImage, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(31, 31), TabStop = false };
        var brand = ToolbarRow(DockStyle.Left, new Padding(14, 0, 0, 0), mark, title, Caption("VC-002Pro / HDMI"));
        var actions = ToolbarRow(DockStyle.Right, new Padding(0, 0, 14, 0), reconnect, fullscreen);
        var header = new Panel { Dock = DockStyle.Top, Height = 55, BackColor = Header };
        header.Controls.AddRange([brand, actions]);
        var divider = new Panel { Width = 1, Height = 24, BackColor = Border, Margin = new Padding(12, 0, 12, 0) };
        var playback = ToolbarRow(DockStyle.Left, new Padding(14, 0, 0, 0),
            Caption("画质"), mode, Caption("补帧"), effect, neural, divider, Caption("音量"), volume, mute);
        var settings = new Panel { Dock = DockStyle.Fill, BackColor = Surface };
        settings.Controls.Add(playback);
        topBar.Controls.AddRange([settings, header]);

        var overlays = ToolbarRow(DockStyle.Right, new Padding(0, 0, 14, 0), showOverlay, monitorOptions, Caption("位置"), fpsPosition);
        bottomBar.Controls.AddRange([status, overlays]);
        hints.SetToolTip(mode, "选择采集画质，或把 1080p 画面超分至 4K。");
        hints.SetToolTip(effect, "设置补帧倍率，受显示器刷新率和画面处理速度限制。");
        hints.SetToolTip(neural, "启用 DLSS 5 FP8 画面增强。");
        hints.SetToolTip(reconnect, "重新连接 USB 采集卡。");
        hints.SetToolTip(fullscreen, "F11 切换全屏，Esc 退出全屏。");
        hints.SetToolTip(monitorOptions, "勾选要显示的帧数和硬件数据，或调整浮层不透明度。");
        hints.SetToolTip(showOverlay, "显示或隐藏整个监控浮层。关闭时保留已选项目。");
        hints.SetToolTip(fpsPosition, "设置帧数和硬件数据在画面上的位置。");
    }

    static Label Caption(string caption) => new() { Text = caption, AutoSize = true, ForeColor = Soft };

    static TableLayoutPanel ToolbarRow(DockStyle dock, Padding padding, params Control[] items)
    {
        var row = new TableLayoutPanel { Dock = dock, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = items.Length, RowCount = 1, GrowStyle = TableLayoutPanelGrowStyle.FixedSize, Padding = padding };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i];
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            item.Anchor = AnchorStyles.None;
            if (item is not Panel) item.Margin = new Padding(4, 0, 4, 0);
            row.Controls.Add(item, i, 0);
        }
        return row;
    }

    void AddMonitor(MonitorItem flag, string caption)
    {
        var item = new ToolStripMenuItem(caption) { CheckOnClick = true, Checked = (selectedMonitors & flag) != 0 };
        item.Click += (_, _) =>
        {
            selectedMonitors = item.Checked ? selectedMonitors | flag : selectedMonitors & ~flag;
            OverlayOptionsChanged();
        };
        monitorItems.Add(flag, item);
        monitorMenu.Items.Add(item);
    }

    void ClearMonitorItems()
    {
        showFps.Checked = false;
        foreach (var item in monitorItems.Values) item.Checked = false;
        selectedMonitors = MonitorItem.None;
        OverlayOptionsChanged();
    }

    void AddOpacitySetting()
    {
        var caption = new Label { Text = "不透明度", AutoSize = true, ForeColor = SystemColors.MenuText };
        var value = new Label { AutoSize = true, MinimumSize = new Size(36, 0), ForeColor = SystemColors.MenuText };
        void UpdateOpacity()
        {
            value.Text = $"{badgeOpacity.Value}%";
            frameBadge.OpacityPercent = badgeOpacity.Value;
        }
        UpdateOpacity();
        badgeOpacity.ValueChanged += (_, _) => UpdateOpacity();
        var row = ToolbarRow(DockStyle.None, new Padding(0, 2, 4, 2), caption, badgeOpacity, value);
        row.BackColor = SystemColors.Menu;
        monitorMenu.Items.Add(new ToolStripControlHost(row) { Margin = new Padding(0, 2, 0, 2) });
        monitorMenu.Items.Add(new ToolStripSeparator());
        monitorMenu.Closed += (_, _) => Save();
    }

    bool OverlayVisible => showOverlay.Checked && (showFps.Checked || selectedMonitors != MonitorItem.None);

    void OverlayOptionsChanged()
    {
        fpsPosition.Enabled = OverlayVisible;
        nextSampleAt = 0;
        if (!OverlayVisible || selectedMonitors == MonitorItem.None) sensors = new();
        RefreshFrameBadge();
        Save();
    }

    protected override bool ShowWithoutActivation => check;

    static ComboBox Choice(string name, int width) => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width,
        BackColor = Color.FromArgb(230, 239, 247), ForeColor = Header, AccessibleName = name };

    static Button Button(string name, int width, bool primary = false)
    {
        var button = new Button { Text = name, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(width, 34), Padding = new Padding(10, 1, 10, 1),
            BackColor = primary ? Accent : Color.FromArgb(36, 47, 62), ForeColor = primary ? Header : Ink,
            FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, Cursor = Cursors.Hand };
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(150, 244, 233) : Color.FromArgb(47, 63, 81);
        return button;
    }

    void ApplySelection() => BeginInvoke(async () =>
    {
        if (busy || closing || (process is not null && captureLow == (mode.SelectedIndex != 0) &&
            superActive == (mode.SelectedIndex == 2) && multiplier == effect.SelectedIndex + 1)) return;
        effectNotice = null;
        Save();
        await StartCapture();
    });

    async Task ApplyNeuralSelection()
    {
        if (busy || closing) return;
        effectNotice = null;
        await StartCapture();
    }

    void VolumeChanged()
    {
        pendingVolume = true;
        if (retained is { } previous && (previous.Audio ?? previous.Player) is { HasExited: false } sound)
            Native.Volume(sound.Id, volume.Value / 100f, muted);
    }

    async Task StartCapture(bool reopen = false, bool defaultAudio = false, bool bypassBridge = false)
    {
        if (busy || closing) return;
        string? enhancedStartupError = null;
        busy = true; reconnect.Enabled = mode.Enabled = effect.Enabled = neural.Enabled = false;
        if (effect.SelectedIndex > 0)
        {
            if (mode.SelectedIndex == 2) { mode.SelectedIndex = 1; effectNotice = "直出补帧已选中，超分请单独开启。"; }
            if (!check)
            {
                int hz = Native.DisplayRefresh(Screen.FromControl(this).DeviceName);
                int maxMultiplier = hz > 0 ? Math.Clamp((hz + 1) / 60, 1, 6) : 2;
                if (effect.SelectedIndex + 1 > maxMultiplier)
                {
                    effect.SelectedIndex = maxMultiplier - 1;
                    effectNotice = maxMultiplier > 1 ? $"按屏幕刷新率切到 {maxMultiplier}×。" :
                        neural.Checked ? "当前屏幕刷新率不足，已关闭补帧，保留 DLSS 5。" : "当前屏幕刷新率不足，已使用原画。";
                }
            }
        }
        bool low = mode.SelectedIndex != 0;
        bool opening = reopen || captureProcess is null || captureProcess.HasExited || captureReader?.IsCompleted != false || captureLow != low;
        // Replacing one GPU filter with another can overload both while the new
        // graph is warming. Briefly use a raw preview between the two graphs.
        bridging = !opening && !bypassBridge && videoWindow != IntPtr.Zero && enhanced &&
            (effect.SelectedIndex > 0 || neural.Checked || mode.SelectedIndex == 2);
        if (opening) StopRetained();
        else if (videoWindow != IntPtr.Zero && process is { HasExited: false } && filterProcess?.HasExited != true)
        {
            StopRetained();
            retained = new RetainedPreview(process, filterProcess, feedCancellation, motionAudio, audioFeed, videoWindow, RecentOutputFps(), directPreview);
            process = filterProcess = motionAudio = null; feedCancellation = audioFeed = null;
        }
        StopPreview();
        placeholder.Visible = retained is null;
        placeholder.Text = opening ? "正在打开采集卡…" : "正在切换画面效果…";
        status.Text = retained is null ? placeholder.Text : "正在切换画面效果 · 当前画面继续播放";
        lastError = null; encodedFrames = 0; encoderSamples.Clear(); previewDelay.Clear(); previewDrops.Clear(); nextDelaySample = 0;
        effectRelay = null;
        motionWindow = IntPtr.Zero; directFps = directGeneratedFps = null; directReportAt = 0;
        latestPreviewDelay = null; behindSince = slowSince = visibleSince = 0; outputFrameAt = Environment.TickCount64; nextWarmupReport = outputFrameAt + 5000;
        nextPlaybackReport = outputFrameAt + 5000;
        videoReady = audioReady = soundReady = motionReady = neuralReady = false;
        multiplier = bridging ? 1 : effect.SelectedIndex + 1;
        neuralActive = !bridging && neural.Checked;
        superActive = !bridging && mode.SelectedIndex == 2;
        try
        {
            var playerPath = FindFfplay();
            var ffmpegPath = Path.Combine(Path.GetDirectoryName(playerPath)!, "ffmpeg.exe");
            selectedMode = superActive ? "1080p→4K 超分" : low ? "1080p60" : "4K60";
            string log = Path.Combine(appData, synthetic ? "synthetic-playback.log" : "playback.log");
            if (opening)
            {
                StopDevice();
                await OpenDevice(ffmpegPath, playerPath, low, defaultAudio, log);
            }
            if (closing) return;
            if (neuralActive) await Task.Run(() => ImageEnhancement.ValidateModel(AppContext.BaseDirectory));
            if (closing) return;
            previewAudio = audioListed && !testVideoOnly;
            bool testVideoClock = check && neuralActive && !motionActive && Environment.GetEnvironmentVariable("CAPTUREPLAYER_NR_VIDEO_CLOCK") == "1";
            audioMaster = previewAudio && !testVideoClock;
            File.AppendAllText(log, $"{DateTimeOffset.Now:O} {selectedMode} {multiplier}x NR={neuralActive} CapturePID={captureProcess!.Id}{(bridging ? " Bridge" : "")}{Environment.NewLine}");
            if (directPreview)
            {
                StartDirectPreview(ffmpegPath, playerPath, low, log);
                return;
            }
            var start = new ProcessStartInfo(playerPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
            start.Environment["SDL_AUDIODRIVER"] = "wasapi";
            start.Environment["SDL_WINDOWS_DPI_AWARENESS"] = "system";
            start.Environment["SDL_WINDOW_NO_ACTIVATION_WHEN_SHOWN"] = "1";
            start.Environment["SDL_VIDEO_MINIMIZE_ON_FOCUS_LOSS"] = "0";
            // ffplay's -noborder also disables SDL resizing on Windows. Keep its resizable
            // window off-screen until Embed removes the frame and attaches it to the preview.
            Add(start, "-hide_banner", "-stats", "-loglevel", "info", "-window_title", "VC-002Pro Preview", "-x", "960", "-y", "540", "-left", "-32000", "-top", "-32000", "-sync", audioMaster ? "audio" : "video", "-framedrop", "-noinfbuf", "-autoexit", "-volume", check || synthetic ? "0" : "100");
            if (!previewAudio) Add(start, "-an");
            if (enhanced) Add(start, "-fflags", "nobuffer", "-flags", "low_delay");
            if (enhanced) Add(start, "-thread_type", "slice", "-fast");
            Add(start, "-threads", enhanced ? "4" : "2", "-probesize", enhanced ? "1M" : "128k", "-analyzeduration", enhanced ? "100000" : "50000", "-f", "matroska", "-i", "pipe:0");
            ProcessStartInfo? filter = null;
            if (enhanced)
            {
                filter = NewFfmpeg(ffmpegPath);
                filter.RedirectStandardInput = true;
                Add(filter, "-progress", "pipe:2", "-stats_period", "0.5");
                ImageEnhancement.Configure(filter, appData, neuralActive, superActive, multiplier);
                Add(filter, "-copyts", "-thread_queue_size", "2", "-threads", "3", "-probesize", "128k", "-analyzeduration", "50000", "-f", "matroska", "-i", "pipe:0");
                double? liveOrigin = motionActive ? await liveCapture!.ClockOriginAsync() : null;
                if (closing) return;
                ImageEnhancement.ConfigureOutput(filter, low, neuralActive, superActive, multiplier, liveOrigin, previewAudio);
                Add(filter, "-fps_mode", "passthrough", "-max_interleave_delta", "20000", "-flush_packets", "1", "-live", "1", "-write_crc32", "0", "-cluster_time_limit", "1", "-cluster_size_limit", "0", "-f", "matroska", "pipe:1");
            }
            int generation = version;
            process = Process.Start(start) ?? throw new InvalidOperationException("无法启动播放器。");
            elapsed.Restart();
            pendingVolume = true;
            _ = ReadDiagnostics(process, generation, log, true);
            if (filter is not null)
            {
                filterProcess = Process.Start(filter) ?? throw new InvalidOperationException("无法启动画面增强组件。");
                _ = ReadDiagnostics(filterProcess, generation, log, false);
                effectRelay = new LiveCapture(filterProcess.StandardOutput.BaseStream, needsKeyframe: true);
                _ = effectRelay.FeedAsync(process.StandardInput.BaseStream, CancellationToken.None);
                _ = effectRelay.ReadAsync();
            }
            feedCancellation = new CancellationTokenSource();
            _ = liveCapture!.FeedAsync((filterProcess ?? process).StandardInput.BaseStream, feedCancellation.Token);
        }
        catch (Exception e)
        {
            if (!closing && enhanced && !check) enhancedStartupError = e.ToString();
            else if (!closing) Fail(e is InvalidOperationException or FileNotFoundException ? e.Message : "无法打开采集卡，检查连接后点重连。", e.ToString());
        }
        finally { if (!closing) { busy = false; reconnect.Enabled = mode.Enabled = effect.Enabled = neural.Enabled = true; } }
        if (enhancedStartupError != null) RecoverEnhancement(enhancedStartupError);
    }

    void StartDirectPreview(string ffmpeg, string ffplay, bool low, string log)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "resources", "gpu-preview.exe");
        string shader = Path.Combine(AppContext.BaseDirectory, "resources", "fg_direct.hlsl");
        if (!File.Exists(executable) || !File.Exists(shader)) throw new FileNotFoundException("缺少直出画面组件。");
        string width = low ? "1920" : "3840", height = low ? "1080" : "2160";
        int generation = version;
        var render = NewFfmpeg(executable, initializeArguments: false);
        render.RedirectStandardInput = true;
        Add(render, "--embedded", shader, width, height, multiplier.ToString(CultureInfo.InvariantCulture),
            unchecked((ulong)preview.Handle.ToInt64()).ToString(CultureInfo.InvariantCulture));
        var decode = NewFfmpeg(ffmpeg);
        decode.RedirectStandardInput = true;
        if (neuralActive) ImageEnhancement.Configure(decode, appData, neural: true, superResolution: false, multiplier: 1);
        Add(decode, "-threads", "4", "-probesize", "128k", "-analyzeduration", "50000", "-f", "matroska", "-i", "pipe:0");
        if (neuralActive) ImageEnhancement.MapDirectNeuralVideo(decode);
        else Add(decode, "-map", "0:v:0", "-vf", $"scale={width}:{height}:in_range=auto:out_range=tv:flags=fast_bilinear:threads=4,format=nv12,settb=1/60,setpts=N");
        Add(decode, "-an", "-fps_mode", "passthrough", "-pix_fmt", "nv12", "-c:v", "rawvideo", "-threads:v", "1", "-flush_packets", "1", "-f", "rawvideo", "pipe:1");

        process = Process.Start(render) ?? throw new InvalidOperationException("无法启动直出画面窗口。");
        _ = ReadDiagnostics(process, generation, log, true);
        elapsed.Restart();
        filterProcess = Process.Start(decode) ?? throw new InvalidOperationException("无法启动采集解码。");
        _ = ReadDiagnostics(filterProcess, generation, log, false);
        _ = RelayRawVideo(filterProcess.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        feedCancellation = new CancellationTokenSource();
        _ = liveCapture!.FeedAsync(filterProcess.StandardInput.BaseStream, feedCancellation.Token);

        if (previewAudio)
        {
            var audio = NewFfmpeg(ffplay, initializeArguments: false);
            audio.RedirectStandardInput = true;
            audio.Environment["SDL_AUDIODRIVER"] = "wasapi";
            Add(audio, "-hide_banner", "-stats", "-loglevel", "info", "-nodisp", "-vn", "-noinfbuf", "-autoexit",
                "-volume", check || synthetic ? "0" : "100", "-probesize", "128k", "-analyzeduration", "50000", "-f", "matroska", "-i", "pipe:0");
            motionAudio = Process.Start(audio) ?? throw new InvalidOperationException("无法启动采集音频。");
            _ = ReadDiagnostics(motionAudio, generation, log, true);
            audioFeed = new CancellationTokenSource();
            _ = liveCapture.FeedAsync(motionAudio.StandardInput.BaseStream, audioFeed.Token);
        }
        pendingVolume = true;
    }

    static async Task RelayRawVideo(Stream from, Stream to)
    {
        try { await from.CopyToAsync(to, 1024 * 1024).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { try { to.Dispose(); } catch (IOException) { } }
    }

    async Task OpenDevice(string ffmpegPath, string playerPath, bool low, bool defaultAudio, string log)
    {
        var (video, audio) = synthetic ? ("Test video", "Test audio") : await FindDevice(playerPath);
        if (closing) return;
        if (video is null) throw new InvalidOperationException("没找到 VC-002Pro。请检查采集卡的 USB 连接，再点重连。");
        dropped = 0;
        captureLow = low;
        retryAudio = defaultAudio;
        audioListed = audio != null;
        string size = low ? "1920x1080" : "3840x2160";
        var capture = NewFfmpeg(ffmpegPath);
        Add(capture, "-progress", "pipe:2", "-stats_period", "0.5");
        if (testInput is not null)
            Add(capture, "-stream_loop", "-1", "-re", "-i", testInput, "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy");
        else if (synthetic)
            Add(capture, "-re", "-f", "lavfi", "-i", $"testsrc2=s={size}:r=60[out0];sine=frequency=440:sample_rate=48000[out1]",
                "-map", "0:v:0", "-map", "0:a:0", "-c:v", "mjpeg", "-threads:v", "4", "-q:v", "5", "-pix_fmt", "yuvj420p", "-c:a", "pcm_s16le");
        else
        {
            Add(capture, "-probesize", "128k", "-analyzeduration", "100000", "-rtbufsize", "16M", "-f", "dshow", "-video_size", size, "-framerate", "60", "-vcodec", "mjpeg");
            if (audioListed && !defaultAudio) Add(capture, "-sample_rate", "48000", "-sample_size", "16", "-channels", "2", "-audio_buffer_size", "40");
            Add(capture, "-i", "video=" + video + (audio is null ? "" : ":audio=" + audio), "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy");
        }
        Add(capture, "-max_interleave_delta", "20000", "-flush_packets", "1", "-live", "1", "-write_crc32", "0", "-cluster_time_limit", "1", "-cluster_size_limit", "0", "-f", "matroska", "pipe:1");
        File.WriteAllText(log, $"{DateTimeOffset.Now:O} Open capture {size}{Environment.NewLine}");
        captureProcess = Process.Start(capture) ?? throw new InvalidOperationException("无法启动采集组件。");
        _ = ReadDiagnostics(captureProcess, -1, log, false);
        liveCapture = new LiveCapture(captureProcess.StandardOutput.BaseStream);
        captureReader = liveCapture.ReadAsync();
    }

    static ProcessStartInfo NewFfmpeg(string ffmpegPath, bool initializeArguments = true)
    {
        var start = new ProcessStartInfo(ffmpegPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = AppContext.BaseDirectory };
        if (initializeArguments) Add(start, "-hide_banner", "-nostdin", "-nostats", "-loglevel", "warning");
        return start;
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

    async Task ReadDiagnostics(Process p, int generation, string log, bool playback)
    {
        try
        {
            int count = 0;
            while (await p.StandardError.ReadLineAsync() is { } line)
            {
                // The previous renderer still needs its stderr drained while we warm the new one.
                if (generation == -1 ? !ReferenceEquals(p, captureProcess) : generation != version) continue;
                if (directPreview && playback && ReferenceEquals(p, process) && line.StartsWith("CaptureFG ", StringComparison.Ordinal))
                {
                    if (line.StartsWith("CaptureFG ready=", StringComparison.Ordinal) &&
                        ulong.TryParse(line.AsSpan("CaptureFG ready=".Length), out ulong handle))
                    {
                        motionWindow = new IntPtr(unchecked((long)handle));
                        videoReady = true;
                        motionReady = motionActive;
                        outputFrameAt = Environment.TickCount64;
                    }
                    else if (line.StartsWith("CaptureFG fps=", StringComparison.Ordinal))
                    {
                        var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (values.Length == 6 &&
                            double.TryParse(values[1].AsSpan(4), CultureInfo.InvariantCulture, out double fps) &&
                            double.TryParse(values[2].AsSpan(10), CultureInfo.InvariantCulture, out double generated) &&
                            double.TryParse(values[5].AsSpan(6), CultureInfo.InvariantCulture, out double queue))
                        {
                            directFps = fps; directGeneratedFps = generated;
                            directReportAt = Environment.TickCount64;
                            if (fps > 0) outputFrameAt = directReportAt;
                            if (check && videoWindow != IntPtr.Zero)
                            {
                                checkRates.Add(fps);
                                previewDelay.Enqueue(queue);
                                if (previewDelay.Count > 12) previewDelay.Dequeue();
                            }
                            if (!check && (++count <= 6 || count % 10 == 0))
                                File.AppendAllText(log, line + Environment.NewLine);
                        }
                        continue;
                    }
                    else if (line.StartsWith("CaptureFG size=", StringComparison.Ordinal))
                    {
                        var size = line["CaptureFG size=".Length..].Split('x');
                        if (size.Length == 2 && int.TryParse(size[0], out int w) && int.TryParse(size[1], out int h))
                            motionSize = new Size(w, h);
                    }
                }
                if (line.Contains("aq=") && line.Contains("vq="))
                {
                    long now = Environment.TickCount64;
                    if (playback && !directPreview && now >= nextDelaySample &&
                        liveCapture?.LatestTimestamp is long stamp && stamp >= 0)
                    {
                        var stats = PlaybackStats.Match(line);
                        if (stats.Success && double.TryParse(stats.Groups["clock"].ValueSpan, CultureInfo.InvariantCulture, out double clock) &&
                            double.TryParse(stats.Groups["diff"].ValueSpan, CultureInfo.InvariantCulture, out double difference))
                        {
                            nextDelaySample = now + (videoWindow == IntPtr.Zero ? 100 : 500);
                            latestPreviewDelay = Math.Round(stamp - (clock - (audioMaster && audioReady ? difference : 0)) * 1000);
                            if (videoWindow != IntPtr.Zero)
                            {
                                previewDelay.Enqueue(latestPreviewDelay.Value);
                                if (previewDelay.Count > 12) previewDelay.Dequeue();
                                if (long.TryParse(stats.Groups["drops"].ValueSpan, out long missed))
                                {
                                    previewDrops.Enqueue((now, missed));
                                    if (previewDrops.Count > 12) previewDrops.Dequeue();
                                }
                                behindSince = latestPreviewDelay > 750 ? (behindSince == 0 ? now : behindSince) : 0;
                            }
                        }
                    }
                    continue;
                }
                if (!playback && (generation != -1 || !enhanced) && line.StartsWith("frame=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(6), out long frames))
                {
                    if (frames > encodedFrames) outputFrameAt = Environment.TickCount64;
                    encodedFrames = frames;
                    encoderSamples.Enqueue((elapsed.Elapsed.TotalSeconds, frames));
                    if (encoderSamples.Count > 5) encoderSamples.Dequeue();
                    if (check && videoWindow != IntPtr.Zero && elapsed.Elapsed.TotalSeconds > 5 && RecentOutputFps() is double rate)
                        checkRates.Add(rate);
                }
                if (!playback && IsProgress(line)) continue;
                if (!playback && line.StartsWith("[CaptureNR] Async result applied", StringComparison.Ordinal))
                    neuralReady = true;
                if (line.Contains("frame dropped!")) dropped++;
                if (playback && !directPreview && line.Contains("Video: ") && line.Contains(captureLow && !superActive ? "1920x1080" : "3840x2160"))
                {
                    videoReady = true;
                    motionReady = motionActive && (line.Contains($"{60 * multiplier} fps") || line.Contains($"{60 * multiplier} tbr"));
                }
                if (playback && previewAudio && (!directPreview || ReferenceEquals(p, motionAudio)) && line.Contains("Audio: ")) audioReady = true;
                if (!line.Contains("Could not find codec parameters", StringComparison.OrdinalIgnoreCase) &&
                    (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("failed", StringComparison.OrdinalIgnoreCase) || line.Contains("Could not", StringComparison.OrdinalIgnoreCase))) lastError = line.Trim();
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
        if (directPreview)
            return directReportAt == 0 ? null : Environment.TickCount64 - directReportAt > 2500 ? 0 : directFps;
        if (encoderSamples.Count < 2) return null;
        var first = encoderSamples.Peek();
        var last = encoderSamples.Last();
        return elapsed.Elapsed.TotalSeconds - last.Time > 2.0 ? 0 :
            Math.Round((last.Frames - first.Frames) / Math.Max(0.01, last.Time - first.Time), 1);
    }

    async Task RefreshSensors()
    {
        if (sampling || !showOverlay.Checked || videoWindow == IntPtr.Zero || selectedMonitors == MonitorItem.None ||
            Environment.TickCount64 < nextSampleAt) return;
        sampling = true;
        nextSampleAt = Environment.TickCount64 + 1000;
        MonitorItem requested = selectedMonitors;
        try
        {
            var sample = await Task.Run(() => telemetry.Read(requested));
            if (!closing && showOverlay.Checked && selectedMonitors == requested)
            {
                sensors = sample;
                RefreshFrameBadge();
            }
        }
        finally { sampling = false; }
    }

    void RefreshFrameBadge()
    {
        if (!OverlayVisible || DisplayWindow == IntPtr.Zero)
        {
            frameBadge.Visible = false;
            return;
        }
        var lines = new List<string>(7);
        if (showFps.Checked) lines.Add($"{((retained?.Direct ?? directPreview) ? "提交" : "处理")} {Number(retained?.OutputFps ?? RecentOutputFps(), "0")} FPS");
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
        var monitored = checkSensors ? sensors : null;
        var originalMonitors = selectedMonitors;
        bool originalFps = showFps.Checked;
        for (int corner = 0; corner < 4; corner++)
        {
            fpsPosition.SelectedIndex = corner;
            placements.Add(frameBadge.Bounds);
        }
        showOverlay.Checked = false;
        bool hiddenBySwitch = !frameBadge.Visible;
        showOverlay.Checked = true;
        bool restoredBySwitch = frameBadge.Visible && selectedMonitors == originalMonitors && showFps.Checked == originalFps;
        bool independent = false;
        bool singleItem = false;
        if (checkSensors)
        {
            showFps.PerformClick();
            independent = frameBadge.Visible && !frameBadge.Text.Contains("FPS");
            ClearMonitorItems();
            monitorItems[MonitorItem.GpuTemp].PerformClick();
            singleItem = frameBadge.Visible && frameBadge.Text.StartsWith("GPU 温度", StringComparison.Ordinal) && !frameBadge.Text.Contains('\n');
        }
        ClearMonitorItems();
        return new { AboveVideo = above, Text = text, DistinctCorners = placements.Distinct().Count(),
            Independent = independent, Sensors = monitored, HiddenWhenOff = !frameBadge.Visible,
            HiddenBySwitch = hiddenBySwitch, RestoredBySwitch = restoredBySwitch, SingleItem = singleItem };
    }

    void CheckPlayback()
    {
        if (checkDirectControls && !busy && motionActive && videoWindow != IntPtr.Zero && captureProcess is not null)
        {
            double seconds = testTime.Elapsed.TotalSeconds;
            if (controlStage == 0 && seconds >= 5 && !motionSize.IsEmpty)
            {
                initialCapturePid = captureProcess.Id;
                initialMotionSize = motionSize;
                checkWindowedFps = directFps;
                checkMotionAudio = soundReady;
                ToggleFullscreen();
                controlStage = 1;
            }
            else if (controlStage == 1 && seconds >= 10)
            {
                fullscreenResized = motionSize.Width > initialMotionSize.Width && motionSize.Height > initialMotionSize.Height;
                checkFullscreenFps = directFps;
                ToggleFullscreen();
                controlStage = 2;
            }
            else if (controlStage == 2 && seconds >= 14)
            {
                windowResized = motionSize == initialMotionSize;
                controlStage = 3;
                ReturnToOriginal();
                return;
            }
        }
        if (check && testTime.Elapsed.TotalSeconds >= (checkDirectControls ? 22 : synthetic ? 16 : 9) &&
            (videoWindow != IntPtr.Zero || testTime.Elapsed.TotalSeconds >= (checkDirectControls ? 24 : 20)))
        {
            double recentFps = RecentOutputFps() ?? 0;
            var directControls = checkDirectControls ? new { FullscreenResized = fullscreenResized, WindowRestored = windowResized,
                FullscreenSubmittedFps = checkFullscreenFps, WindowedSubmittedFps = checkWindowedFps, MotionAudio = checkMotionAudio,
                RawAudio = soundReady, ReturnedToRaw = controlStage == 3 && !enhanced && videoWindow != IntPtr.Zero,
                SameCapture = initialCapturePid > 0 && captureProcess?.Id == initialCapturePid } : null;
            File.WriteAllText(Path.Combine(appData, neuralActive || superActive ? "enhancement-check.json" : checkMultiplier > 0 ? "motion-check.json" : "check.json"),
                JsonSerializer.Serialize(new { Mode = selectedMode, Multiplier = multiplier, MotionOutputFps = motionReady ? 60 * multiplier : 0, DirectMotion = motionActive, DirectSubmittedFps = directFps, DirectGeneratedFps = directGeneratedFps, DirectControls = directControls, NeuralRequested = neuralActive, NeuralOutput = neuralReady, SuperResolution = superActive, EncodedFrames = encodedFrames, RecentEncodedFps = recentFps, ProcessingFpsSamples = checkRates, FpsOverlay = CheckFpsBadge(), Seconds = elapsed.Elapsed.TotalSeconds, Synthetic = synthetic, Embedded = videoWindow != IntPtr.Zero, VideoStream = videoReady, AudioStream = audioReady, AudioSession = soundReady, CaptureBufferDropWarnings = dropped, SkippedLiveClusters = liveCapture?.Dropped, SkippedEffectClusters = effectRelay?.Dropped, PreviewDelayMs = previewDelay.ToArray(), PreviewDroppedFrames = previewDrops.Select(sample => sample.Count).ToArray(), Error = lastError }, new JsonSerializerOptions { WriteIndented = true }));
            Close(); return;
        }
        if (busy || process == null) return;
        if (retained is { } previous && (previous.Player.HasExited || previous.Filter?.HasExited == true))
        {
            StopRetained();
            placeholder.Visible = videoWindow == IntPtr.Zero;
        }
        if (process.HasExited || filterProcess?.HasExited == true || motionAudio?.HasExited == true || captureProcess?.HasExited == true || captureReader?.IsCompleted == true)
        {
            var error = lastError ?? (process.HasExited ? $"FFplay exited ({process.ExitCode})." :
                filterProcess?.HasExited == true ? $"Enhancement exited ({filterProcess.ExitCode})." :
                motionAudio?.HasExited == true ? $"Preview audio exited ({motionAudio.ExitCode})." : "Capture process exited.");
            if (bridging) { _ = StartCapture(bypassBridge: true); return; }
            if (enhanced && !check) { RecoverEnhancement(error); return; }
            if (audioListed && !retryAudio && videoWindow == IntPtr.Zero) { _ = StartCapture(reopen: true, defaultAudio: true); return; }
            Fail("采集连接中断。请确认采集卡已插好、OBS 已关闭，再点重连。", error); return;
        }
        if (videoWindow == IntPtr.Zero)
        {
            long warmupNow = Environment.TickCount64;
            if (retained is not null && warmupNow >= nextWarmupReport)
            {
                nextWarmupReport = warmupNow + 2000;
                try { File.AppendAllText(PlaybackLog, $"Waiting for preview: DelayMs={latestPreviewDelay:0} OutputFps={RecentOutputFps():0}{Environment.NewLine}"); }
                catch (IOException) { }
            }
            if (retained is not null && audioListed && audioReady && !soundReady && SoundProcess is { } startupSound)
                soundReady = Native.Volume(startupSound.Id, volume.Value / 100f, true);
            process.Refresh();
            var hwnd = directPreview ? motionWindow : process.MainWindowHandle;
            // FFplay's first picture can still belong to startup. Keep the old
            // preview visible until the candidate has caught up to live input.
            bool caughtUp = directPreview ? videoReady : latestPreviewDelay is >= -100 and <= 500;
            if (hwnd != IntPtr.Zero && (retained is null || caughtUp))
            {
                if (directPreview || Native.Embed(hwnd, preview.Handle))
                {
                    videoWindow = hwnd; placeholder.Visible = false;
                    visibleSince = Environment.TickCount64;
                    FitVideo();
                    if (frameBadge.Visible) Native.RaiseChild(frameBadge.Handle);
                    bool switched = retained is not null;
                    StopRetained();
                    pendingVolume = true;
                    try { File.AppendAllText(PlaybackLog, $"{DateTimeOffset.Now:O} Preview ready CapturePID={captureProcess!.Id}  Continued={switched}  DelayMs={latestPreviewDelay:0}{Environment.NewLine}"); }
                    catch (IOException) { }
                    if (bridging)
                    {
                        if (audioListed && audioReady && SoundProcess is { } bridgeSound) Native.Volume(bridgeSound.Id, volume.Value / 100f, muted);
                        _ = StartCapture(bypassBridge: true);
                        return;
                    }
                }
                else if (enhanced && !check) { RecoverEnhancement("Could not embed SDL window."); return; }
                else { Fail("无法显示预览窗口，点重连重试。", "Could not embed SDL window."); return; }
            }
            else if (elapsed.Elapsed.TotalSeconds > (retained is not null ? (neuralActive ? 10 : 6) : neuralActive ? 20 : 12))
            {
                if (bridging) { _ = StartCapture(bypassBridge: true); return; }
                if (enhanced && !check) { RecoverEnhancement(lastError ?? "Enhanced preview did not catch up."); return; }
                if (audioListed && !retryAudio) { _ = StartCapture(reopen: true, defaultAudio: true); return; }
                Fail("还没收到画面。请打开底座上的 Switch 2，再点重连。", lastError); return;
            }
        }
        if (retained is null && audioListed && audioReady && pendingVolume && SoundProcess is { } currentSound)
        {
            soundReady = Native.Volume(currentSound.Id, volume.Value / 100f, muted);
            pendingVolume = !soundReady;
        }
        long now = Environment.TickCount64;
        if (!check && videoWindow != IntPtr.Zero && now >= nextPlaybackReport)
        {
            nextPlaybackReport = now + 5000;
            try
            {
                if (new FileInfo(PlaybackLog).Length < 256 * 1024)
                    File.AppendAllText(PlaybackLog, FormattableString.Invariant($"{DateTimeOffset.Now:O} Playback: CapturePID={captureProcess!.Id} ProcessingFps={RecentOutputFps():0.0} DelayMs={latestPreviewDelay:0} RendererDrops={(previewDrops.Count == 0 ? null : (long?)previewDrops.Last().Count)} CaptureDrops={dropped} SkippedClusters={liveCapture?.Dropped} EffectDrops={effectRelay?.Dropped}{Environment.NewLine}"));
            }
            catch (IOException) { }
        }
        if (enhanced && !check && videoWindow != IntPtr.Zero && WindowState != FormWindowState.Minimized &&
            (now - outputFrameAt > 3500 || behindSince != 0 && now - behindSince > 1500))
        {
            bool stalled = now - outputFrameAt > 3500;
            RecoverEnhancement(stalled ? "Enhancement stopped producing frames." : "Enhanced preview stayed more than 750 ms behind live capture.", returnToOriginal: true);
            return;
        }
        if (enhanced && !check && videoWindow != IntPtr.Zero && WindowState != FormWindowState.Minimized && now - visibleSince > 2500)
        {
            int target = 60 * multiplier;
            string device = Screen.FromControl(this).DeviceName;
            if (device != displayDevice) { displayDevice = device; displayRefresh = Native.DisplayRefresh(device); }
            int expectedDisplayFps = displayRefresh > 0 ? Math.Min(target, displayRefresh) : Math.Min(target, 60);
            bool rendererDrops = previewDrops.Count >= 10 &&
                (previewDrops.Last().Count - previewDrops.Peek().Count) * 1000d / Math.Max(1, previewDrops.Last().At - previewDrops.Peek().At) >
                target - expectedDisplayFps + expectedDisplayFps * 0.20;
            bool tooSlow = rendererDrops || RecentOutputFps() is double rate && rate < target * 0.84;
            slowSince = tooSlow ? (slowSince == 0 ? now : slowSince) : 0;
            if (slowSince != 0 && now - slowSince >= 2500)
            {
                RecoverEnhancement(rendererDrops ? "Enhanced playback kept dropping frames." : $"Enhancement stayed below 84% of its {target} FPS target.", reduceMotion: true, returnToOriginal: !motionActive);
                return;
            }
        }
        RefreshFrameBadge();
        _ = RefreshSensors();
        if (videoWindow != IntPtr.Zero)
            status.Text = effectNotice ?? $"{selectedMode}  ·  {(muted ? "已静音" : audioReady ? "采集卡音频" : audioListed ? "等待采集卡音频" : "未找到采集卡音频")}"
                + (neuralActive ? (neuralReady ? "  ·  DLSS 5 FP8" : "  ·  DLSS 5 等待有效画面") : "")
                + (motionActive ? $"  ·  补帧 {multiplier}×（目标 {60 * multiplier} 帧）" : "")
                + (enhanced && dropped >= 3 ? "  ·  采集掉帧" : "")
                + (enhanced ? "  ·  F8 原画" : "  ·  F11 全屏 / Esc 返回");
    }

    string PlaybackLog => Path.Combine(appData, synthetic ? "synthetic-playback.log" : "playback.log");

    void RecoverEnhancement(string detail, bool reduceMotion = false, bool returnToOriginal = false)
    {
        try { File.WriteAllText(Path.Combine(appData, "enhancement-error.log"), detail + Environment.NewLine); } catch (IOException) { }
        busy = true;
        if (returnToOriginal || !motionActive)
        {
            SetOriginalOptions();
            effectNotice = returnToOriginal ? "增强预览未跟上实时画面，已切回原画。" : "画面增强启动失败或中断，已切回原画。";
        }
        else
        {
            int next = reduceMotion ? multiplier - 1 : 1;
            effect.SelectedIndex = next - 1;
            effectNotice = next > 1 ? $"{multiplier}× 未能稳定，已切到 {next}×。" : "补帧未能跟上实时画面，已关闭补帧。";
        }
        busy = false;
        Save();
        _ = StartCapture();
    }

    void SetOriginalOptions()
    {
        effect.SelectedIndex = 0;
        neural.Checked = false;
        if (mode.SelectedIndex == 2) mode.SelectedIndex = 1;
    }

    void ReturnToOriginal()
    {
        if (busy || closing) return;
        busy = true;
        SetOriginalOptions();
        busy = false;
        effectNotice = null;
        Save();
        _ = StartCapture();
    }

    void Fail(string message, string? detail)
    {
        StopCapture(); lastError = detail ?? message;
        placeholder.Visible = true; placeholder.Text = message;
        status.Text = "未连接";
        try { File.AppendAllText(PlaybackLog, lastError + Environment.NewLine); } catch (IOException) { }
    }

    void StopPreview()
    {
        version++; videoWindow = IntPtr.Zero;
        if (retained is null) frameBadge.Visible = false;
        feedCancellation?.Cancel(); feedCancellation?.Dispose(); feedCancellation = null;
        audioFeed?.Cancel(); audioFeed?.Dispose(); audioFeed = null;
        Stop(motionAudio); motionAudio = null;
        Stop(filterProcess); filterProcess = null;
        Stop(process); process = null;
    }

    void StopRetained()
    {
        if (retained is not { } previous) return;
        retained = null;
        Native.HideSurface(previous.Window);
        previous.Feed?.Cancel(); previous.Feed?.Dispose();
        previous.AudioFeed?.Cancel(); previous.AudioFeed?.Dispose();
        Stop(previous.Audio);
        Stop(previous.Filter);
        Stop(previous.Player);
    }

    void StopDevice()
    {
        var device = captureProcess;
        captureProcess = null;
        Stop(device, wait: true);
        captureReader = null; liveCapture = null;
    }

    void StopCapture() { StopRetained(); StopPreview(); StopDevice(); }

    static void Stop(Process? p, bool wait = false)
    {
        if (p == null) return;
        try { if (!p.HasExited) { p.Kill(); if (wait) p.WaitForExit(1200); } } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        p.Dispose();
    }

    void FitVideo()
    {
        if (changingFullscreen || DisplayWindow == IntPtr.Zero) return;
        Native.ResizeSurface(DisplayWindow, new Rectangle(0, 0, Math.Max(1, preview.ClientSize.Width),
            Math.Max(1, preview.ClientSize.Height)), show: true);
        RefreshFrameBadge();
    }

    void ToggleFullscreen()
    {
        changingFullscreen = true;
        if (DisplayWindow != IntPtr.Zero) Native.HideSurface(DisplayWindow);
        frameBadge.Hide();
        SuspendLayout();
        if (!full)
        {
            restore = new RectState(WindowState, WindowState == FormWindowState.Normal ? Bounds : RestoreBounds);
            var screen = Screen.FromControl(this).Bounds;
            full = true;
            fullscreen.Text = ExitFullscreen;
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
            fullscreen.Text = EnterFullscreen;
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
            if (DisplayWindow != IntPtr.Zero) Native.HideSurface(DisplayWindow);
            topBar.Hide();
            Native.RepaintSurface(Handle);
            FitVideo();
            toolbarHideAt = 0;
        }
    }

    bool IMessageFilter.PreFilterMessage(ref Message msg)
    {
        if (msg.Msg != 0x0100 || !ContainsFocus) return false; // WM_KEYDOWN
        var key = (Keys)(int)msg.WParam;
        if (key == Keys.F8 && (effect.SelectedIndex > 0 || neural.Checked || mode.SelectedIndex == 2))
        {
            if ((msg.LParam.ToInt64() & (1L << 30)) == 0) ReturnToOriginal();
            return true;
        }
        if (key == Keys.F11 || key == Keys.Escape && full)
        {
            if ((msg.LParam.ToInt64() & (1L << 30)) == 0) ToggleFullscreen();
            return true;
        }
        return false;
    }

    void Save()
    {
        if (check || synthetic) return;
        try { File.WriteAllText(Path.Combine(appData, "settings.json"), JsonSerializer.Serialize(new Preferences(volume.Value, muted, mode.SelectedIndex != 0, mode.SelectedIndex == 2, showFps.Checked, fpsPosition.SelectedIndex, selectedMonitors, frameBadge.OpacityPercent, showOverlay.Checked))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    sealed record Preferences(int Volume, bool Muted, bool Low, bool SuperRes = false, bool ShowFps = false, int FpsCorner = 1,
        MonitorItem Monitors = MonitorItem.None, int BadgeOpacity = FrameBadge.DefaultOpacity, bool OverlayEnabled = true);
    sealed record RectState(FormWindowState State, Rectangle Bounds);
    sealed record RetainedPreview(Process Player, Process? Filter, CancellationTokenSource? Feed,
        Process? Audio, CancellationTokenSource? AudioFeed, IntPtr Window, double? OutputFps, bool Direct);
}
