using System.Text.Json.Nodes;

namespace HexEditor.Core.Compare;

/// <summary>
/// 比較の方式とオプション (ANA-01 の仕様 3・4、ANA-02 の仕様 3・5、ANA-03 の仕様 4)。値の範囲外は <see cref="Validate"/> で拒否する
/// (ダイアログの入力欄で拒否する。ANA-03 の「エラー」)。
/// </summary>
public sealed record CompareOptions
{
    public const int MinWindow = 256;
    public const int MaxWindow = 16 * 1024 * 1024;
    public const int DefaultWindow = 64 * 1024;
    public const int MinMinMatch = 1;
    public const int MaxMinMatch = 4096;
    public const int DefaultMinMatch = 8;
    public const int MaxMergeGap = 65536;

    /// <summary>比較の単位として選べる値 (バイト)。</summary>
    public static readonly IReadOnlyList<int> Units = [1, 2, 4, 8];

    public CompareMethod Method { get; init; } = CompareMethod.Simple;

    /// <summary>比較の単位 (1 / 2 / 4 / 8 バイト)。単位内の 1 バイトでも異なれば単位全体を差分にする。</summary>
    public int Unit { get; init; } = 1;

    /// <summary>近い差分をまとめる: 間の一致がこのバイト数以下なら 1 つの差分にする (0〜65,536)。</summary>
    public int MergeGap { get; init; }

    /// <summary>再同期ウィンドウ W (256 バイト〜16 MB)。</summary>
    public int Window { get; init; } = DefaultWindow;

    /// <summary>最小一致長 M (1〜4,096 バイト)。これより短い一致は再同期点にしない。</summary>
    public int MinMatch { get; init; } = DefaultMinMatch;

    /// <summary>1 つのウィンドウにかけてよい時間 (ANA-03 の「巨大ファイル」: 5 秒で打ち切る)。テストで短くする。</summary>
    public TimeSpan WindowTimeLimit { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>値が範囲内か。範囲外ならその項目の名前 (unit、mergeGap、window、minMatch) を返す。</summary>
    public string? Validate()
    {
        if (!Units.Contains(Unit))
        {
            return "unit";
        }

        if (MergeGap is < 0 or > MaxMergeGap)
        {
            return "mergeGap";
        }

        if (Window is < MinWindow or > MaxWindow)
        {
            return "window";
        }

        return MinMatch is < MinMinMatch or > MaxMinMatch ? "minMatch" : null;
    }

    /// <summary>前回の指定を記憶するための JSON (state.json の compare.options。ANA-01 の仕様 4)。</summary>
    public JsonObject ToJson() => new()
    {
        ["method"] = Method == CompareMethod.InsertDelete ? "insertDelete" : "simple",
        ["unit"] = Unit,
        ["mergeGap"] = MergeGap,
        ["window"] = Window,
        ["minMatch"] = MinMatch,
    };

    /// <summary>記憶した指定を読む。読めない項目・範囲外の項目は既定値にする。</summary>
    public static CompareOptions FromJson(JsonNode? node)
    {
        var defaults = new CompareOptions();
        if (node is not JsonObject o)
        {
            return defaults;
        }

        int Int(string key, int fallback) => o[key] is JsonValue v && v.TryGetValue(out int i) ? i : fallback;
        var result = new CompareOptions
        {
            Method = o["method"] is JsonValue m && m.TryGetValue(out string? s) && s == "insertDelete" ? CompareMethod.InsertDelete : CompareMethod.Simple,
            Unit = Int("unit", defaults.Unit),
            MergeGap = Int("mergeGap", defaults.MergeGap),
            Window = Int("window", defaults.Window),
            MinMatch = Int("minMatch", defaults.MinMatch),
        };
        while (result.Validate() is { } bad)
        {
            result = bad switch
            {
                "unit" => result with { Unit = defaults.Unit },
                "mergeGap" => result with { MergeGap = defaults.MergeGap },
                "window" => result with { Window = defaults.Window },
                _ => result with { MinMatch = defaults.MinMatch },
            };
        }

        return result;
    }
}
