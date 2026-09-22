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
    private const double MinClickHoldMs = 200.0;

    /// <summary>
    /// Minimum real (wall-clock) time since the last SendInput call before a button-down is sent.
    /// Gives the target app's hit-testing/hover state a moment to catch up with the cursor's new
    /// position before the click starts, instead of pressing the instant the cursor arrives.
    /// </summary>
    private const double MinPreClickSettleMs = 200.0;

    /// <summary>
    /// Minimum real (wall-clock) spacing between successive SendInput calls during interpolated
    /// movement, regardless of playback speed. Without this floor, a fast movement segment played
    /// back at high speed (e.g. x10) sends the same number of move events in a tenth of the time —
    /// flooding the target app's input queue far faster than it can keep up, which is the likely
    /// cause of clicks being dropped or misrouted intermittently at higher speeds.
    /// </summary>
    private const int MinMoveStepMs = 8;

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
        double lastSendAtMs = 0;

        double? leftDownAtMs = null;
        double? rightDownAtMs = null;
        double? middleDownAtMs = null;

        foreach (var ev in recording.Events)
        {
            if (token.IsCancellationRequested) return;

            if (ev.Kind == MouseEventKind.Move)
            {
                InterpolateMove(currentPos, new Point(ev.X, ev.Y), currentTimeMs, ev.TimestampMs, speedDivisor, sw, token, ref lastSendAtMs);
                currentPos = new Point(ev.X, ev.Y);
                currentTimeMs = ev.TimestampMs;
                continue;
            }

            WaitUntil(ev.TimestampMs / (double)speedDivisor, sw, token);
            if (token.IsCancellationRequested) return;

            switch (ev.Kind)
            {
                case MouseEventKind.LeftDown:
                case MouseEventKind.RightDown:
                case MouseEventKind.MiddleDown:
                    EnsureMinimumGap(lastSendAtMs, MinPreClickSettleMs, sw, token);
                    break;
                case MouseEventKind.LeftUp:
                    EnsureMinimumGap(leftDownAtMs ?? 0, MinClickHoldMs, sw, token);
                    leftDownAtMs = null;
                    break;
                case MouseEventKind.RightUp:
                    EnsureMinimumGap(rightDownAtMs ?? 0, MinClickHoldMs, sw, token);
                    rightDownAtMs = null;
                    break;
                case MouseEventKind.MiddleUp:
                    EnsureMinimumGap(middleDownAtMs ?? 0, MinClickHoldMs, sw, token);
                    middleDownAtMs = null;
                    break;
            }
            if (token.IsCancellationRequested) return;

            SendEvent(ev, pressed);
            currentPos = new Point(ev.X, ev.Y);
            currentTimeMs = ev.TimestampMs;
            lastSendAtMs = sw.Elapsed.TotalMilliseconds;
            Progress.SetPosition(ev.X, ev.Y);

            switch (ev.Kind)
            {
                case MouseEventKind.LeftDown: leftDownAtMs = lastSendAtMs; break;
                case MouseEventKind.RightDown: rightDownAtMs = lastSendAtMs; break;
                case MouseEventKind.MiddleDown: middleDownAtMs = lastSendAtMs; break;
            }
        }
    }

    /// <summary>Blocks until at least <paramref name="minGapMs"/> has passed since <paramref name="sinceMs"/>.</summary>
    private static void EnsureMinimumGap(double sinceMs, double minGapMs, Stopwatch sw, CancellationToken token)
    {
        double elapsed = sw.Elapsed.TotalMilliseconds - sinceMs;
        if (elapsed < minGapMs)
        {
            WaitUntil(sinceMs + minGapMs, sw, token);
        }
    }

    private void InterpolateMove(Point from, Point to, long fromMs, long toMs, int speedDivisor, Stopwatch sw, CancellationToken token, ref double lastSendAtMs)
    {
        long duration = toMs - fromMs;
        double wallClockDuration = duration / (double)speedDivisor;

        if (duration <= 0 || wallClockDuration < MinMoveStepMs)
        {
            WaitUntil(toMs / (double)speedDivisor, sw, token);
            if (token.IsCancellationRequested) return;
            SendMove(to);
            lastSendAtMs = sw.Elapsed.TotalMilliseconds;
            Progress.SetPosition(to.X, to.Y);
            return;
        }

        // Cap the number of steps so successive SendInput calls stay at least MinMoveStepMs apart in
        // real time, no matter how much this segment gets compressed by the speed divisor.
        int steps = Math.Max(1, (int)(wallClockDuration / MinMoveStepMs));
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
            lastSendAtMs = sw.Elapsed.TotalMilliseconds;
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

    /// <summary>
    /// Sends the cursor position and the button/wheel action in a single, atomic SendInput packet.
    /// A real mouse always reports position together with the button state on every hardware report,
    /// so a click's coordinates are never separated from the click itself; some pages/games that read
    /// clientX/clientY straight off the mousedown/mouseup event (rather than tracking a continuously
    /// updated cursor position from mousemove) can otherwise see a click with stale or missing
    /// coordinates if position and button state arrive as two separate injected events.
    /// </summary>
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
