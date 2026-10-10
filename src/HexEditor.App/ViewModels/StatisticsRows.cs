using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Statistics;

namespace HexEditor.App.ViewModels;

// 統計パネル (ANA-10〜ANA-16) の表の行。数値は地域設定に従い、16 進の値は地域設定に依存しない (06 の 0.4)。

/// <summary>ヒストグラムの表の 1 行 (ANA-10 の仕様 6)。</summary>
public sealed class HistogramRowViewModel(int bin, string value, string character, long count, double percent, double cumulative)
{
    public int Bin { get; } = bin;

    public string Value { get; } = value;

    /// <summary>u8 の文字の列 (現在の文字コードでの表示)。u8 以外は空。</summary>
    public string Character { get; } = character;

    public long Count { get; } = count;

    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);

    public double Percent { get; } = percent;

    public string PercentText => Percent.ToString("N2", CultureInfo.CurrentCulture) + "%";

    public string CumulativeText => cumulative.ToString("N2", CultureInfo.CurrentCulture) + "%";

    public IEnumerable<string> Cells(bool withCharacter) =>
        withCharacter ? [Value, Character, CountText, PercentText, CumulativeText] : [Value, CountText, PercentText, CumulativeText];

    /// <summary>スクリーンリーダーの読み上げ (一覧の項目の ToString)。</summary>
    public override string ToString() => Loc.Format("Stats_HistogramRowName", Value, CountText, PercentText);
}

/// <summary>名前と値の 2 列の行 (記述統計、エントロピー、分類ごとの割合)。値を求められないときは「—」とツールチップに理由。</summary>
public sealed class NameValueRowViewModel(string id, string name, string value, string? toolTip = null, string? description = null)
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public string Value { get; } = value;

    /// <summary>値の欄のツールチップ (求められない理由)。</summary>
    public string? ToolTip { get; } = toolTip;

    /// <summary>項目名のツールチップ (定義。ANA-11 の「画面」)。</summary>
    public string? Description { get; } = description;

    public string AutomationName => Loc.Format("Stats_NameValue", Name, Value);

    public override string ToString() => AutomationName;
}

/// <summary>エントロピーグラフの表の 1 行 (06 の 0.4 の「表で表示」)。</summary>
public sealed class EntropyBlockRowViewModel(int index, string offset, string blockSize, string entropy, string zero, string printable)
{
    public int Index { get; } = index;

    public string Offset { get; } = offset;

    public string BlockSize { get; } = blockSize;

    public string Entropy { get; } = entropy;

    public string Zero { get; } = zero;

    public string Printable { get; } = printable;

    public override string ToString() => Loc.Format("Stats_BlockRowName", Offset, Entropy);
}

/// <summary>ダイグラムの表の 1 行 (件数の多い組の上位 1,000 件。ANA-14 の仕様 6)。</summary>
public sealed class DigramRowViewModel(int first, int second, long count, double percent)
{
    public int First { get; } = first;

    public int Second { get; } = second;

    public string Pair => $"0x{First:X2} 0x{Second:X2}";

    public string CountText => count.ToString("N0", CultureInfo.CurrentCulture);

    public string PercentText => percent.ToString("N3", CultureInfo.CurrentCulture) + "%";

    public override string ToString() => Loc.Format("Stats_DigramRowName", Pair, CountText, PercentText);
}

/// <summary>よく現れるバイト列の 1 行 (ANA-15 の仕様 2)。</summary>
public sealed class NGramRowViewModel(int rank, NGramCount row, long positions)
{
    public NGramCount Row { get; } = row;

    public string Rank { get; } = rank.ToString("N0", CultureInfo.CurrentCulture);

    public string Hex => Convert.ToHexString(Row.Bytes).Chunk(2).Select(c => new string(c)).Aggregate((a, b) => a + " " + b);

    public string Text => new([.. Row.Bytes.Select(b => b is >= 0x20 and <= 0x7E ? (char)b : '.')]);

    public string CountText => Row.Count.ToString("N0", CultureInfo.CurrentCulture);

    public string PercentText => (positions == 0 ? 0 : Row.Count * 100.0 / positions).ToString("N3", CultureInfo.CurrentCulture) + "%";

    public string First => "0x" + Row.FirstOffset.ToString("X", CultureInfo.InvariantCulture);

    public override string ToString() => Loc.Format("Stats_NGramRowName", Rank, Hex, CountText);
}

/// <summary>繰り返しの周期の 1 行 (ANA-15 の仕様 3)。</summary>
public sealed class PeriodRowViewModel(int rank, PeriodCandidate candidate)
{
    public PeriodCandidate Candidate { get; } = candidate;

    public string Rank { get; } = rank.ToString("N0", CultureInfo.CurrentCulture);

    public string Period => Candidate.Period.ToString("N0", CultureInfo.CurrentCulture);

    public string Ratio => (Candidate.Ratio * 100).ToString("N2", CultureInfo.CurrentCulture) + "%";

    public override string ToString() => Loc.Format("Stats_PeriodRowName", Rank, Period, Ratio);
}

/// <summary>分類の区間・シグネチャの一覧の 1 行 (ANA-16 の仕様 3、4)。</summary>
public sealed class ClassRowViewModel(long offset, long length, string kind, string entropy, string confidence, SignatureHit? signature = null,
    DataClass? dataClass = null)
{
    public long OffsetValue { get; } = offset;

    public long LengthValue { get; } = length;

    public string Offset => "0x" + OffsetValue.ToString("X", CultureInfo.InvariantCulture);

    public string Length => LengthValue < 0 ? "—" : LengthValue.ToString("N0", CultureInfo.CurrentCulture);

    public string Kind { get; } = kind;

    public string Entropy { get; } = entropy;

    public string Confidence { get; } = confidence;

    /// <summary>シグネチャの行 (圧縮形式なら「ここから展開...」を出す)。区間の行では null。</summary>
    public SignatureHit? Signature { get; } = signature;

    public DataClass? Class { get; } = dataClass;

    public override string ToString() => Loc.Format("Stats_ClassRowName", Kind, Offset, Length);
}

/// <summary>分類の凡例の 1 項目 (分類名と模様。ANA-16 の「画面」)。</summary>
public sealed partial class LegendItemViewModel(DataClass dataClass, string name, string pattern, string share) : ObservableObject
{
    public DataClass Class { get; } = dataClass;

    public string Name { get; } = name;

    /// <summary>模様のリソース名 (描画の情報。TC-ANA-16-04)。</summary>
    public string Pattern { get; } = pattern;

    public string Share { get; } = share;

    public string AutomationName => Loc.Format("Stats_LegendName", Name, Share);

    public override string ToString() => AutomationName;
}
