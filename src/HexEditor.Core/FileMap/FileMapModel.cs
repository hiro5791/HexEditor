using System.Globalization;
using HexEditor.Core.Engine;
using HexEditor.Core.Statistics;
using HexEditor.Core.Templates;

namespace HexEditor.Core.FileMap;

/// <summary>ファイルマップの領域の種類 (ANA-33 の仕様 3。JSON の <c>kind</c> の語は <see cref="FileMapWords"/>)。</summary>
public enum FileMapKind : byte
{
    /// <summary>形式の構造 (先頭の形式のテンプレートの最上位のパターンと、属性 <c>[[bookmark]]</c> の付いたパターン)。</summary>
    Structure,

    /// <summary>埋め込まれたファイル。</summary>
    Embedded,

    Compressed,
    Encrypted,
    Text,

    /// <summary>ゼロ (定数のうち 0x00)。</summary>
    Zero,

    /// <summary>定数 (埋め草。0x00 以外)。</summary>
    Constant,

    /// <summary>繰り返しのレコード。</summary>
    Records,

    Binary,
    Unreadable,
}

/// <summary>帯のレーン (ANA-34 の仕様 2)。</summary>
public enum FileMapLane : byte
{
    Structure,
    Content,
}

/// <summary>どの段階の結果か (ANA-33 の仕様 2・4)。</summary>
public enum FileMapStage : byte
{
    /// <summary>概観 (標本による推定)。</summary>
    Sample,

    /// <summary>全体の走査。</summary>
    Scan,

    /// <summary>構造の解析。</summary>
    Structure,
}

/// <summary>段階の状態。</summary>
public enum FileMapStageState : byte
{
    NotStarted,
    Running,
    Done,

    /// <summary>キャンセルした (済んだ範囲の結果は残る)。</summary>
    Cancelled,
}

/// <summary>
/// 領域 1 つ (ANA-33 の仕様 4)。メモリを抑えるため値型で持ち (領域が 100 万個でも 40 MB)、ラベルと根拠の文は表示するときに作る
/// (<see cref="FileMapResult.LabelOf"/>、<see cref="FileMapResult.EvidenceOf"/>)。
/// </summary>
/// <param name="Parent">入れ子の親 (<see cref="FileMapResult.Regions"/> の番号。なければ -1)。</param>
/// <param name="Detail">詳細 (<see cref="FileMapResult.Details"/> の番号。統計の分類だけで決まった領域は -1)。</param>
/// <param name="Entropy">平均のエントロピー (ビット / バイト。求めていなければ NaN)。</param>
/// <param name="PValue">カイ二乗の p 値の平均 (求めていなければ NaN)。</param>
/// <param name="Extra">種類ごとの補足: 定数のバイトの値、テキストの文字コード (<see cref="FileMapTextEncoding"/>)、レコード長、ブロックの数。</param>
public readonly record struct FileMapRegion(long Offset, long Length, FileMapKind Kind, Confidence Confidence, FileMapStage Stage, int Parent, int Detail,
    float Entropy, float PValue, int Extra)
{
    public long End => Offset + Length;

    /// <summary>最後のバイトの位置 (このバイトを含む)。</summary>
    public long Last => Offset + Length - 1;

    public FileMapLane Lane => Kind is FileMapKind.Structure or FileMapKind.Embedded ? FileMapLane.Structure : FileMapLane.Content;

    public bool Contains(long offset) => offset >= Offset && offset < End;
}

/// <summary>テキストの領域の文字コード (ラベルに付ける。ANA-33 の仕様 3)。</summary>
public enum FileMapTextEncoding
{
    Ascii,
    Utf8,
    Utf16LE,
    Utf16BE,
}

/// <summary>
/// 画面の文 1 つ (リソースのキーと引数)。文は文単位のリソースにし、数値は地域設定で書式化する (ANA-33 の仕様 4、00 の 5.4)。
/// <see cref="Key"/> が空なら <see cref="Args"/>[0] をそのまま使う (テンプレートのパスなど、訳さない文字列)。
/// </summary>
public sealed record FileMapMessage(string Key, params object[] Args)
{
    public static FileMapMessage Raw(string text) => new(string.Empty, text);
}

/// <summary>文の引数: 16 進のオフセット (地域設定によらない <c>0x50000</c> の形)。</summary>
public readonly record struct FileMapHex(long Value);

