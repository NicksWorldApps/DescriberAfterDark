using System.Runtime.InteropServices;

namespace ImageDescriber.App;

/// <summary>A system-wide keyboard shortcut that works while other programs have focus.</summary>
internal sealed class GlobalHotkey : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;
    private const int HotkeyId = 0x4D44;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private bool _registered;

    public event EventHandler? Pressed;

    /// <summary>Spoken form of the shortcut, for example "Control Alt D".</summary>
    public string Description { get; private set; } = "";

    public GlobalHotkey()
    {
        CreateHandle(new CreateParams()); // a hidden message-only style window to receive WM_HOTKEY
    }

    /// <summary>Registers the shortcut. Returns false if another program already uses it.</summary>
    public bool Register(string modifiers, string key)
    {
        Unregister();

        uint mods = MOD_NOREPEAT;
        var spoken = new List<string>();
        foreach (var part in modifiers.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "control" or "ctrl": mods |= MOD_CONTROL; spoken.Add("Control"); break;
                case "alt": mods |= MOD_ALT; spoken.Add("Alt"); break;
                case "shift": mods |= MOD_SHIFT; spoken.Add("Shift"); break;
                case "win" or "windows": mods |= MOD_WIN; spoken.Add("Windows"); break;
            }
        }

        if (!Enum.TryParse<Keys>(key, ignoreCase: true, out var vk)) vk = Keys.D;
        spoken.Add(vk.ToString());
        Description = string.Join(" ", spoken);

        _registered = RegisterHotKey(Handle, HotkeyId, mods, (uint)vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(Handle, HotkeyId);
            _registered = false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }
}
