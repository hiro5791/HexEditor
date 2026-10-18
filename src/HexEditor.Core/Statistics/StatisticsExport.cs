using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Core.Statistics;

/// <summary>ヒストグラムの表の 1 行 (ANA-10 の仕様 6)。</summary>
public sealed record HistogramRow(int Bin, double Low, double High, long Count, double Percent, double Cumulative)
{
    /// <summary>値ごとのビン (値が 1 つ)。</summary>
    public bool IsValue => Low == High;
}

/// <summary>統計の結果の書き出し (ANA-10 の仕様 10、ANA-13 の仕様 9)。数値は地域設定に依存しない形で書く。</summary>
public static class StatisticsExport
{
    /// <summary>表の行 (ビンの順)。割合の分母は範囲外を含む要素数。</summary>
    public static IReadOnlyList<HistogramRow> Rows(Histogram h)
    {
        long total = h.Total;
        var rows = new List<HistogramRow>(h.BinCount);
        double cumulative = h.Below;
        for (int i = 0; i < h.BinCount; i++)
        {
            long c = h.Counts[i];
            cumulative += c;
            rows.Add(new HistogramRow(i, h.BinLow(i), h.BinHigh(i), c, Percent(c, total), total == 0 ? 0 : cumulative * 100 / total));
        }

        return rows;
    }

    public static double Percent(long count, long total) => total == 0 ? 0 : count * 100.0 / total;

    /// <summary>値の表記 (整数の値は 10 進、小数は有効数字 10 桁)。地域設定に依存しない。</summary>
    public static string Value(double v) =>
        v == Math.Floor(v) && Math.Abs(v) < 1e18 ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("G10", CultureInfo.InvariantCulture);

    /// <summary>
    /// 表の CSV。変換ビュー (EDIT-45 の仕様 11、06 の 0.1.1) のデータを数えた結果は、列 <c>transformView</c> に <paramref name="transformView"/>
    /// (変換の表示。例: <c>XOR 5A</c>) を記録する。
    /// </summary>
    public static string Csv(StatisticsResult r, string? transformView = null)
    {
        bool transformed = transformView is { Length: > 0 };
        string extra = transformed
            ? "," + (transformView!.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + transformView.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : transformView)
            : string.Empty;
        var sb = new StringBuilder();
        sb.Append(transformed ? "low,high,count,percent,cumulative,transformView\r\n" : "low,high,count,percent,cumulative\r\n");
        foreach (HistogramRow row in Rows(r.Histogram))
        {
            sb.Append(Value(row.Low)).Append(',').Append(Value(row.High)).Append(',')
                .Append(row.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.Percent.ToString("0.######", CultureInfo.InvariantCulture)).Append(',')
                .Append(row.Cumulative.ToString("0.######", CultureInfo.InvariantCulture)).Append(extra).Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>結果の JSON。変換ビューのデータを数えた結果は <c>transformView</c> に <paramref name="transformView"/> を記録する。</summary>
    public static string Json(StatisticsResult r, string? transformView = null)
    {
        Histogram h = r.Histogram;
        var bins = new JsonArray();
        foreach (HistogramRow row in Rows(h))
        {
            bins.Add(new JsonObject
            {
                ["low"] = row.Low,
                ["high"] = row.High,
                ["count"] = row.Count,
                ["percent"] = row.Percent,
                ["cumulative"] = row.Cumulative,
            });
        }

        var root = new JsonObject
        {
            ["type"] = ElementTypes.Name(r.Element.Type),
            ["bigEndian"] = r.Element.BigEndian,
            ["stride"] = r.Element.EffectiveStride,
            ["ranges"] = new JsonArray([.. r.Ranges.Ranges.Select(x => (JsonNode?)new JsonObject { ["offset"] = x.Offset, ["length"] = x.Length })]),
            ["elements"] = r.Elements,
            ["remainder"] = r.Remainder,
            ["below"] = h.Below,
            ["above"] = h.Above,
            ["nan"] = h.NaN,
            ["positiveInfinity"] = h.PositiveInfinity,
            ["negativeInfinity"] = h.NegativeInfinity,
            ["entropy"] = r.Entropy.Bits,
            ["completed"] = r.Completed,
            ["bins"] = bins,
        };
        if (transformView is { Length: > 0 })
        {
            root["transformView"] = transformView;
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>表をタブ区切りでコピーする形 (見出しを含む)。<paramref name="cells"/> は表示中の文字列。</summary>
    public static string Tsv(IEnumerable<string> header, IEnumerable<IEnumerable<string>> cells)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join('\t', header)).Append("\r\n");
        foreach (IEnumerable<string> row in cells)
        {
            sb.Append(string.Join('\t', row)).Append("\r\n");
        }

        return sb.ToString();
    }
}
