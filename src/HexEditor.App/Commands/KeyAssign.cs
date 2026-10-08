using HexEditor.App.Services;
using HexEditor.Core.Commands;

namespace HexEditor.App.Commands;

/// <summary>キーを割り当てようとした結果 (UI-18 の仕様 6、7)。</summary>
public sealed record KeyAssignResult(bool Added, string? Rejection, IReadOnlyList<EffectiveBinding> Conflicts, IReadOnlyList<string> Warnings)
{
    public bool NeedsReplaceConfirmation => !Added && Rejection is null && Conflicts.Count > 0;
}

/// <summary>キーの割り当ての確定 (設定画面の「編集」と、テスト用の命令の両方から使う)。</summary>
public static class KeyAssign
{
    /// <summary>
    /// 割り当てる。割り当てられないキーは理由を返して拒否する。同じ有効範囲の重複は、<paramref name="replace"/> でなければ
    /// 重複の一覧を返して確定しない (呼び出し側が「置き換える」「キャンセル」を選ばせる)。
    /// </summary>
    public static KeyAssignResult TryAssign(string command, KeyChord chord, KeyScope scope, bool replace)
    {
        KeyRejection rejection = KeyValidation.Check(chord);
        if (rejection != KeyRejection.None)
        {
            return new KeyAssignResult(false, Loc.Get("KeyReject_" + rejection), [], []);
        }

        var binding = new KeyBinding(chord, scope);
        if (CommandService.Keys.BindingsFor(command).Any(b => b.Binding == binding))
        {
            return new KeyAssignResult(false, Loc.Get("KeyReject_Already"), [], []);
        }

        var conflicts = CommandService.Keys.FindConflicts(command, binding);
        if (conflicts.Count > 0 && !replace)
        {
            return new KeyAssignResult(false, null, conflicts, []);
        }

        var warnings = Warnings(command, binding);
        bool added = CommandService.Keys.Add(command, binding, replace);
        return new KeyAssignResult(added, added ? null : Loc.Get("KeyReject_Unknown"), conflicts, warnings);
    }

    /// <summary>確定はできるが知らせること: グローバルの割り当てを隠す、配列で押せない、AltGr の文字入力と重なる。</summary>
    public static IReadOnlyList<string> Warnings(string command, KeyBinding binding)
    {
        var warnings = new List<string>();
        foreach (EffectiveBinding hidden in CommandService.Keys.FindShadowed(command, binding))
        {
            warnings.Add(Loc.Format("Keys_ShadowWarning", CommandService.ScopeName(binding.Scope), CommandService.DisplayName(hidden.Command)));
        }

        warnings.AddRange(StateWarnings(binding.Chord));
        return warnings;
    }

    /// <summary>今の配列で押せない (UI-20 の仕様 3)、AltGr の文字入力と重なる (UI-20 の仕様 4)。</summary>
    public static IEnumerable<string> StateWarnings(KeyChord chord)
    {
        if (KeyboardLayout.IsUnavailable(chord))
        {
            yield return Loc.Get("Keys_UnavailableOnLayout");
        }

        if (chord.Strokes.Any(KeyboardLayout.ProducesAltGrCharacter))
        {
            yield return Loc.Get("Keys_AltGrOverlap");
        }
    }
}