/// <summary>文の引数: 割合 (0〜1。地域設定の百分率)。</summary>
public readonly record struct FileMapPercent(double Value);

/// <summary>文の引数: 小数 (<see cref="Digits"/> 桁)。</summary>
public readonly record struct FileMapDecimal(double Value, int Digits);

/// <summary>文の引数: 表示名 (テンプレートの <c>name.&lt;言語タグ&gt;</c> (TPL-17) を表示言語で選ぶ。ANA-34 の仕様 11)。</summary>
public sealed record FileMapName(string Fallback, TemplateCatalogEntry? Template = null);

/// <summary>統計の分類だけでは決まらない領域 (構造・埋め込まれたファイル・確かめた圧縮のストリーム) の詳細。</summary>
public sealed class FileMapDetail
{
    /// <summary>ラベル (パターンのパス、形式名など)。</summary>
    public required FileMapMessage Label { get; init; }

    /// <summary>根拠の文 (1 つ以上。数値を含む)。</summary>
    public required IReadOnlyList<FileMapMessage> Evidence { get; init; }

    /// <summary>使った (または決まった) テンプレート (「テンプレート "…" を適用」。ANA-34 の仕様 6)。</summary>
    public TemplateCatalogEntry? Template { get; init; }

    /// <summary>テンプレートで解析できたか (できなかったら、長さは確度の低い方法で決めた)。</summary>
    public bool TemplateParsed { get; init; }

    /// <summary>形式名 (シグネチャデータベース・カービングの定義の名前、圧縮形式の名前)。</summary>
    public string? Format { get; init; }

    /// <summary>MIME (分かれば)。</summary>
    public string? Mime { get; init; }

    /// <summary>拡張子 (「ファイルとして保存...」の既定。分かれば)。</summary>
    public string? Extension { get; init; }

    /// <summary>入れ子の深さ (0 は最上位)。</summary>
    public int Depth { get; init; }
}

/// <summary>段階ごとの状態 (JSON の <c>stages</c>)。</summary>
public sealed record FileMapStages(FileMapStageState Sample, FileMapStageState Scan, FileMapStageState Structure, double ScanFraction)
{
    public static FileMapStages Initial { get; } = new(FileMapStageState.NotStarted, FileMapStageState.NotStarted, FileMapStageState.NotStarted, 0);

    public bool Complete => Sample == FileMapStageState.Done && Scan == FileMapStageState.Done && Structure == FileMapStageState.Done;

    public bool Cancelled => Sample == FileMapStageState.Cancelled || Scan == FileMapStageState.Cancelled || Structure == FileMapStageState.Cancelled;

    /// <summary>実行中の段階 (なければ null)。</summary>
    public FileMapStage? Running => Sample == FileMapStageState.Running ? FileMapStage.Sample
        : Scan == FileMapStageState.Running ? FileMapStage.Scan
        : Structure == FileMapStageState.Running ? FileMapStage.Structure : null;
}

/// <summary>表示言語によらない英語の語 (JSON・CSV の <c>kind</c>・<c>lane</c>・<c>confidence</c>・<c>stage</c>。ANA-34 の仕様 9)。</summary>
public static class FileMapWords
{
    public static string Kind(FileMapKind kind) => kind switch
    {
        FileMapKind.Structure => "structure",
        FileMapKind.Embedded => "embedded",
        FileMapKind.Compressed => "compressed",
        FileMapKind.Encrypted => "encrypted",
        FileMapKind.Text => "text",
        FileMapKind.Zero => "zero",
        FileMapKind.Constant => "constant",
        FileMapKind.Records => "records",
        FileMapKind.Binary => "binary",
        _ => "unreadable",
    };

    public static string Lane(FileMapLane lane) => lane == FileMapLane.Structure ? "structure" : "content";

    public static string Confidence(Confidence c) => c switch
    {
        Statistics.Confidence.High => "high",
        Statistics.Confidence.Medium => "medium",
        _ => "low",
    };

    public static string Stage(FileMapStage stage) => stage switch
    {
        FileMapStage.Sample => "sample",
        FileMapStage.Scan => "scan",
        _ => "structure",
    };

