using System.Diagnostics;
using System.Drawing;

namespace MouseRecorder;

/// <summary>Nagrywa ruchy i kliknięcia myszy na podstawie globalnego low-level hooka.</summary>
public sealed class Recorder
{
    private const int MinMoveDistancePx = 3;
    private const int MinMoveIntervalMs = 50;

    private readonly MouseHook _hook = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly List<RecordedEvent> _events = new();

    private Point _lastSavedPoint;
    private long _lastSavedTimeMs;
    private bool _hasLastSavedPoint;

    /// <summary>Prostokąt (współrzędne ekranu), w którym zdarzenia są ignorowane (okienko STOP).</summary>
    public Rectangle ExcludedRegion { get; set; }

    public bool IsRecording { get; private set; }

    public Point StartPosition { get; private set; }

    public void Start()
    {
        _events.Clear();
        _hasLastSavedPoint = false;
        _lastSavedTimeMs = 0;

        Native.GetCursorPos(out var cursor);
        StartPosition = new Point(cursor.X, cursor.Y);

        _hook.MouseEvent += OnMouseEvent;
        _hook.Start();
        _stopwatch.Restart();
        IsRecording = true;
    }

    public Recording Stop()
    {
        IsRecording = false;
        _stopwatch.Stop();
        _hook.MouseEvent -= OnMouseEvent;
        _hook.Stop();

        var recording = new Recording { StartPosition = StartPosition };
        recording.Events.AddRange(_events);
        return recording;
    }

    private void OnMouseEvent(object? sender, MouseHookEventArgs e)
    {
        if (e.Injected) return;
        if (ExcludedRegion.Contains(e.Point)) return;

        switch (e.Message)
        {
            case Native.WM_MOUSEMOVE:
                MaybeRecordMove(e.Point);
                break;
            case Native.WM_LBUTTONDOWN:
                RecordExactThenEvent(e.Point, MouseEventKind.LeftDown);
                break;
            case Native.WM_LBUTTONUP:
                RecordExactThenEvent(e.Point, MouseEventKind.LeftUp);
                break;
            case Native.WM_RBUTTONDOWN:
                RecordExactThenEvent(e.Point, MouseEventKind.RightDown);
                break;
            case Native.WM_RBUTTONUP:
                RecordExactThenEvent(e.Point, MouseEventKind.RightUp);
                break;
            case Native.WM_MBUTTONDOWN:
                RecordExactThenEvent(e.Point, MouseEventKind.MiddleDown);
                break;
            case Native.WM_MBUTTONUP:
                RecordExactThenEvent(e.Point, MouseEventKind.MiddleUp);
                break;
            case Native.WM_MOUSEWHEEL:
                RecordExactThenEvent(e.Point, MouseEventKind.WheelVertical, e.WheelDelta);
                break;
            case Native.WM_MOUSEHWHEEL:
                RecordExactThenEvent(e.Point, MouseEventKind.WheelHorizontal, e.WheelDelta);
                break;
        }
    }

    private void MaybeRecordMove(Point point)
    {
        long t = _stopwatch.ElapsedMilliseconds;

        if (!_hasLastSavedPoint)
        {
            SavePoint(point, t);
            _events.Add(new RecordedEvent(t, MouseEventKind.Move, point.X, point.Y));
            return;
        }

        double dx = point.X - _lastSavedPoint.X;
        double dy = point.Y - _lastSavedPoint.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);

        bool moved = point != _lastSavedPoint;
        bool farEnough = distance >= MinMoveDistancePx;
        bool longEnough = moved && (t - _lastSavedTimeMs) >= MinMoveIntervalMs;

        if (farEnough || longEnough)
        {
            SavePoint(point, t);
            _events.Add(new RecordedEvent(t, MouseEventKind.Move, point.X, point.Y));
        }
    }

    private void RecordExactThenEvent(Point point, MouseEventKind kind, int delta = 0)
    {
        long t = _stopwatch.ElapsedMilliseconds;

        if (!_hasLastSavedPoint || point != _lastSavedPoint)
        {
            SavePoint(point, t);
            _events.Add(new RecordedEvent(t, MouseEventKind.Move, point.X, point.Y));
        }

        _events.Add(new RecordedEvent(t, kind, point.X, point.Y, delta));
    }

    private void SavePoint(Point point, long timeMs)
    {
        _lastSavedPoint = point;
        _lastSavedTimeMs = timeMs;
        _hasLastSavedPoint = true;
    }
}
