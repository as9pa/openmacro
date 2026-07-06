using SharpHook.Data;

namespace OpenMacro.App;

/// <summary>
/// ANSI layout for the visual keyboard. Width is in key units
/// (1u = one letter key), matching physical keycap proportions.
/// </summary>
internal static class KeyboardLayout
{
    internal sealed record Key(KeyCode Code, string Label, double Width = 1.0);

    internal static readonly Key[][] Rows =
    [
        [
            new(KeyCode.VcEscape, "Esc"),
            new(KeyCode.VcF1, "F1"),
            new(KeyCode.VcF2, "F2"),
            new(KeyCode.VcF3, "F3"),
            new(KeyCode.VcF4, "F4"),
            new(KeyCode.VcF5, "F5"),
            new(KeyCode.VcF6, "F6"),
            new(KeyCode.VcF7, "F7"),
            new(KeyCode.VcF8, "F8"),
            new(KeyCode.VcF9, "F9"),
            new(KeyCode.VcF10, "F10"),
            new(KeyCode.VcF11, "F11"),
            new(KeyCode.VcF12, "F12"),
            new(KeyCode.VcDelete, "Del", 2.0),
        ],
        [
            new(KeyCode.VcBackQuote, "`"),
            new(KeyCode.Vc1, "1"),
            new(KeyCode.Vc2, "2"),
            new(KeyCode.Vc3, "3"),
            new(KeyCode.Vc4, "4"),
            new(KeyCode.Vc5, "5"),
            new(KeyCode.Vc6, "6"),
            new(KeyCode.Vc7, "7"),
            new(KeyCode.Vc8, "8"),
            new(KeyCode.Vc9, "9"),
            new(KeyCode.Vc0, "0"),
            new(KeyCode.VcMinus, "-"),
            new(KeyCode.VcEquals, "="),
            new(KeyCode.VcBackspace, "Backspace", 2.0),
        ],
        [
            new(KeyCode.VcTab, "Tab", 1.5),
            new(KeyCode.VcQ, "Q"),
            new(KeyCode.VcW, "W"),
            new(KeyCode.VcE, "E"),
            new(KeyCode.VcR, "R"),
            new(KeyCode.VcT, "T"),
            new(KeyCode.VcY, "Y"),
            new(KeyCode.VcU, "U"),
            new(KeyCode.VcI, "I"),
            new(KeyCode.VcO, "O"),
            new(KeyCode.VcP, "P"),
            new(KeyCode.VcOpenBracket, "["),
            new(KeyCode.VcCloseBracket, "]"),
            new(KeyCode.VcBackslash, "\\", 1.5),
        ],
        [
            new(KeyCode.VcCapsLock, "Caps", 1.75),
            new(KeyCode.VcA, "A"),
            new(KeyCode.VcS, "S"),
            new(KeyCode.VcD, "D"),
            new(KeyCode.VcF, "F"),
            new(KeyCode.VcG, "G"),
            new(KeyCode.VcH, "H"),
            new(KeyCode.VcJ, "J"),
            new(KeyCode.VcK, "K"),
            new(KeyCode.VcL, "L"),
            new(KeyCode.VcSemicolon, ";"),
            new(KeyCode.VcQuote, "'"),
            new(KeyCode.VcEnter, "Enter", 2.25),
        ],
        [
            new(KeyCode.VcLeftShift, "Shift", 2.25),
            new(KeyCode.VcZ, "Z"),
            new(KeyCode.VcX, "X"),
            new(KeyCode.VcC, "C"),
            new(KeyCode.VcV, "V"),
            new(KeyCode.VcB, "B"),
            new(KeyCode.VcN, "N"),
            new(KeyCode.VcM, "M"),
            new(KeyCode.VcComma, ","),
            new(KeyCode.VcPeriod, "."),
            new(KeyCode.VcSlash, "/"),
            new(KeyCode.VcRightShift, "Shift", 2.75),
        ],
        [
            new(KeyCode.VcLeftControl, "Ctrl", 1.25),
            new(KeyCode.VcLeftMeta, "Win", 1.25),
            new(KeyCode.VcLeftAlt, "Alt", 1.25),
            new(KeyCode.VcSpace, "Space", 6.25),
            new(KeyCode.VcRightAlt, "Alt", 1.25),
            new(KeyCode.VcRightControl, "Ctrl", 1.25),
            new(KeyCode.VcLeft, "←", 1.25),
            new(KeyCode.VcDown, "↓", 1.25),
            new(KeyCode.VcRight, "→", 1.25),
        ],
    ];
}
