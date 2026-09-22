using System.Drawing;

namespace MouseRecorder;

/// <summary>Thread-safe current cursor position during playback (written by the Player thread, read by the UI overlay).</summary>
public sealed class PlaybackProgress
{
    private readonly object _lock = new();
    private int _x;
    private int _y;

    public void SetPosition(int x, int y)
    {
        lock (_lock)
        {
            _x = x;
            _y = y;
        }
    }

    public Point GetPosition()
    {
        lock (_lock)
        {
            return new Point(_x, _y);
        }
    }
}
