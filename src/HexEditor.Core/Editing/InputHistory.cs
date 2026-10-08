using System.Text.Json;

namespace HexEditor.Core.Editing;

/// <summary>
/// 入力欄の入力履歴 (EDIT-04 の仕様 10: 各欄は直近 20 件の入力を候補として出す)。新しいものを先頭に置き、同じ入力は 1 つにまとめる。
/// アプリの状態 (state.json) に JSON の配列で保存する。
/// </summary>
public static class InputHistory
{
    /// <summary>1 つの欄に残す件数。</summary>
    public const int Limit = 20;

    /// <summary>1 件の長さの上限 (これより長い入力は残さない)。</summary>
    public const int MaxEntryLength = 1024;

    /// <summary><paramref name="value"/> を先頭に加えた一覧 (空白だけの入力・長すぎる入力は加えない)。</summary>
    public static IReadOnlyList<string> Push(IReadOnlyList<string> history, string? value, int limit = Limit)
    {
        string v = (value ?? string.Empty).Trim();
        if (v.Length == 0 || v.Length > MaxEntryLength || limit <= 0)
        {
            return history;
        }

        return [.. new[] { v }.Concat(history.Where(h => !string.Equals(h, v, StringComparison.Ordinal))).Take(limit)];
    }

    /// <summary>保存した文字列 (JSON の配列) を読む。読めなければ空。</summary>
    public static IReadOnlyList<string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(stored)?.Where(s => !string.IsNullOrWhiteSpace(s)).Take(Limit).ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>保存する文字列 (JSON の配列)。</summary>
    public static string Serialize(IReadOnlyList<string> history) => JsonSerializer.Serialize(history);
}
