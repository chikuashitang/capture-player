# CapturePlayer · 采集卡播放器

给 Windows 用的轻量 HDMI 采集卡播放器。连接阿卡西斯 VC-002Pro 后自动预览画面和声音，支持 4K60 MJPEG。适合把 Switch 2 等 HDMI 设备接到电脑显示器上玩。应用只做预览，不录制或直播。

| 功能 | 说明 |
| --- | --- |
| HDMI 预览 | DirectShow 采集，使用 Windows 默认音频输出；支持调音量、静音和重连。 |
| 全屏 | F11 进入全屏，Esc 退出。鼠标移到最上沿显示工具栏，移开后自动隐藏。 |
| 监控 | 可选帧数、GPU 占用、温度、显存、功耗、CPU 占用和温度。支持四角位置；「监控设置」可调整 10%–100% 不透明度，拖动时实时预览，设置自动保存。CPU 温度通过正在运行的 MSI Afterburner 读取。 |
| 画面增强 | 2×–6× 动作补帧，FSRCNNX 1080p→4K 超分，可选实验性 DLSS 5 FP8 预览。增强需要兼容的 NVIDIA GPU、驱动和 FFmpeg 构建。 |

### 使用编译版

从仓库的 **Releases** 下载 Windows x64 压缩包，解压整个目录。首次使用如未装 FFmpeg，可运行 `Install-FFmpeg.cmd`（使用 Windows 的 winget 安装）。也可以把已有的 `ffmpeg.exe` 和 `ffplay.exe` 放在 `CapturePlayer.exe` 旁边。完成后双击 `CapturePlayer.exe`，无需另装 .NET。

### 构建和运行

1. 在 Windows 11 x64 上安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)、Visual Studio 的 C++ 工具和 Windows SDK。
2. 从 [FFmpeg 下载页](https://ffmpeg.org/download.html#build-windows) 获取包含 `ffmpeg.exe` 和 `ffplay.exe` 的 Windows 构建。将它们放在同一个已加入 `PATH` 的目录里，或并排放在构建后的播放器目录中。需要增强功能时，选择同时启用了 OpenCL、Vulkan、libplacebo、Frei0r 和 NVENC 的构建（开发时使用 FFmpeg 8.1.1）。
3. 在 **x64 Native Tools Command Prompt for Visual Studio** 打开源码目录，编译 D3D11 预览组件，再构建播放器：

   ```powershell
   powershell -NoProfile -File .\native\build-fg.ps1
   dotnet publish CapturePlayer.csproj -c Release -o out
   .\out\CapturePlayer.exe
   ```

4. 接入采集卡并关闭正在占用它的其他采集软件。原生 4K60 输入选「4K · 60 帧」，1080p 输入想放大选「1080p → 4K 超分」。启动时请求 3× 补帧，刷新率不足时会自动降低倍率；按 F8 可切回原画。60 帧输入配 3× 补帧时，提交目标为 180 FPS；浮层的「提交 FPS」并非屏幕实测帧率。

### 可选的实验性 DLSS 5

基本预览、FSRCNNX 超分和动作补帧都可以不安装 DLSS 5。HDMI 视频没有游戏引擎的原生运动、深度和材质数据，本项目的 SDR 预览与游戏内集成的 DLSS 5 效果不同。DLSS 5 可与 D3D11 直出补帧同时开启；FSRCNNX 超分与补帧分开使用。

如已合法获得兼容的 NVIDIA NGX SDK 头文件、x64 静态库和原版模型，可在 **x64 Native Tools Command Prompt for Visual Studio** 中构建适配器：

```powershell
powershell -NoProfile -File .\native\build.ps1 -NgxIncludeDir "C:\path\to\ngx\include" -NgxLibDir "C:\path\to\ngx\lib" -Out .\out\resources\frei0r
```

把自行取得的 NVIDIA 签名 `nvngx_dlssnr.dll` 放到 `out/resources/models/`。目前适配器验证的是 310.9.16.0（CC Small FP8），SHA-256 为 `2BC19BC09A7B89DD44F73E2AC0108FCC586E2154BDD0830F242F4F040E28789F`。两个组件齐全时，下次启动会自动开启 DLSS 5；缺少组件不影响补帧。这个仓库不提供 NVIDIA SDK、DLL 或模型下载。

### 许可证

CapturePlayer 源码以 [GPL-3.0](LICENSE) 发布。FSRCNNX 及独立依赖遵循各自的许可证，见 [第三方声明](THIRD-PARTY-NOTICES.md)。
