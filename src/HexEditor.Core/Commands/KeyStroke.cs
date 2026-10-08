using System.Globalization;
using System.Text;

namespace HexEditor.Core.Commands;

/// <summary>修飾キー (UI-20 の仕様 1。仮想キーコードと組で保存する)。</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Win = 8,
}

/// <summary>
/// キーの 1 打鍵: 修飾キーと仮想キーコード (Windows の VK_*)。英字キーは配列上の文字の仮想キーで一致する
/// (AZERTY でも「Z」と刻印されたキーは VK_Z。UI-20 の仕様 1)。
/// </summary>
public readonly record struct KeyStroke(KeyModifiers Modifiers, int Key)
{
    /// <summary>保存用の表記 (例: <c>Ctrl+Shift+P</c>、<c>Ctrl+OemComma</c>)。修飾キーは Ctrl、Shift、Alt、Win の順。</summary>
    public override string ToString() => Format(VirtualKeys.Name);

    /// <summary>キーと修飾キーの表示名を差し替えて表記する (配列ごとの表示。UI-20 の仕様 2)。</summary>
    public string Format(Func<int, string> keyName, Func<KeyModifiers, string>? modifierName = null)
    {
        modifierName ??= m => m.ToString();
        var text = new StringBuilder();
        foreach (KeyModifiers m in new[] { KeyModifiers.Ctrl, KeyModifiers.Shift, KeyModifiers.Alt, KeyModifiers.Win })
        {
            if (Modifiers.HasFlag(m))
            {
                text.Append(modifierName(m)).Append('+');
            }
        }

        return text.Append(keyName(Key)).ToString();
    }

    /// <summary><c>Ctrl+Shift+P</c> などを読む。修飾キーの順序と大文字・小文字は問わない。</summary>
    public static bool TryParse(string text, out KeyStroke stroke)
    {
        stroke = default;
        text = text.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        KeyModifiers modifiers = KeyModifiers.None;
        int start = 0;
        while (true)
        {
            // 「Ctrl++」のような記号の + は扱わない (OemPlus と書く)。区切りの + は最後のキー名の前まで。
            int plus = text.IndexOf('+', start);
            if (plus < 0 || plus == text.Length - 1)
            {
                break;
            }

            string part = text[start..plus].Trim();
            KeyModifiers? m = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" or "strg" => KeyModifiers.Ctrl,
                "shift" => KeyModifiers.Shift,
                "alt" or "menu" => KeyModifiers.Alt,
                "win" or "windows" or "meta" => KeyModifiers.Win,
                _ => null,
            };
            if (m is null)
            {
                return false;
            }

            modifiers |= m.Value;
            start = plus + 1;
        }

        if (!VirtualKeys.TryParse(text[start..].Trim(), out int key))
        {
            return false;
        }

        stroke = new KeyStroke(modifiers, key);
        return true;
    }

    /// <summary>修飾キー自体 (Shift、Ctrl、Alt、Windows キー) か。</summary>
    public bool IsModifierOnly => VirtualKeys.IsModifier(Key);
}

/// <summary>
/// ショートカット: 1 打鍵、または 2 打鍵までの連続キー (例: <c>Ctrl+K Ctrl+S</c>。UI-18 の仕様 3)。
/// </summary>
public sealed record KeyChord
{
    public KeyChord(params KeyStroke[] strokes)
    {
        if (strokes.Length is < 1 or > 2)
        {
            throw new ArgumentException("ショートカットは 1 打鍵か 2 打鍵です。", nameof(strokes));
        }

        Strokes = strokes;
    }

    public IReadOnlyList<KeyStroke> Strokes { get; }

    public KeyStroke First => Strokes[0];

    public bool Equals(KeyChord? other) => other is not null && Strokes.SequenceEqual(other.Strokes);

    public override int GetHashCode() => Strokes.Aggregate(17, (h, s) => (h * 31) + s.GetHashCode());

    /// <summary>保存用の表記。打鍵の間は空白 (keybindings.json の <c>key</c>。UI-18 の仕様 9)。</summary>
    public override string ToString() => string.Join(' ', Strokes.Select(s => s.ToString()));

    public string Format(Func<int, string> keyName, Func<KeyModifiers, string>? modifierName = null) =>
        string.Join(' ', Strokes.Select(s => s.Format(keyName, modifierName)));

    /// <summary>この連続キーの先頭が <paramref name="other"/> と同じか (どちらかがもう一方の先頭部分)。</summary>
    public bool OverlapsPrefix(KeyChord other) =>
        Strokes.Count != other.Strokes.Count && Strokes[0] == other.Strokes[0];

    /// <summary><c>Ctrl+K Ctrl+F</c>、<c>Ctrl+K, Ctrl+F</c> を読む。</summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // 打鍵の区切りは空白 (説明文の「Ctrl+K, Ctrl+S」のカンマ + 空白も受け付ける)。「Ctrl+,」のカンマはキー。
        string[] parts = text.Replace(", ", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2)
        {
            return false;
        }

        var strokes = new KeyStroke[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!KeyStroke.TryParse(parts[i], out strokes[i]))
            {
                return false;
            }
        }

        chord = new KeyChord(strokes);
        return true;
    }

    public static KeyChord Parse(string text) =>
        TryParse(text, out KeyChord chord) ? chord : throw new FormatException($"ショートカットの表記が正しくありません: {text}");
}

