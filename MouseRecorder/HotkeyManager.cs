using System.Runtime.InteropServices;

namespace MouseRecorder;

/// <summary>Manages global hotkeys (RegisterHotKey), independent of which window has focus.</summary>
public sealed class HotkeyManager : IDisposable
{
    public const int HotkeyIdPlay = 1;
    public const int HotkeyIdStop = 2;

    private readonly IntPtr _windowHandle;
    private readonly HashSet<int> _registeredIds = new();

    public HotkeyManager(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
    }

    /// <summary>Registers F8 (play) and F9 (stop). Returns the list of hotkeys that failed to register.</summary>
    public List<string> RegisterAll()
    {
        var failed = new List<string>();

        if (!TryRegister(HotkeyIdPlay, Native.VK_F8))
            failed.Add("F8");

        if (!TryRegister(HotkeyIdStop, Native.VK_F9))
            failed.Add("F9");

        return failed;
    }

    private bool TryRegister(int id, uint vk)
    {
        bool ok = Native.RegisterHotKey(_windowHandle, id, 0, vk);
        if (ok)
        {
            _registeredIds.Add(id);
        }
        return ok;
    }

    public void UnregisterAll()
    {
        foreach (int id in _registeredIds)
        {
            Native.UnregisterHotKey(_windowHandle, id);
        }
        _registeredIds.Clear();
    }

    public void Dispose() => UnregisterAll();
}
