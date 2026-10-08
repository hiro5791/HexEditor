using HexEditor.Core.Commands;

namespace HexEditor.App.Commands;

/// <summary>キーを押した場所 (UI-18 の仕様 5 の有効範囲と、文字を入力できる場所か。UI-20 の仕様 4)。</summary>
public readonly record struct KeyContext(KeyScope Scope, bool TextInput);

/// <summary>振り分けの結果。</summary>
public readonly record struct DispatchResult(bool Handled, string? Command = null)
{
    public static readonly DispatchResult Pass = new(false);
}

/// <summary>
/// ウィンドウのキーの振り分け (UI-16、UI-18、UI-20): 押したキーを今の割り当てでコマンドにし、<see cref="CommandHost"/> で実行する。
/// ウィンドウのルートの PreviewKeyDown (フォーカスのある部品より先) から呼ぶ。
/// </summary>
/// <remarks>
/// - 狭い有効範囲 (エディタ・検索バー・パネルなど) の割り当てがグローバルより優先する (00-overview.md 8.1)。
/// - 2 打鍵の連続キーは 1 打鍵目を覚えて次の打鍵を待つ。
/// - 文字を入力できる場所では、文字の入力・編集のキーと、AltGr (Ctrl+Alt) で文字を生むキーは部品に任せる (UI-20 の仕様 4)。
/// - IME の変換中のキー (VK_PROCESSKEY) は扱わない (UI-20 の仕様 5)。
/// - 既定の割り当てを部品自身が処理するコマンド (<see cref="CommandDefinition.NativeScopes"/>) は、その部品に任せる。
///   利用者がその既定のキーを外した場合は、キーを部品に渡さずに捨てる。
/// </remarks>
public sealed class KeyDispatcher(CommandHost host)
{
    private const int ProcessKey = 229, Digit0 = 0x30, NumPad0 = 0x60, NumPad1 = 0x61, NumPad9 = 0x69;
    private readonly List<KeyStroke> _pending = [];

    /// <summary>連続キーの 1 打鍵目を待っているか。</summary>
    public bool IsPending => _pending.Count > 0;

    public void Reset() => _pending.Clear();

    public DispatchResult Dispatch(int key, KeyModifiers modifiers, KeyContext context)
    {
        if (key == ProcessKey || VirtualKeys.IsModifier(key))
        {
            return DispatchResult.Pass;
        }

        var stroke = new KeyStroke(modifiers, key);
        // Hex ビューのテキスト列は AltGr の文字入力だけを優先する (Ctrl+Z などはコマンド)。入力欄は編集のキーも入力欄に任せる。
        if (context.TextInput && _pending.Count == 0
            && (KeyboardLayout.ProducesAltGrCharacter(stroke) || (context.Scope != KeyScope.Editor && IsTextEditingKey(stroke))))
        {
            return DispatchResult.Pass;
        }

        KeyScope[] scopes = context.Scope == KeyScope.Global ? [KeyScope.Global] : [context.Scope, KeyScope.Global];
        var pressed = new List<KeyStroke>(_pending) { stroke };
        KeyResolution resolution = CommandService.Keys.Resolve(pressed, scopes);
        switch (resolution.Kind)
        {
            case KeyMatchKind.Prefix:
                _pending.Clear();
                _pending.Add(stroke);
                return new DispatchResult(true);
            case KeyMatchKind.Command:
                _pending.Clear();
                EffectiveBinding binding = resolution.Binding!;
                if (CommandService.Catalog.Find(binding.Command) is { } def && IsNative(def, binding.Binding, context.Scope))
                {
                    return DispatchResult.Pass;
                }

                _ = host.ExecuteAsync(binding.Command);
                return new DispatchResult(true, binding.Command);
            default:
                if (_pending.Count == 0 && key is >= NumPad1 and <= NumPad9 && modifiers != 0)
                {
                    // テンキーの数字は、割り当てがなければ数字キーの段と同じに扱う (番号付きブックマーク。00-overview.md 8.7)。
                    // 修飾キーなしのテンキーは数字の入力なので、置き換えない。
                    return Dispatch(Digit0 + (key - NumPad0), modifiers, context);
                }

                if (_pending.Count > 0)
                {
                    // 連続キーの 2 打鍵目が割り当てにない: 捨てる。
                    _pending.Clear();
                    return new DispatchResult(true);
                }

                return IsRemovedNativeDefault(stroke, context.Scope) ? new DispatchResult(true) : DispatchResult.Pass;
        }
    }

    /// <summary>既定のキーのまま、その有効範囲の部品自身が処理するか。</summary>
    private static bool IsNative(CommandDefinition def, KeyBinding binding, KeyScope scope) =>
        def.NativeScopes.Contains(scope) && def.DefaultBindings.Contains(binding);

    /// <summary>部品自身が処理する既定のキーを、利用者が外したか (外したキーは部品に渡さない)。</summary>
    private static bool IsRemovedNativeDefault(KeyStroke stroke, KeyScope scope)
    {
        foreach (CommandDefinition def in CommandService.Catalog.All)
        {
            if (!def.NativeScopes.Contains(scope))
            {
                continue;
            }

            foreach (KeyBinding b in def.DefaultBindings)
            {
                if (b.Chord.Strokes.Count == 1 && b.Chord.First == stroke
                    && !CommandService.Keys.BindingsFor(def.Id).Any(e => e.Binding == b))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>入力欄が自分で処理する文字の入力・編集のキー (入力欄では割り当てより優先する)。</summary>
    private static bool IsTextEditingKey(KeyStroke s)
    {
        KeyModifiers m = s.Modifiers & ~KeyModifiers.Shift;
        if (m == KeyModifiers.None)
        {
            return !VirtualKeys.IsFunctionKey(s.Key) && s.Key != VirtualKeys.Escape;
        }

        return m == KeyModifiers.Ctrl && s.Key is 'A' or 'C' or 'V' or 'X' or 'Z' or 'Y' or VirtualKeys.Insert or VirtualKeys.Left
            or VirtualKeys.Right or VirtualKeys.Home or VirtualKeys.End or VirtualKeys.Back or VirtualKeys.Delete;
    }
}
