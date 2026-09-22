using System.Drawing;

namespace MouseRecorder;

/// <summary>Bieżąca pozycja kursora podczas odtwarzania, bezpieczna wątkowo (aktualizowana z wątku Playera, czytana przez UI overlay).</summary>
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
