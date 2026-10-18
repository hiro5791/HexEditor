using System.Text.Json;
using System.Text.Json.Serialization;

namespace HexEditor.Core.Calculator;

/// <summary>履歴の 1 件 (式と結果)。</summary>
public sealed record CalculatorHistoryEntry(
    [property: JsonPropertyName("e")] string Expression,
    [property: JsonPropertyName("r")] string Result);

/// <summary>
/// 電卓の履歴 (TOOL-22 の仕様 7): 式と結果を最大 1,000 件。新しいものが先頭。アプリの状態 (state.json) に JSON で保存し、
/// 再起動後も使える。
/// </summary>
public static class CalculatorHistory
{
    public const int Limit = 1000;

    /// <summary>1 件の式の長さの上限。</summary>
    public const int MaxExpressionLength = 1024;

    /// <summary>先頭に加えた一覧。空の式・長すぎる式、直前と同じ式と結果は加えない。上限を超えた古いものは捨てる。</summary>
    public static IReadOnlyList<CalculatorHistoryEntry> Push(IReadOnlyList<CalculatorHistoryEntry> history, string expression, string result)
    {
        string e = expression.Trim();
        if (e.Length == 0 || e.Length > MaxExpressionLength)
        {
            return history;
        }

        var entry = new CalculatorHistoryEntry(e, result);
        if (history.Count > 0 && history[0] == entry)
        {
            return history;
        }

        return [.. new[] { entry }.Concat(history).Take(Limit)];
    }

    /// <summary>保存した文字列 (JSON の配列) を読む。読めなければ空。</summary>
    public static IReadOnlyList<CalculatorHistoryEntry> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<CalculatorHistoryEntry[]>(stored)?
                .Where(h => !string.IsNullOrWhiteSpace(h.Expression) && h.Result is not null).Take(Limit).ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IReadOnlyList<CalculatorHistoryEntry> history) => JsonSerializer.Serialize(history);
}