    public static string StageState(FileMapStageState state) => state switch
    {
        FileMapStageState.NotStarted => "notStarted",
        FileMapStageState.Running => "running",
        FileMapStageState.Done => "done",
        _ => "cancelled",
    };
}

/// <summary>
/// ファイルマップ (ANA-33)。どの段階の途中の結果も同じ形で表す (各段階の結果は、できたところから画面に出す。仕様 2)。不変。
/// 領域はオフセットの順 (同じオフセットでは構造のレーン、親の順)。内容のレーンの領域は重ならず、対象の全体を覆う。
/// </summary>
public sealed class FileMapResult
{
    private readonly int[] _content;
    private readonly int[] _structure;
    private readonly long[] _bytes;

    internal FileMapResult(DocumentSnapshot snapshot, long start, long length, int blockSize, FileMapRegion[] regions, IReadOnlyList<FileMapDetail> details,
        FileMapStages stages, long[] legendBytes)
    {
        Snapshot = snapshot;
        Start = start;
        Length = length;
        BlockSize = blockSize;
        Regions = regions;
        Details = details;
        Stages = stages;
        _bytes = legendBytes;
        var content = new List<int>(regions.Length);
        var structure = new List<int>();
        for (int i = 0; i < regions.Length; i++)
        {
            (regions[i].Lane == FileMapLane.Content ? content : structure).Add(i);
        }

        _content = [.. content];
        _structure = [.. structure];
    }

    /// <summary>作った時点のデータ (0.2: 開始時点のルート)。</summary>
    public DocumentSnapshot Snapshot { get; }

    /// <summary>対象の先頭 (ドキュメント上のオフセット)。</summary>
    public long Start { get; }

    public long Length { get; }

    public long End => Start + Length;

    /// <summary>全体の走査のブロックの大きさ (4 KiB。64 GiB を超える対象では、ブロックの数が 2^24 以下になる最小の 2 の累乗)。</summary>
    public int BlockSize { get; }

    /// <summary>概観の区間の長さ (「対象の長さ / 4,096」以上で最小の 2 の累乗。最小 4 KiB)。</summary>
    public long SampleInterval { get; init; }

    public IReadOnlyList<FileMapRegion> Regions { get; }

    public IReadOnlyList<FileMapDetail> Details { get; }

    public FileMapStages Stages { get; }

    public bool Complete => Stages.Complete;

    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>全体の走査が済んだ範囲の終わり (ドキュメント上のオフセット)。これより後ろは標本による推定。</summary>
    public long ScannedEnd { get; init; }

    /// <summary>全体の走査の分類 (ANA-16 の結果として共有する。仕様 5)。走査していなければ null。</summary>
    public ClassificationResult? Classification { get; init; }

    /// <summary>先頭のファイル形式の判定 (ANA-17 の 4) の形式名 (分からなければ null)。</summary>
    public string? FileTypeName { get; init; }

    /// <summary>上限を超えて持たなかった領域の数 (「ほか n 件」)。</summary>
    public long OmittedRegions { get; init; }

    /// <summary>上限を超えて持たなかった埋め込まれた形式の候補の数 (「ほか n 件」)。</summary>
    public long OmittedCandidates { get; init; }

    /// <summary>テンプレートで評価した埋め込まれた形式の数 (上限 256 個)。</summary>
    public int EvaluatedEmbedded { get; init; }

    /// <summary>変換ビューの説明 (変換ビューのデータに対して作ったとき。「変換ビュー: XOR 0x5A」。0.1.1)。App が入れる。</summary>
    public string? TransformDescription { get; init; }

    /// <summary>内容のレーンの領域の番号 (オフセットの順)。</summary>
    public IReadOnlyList<int> ContentLane => _content;

    /// <summary>構造のレーンの領域の番号 (オフセットの順、親が先)。</summary>
    public IReadOnlyList<int> StructureLane => _structure;

    public IReadOnlyList<int> Lane(FileMapLane lane) => lane == FileMapLane.Content ? _content : _structure;

    /// <summary>種類ごとのバイト数 (凡例。内容のレーンは対象の長さの合計になる。構造のレーンは最上位の領域の長さの合計)。</summary>
    public long BytesOf(FileMapKind kind) => _bytes[(int)kind];

