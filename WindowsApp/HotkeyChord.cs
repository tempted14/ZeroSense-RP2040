using System;
using System.Collections.Generic;
using System.Linq;

namespace RainbowRecoil;

public sealed record HotkeyChord(string Modifier, string Key)
{
    public static IReadOnlyList<string> Modifiers { get; } = ["None", "Shift", "Ctrl", "Alt"];

    public static IReadOnlyList<string> Keys { get; } =
    [
        "M1", "M2", "F1", "F2", "F3", "F4", "F5", "F6",
        "F7", "F8", "F9", "F10", "F11", "F12"
    ];

    public string DisplayName => Modifier.Equals("None", StringComparison.OrdinalIgnoreCase)
        ? Key
        : $"{Modifier} + {Key}";

    public static string NormalizeKey(string? value, string fallback)
    {
        var match = Keys.FirstOrDefault(option =>
            option.Equals(value, StringComparison.OrdinalIgnoreCase));
        return match ?? fallback;
    }
}
