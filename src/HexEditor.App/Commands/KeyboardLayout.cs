using System.Runtime.InteropServices;
using System.Text;
using HexEditor.Core.Commands;

namespace HexEditor.App.Commands;

/// <summary>
/// 今のキーボード配列でのキーの表示名 (UI-20 の仕様 2、3、4)。キーの名前は <c>GetKeyNameTextW</c> / <c>ToUnicodeEx</c>、
/// 修飾キーの名前も OS の表記を使い、アプリでは翻訳しない (例: ドイツ語配列の Ctrl は <c>Strg</c>)。
/// 配列が変わったら (<c>WM_INPUTLANGCHANGE</c>、またはウィンドウがアクティブになったときの確認) <see cref="Changed"/> を出す。
/// </summary>
public static class KeyboardLayout
{
    private const uint MapVkToVsc = 0, MapVkToVscEx = 4;
    private static readonly Dictionary<int, string> KeyNames = [];
    private static readonly Dictionary<KeyModifiers, string> ModifierNames = [];
    private static nint _layout;

    /// <summary>配列が変わった (表示名を作り直す)。UI スレッドで呼ばれる。</summary>
    public static event Action? Changed;

    /// <summary>今の配列を確かめ、変わっていれば表示名を捨てて <see cref="Changed"/> を出す。</summary>
    public static void Refresh()
    {
        nint layout = GetKeyboardLayout(0);
        if (layout == _layout)
        {
            return;
        }

        _layout = layout;
        KeyNames.Clear();
        ModifierNames.Clear();
        Changed?.Invoke();
    }

    /// <summary>ショートカットの表示 (例: <c>Ctrl+K Ctrl+S</c>、ドイツ語配列で <c>Strg+,</c>)。</summary>
    public static string Format(KeyChord chord)
    {
        EnsureLayout();
        return chord.Format(KeyName, ModifierName);
    }

    public static string Format(IEnumerable<KeyChord> chords) => string.Join(", ", chords.Select(Format));

    /// <summary>今の配列で押せないキーを使っているか (UI-20 の仕様 3)。</summary>
    public static bool IsUnavailable(KeyChord chord)
    {
        EnsureLayout();
        return chord.Strokes.Any(s => MapVirtualKeyExW((uint)s.Key, MapVkToVsc, _layout) == 0);
    }

    /// <summary>Ctrl+Alt (AltGr) + キーが今の配列で文字を生むか (UI-20 の仕様 4)。</summary>
    public static bool ProducesAltGrCharacter(KeyStroke stroke)
    {
        if (!stroke.Modifiers.HasFlag(KeyModifiers.Ctrl) || !stroke.Modifiers.HasFlag(KeyModifiers.Alt))
        {
            return false;
        }

        EnsureLayout();
        byte[] state = new byte[256];
        state[0x11] = state[0x12] = state[0xA2] = state[0xA5] = 0x80;
        if (stroke.Modifiers.HasFlag(KeyModifiers.Shift))
        {
            state[0x10] = 0x80;
        }

        return Translate((uint)stroke.Key, state) is { Length: > 0 } text && !char.IsControl(text[0]);
    }

    /// <summary>キー 1 つの表示名。</summary>
    public static string KeyName(int key)
    {
        if (KeyNames.TryGetValue(key, out string? name))
        {
            return name;
        }

        name = Compute(key);
        KeyNames[key] = name;
        return name;
    }

    private static string Compute(int key)
    {
        if (key is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)key).ToString();
        }

        // 記号のキーは、その配列で入力される文字 (例: ドイツ語配列の OemPlus は +)。
        if (key is >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2 && Translate((uint)key, new byte[256]) is { Length: > 0 } ch && !char.IsControl(ch[0]))
        {
            return ch.ToUpperInvariant();
        }

        if (OsKeyName(key) is { Length: > 0 } os)
        {
            return os;
        }

        return VirtualKeys.Name(key);
    }

    private static string ModifierName(KeyModifiers modifier)
    {
        if (ModifierNames.TryGetValue(modifier, out string? name))
        {
            return name;
        }

        int vk = modifier switch
        {
            KeyModifiers.Ctrl => 0x11,
            KeyModifiers.Shift => 0x10,
            KeyModifiers.Alt => 0x12,
            _ => 0,
        };
        name = vk != 0 && OsKeyName(vk) is { Length: > 0 } os ? Capitalize(os) : modifier.ToString();
        ModifierNames[modifier] = name;
        return name;
    }

    /// <summary>大文字だけの名前 (例: STRG) を先頭だけ大文字にする。</summary>
    private static string Capitalize(string name) =>
        name.Length > 1 && name.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? name[0] + name[1..].ToLowerInvariant() : name;

    private static string? OsKeyName(int vk)
    {
        uint scan = MapVirtualKeyExW((uint)vk, MapVkToVscEx, _layout);
        if (scan == 0)
        {
            return null;
        }

        bool extended = (scan & 0xFF00) is 0xE000 or 0xE100
            || vk is VirtualKeys.Left or VirtualKeys.Right or VirtualKeys.Up or VirtualKeys.Down or VirtualKeys.Home or VirtualKeys.End
                or VirtualKeys.PageUp or VirtualKeys.PageDown or VirtualKeys.Insert or VirtualKeys.Delete or 0x6F;
        int lParam = (int)((scan & 0xFF) << 16) | (extended ? 1 << 24 : 0);
        var buffer = new StringBuilder(64);
        return GetKeyNameTextW(lParam, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
    }

    private static string? Translate(uint vk, byte[] state)
    {
        var buffer = new StringBuilder(8);
        uint scan = MapVirtualKeyExW(vk, MapVkToVsc, _layout);
        // flags 4: キーボードの状態 (デッドキー) を変えない (Windows 10 1607 以降)。
        int n = ToUnicodeEx(vk, scan, state, buffer, buffer.Capacity, 4, _layout);
        return n switch
        {
            > 0 => buffer.ToString(0, n),
            < 0 when buffer.Length > 0 => buffer.ToString(0, 1),
            _ => null,
        };
    }

    private static void EnsureLayout()
    {
        if (_layout == 0)
        {
            _layout = GetKeyboardLayout(0);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyExW(uint code, uint mapType, nint layout);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameTextW(int lParam, StringBuilder buffer, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, StringBuilder buffer, int size, uint flags, nint layout);
}
