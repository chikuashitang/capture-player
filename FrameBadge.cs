namespace CapturePlayer;

internal sealed class FrameBadge : Control
{
    internal FrameBadge()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
        SetStyle(ControlStyles.Selectable, false);
        AccessibleName = "帧率和硬件监控";
        BackColor = Color.FromArgb(16, 21, 29);
        ForeColor = Color.White;
        Visible = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        var area = Rectangle.Inflate(ClientRectangle, -9 * DeviceDpi / 96, -4 * DeviceDpi / 96);
        TextRenderer.DrawText(e.Graphics, Text, Font, area, ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084) { m.Result = new IntPtr(-1); return; } // WM_NCHITTEST: pass through.
        base.WndProc(ref m);
    }

    internal static Rectangle PositionIn(Size preview, int corner, int dpi, Size text)
    {
        int videoWidth = Math.Min(preview.Width, preview.Height * 16 / 9);
        int videoHeight = Math.Min(preview.Height, preview.Width * 9 / 16);
        int left = (preview.Width - videoWidth) / 2, top = (preview.Height - videoHeight) / 2;
        int pad = Math.Min(12 * dpi / 96, Math.Min(videoWidth, videoHeight) / 8);
        int width = Math.Min(text.Width + 20 * dpi / 96, Math.Max(1, videoWidth - 2 * pad));
        int height = Math.Min(text.Height + 14 * dpi / 96, Math.Max(1, videoHeight - 2 * pad));
        int x = (corner & 1) != 0 ? left + videoWidth - width - pad : left + pad;
        int y = (corner & 2) != 0 ? top + videoHeight - height - pad : top + pad;
        return new Rectangle(Math.Max(0, x), Math.Max(0, y), width, height);
    }
}
