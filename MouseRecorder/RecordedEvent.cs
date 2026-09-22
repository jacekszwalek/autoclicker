using System.Drawing;

namespace MouseRecorder;

public enum MouseEventKind
{
    Move,
    LeftDown,
    LeftUp,
    RightDown,
    RightUp,
    MiddleDown,
    MiddleUp,
    WheelVertical,
    WheelHorizontal
}

/// <summary>A single recorded mouse event with a timestamp (ms since recording start).</summary>
public readonly struct RecordedEvent
{
    public long TimestampMs { get; }
    public MouseEventKind Kind { get; }
    public int X { get; }
    public int Y { get; }
    public int Delta { get; }

    public RecordedEvent(long timestampMs, MouseEventKind kind, int x, int y, int delta = 0)
    {
        TimestampMs = timestampMs;
        Kind = kind;
        X = x;
        Y = y;
        Delta = delta;
    }
}

/// <summary>State of the application's state machine.</summary>
public enum AppState
{
    Idle,
    Countdown,
    Recording,
    Playing
}

/// <summary>The result of a recording: a list of events plus the cursor's starting position.</summary>
public sealed class Recording
{
    public List<RecordedEvent> Events { get; } = new();
    public Point StartPosition { get; set; }
    public long DurationMs => Events.Count == 0 ? 0 : Events[^1].TimestampMs;
}