/// <summary>Windows の仮想キーコードと、その保存用の名前 (UI-20 の仕様 1)。</summary>
public static class VirtualKeys
{
    public const int Back = 0x08, Tab = 0x09, Enter = 0x0D, Shift = 0x10, Control = 0x11, Menu = 0x12, Pause = 0x13,
        Escape = 0x1B, Space = 0x20, PageUp = 0x21, PageDown = 0x22, End = 0x23, Home = 0x24, Left = 0x25, Up = 0x26,
        Right = 0x27, Down = 0x28, Insert = 0x2D, Delete = 0x2E, LeftWindows = 0x5B, RightWindows = 0x5C, Apps = 0x5D,
        NumPad0 = 0x60, F1 = 0x70, F4 = 0x73, F24 = 0x87, LeftShift = 0xA0, RightMenu = 0xA5, OemComma = 0xBC, OemPlus = 0xBB,
        OemMinus = 0xBD, OemPeriod = 0xBE, Oem5 = 0xDC;

    private static readonly Dictionary<int, string> Names = BuildNames();

    private static readonly Dictionary<string, int> ByName = BuildByName();

    private static Dictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>
        {
            [Back] = "Backspace",
            [Tab] = "Tab",
            [Enter] = "Enter",
            [Pause] = "Pause",
            [Escape] = "Escape",
            [Space] = "Space",
            [PageUp] = "PageUp",
            [PageDown] = "PageDown",
            [End] = "End",
            [Home] = "Home",
            [Left] = "Left",
            [Up] = "Up",
            [Right] = "Right",
            [Down] = "Down",
            [0x2C] = "PrintScreen",
            [Insert] = "Insert",
            [Delete] = "Delete",
            [Apps] = "Apps",
            [0x6A] = "Multiply",
            [0x6B] = "Add",
            [0x6C] = "Separator",
            [0x6D] = "Subtract",
            [0x6E] = "Decimal",
            [0x6F] = "Divide",
            [0x91] = "ScrollLock",
            [0xBA] = "Oem1",
            [OemPlus] = "OemPlus",
            [OemComma] = "OemComma",
            [OemMinus] = "OemMinus",
            [OemPeriod] = "OemPeriod",
            [0xBF] = "Oem2",
            [0xC0] = "Oem3",
            [0xDB] = "Oem4",
            [Oem5] = "Oem5",
            [0xDD] = "Oem6",
            [0xDE] = "Oem7",
            [0xDF] = "Oem8",
            [0xE2] = "Oem102",
            [Shift] = "Shift",
            [Control] = "Ctrl",
            [Menu] = "Alt",
            [LeftWindows] = "LWin",
            [RightWindows] = "RWin",
        };
        for (int c = 'A'; c <= 'Z'; c++)
        {
            names[c] = ((char)c).ToString();
        }

        for (int d = 0; d <= 9; d++)
        {
            names['0' + d] = d.ToString(CultureInfo.InvariantCulture);
            names[NumPad0 + d] = "NumPad" + d.ToString(CultureInfo.InvariantCulture);
        }

        for (int f = 1; f <= 24; f++)
        {
            names[F1 + f - 1] = "F" + f.ToString(CultureInfo.InvariantCulture);
        }

        return names;
    }

    private static Dictionary<string, int> BuildByName()
    {
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach ((int key, string name) in Names)
        {
            byName[name] = key;
        }

        // 別名 (手で書いた keybindings.json と、WinUI の VirtualKey の名前)。
        (string, int)[] aliases =
        [
            ("Esc", Escape), ("Del", Delete), ("Ins", Insert), ("Back", Back), ("Return", Enter), ("PgUp", PageUp),
            ("PgDn", PageDown), ("PageDn", PageDown), ("Control", Control), ("Menu", Menu), ("Application", Apps),
            ("←", Left), ("→", Right), ("↑", Up), ("↓", Down),
            (",", OemComma), ("=", OemPlus), ("-", OemMinus), (".", OemPeriod), ("/", 0xBF), ("\\", Oem5), (";", 0xBA),
            ("`", 0xC0), ("[", 0xDB), ("]", 0xDD), ("'", 0xDE),
        ];
        foreach ((string alias, int key) in aliases)
        {
            byName[alias] = key;
        }

        for (int d = 0; d <= 9; d++)
        {
            byName["D" + d.ToString(CultureInfo.InvariantCulture)] = '0' + d;
            byName["Number" + d.ToString(CultureInfo.InvariantCulture)] = '0' + d;
        }

        return byName;
    }

    /// <summary>保存用の名前。表にないキーは <c>VK_xx</c> (16 進)。</summary>
    public static string Name(int key) =>
        Names.TryGetValue(key, out string? name) ? name : "VK_" + key.ToString("X2", CultureInfo.InvariantCulture);

    public static bool TryParse(string name, out int key)
    {
        if (ByName.TryGetValue(name, out key))
        {
            return true;
        }

        if (name.StartsWith("VK_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out key) && key is > 0 and < 0xFF)
        {
            return true;
        }

        key = 0;
        return false;
    }

    public static bool IsModifier(int key) =>
        key is Shift or Control or Menu or LeftWindows or RightWindows || key is >= LeftShift and <= RightMenu;

    /// <summary>修飾キーなしで文字を入力するキー (英字・数字・記号・空白。UI-18 の仕様 7)。</summary>
    public static bool IsTypingKey(int key) =>
        key is >= 'A' and <= 'Z' or >= '0' and <= '9' or Space or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2
            or >= NumPad0 and <= 0x6F;

    public static bool IsFunctionKey(int key) => key is >= F1 and <= F24;
}
