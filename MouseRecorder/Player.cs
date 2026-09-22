using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace MouseRecorder;

/// <summary>Replays recorded mouse events using SendInput, on a dedicated thread.</summary>
public sealed class Player
{
    /// <summary>
    /// Minimum real (wall-clock) time a mouse button stays down before its release is sent, no matter
    /// the playback speed. At high speed dividers a click's down/up gap can shrink to just a few ms,
    /// which some apps (browsers in particular) fail to register as a real click.
    /// </summary>
    private const double MinClickHoldMs = 40.0;

    /// <summary>
    /// Real time to wait after the very last input event before handing focus back to our own
    /// window. SendInput only queues the OS input message; if MainForm reactivates itself
    /// (Activate/BringToFront) before the target app has actually dequeued and processed the final
    /// button-up, the target can lose the click that was in flight — most visible when the clicked
    /// element sits right at the end of the recorded path, since nothing else delays the return to
    /// MainForm in that case.
    /// </summary>
    private const int EndOfPlaybackSettleMs = 150;

    public bool IsPlaying { get; private set; }
    public PlaybackProgress Progress { get; } = new();

    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private Action? _onFinished;

    /// <param name="repeatCount">0 or less = play forever.</param>
    public void Play(Recording recording, int speedDivisor, int repeatCount, Action onFinished)
    {
        if (IsPlaying || recording.Events.Count == 0)
        {
            onFinished();
            return;
        }

        IsPlaying = true;
        _onFinished = onFinished;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _thread = new Thread(() => RunPlayback(recording, Math.Max(1, speedDivisor), repeatCount, token))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    public void Stop() => _cts?.Cancel();

    private void RunPlayback(Recording recording, int speedDivisor, int repeatCount, CancellationToken token)
    {
        var pressed = new HashSet<MouseEventKind>();
        try
        {
            Native.timeBeginPeriod(1);
            bool infinite = repeatCount <= 0;
            int loops = 0;

            MoveTo(recording.StartPosition);
            Progress.SetPosition(recording.StartPosition.X, recording.StartPosition.Y);

            while (!token.IsCancellationRequested && (infinite || loops < repeatCount))
            {
                RunSingleLoop(recording, speedDivisor, token, pressed);
                loops++;

                if (!token.IsCancellationRequested && (infinite || loops < repeatCount))
                {
                    MoveTo(recording.StartPosition);
                    Progress.SetPosition(recording.StartPosition.X, recording.StartPosition.Y);
                }
            }

            if (!token.IsCancellationRequested)
            {
                Thread.Sleep(EndOfPlaybackSettleMs);
            }
        }
        finally
        {
            ReleaseHeld(pressed);
            Native.timeEndPeriod(1);
            IsPlaying = false;
            _onFinished?.Invoke();
        }
    }

    private void RunSingleLoop(Recording recording, int speedDivisor, CancellationToken token, HashSet<MouseEventKind> pressed)
    {
        var sw = Stopwatch.StartNew();
        Point currentPos = recording.StartPosition;
        long currentTimeMs = 0;

        double? leftDownAtMs = null;
        double? rightDownAtMs = null;
        double? middleDownAtMs = null;

        foreach (var ev in recording.Events)
        {
            if (token.IsCancellationRequested) return;

            if (ev.Kind == MouseEventKind.Move)
            {
                InterpolateMove(currentPos, new Point(ev.X, ev.Y), currentTimeMs, ev.TimestampMs, speedDivisor, sw, token);
                currentPos = new Point(ev.X, ev.Y);
                currentTimeMs = ev.TimestampMs;
                continue;
            }

            WaitUntil(ev.TimestampMs / (double)speedDivisor, sw, token);
            if (token.IsCancellationRequested) return;

            switch (ev.Kind)
            {
                case MouseEventKind.LeftUp:
                    EnsureMinimumHold(leftDownAtMs, sw, token);
                    leftDownAtMs = null;
                    break;
                case MouseEventKind.RightUp:
                    EnsureMinimumHold(rightDownAtMs, sw, token);
                    rightDownAtMs = null;
                    break;
                case MouseEventKind.MiddleUp:
                    EnsureMinimumHold(middleDownAtMs, sw, token);
                    middleDownAtMs = null;
                    break;
            }
            if (token.IsCancellationRequested) return;

            SendEvent(ev, pressed);
            currentPos = new Point(ev.X, ev.Y);
            currentTimeMs = ev.TimestampMs;
            Progress.SetPosition(ev.X, ev.Y);

            switch (ev.Kind)
            {
                case MouseEventKind.LeftDown: leftDownAtMs = sw.Elapsed.TotalMilliseconds; break;
                case MouseEventKind.RightDown: rightDownAtMs = sw.Elapsed.TotalMilliseconds; break;
                case MouseEventKind.MiddleDown: middleDownAtMs = sw.Elapsed.TotalMilliseconds; break;
            }
        }
    }

    /// <summary>Blocks until at least MinClickHoldMs has passed since the matching button-down was sent.</summary>
    private static void EnsureMinimumHold(double? downAtMs, Stopwatch sw, CancellationToken token)
    {
        if (downAtMs is not { } downAt) return;

        double heldFor = sw.Elapsed.TotalMilliseconds - downAt;
        if (heldFor < MinClickHoldMs)
        {
            WaitUntil(downAt + MinClickHoldMs, sw, token);
        }
    }

    private void InterpolateMove(Point from, Point to, long fromMs, long toMs, int speedDivisor, Stopwatch sw, CancellationToken token)
    {
        const int stepMs = 8;
        long duration = toMs - fromMs;

        if (duration <= 0)
        {
            WaitUntil(toMs / (double)speedDivisor, sw, token);
            if (token.IsCancellationRequested) return;
            SendMove(to);
            Progress.SetPosition(to.X, to.Y);
            return;
        }

        int steps = Math.Max(1, (int)(duration / stepMs));
        for (int i = 1; i <= steps; i++)
        {
            if (token.IsCancellationRequested) return;

            double frac = i / (double)steps;
            int x = from.X + (int)Math.Round((to.X - from.X) * frac);
            int y = from.Y + (int)Math.Round((to.Y - from.Y) * frac);
            long recordedT = fromMs + (long)Math.Round(duration * frac);

            WaitUntil(recordedT / (double)speedDivisor, sw, token);
            if (token.IsCancellationRequested) return;

            SendMove(new Point(x, y));
            Progress.SetPosition(x, y);
        }
    }

    private static void WaitUntil(double targetMs, Stopwatch sw, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            double remaining = targetMs - sw.Elapsed.TotalMilliseconds;
            if (remaining <= 0) return;
            Thread.Sleep(remaining > 3 ? 1 : 0);
        }
    }

