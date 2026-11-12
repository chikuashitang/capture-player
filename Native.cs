using System.Runtime.InteropServices;

namespace CapturePlayer;

internal static class Native
{
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool EnableWindow(IntPtr hwnd, bool enable);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DisplayMode settings);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd, IntPtr updateRect, IntPtr updateRegion, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr GetTopWindow(IntPtr parent);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    internal static void SetLayeredSurfaceOpacity(IntPtr hwnd, byte alpha) =>
        SetLayeredWindowAttributes(hwnd, 0, alpha, 0x0002); // LWA_ALPHA

    [StructLayout(LayoutKind.Explicit, Size = 220)]
    struct DisplayMode
    {
        [FieldOffset(68)] internal ushort Size;
        [FieldOffset(184)] internal uint Frequency;
    }

    internal static int DisplayRefresh(string device)
    {
        var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>() };
        return EnumDisplaySettings(device, -1, ref mode) && mode.Frequency is >= 24 and <= 600 ? (int)mode.Frequency : 0;
    }

    internal static void RaiseChild(IntPtr child) =>
        SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);

    internal static void ResizeSurface(IntPtr hwnd, Rectangle bounds, bool show = false) =>
        // Do not copy pixels from the old location when SDL or the host changes size.
        SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            0x0004 | 0x0010 | 0x0100 | (show ? 0x0040u : 0));

    internal static void HideSurface(IntPtr hwnd) =>
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0080);

    internal static void RepaintSurface(IntPtr hwnd) =>
        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100);

    internal static bool Embed(IntPtr child, IntPtr host)
    {
        const int WS_CHILD = 0x40000000, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
        const int WS_POPUP = unchecked((int)0x80000000);
        EnableWindow(child, false); // Clear keyboard focus so SDL cannot act on Escape.
        int style = GetWindowLong(child, -16);
        SetWindowLong(child, -16, (style & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME)) | WS_CHILD);
        SetParent(child, host);
        if (GetParent(child) != host) return false;
        // Recalculate the window frame without activating the child.
        SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0020 | 0x0040);
        return true;
    }

    internal static bool Volume(int pid, float level, bool muted)
    {
        object? enumerator = null;
        try
        {
            enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!);
            var endpoints = (IMMDeviceEnumerator)enumerator!;
            var managerId = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
            for (int role = 0; role <= 2; role++)
            {
                IMMDevice? device = null;
                object? manager = null;
                IAudioSessionEnumerator? sessions = null;
                try
                {
                    if (endpoints.GetDefaultAudioEndpoint(0, role, out device) != 0) continue;
                    if (device.Activate(ref managerId, 23, IntPtr.Zero, out manager) != 0) continue;
                    if (((IAudioSessionManager2)manager).GetSessionEnumerator(out sessions) != 0) continue;
                    if (sessions.GetCount(out int count) != 0) continue;
                    for (int i = 0; i < count; i++)
                    {
                        if (sessions.GetSession(i, out var session) != 0) continue;
                        try
                        {
                            if (session.GetProcessId(out uint actual) != 0 || actual != pid || session is not ISimpleAudioVolume volume) continue;
                            var context = Guid.Empty;
                            return volume.SetMasterVolume(level, ref context) == 0 && volume.SetMute(muted, ref context) == 0;
                        }
                        finally { Release(session); }
                    }
                }
                finally { Release(sessions); Release(manager); Release(device); }
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { }
        finally { Release(enumerator); }
        return false;
    }

    static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint flags, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsctx, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid id, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid id, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, ref Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr events);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    }
}
