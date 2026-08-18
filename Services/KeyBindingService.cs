using CutFlow.Models;
using System.Windows.Input;

namespace CutFlow.Services;

public static class KeyBindingService
{
    private static readonly Dictionary<string, string> DefaultsMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PlayPause"] = "Space",
        ["StepBack"] = "Left",
        ["StepForward"] = "Right",
        ["Rewind"] = "Shift+Left",
        ["FastForward"] = "Shift+Right",
        ["Cut"] = "Enter",
        ["Keep"] = "Delete",
        ["CutAll"] = "Ctrl+Delete",
        ["Undo"] = "Ctrl+Z",
        ["Redo"] = "Ctrl+Y",
        ["Save"] = "Ctrl+S",
        ["Export"] = "Ctrl+E",
        ["FullScreen"] = "F11"
    };

    public static IReadOnlyDictionary<string, string> Defaults => DefaultsMap;

    public static string Get(AppSettings settings, string action)
    {
        if (settings.KeyBindings is not null && settings.KeyBindings.TryGetValue(action, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        return DefaultsMap.TryGetValue(action, out var fallback) ? fallback : string.Empty;
    }

    public static bool Matches(AppSettings settings, string action, KeyEventArgs e) => Matches(Get(settings, action), e);

    public static bool Matches(string? binding, KeyEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(binding)) return false;
        var parts = binding.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        var modifiers = ModifierKeys.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            modifiers |= parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModifierKeys.Control,
                "shift" => ModifierKeys.Shift,
                "alt" => ModifierKeys.Alt,
                "win" or "windows" => ModifierKeys.Windows,
                _ => ModifierKeys.None
            };
        }
        if (!Enum.TryParse<Key>(NormalizeKeyName(parts[^1]), true, out var wanted)) return false;
        var actual = e.Key == Key.System ? e.SystemKey : e.Key;
        return actual == wanted && Keyboard.Modifiers == modifiers;
    }

    public static string FromEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return FromKey(key, Keyboard.Modifiers);
    }

    public static string FromKey(Key key, ModifierKeys modifiers)
    {
        if (IsModifierKey(key)) return string.Empty;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(DisplayKey(key));
        return string.Join('+', parts);
    }

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private static string NormalizeKeyName(string value) => value switch
    {
        "Backspace" => nameof(Key.Back),
        "Esc" => nameof(Key.Escape),
        "Return" => nameof(Key.Enter),
        _ => value
    };

    private static string DisplayKey(Key key) => key switch
    {
        Key.Back => "Backspace",
        Key.Escape => "Esc",
        _ => key.ToString()
    };
}
