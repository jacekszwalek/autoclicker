using System.Drawing;

namespace MouseRecorder;

/// <summary>Współrzędne i pomocnicze funkcje dla całego wirtualnego pulpitu (wszystkie monitory).</summary>
public static class VirtualDesktop
{
    public static Rectangle Bounds
    {
        get
        {
            int left = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int top = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int width = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int height = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            return new Rectangle(left, top, width, height);
        }
    }

    /// <summary>Zamienia współrzędne piksela na znormalizowane 0..65535 wymagane przez SendInput (MOUSEEVENTF_ABSOLUTE|VIRTUALDESK).</summary>
    public static (int nx, int ny) ToNormalized(int x, int y)
    {
        var b = Bounds;
        int nx = (int)Math.Round((x - b.Left) * 65535.0 / Math.Max(1, b.Width - 1));
        int ny = (int)Math.Round((y - b.Top) * 65535.0 / Math.Max(1, b.Height - 1));
        return (nx, ny);
    }
}
