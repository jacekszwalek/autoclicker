using System.Drawing;
using System.Runtime.InteropServices;

namespace MouseRecorder;

public sealed class MouseHookEventArgs : EventArgs
{
    public int Message { get; init; }
    public Point Point { get; init; }
    public short WheelDelta { get; init; }
    public bool Injected { get; init; }
}

/// <summary>Globalny low-level hook myszy (WH_MOUSE_LL).</summary>
public sealed class MouseHook : IDisposable
{
    private Native.LowLevelMouseProc? _proc;
    private IntPtr _hookHandle = IntPtr.Zero;

    public event EventHandler<MouseHookEventArgs>? MouseEvent;

    public bool IsInstalled => _hookHandle != IntPtr.Zero;

    public void Start()
    {
        if (IsInstalled) return;

        // Trzymamy referencję do delegata, żeby GC jej nie zebrał póki hook jest aktywny.
        _proc = HookCallback;

        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule!;
        IntPtr hModule = Native.GetModuleHandle(currentModule.ModuleName);

        _hookHandle = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, hModule, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            _proc = null;
            throw new InvalidOperationException($"Nie udało się zainstalować globalnego hooka myszy (kod błędu {error}).");
        }
    }

    public void Stop()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        _proc = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && MouseEvent != null)
        {
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            int message = wParam.ToInt32();
            bool injected = (data.flags & Native.LLMHF_INJECTED) != 0;
            short wheelDelta = 0;

            if (message == Native.WM_MOUSEWHEEL || message == Native.WM_MOUSEHWHEEL)
            {
                wheelDelta = unchecked((short)((data.mouseData >> 16) & 0xFFFF));
            }

            MouseEvent.Invoke(this, new MouseHookEventArgs
            {
                Message = message,
                Point = new Point(data.pt.X, data.pt.Y),
                WheelDelta = wheelDelta,
                Injected = injected
            });
        }

        return Native.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
