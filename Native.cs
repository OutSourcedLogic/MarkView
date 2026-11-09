using System.Runtime.InteropServices;

namespace MarkView;

/// <summary>Win32 pieces for the custom title bar, plain-text editor and theming.</summary>
static class Native
{
    public const int WM_NCCALCSIZE = 0x0083, WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0,
        WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_NCLBUTTONDBLCLK = 0x00A3, WM_NCMOUSELEAVE = 0x02A2;

    public const int HTTRANSPARENT = -1, HTCLIENT = 1, HTCAPTION = 2, HTMINBUTTON = 8, HTMAXBUTTON = 9,
        HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14, HTCLOSE = 20;

    public const int SM_CYSIZEFRAME = 33, SM_CXPADDEDBORDER = 92;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
    public const uint TME_LEAVE = 0x2, TME_NONCLIENT = 0x10;
    public const int EM_SETTEXTMODE = 0x0400 + 89;
    public const int TM_PLAINTEXT = 1, TM_MULTILEVELUNDO = 8, TM_MULTICODEPAGE = 32;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] public static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);

    /// <summary>Screen point packed in a mouse message's lParam (signed, for monitors left of/above the primary).</summary>
    public static Point PointFromLParam(IntPtr lParam)
    {
        int v = unchecked((int)(long)lParam);
        return new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
    }
}
