namespace HexEditor.Core.Commands;

/// <summary>
/// ショートカットの有効範囲 (00-overview.md 8.1、UI-18 の仕様 5)。狭い有効範囲 (グローバル以外) の割り当てが、
/// グローバルの同じキーより優先する。
/// </summary>
public enum KeyScope
{
    Global,
    Editor,
    FindBar,
    CodeEditor,
    Panel,
}

public static class KeyScopes
{
    public static readonly KeyScope[] All = [KeyScope.Global, KeyScope.Editor, KeyScope.FindBar, KeyScope.CodeEditor, KeyScope.Panel];

    /// <summary>keybindings.json の <c>when</c> の値。</summary>
    public static string Name(KeyScope scope) => scope switch
    {
        KeyScope.Editor => "editor",
        KeyScope.FindBar => "findBar",
        KeyScope.CodeEditor => "codeEditor",
        KeyScope.Panel => "panel",
        _ => "global",
    };

    /// <summary><c>when</c> を読む。省略 (null・空) はグローバル (UI-18 の仕様 9)。</summary>
    public static bool TryParse(string? text, out KeyScope scope)
    {
        scope = KeyScope.Global;
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        foreach (KeyScope s in All)
        {
            if (string.Equals(Name(s), text, StringComparison.OrdinalIgnoreCase))
            {
                scope = s;
                return true;
            }
        }

        return false;
    }
}

/// <summary>キーの割り当て 1 つ: どのキーを、どの有効範囲で。</summary>
public sealed record KeyBinding(KeyChord Chord, KeyScope Scope = KeyScope.Global)
{
    public static KeyBinding Parse(string chord, KeyScope scope = KeyScope.Global) => new(KeyChord.Parse(chord), scope);

    public override string ToString() => Scope == KeyScope.Global ? Chord.ToString() : $"{Chord} ({KeyScopes.Name(Scope)})";
}

/// <summary>割り当てられないキーの理由 (UI-18 の仕様 7)。</summary>
public enum KeyRejection
{
    None,

    /// <summary>修飾キーだけ。</summary>
    ModifierOnly,

    /// <summary>Alt+F4 (ウィンドウを閉じる)。</summary>
    AltF4,

    /// <summary>Windows キーとの組み合わせ (OS が使う)。</summary>
    WindowsKey,

    /// <summary>Ctrl+Alt+Delete。</summary>
    CtrlAltDelete,

    /// <summary>修飾キーなし (または Shift だけ) の文字キー・数字キー (入力に使う)。</summary>
    TypingKey,
}

public static class KeyValidation
{
    /// <summary>
    /// 割り当てられるかを調べる。2 打鍵目は前の打鍵の続きなので、修飾キーなしの文字キーも使える
    /// (例: <c>Ctrl+K S</c>)。ファンクションキー・Insert・Delete などは修飾キーなしでも使える。
    /// </summary>
    public static KeyRejection Check(KeyChord chord)
    {
        for (int i = 0; i < chord.Strokes.Count; i++)
        {
            KeyStroke s = chord.Strokes[i];
            if (s.IsModifierOnly)
            {
                return KeyRejection.ModifierOnly;
            }

            if (s.Modifiers.HasFlag(KeyModifiers.Win))
            {
                return KeyRejection.WindowsKey;
            }

            if (s.Key == VirtualKeys.F4 && s.Modifiers == KeyModifiers.Alt)
            {
                return KeyRejection.AltF4;
            }

            if (s.Key == VirtualKeys.Delete && s.Modifiers.HasFlag(KeyModifiers.Ctrl) && s.Modifiers.HasFlag(KeyModifiers.Alt))
            {
                return KeyRejection.CtrlAltDelete;
            }

            if (i == 0 && (s.Modifiers & ~KeyModifiers.Shift) == KeyModifiers.None && VirtualKeys.IsTypingKey(s.Key))
            {
                return KeyRejection.TypingKey;
            }
        }

        return KeyRejection.None;
    }
}