    /// <summary>テンプレートの決まった埋め込まれたファイル (一覧の順。「見つかったファイルにテンプレートを適用」。ANA-34 の仕様 7)。</summary>
    public IEnumerable<int> EmbeddedWithTemplate =>
        _structure.Where(i => Regions[i].Kind == FileMapKind.Embedded && Regions[i].Detail >= 0 && Details[Regions[i].Detail].Template is not null);

    /// <summary>内容のレーンで <paramref name="offset"/> を含む領域 (Regions の番号)。なければ -1。</summary>
    public int ContentAt(long offset)
    {
        int lo = 0;
        int hi = _content.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            FileMapRegion r = Regions[_content[mid]];
            if (offset < r.Offset)
            {
                hi = mid - 1;
            }
            else if (offset >= r.End)
            {
                lo = mid + 1;
            }
            else
            {
                return _content[mid];
            }
        }

        return -1;
    }

    /// <summary>構造のレーンで <paramref name="offset"/> を含む最も深い領域。なければ -1。</summary>
    public int StructureAt(long offset)
    {
        int best = -1;
        foreach (int i in StructureInRange(offset, offset + 1))
        {
            if (best < 0 || Details.ElementAtOrDefault(Regions[i].Detail)?.Depth > Details.ElementAtOrDefault(Regions[best].Detail)?.Depth)
            {
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// レーンの中で [<paramref name="from"/>, <paramref name="to"/>) に重なる領域 (レーンの中の位置の順)。内容のレーンは区間の索引 (二分探索) で探し、
    /// 領域が 100 万個でも表示中の範囲の分だけを返す (ANA-34 の「巨大ファイル・長時間処理」)。
    /// </summary>
    public IEnumerable<int> InRange(FileMapLane lane, long from, long to) => lane == FileMapLane.Content ? ContentInRange(from, to) : StructureInRange(from, to);

    private IEnumerable<int> ContentInRange(long from, long to)
    {
        int lo = 0;
        int hi = _content.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (Regions[_content[mid]].End <= from)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (int k = lo; k < _content.Length && Regions[_content[k]].Offset < to; k++)
        {
            yield return _content[k];
        }
    }

    private IEnumerable<int> StructureInRange(long from, long to)
    {
        // 構造のレーンの領域は多くない (埋め込まれた形式の評価は 256 個まで) ので、順に見る。
        foreach (int i in _structure)
        {
            FileMapRegion r = Regions[i];
            if (r.Offset >= to)
            {
                break;
            }

            if (r.End > from)
            {
                yield return i;
            }
        }
    }

    /// <summary>レーンの中の位置 (<see cref="Lane"/> の添字)。領域がそのレーンになければ -1。</summary>
    public int PositionInLane(int region)
    {
        if (region < 0 || region >= Regions.Count)
        {
            return -1;
        }

        int[] lane = Regions[region].Lane == FileMapLane.Content ? _content : _structure;
        int at = Array.BinarySearch(lane, region);
        return at >= 0 ? at : -1;
    }

    /// <summary>ラベル (ANA-33 の仕様 3 の表の「ラベル」)。</summary>
    public FileMapMessage LabelOf(int index)
    {
        FileMapRegion r = Regions[index];
        if (r.Detail >= 0)
        {
            return Details[r.Detail].Label;
        }

        return r.Kind switch
        {
            FileMapKind.Zero => new FileMapMessage("FileMap_Label_Zero"),
            FileMapKind.Constant when r.Stage != FileMapStage.Sample && r.Extra is >= 0 and <= 255 =>
                new FileMapMessage("FileMap_Label_Constant", "0x" + r.Extra.ToString("X2", CultureInfo.InvariantCulture)),
            FileMapKind.Text when r.Stage != FileMapStage.Sample && r.Extra >= 0 => new FileMapMessage("FileMap_Label_Text", ((FileMapTextEncoding)r.Extra) switch
            {
                FileMapTextEncoding.Utf8 => "UTF-8",
                FileMapTextEncoding.Utf16LE => "UTF-16LE",
                FileMapTextEncoding.Utf16BE => "UTF-16BE",
                _ => "ASCII",
            }),
            FileMapKind.Records when r.Extra > 0 => new FileMapMessage("FileMap_Label_Records", (long)r.Extra),
            _ => new FileMapMessage("FileMap_Kind_" + r.Kind),
        };
    }

    /// <summary>根拠の文 (数値を含む。ANA-33 の仕様 4)。</summary>
    public IReadOnlyList<FileMapMessage> EvidenceOf(int index)
    {
        FileMapRegion r = Regions[index];
        if (r.Detail >= 0)
        {
            return Details[r.Detail].Evidence;
        }

        var list = new List<FileMapMessage>(2);
        if (r.Stage == FileMapStage.Sample)
        {
            // Extra は区間の数。
            list.Add(new FileMapMessage("FileMap_Ev_Sample", (long)r.Extra, SampleInterval));
        }

        switch (r.Kind)
        {
            case FileMapKind.Zero or FileMapKind.Constant when r.Stage != FileMapStage.Sample && r.Extra is >= 0 and <= 255:
                list.Add(new FileMapMessage("FileMap_Ev_Constant", r.Length, "0x" + (r.Extra & 0xFF).ToString("X2", CultureInfo.InvariantCulture)));
                break;
            case FileMapKind.Unreadable:
                list.Add(new FileMapMessage("FileMap_Ev_Unreadable", r.Length));
                break;
            case FileMapKind.Encrypted when !float.IsNaN(r.PValue):
                list.Add(new FileMapMessage("FileMap_Ev_EntropyP", new FileMapDecimal(r.Entropy, 2), new FileMapDecimal(r.PValue, 2)));
                break;
            default:
                if (!float.IsNaN(r.Entropy))
                {
                    list.Add(new FileMapMessage("FileMap_Ev_Entropy", new FileMapDecimal(r.Entropy, 2)));
                }

                break;
        }

        if (r.Stage == FileMapStage.Scan && r.Kind is not (FileMapKind.Zero or FileMapKind.Constant or FileMapKind.Unreadable))
        {
            list.Add(new FileMapMessage("FileMap_Ev_Blocks", (r.Length + BlockSize - 1) / BlockSize, (long)BlockSize));
        }

        if (list.Count == 0)
        {
            list.Add(new FileMapMessage("FileMap_Ev_Range", new FileMapHex(r.Offset), r.Length));
        }

        return list;
    }

    /// <summary>入れ子の深さ (0 は最上位)。</summary>
    public int DepthOf(int index) => Regions[index].Parent < 0 ? 0 : 1 + DepthOf(Regions[index].Parent);
}

/// <summary>文を表示の文字列にする (リソースの文と地域設定の数値の書式。ANA-33 の仕様 4)。</summary>
public static class FileMapText
{
    /// <summary>
    /// <paramref name="lookup"/> はリソースのキーから文 (ICU MessageFormat) を返す。<paramref name="culture"/> は数値の書式、
    /// <paramref name="uiCulture"/> は表示言語 (複数形の規則とテンプレートの表示名)。
    /// </summary>
    public static string Format(FileMapMessage message, Func<string, string> lookup, CultureInfo culture, CultureInfo uiCulture)
    {
        object?[] args = [.. message.Args.Select(a => Arg(a, lookup, culture, uiCulture))];
        if (message.Key.Length == 0)
        {
            return args.Length > 0 ? args[0]?.ToString() ?? string.Empty : string.Empty;
        }

        return Text.MessageFormat.Format(lookup(message.Key), culture, uiCulture.Name, args);
    }

    /// <summary>複数の文を空白でつなぐ (日本語でも文の間は句点で区切られている)。</summary>
    public static string Join(IEnumerable<FileMapMessage> messages, Func<string, string> lookup, CultureInfo culture, CultureInfo uiCulture) =>
        string.Join(" ", messages.Select(m => Format(m, lookup, culture, uiCulture)));

    private static object? Arg(object a, Func<string, string> lookup, CultureInfo culture, CultureInfo uiCulture) => a switch
    {
        FileMapHex h => "0x" + h.Value.ToString("X", CultureInfo.InvariantCulture),
        FileMapPercent p => p.Value.ToString("P0", culture),
        FileMapDecimal d => d.Value.ToString("N" + d.Digits.ToString(CultureInfo.InvariantCulture), culture),
        FileMapName n => n.Template?.DisplayName(uiCulture) ?? n.Fallback,
        FileMapMessage m => Format(m, lookup, culture, uiCulture),
        long or int => a,
        _ => a.ToString(),
    };
}
