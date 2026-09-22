using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MouseRecorder;

/// <summary>
/// Pełnoekranowa nakładka wizualizująca odtwarzanie: cała ścieżka, punkty kliknięć i ruchomy marker.
/// Warstwowe okno (per-pixel alpha) click-through, niewidoczne na pasku zadań, nie przejmuje focusa.
/// </summary>
public sealed class OverlayForm : Form
{
    private readonly PlaybackProgress _progress;
    private readonly Rectangle _bounds;
    private readonly System.Windows.Forms.Timer _timer;

    private Bitmap? _staticLayer;
    private Bitmap? _frameBuffer;
    private IntPtr _screenDc;
    private IntPtr _memDc;
    private IntPtr _hBitmap;
    private IntPtr _oldBitmap;

    public OverlayForm(Recording recording, PlaybackProgress progress)
    {
        _progress = progress;
        _bounds = VirtualDesktop.Bounds;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = _bounds;
        TopMost = true;
        ShowInTaskbar = false;

        BuildStaticLayer(recording);

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += (_, _) => RenderFrame();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT
                        | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        InitLayeredBuffers();
        RenderFrame();
        _timer.Start();
    }

    private void InitLayeredBuffers()
    {
        int w = Math.Max(1, _bounds.Width);
        int h = Math.Max(1, _bounds.Height);

        _screenDc = Native.GetDC(IntPtr.Zero);
        _memDc = Native.CreateCompatibleDC(_screenDc);

        var header = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
            biCompression = Native.BI_RGB
        };

        _hBitmap = Native.CreateDIBSection(_screenDc, ref header, Native.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        _oldBitmap = Native.SelectObject(_memDc, _hBitmap);

        _frameBuffer = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits);
    }

    private void BuildStaticLayer(Recording recording)
    {
        int w = Math.Max(1, _bounds.Width);
        int h = Math.Max(1, _bounds.Height);
        _staticLayer = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(_staticLayer);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.HighQuality;

        Point ToLocal(int x, int y) => new(x - _bounds.Left, y - _bounds.Top);

        using var pathPen = new Pen(Color.FromArgb(140, 30, 144, 255), 2f);
        Point? prev = null;
        foreach (var ev in recording.Events)
        {
            var p = ToLocal(ev.X, ev.Y);
            if (prev.HasValue)
            {
                g.DrawLine(pathPen, prev.Value, p);
            }
            prev = p;
        }

        using var leftBrush = new SolidBrush(Color.FromArgb(200, 0, 200, 0));
        using var rightBrush = new SolidBrush(Color.FromArgb(200, 220, 20, 60));
        using var middleBrush = new SolidBrush(Color.FromArgb(200, 30, 144, 255));

        const int r = 5;
        foreach (var ev in recording.Events)
        {
            Brush? brush = ev.Kind switch
            {
                MouseEventKind.LeftDown => leftBrush,
                MouseEventKind.RightDown => rightBrush,
                MouseEventKind.MiddleDown => middleBrush,
                _ => null
            };
            if (brush == null) continue;

            var p = ToLocal(ev.X, ev.Y);
            g.FillEllipse(brush, p.X - r, p.Y - r, r * 2, r * 2);
        }
    }

    private void RenderFrame()
    {
        if (_frameBuffer == null || _staticLayer == null) return;

        using (var g = Graphics.FromImage(_frameBuffer))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            g.DrawImageUnscaled(_staticLayer, 0, 0);

            var pos = _progress.GetPosition();
            int lx = pos.X - _bounds.Left;
            int ly = pos.Y - _bounds.Top;
            const int r = 7;

            using var markerBrush = new SolidBrush(Color.FromArgb(230, 255, 215, 0));
            using var markerPen = new Pen(Color.FromArgb(230, 0, 0, 0), 1.5f);
            g.FillEllipse(markerBrush, lx - r, ly - r, r * 2, r * 2);
            g.DrawEllipse(markerPen, lx - r, ly - r, r * 2, r * 2);
        }

        var topLeft = new Native.POINT { X = _bounds.Left, Y = _bounds.Top };
        var size = new Native.SIZE { cx = _bounds.Width, cy = _bounds.Height };
        var srcPoint = new Native.POINT { X = 0, Y = 0 };
        var blend = new Native.BLENDFUNCTION
        {
            BlendOp = Native.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = Native.AC_SRC_ALPHA
        };

        Native.UpdateLayeredWindow(Handle, _screenDc, ref topLeft, ref size, _memDc, ref srcPoint, 0, ref blend, Native.ULW_ALPHA);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();

            _frameBuffer?.Dispose();
            _staticLayer?.Dispose();

            if (_memDc != IntPtr.Zero)
            {
                Native.SelectObject(_memDc, _oldBitmap);
                Native.DeleteDC(_memDc);
                _memDc = IntPtr.Zero;
            }
            if (_hBitmap != IntPtr.Zero)
            {
                Native.DeleteObject(_hBitmap);
                _hBitmap = IntPtr.Zero;
            }
            if (_screenDc != IntPtr.Zero)
            {
                Native.ReleaseDC(IntPtr.Zero, _screenDc);
                _screenDc = IntPtr.Zero;
            }
        }
        base.Dispose(disposing);
    }
}
