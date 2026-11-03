using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace BoostStudio.App.Services;

public enum HotkeyAction
{
    ToggleBass,
    ToggleVolume,
    ToggleProcessing
}

public sealed record HotkeyRegistrationResult(bool Success, IReadOnlyList<string> Errors)
{
    public string Summary => Success
        ? "All global shortcuts are registered."
        : string.Join(" ", Errors);
}

/// <summary>
/// System-wide hotkeys via RegisterHotKey. Registration is all-or-nothing for the
/// requested set so a partial failure is reported clearly instead of silently stealing
/// a shortcut the user believes is active.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int MasterId = 0xB501;
    private const int BassId = 0xB502;
    private const int VolumeId = 0xB503;

    private readonly Dictionary<int, HotkeyAction> _actions = new();
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _disposed;
    private bool _registered;

    public event EventHandler<HotkeyAction>? Pressed;

    public string? LastError { get; private set; }

    public bool IsRegistered => _registered;

    public void Attach(IntPtr windowHandle)
    {
        if (_disposed) return;
        _handle = windowHandle;
        _source = HwndSource.FromHwnd(windowHandle);
        if (_source is not null)
            _source.AddHook(WndProc);
    }

    public HotkeyRegistrationResult Register(string bassGesture, string volumeGesture, string masterGesture)
    {
        UnregisterAll();

        if (_source is null || _handle == IntPtr.Zero)
        {
            LastError = "The app window is not ready for global shortcuts yet.";
            return new HotkeyRegistrationResult(false, new[] { LastError });
        }

        var requests = new[]
        {
            (Action: HotkeyAction.ToggleBass, Gesture: bassGesture, Id: BassId),
            (Action: HotkeyAction.ToggleVolume, Gesture: volumeGesture, Id: VolumeId),
            (Action: HotkeyAction.ToggleProcessing, Gesture: masterGesture, Id: MasterId)
        };

        var definitions = new List<(HotkeyAction Action, HotkeyDefinition Definition, int Id, string Gesture)>();
        var errors = new List<string>();

        foreach (var request in requests)
        {
            if (!TryParse(request.Gesture, out var definition, out var error))
            {
                errors.Add(error!);
                continue;
            }

            if (definitions.Any(d => d.Definition.Modifiers == definition.Modifiers && d.Definition.Key == definition.Key))
            {
                errors.Add($"The shortcut {definition.DisplayName} is assigned more than once.");
                continue;
            }

            definitions.Add((request.Action, definition, request.Id, request.Gesture));
        }

        if (errors.Count > 0)
        {
            LastError = string.Join(" ", errors);
            return new HotkeyRegistrationResult(false, errors);
        }

        var registered = new List<(int Id, HotkeyAction Action)>();
        foreach (var definition in definitions)
        {
            if (RegisterHotKey(_handle, definition.Id, definition.Definition.NativeModifiers | ModNoRepeat,
                    definition.Definition.VirtualKey))
            {
                _actions[definition.Id] = definition.Action;
                registered.Add((definition.Id, definition.Action));
                continue;
            }

            var code = Marshal.GetLastWin32Error();
            errors.Add(DescribeRegistrationFailure(definition.Definition.DisplayName, code));
            LastError = string.Join(" ", errors);
            foreach (var (id, _) in registered)
            {
                UnregisterHotKey(_handle, id);
                _actions.Remove(id);
            }

            return new HotkeyRegistrationResult(false, errors);
        }

        _registered = true;
        LastError = null;
        return new HotkeyRegistrationResult(true, Array.Empty<string>());
    }

    public void UnregisterAll()
    {
        if (_handle != IntPtr.Zero)
        {
            foreach (var id in _actions.Keys.ToArray())
                UnregisterHotKey(_handle, id);
        }

        _actions.Clear();
        _registered = false;
        LastError = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        var id = wParam.ToInt32();
        if (_actions.TryGetValue(id, out var action))
        {
            handled = true;
            Pressed?.Invoke(this, action);
        }

        return IntPtr.Zero;
    }

    private static string DescribeRegistrationFailure(string displayName, int code)
    {
        const int errorHotkeyAlreadyRegistered = 1409;
        return code == errorHotkeyAlreadyRegistered
            ? $"{displayName} is already used by another application."
            : $"{displayName} could not be registered (Windows error {code}).";
    }

    public static bool TryParse(string? gesture, out HotkeyDefinition definition, out string? error)
    {
        definition = default;
        error = null;

        if (string.IsNullOrWhiteSpace(gesture))
        {
            error = "A shortcut cannot be empty.";
            return false;
        }

        var parts = gesture
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (parts.Length < 2)
        {
            error = $"“{gesture}” needs at least one modifier and a key.";
            return false;
        }

        var modifiers = ModifierKeys.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var part = NormalizeModifier(parts[i]);
            if (part is null)
            {
                error = $"“{parts[i]}” is not a modifier. Use Ctrl, Alt, Shift or Win.";
                return false;
            }

            modifiers |= part.Value;
        }

        if (!TryParseKey(parts[^1], out var key))
        {
            error = $"“{parts[^1]}” is not a supported key.";
            return false;
        }

        if (modifiers == ModifierKeys.None && !IsFunctionKey(key))
        {
            error = "Include Ctrl, Alt, Shift or Win so the shortcut does not capture normal typing.";
            return false;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            error = $"“{parts[^1]}” cannot be used as a global shortcut.";
            return false;
        }

        definition = new HotkeyDefinition(modifiers, key, virtualKey, Format(modifiers, key));
        return true;
    }

    private static bool TryParseKey(string text, out Key key)
    {
        key = Key.None;
        var alias = text.Trim() switch
        {
            "Return" => "Enter",
            "Esc" => "Escape",
            "Del" => "Delete",
            "Ins" => "Insert",
            "PgUp" => "PageUp",
            "PgDn" => "PageDown",
            var value when value.Length == 1 && char.IsDigit(value[0]) =>
                value[0] == '0' ? "D0" : $"D{value}",
            var value => value
        };

        if (!Enum.TryParse<Key>(alias, ignoreCase: true, out var parsed))
            return false;

        if (parsed is Key.None
            or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.System)
            return false;

        key = parsed;
        return true;
    }

    private static ModifierKeys? NormalizeModifier(string text) => text.Trim().ToLowerInvariant() switch
    {
        "ctrl" or "control" => ModifierKeys.Control,
        "alt" => ModifierKeys.Alt,
        "shift" => ModifierKeys.Shift,
        "win" or "windows" => ModifierKeys.Windows,
        _ => null
    };

    private static bool IsFunctionKey(Key key) => key is >= Key.F1 and <= Key.F24;

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        Key.D0 => "0",
        Key.D1 => "1",
        Key.D2 => "2",
        Key.D3 => "3",
        Key.D4 => "4",
        Key.D5 => "5",
        Key.D6 => "6",
        Key.D7 => "7",
        Key.D8 => "8",
        Key.D9 => "9",
        Key.OemPlus => "OemPlus",
        Key.OemMinus => "OemMinus",
        Key.OemComma => "OemComma",
        Key.OemPeriod => "OemPeriod",
        Key.OemQuestion => "OemQuestion",
        Key.OemSemicolon => "OemSemicolon",
        Key.OemQuotes => "OemQuotes",
        Key.OemOpenBrackets => "OemOpenBrackets",
        Key.OemCloseBrackets => "OemCloseBrackets",
        Key.OemPipe => "OemPipe",
        Key.OemTilde => "OemTilde",
        _ => key.ToString()
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        if (_source is not null)
        {
            try { _source.RemoveHook(WndProc); } catch { /* ignore */ }
            _source = null;
        }

        _handle = IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

public readonly record struct HotkeyDefinition(
    ModifierKeys Modifiers,
    Key Key,
    int VirtualKey,
    string DisplayName)
{
    public uint NativeModifiers
    {
        get
        {
            uint value = 0;
            if (Modifiers.HasFlag(ModifierKeys.Alt)) value |= 0x0001;
            if (Modifiers.HasFlag(ModifierKeys.Control)) value |= 0x0002;
            if (Modifiers.HasFlag(ModifierKeys.Shift)) value |= 0x0004;
            if (Modifiers.HasFlag(ModifierKeys.Windows)) value |= 0x0008;
            return value;
        }
    }
}
