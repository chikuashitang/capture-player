@echo off
where winget >nul 2>nul
if errorlevel 1 (
    echo Windows Package Manager ^(winget^) is not available.
    echo Get FFmpeg from https://ffmpeg.org/download.html#build-windows
    echo Place ffmpeg.exe and ffplay.exe next to CapturePlayer.exe.
    pause
    exit /b 1
)
winget install --id Gyan.FFmpeg --exact --source winget
if errorlevel 1 (
    echo Installation did not complete. You can also place ffmpeg.exe and ffplay.exe next to CapturePlayer.exe.
    pause
    exit /b 1
)
echo Close and reopen CapturePlayer. If FFmpeg is still not found, sign out and back in to refresh PATH.
pause