    private static void MoveTo(Point p) => SendMove(p);

    private static void SendMove(Point p)
    {
        var (nx, ny) = VirtualDesktop.ToNormalized(p.X, p.Y);
        SendMouseInput(nx, ny, Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK, 0);
    }

    private static void SendEvent(RecordedEvent ev, HashSet<MouseEventKind> pressed)
    {
        var (nx, ny) = VirtualDesktop.ToNormalized(ev.X, ev.Y);
        const uint baseFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;

        switch (ev.Kind)
        {
            case MouseEventKind.LeftDown:
                SendMouseInput(nx, ny, baseFlags | Native.MOUSEEVENTF_LEFTDOWN, 0);
                pressed.Add(MouseEventKind.LeftDown);
                break;
            case MouseEventKind.LeftUp:
                SendMouseInput(nx, ny, baseFlags | Native.MOUSEEVENTF_LEFTUP, 0);
                pressed.Remove(MouseEventKind.LeftDown);
                break;
            case MouseEventKind.RightDown:
                SendMouseInput(nx, ny, baseFlags | Native.MOUSEEVENTF_RIGHTDOWN, 0);
                pressed.Add(MouseEventKind.RightDown);
                break;
            case MouseEventKind.RightUp:
                SendMouseInput(nx, ny, baseFlags | Native.MOUSEEVENTF_RIGHTUP, 0);
                pressed.Remove(MouseEventKind.RightDown);
                break;
            case MouseEventKind.MiddleDown:
                SendMouseInput(nx, ny, baseFlags | Native.MOUSEEVENTF_MIDDLEDOWN, 0);
                pressed.Add(MouseEventKind.MiddleDown);
                break;
            case MouseEventKind.MiddleUp:
                SendMouseInput(nx, ny, baseFlags | Native.MOUSEEVENTF_MIDDLEUP, 0);
                pressed.Remove(MouseEventKind.MiddleDown);
                break;
            case MouseEventKind.WheelVertical:
                SendMouseInput(nx, ny, baseFlags | Native.MOUSEEVENTF_WHEEL, ev.Delta);
                break;
            case MouseEventKind.WheelHorizontal:
                SendMouseInput(nx, ny, baseFlags | Native.MOUSEEVENTF_HWHEEL, ev.Delta);
                break;
        }
    }

    private static void SendMouseInput(int nx, int ny, uint flags, int mouseData)
    {
        var input = new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            mi = new Native.MOUSEINPUT
            {
                dx = nx,
                dy = ny,
                mouseData = unchecked((uint)mouseData),
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            }
        };
        Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());
    }

    /// <summary>Safety: releases any button that playback left pressed down.</summary>
    private static void ReleaseHeld(HashSet<MouseEventKind> pressed)
    {
        if (pressed.Contains(MouseEventKind.LeftDown))
            SendMouseInput(0, 0, Native.MOUSEEVENTF_LEFTUP, 0);
        if (pressed.Contains(MouseEventKind.RightDown))
            SendMouseInput(0, 0, Native.MOUSEEVENTF_RIGHTUP, 0);
        if (pressed.Contains(MouseEventKind.MiddleDown))
            SendMouseInput(0, 0, Native.MOUSEEVENTF_MIDDLEUP, 0);

        pressed.Clear();
    }
}
