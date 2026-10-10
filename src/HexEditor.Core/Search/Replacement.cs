using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Search;

/// <summary>置換語と一致の長さが違う場合の扱い (FIND-24 の仕様 1)。</summary>
public enum LengthPolicy
{
    /// <summary>一致を置換語に置き換え、ファイルの長さが変わる (挿入・削除)。長さを変えられるドキュメントの既定。</summary>
    ChangeLength,

    /// <summary>置換語が短い場合は残りを埋め草で埋める。長い場合はエラー。長さが固定のドキュメントの既定。</summary>
    PadKeepLength,

    /// <summary>置換語が長い場合は一致の後ろのバイトを上書きする。短い場合は残りのバイトをそのまま残す。</summary>
    OverwriteFollowing,
}

/// <summary>「後ろを上書きする」で置換語がドキュメントの末尾を超える場合の扱い (FIND-24 の仕様 5)。</summary>
public enum OverflowMode
{
    /// <summary>まだ決めていない (超える場合は <see cref="ReplaceIssue.ExceedsEnd"/> を返して確認を求める)。</summary>
    Ask,

    /// <summary>超える分を書かない (長さが固定のドキュメント)。</summary>
    Truncate,

    /// <summary>末尾に追加する (ファイル)。</summary>
    Append,
}

/// <summary>置換の条件 (FIND-24)。</summary>
public sealed record ReplaceOptions
{
    public LengthPolicy Policy { get; init; } = LengthPolicy.ChangeLength;

    /// <summary>埋め草 (1〜16 バイトのパターンを繰り返す。既定 `00`。FIND-24 の仕様 3)。</summary>
    public byte[] Filler { get; init; } = [0x00];

    public OverflowMode Overflow { get; init; } = OverflowMode.Ask;

    /// <summary>ドキュメントの長さを変えられるか (長さが固定なら「長さを変える」は選べない。FIND-24 の仕様 2)。</summary>
    public static LengthPolicy DefaultPolicy(bool canResize) => canResize ? LengthPolicy.ChangeLength : LengthPolicy.PadKeepLength;
}

/// <summary>置換できない理由。</summary>
public enum ReplaceIssue
{
    None,

    /// <summary>「埋めて長さを保つ」で置換語が一致より長い (FIND-24 の「エラー」)。</summary>
    TooLong,

    /// <summary>「後ろを上書きする」で置換語がドキュメントの末尾を超える。<see cref="OverflowMode"/> を決めてから置換し直す。</summary>
    ExceedsEnd,

    /// <summary>長さが固定のドキュメントで長さを変えようとした (「長さを変える」、空の置換語による削除)。</summary>
    CannotResize,

    /// <summary>その位置は (もう) 一致しない (FIND-03 の仕様 5)。</summary>
    NoLongerMatches,
}

/// <summary>
/// 置換語 (FIND-22 の仕様 2)。バイトごとに値と「元のバイトを残す」(Hex の `??`) を持つ。数値のエンディアン「両方」では、
/// 一致した種類ごとに符号化を変える。
/// </summary>
public sealed class ReplacementTemplate
{
    private readonly (byte Value, bool Keep)[][] _byVariant;

    /// <summary>一致ごとに置換語のバイト列を作る関数 (正規表現のグループの参照。FIND-18 の仕様 8、FIND-19 の仕様 7)。</summary>
    private readonly Func<DocumentSnapshot, long, long, byte[]?>? _dynamic;

    private readonly bool _dynamicEmpty;

    private ReplacementTemplate((byte, bool)[][] byVariant) => _byVariant = byVariant;

    private ReplacementTemplate(Func<DocumentSnapshot, long, long, byte[]?> dynamic, bool empty)
    {
        _byVariant = [[]];
        _dynamic = dynamic;
        _dynamicEmpty = empty;
    }

    /// <summary>置換語が空 (一致を削除する。FIND-22 の仕様 8)。</summary>
    public bool IsEmpty => _dynamic is null ? _byVariant.All(v => v.Length == 0) : _dynamicEmpty;

    /// <summary>一致ごとに内容が決まる (正規表現のグループの参照)。長さは一致を見るまでわからない。</summary>
    public bool IsDynamic => _dynamic is not null;

    /// <summary>一致ごとに内容が決まる置換語の、今の状態の一致 (<paramref name="offset"/>、長さ <paramref name="length"/>) での内容。一致しなければ null。</summary>
    public ReplacementTemplate? ForMatch(DocumentSnapshot current, long offset, long length) =>
        _dynamic is null ? this : _dynamic(current, offset, length) is { } bytes ? FromBytes(bytes) : null;

    /// <summary>
    /// 正規表現の置換語 (FIND-18 の仕様 8、FIND-19 の仕様 7)。テキストでは `$1` `$&lt;name&gt;` `$&amp;` `$$` を使え、同じ文字コードで
    /// 符号化する。バイト列では Hex とグループの参照を並べて書く (`$2 00 $1`)。書き方の誤りは <see cref="PatternException"/>。
    /// </summary>
    public static ReplacementTemplate FromRegex(SearchPattern pattern, string text)
    {
        RegexMatcher regex = pattern.Regex ?? throw new ArgumentException("正規表現のパターンではありません。", nameof(pattern));
        if (regex.IsBytes)
        {
            RegexSearch.ValidateByteReplacement(text);
        }

        return new ReplacementTemplate((snapshot, offset, length) =>
            regex.MatchAt(snapshot, offset, length, out _) is { } match ? regex.Replace(match, text) : null, text.Trim().Length == 0);
    }

    /// <summary>種類 (一致した文字コードなど) ごとに違う置換語 (複数の文字コードの検索の置換。FIND-08)。</summary>
    public static ReplacementTemplate PerVariant(IReadOnlyList<byte[]> bytes) =>
        new([.. bytes.Select(b => b.Select(x => (x, false)).ToArray())]);

    /// <summary>種類 <paramref name="variant"/> の置換語の長さ。</summary>
    public int LengthFor(int variant) => Bytes(variant).Length;

    /// <summary>種類 0 の置換語の変換結果 (置換欄の横の Hex 表示)。`??` は「??」。一致ごとに決まる置換語では空。</summary>
    public string Preview(int maxBytes = 32) =>
        string.Join(' ', Bytes(0).Take(maxBytes).Select(b => b.Keep ? "??" : b.Value.ToString("X2")));

    /// <summary>Hex の置換語。`??` の位置は元のバイトを残す (FIND-06 の仕様 6)。`?` と `*` は使えない。</summary>
    public static ReplacementTemplate FromHex(string text) => new([SearchPattern.ParseReplacementHex(text)]);

    /// <summary>テキストの置換語 (検索と同じ文字コード・エスケープの設定で符号化する)。</summary>
    public static ReplacementTemplate FromText(string text, Encoding encoding, bool useEscapes) =>
        FromBytes(SearchPattern.EncodeText(text, encoding, useEscapes));

    /// <summary>
    /// 数値の置換語 (検索と同じサイズ・形式で符号化する。エンディアン「両方」は一致したエンディアンで符号化する)。空なら削除。
    /// </summary>
    public static ReplacementTemplate FromNumeric(string text, NumericSearchInfo info, IExpressionContext? context = null)
    {
        if (text.Trim().Length == 0)
        {
            return FromBytes([]);
        }

        int variants = Math.Max(1, info.VariantBigEndian.Count);
        var byVariant = new (byte, bool)[variants][];
        for (int v = 0; v < variants; v++)
        {
            byVariant[v] = [.. info.EncodeReplacement(text, v, context).Select(b => (b, false))];
        }

        return new ReplacementTemplate(byVariant);
    }

    public static ReplacementTemplate FromBytes(byte[] bytes) => new([[.. bytes.Select(b => (b, false))]]);

    internal (byte Value, bool Keep)[] Bytes(int variant) => _byVariant[Index(variant)];

    private readonly Dictionary<int, IReadOnlyList<(bool Keep, int Start, int Length, byte[]? Data)>> _runs = [];

    /// <summary>
    /// 置換語を「元のバイトを残す」の連続とバイト列の連続に分けたもの。バイト列は同じ配列を使い回す
    /// (すべて置換で追加バッファに 1 回だけ書くため)。
    /// </summary>
    internal IReadOnlyList<(bool Keep, int Start, int Length, byte[]? Data)> Runs(int variant)
    {
        int index = Index(variant);
        lock (_runs)
        {
            if (_runs.TryGetValue(index, out var cached))
            {
                return cached;
            }

            (byte Value, bool Keep)[] bytes = _byVariant[index];
            var runs = new List<(bool, int, int, byte[]?)>();
            int i = 0;
            while (i < bytes.Length)
            {
                bool keep = bytes[i].Keep;
                int j = i;
                while (j < bytes.Length && bytes[j].Keep == keep)
                {
                    j++;
                }

                runs.Add((keep, i, j - i, keep ? null : [.. bytes[i..j].Select(b => b.Value)]));
                i = j;
            }

            _runs[index] = runs;
            return runs;
        }
    }

    private int Index(int variant) => variant >= 0 && variant < _byVariant.Length ? variant : 0;
}

/// <summary>1 件の置換の結果。</summary>
public readonly record struct ReplaceOneResult(ReplaceIssue Issue, long Offset, long InsertedLength)
{
    public bool Applied => Issue == ReplaceIssue.None;
}

/// <summary>すべて置換の結果。</summary>
public sealed record ReplaceAllResult(long Count, long LengthDelta);

/// <summary>置換の計画と適用 (FIND-22〜FIND-24)。</summary>
public static class Replacer
{
    /// <summary>
    /// 一致 1 件の置換の内容を決める (FIND-24 の規則)。置換できない場合は理由を返す (<paramref name="edit"/> は null)。
    /// </summary>
    public static ReplaceIssue Plan(ReplacementTemplate template, long offset, long matchLength, int variant, ReplaceOptions options,
        long documentLength, bool canResize, out ReplacementEdit? edit, DocumentSnapshot? current = null)
    {
        edit = null;
        if (template.IsDynamic)
        {
            // 一致ごとに内容が決まる置換語 (正規表現のグループの参照。FIND-18 の仕様 8): 今の状態の一致から作る。
            if (current is null || template.ForMatch(current, offset, matchLength) is not { } dynamic)
            {
                return ReplaceIssue.NoLongerMatches;
            }

            return Plan(dynamic, offset, matchLength, 0, options, documentLength, canResize, out edit);
        }

        (byte Value, bool Keep)[] bytes = template.Bytes(variant);
        long n = bytes.Length;
        long m = matchLength;
        if (n == m)
        {
            edit = new ReplacementEdit(offset, m, Parts(template, variant, bytes.Length, offset, documentLength));
            return ReplaceIssue.None;
        }

        switch (options.Policy)
        {
            case LengthPolicy.ChangeLength:
                if (!canResize)
                {
                    return ReplaceIssue.CannotResize;
                }

                edit = new ReplacementEdit(offset, m, Parts(template, variant, bytes.Length, offset, documentLength));
                return ReplaceIssue.None;

            case LengthPolicy.PadKeepLength:
                if (n > m)
                {
                    return ReplaceIssue.TooLong;
                }

                var padded = new List<ReplacementPart>(Parts(template, variant, bytes.Length, offset, documentLength))
                {
                    ReplacementPart.Fill(options.Filler.Length == 0 ? [0x00] : options.Filler, m - n),
                };
                edit = new ReplacementEdit(offset, m, padded);
                return ReplaceIssue.None;

            default:
                // 後ろを上書きする: 短い場合は残りを残し、長い場合は一致の後ろを上書きする。
                if (n < m)
                {
                    edit = new ReplacementEdit(offset, n, Parts(template, variant, bytes.Length, offset, documentLength));
                    return ReplaceIssue.None;
                }

                long available = documentLength - offset;
                if (n <= available)
                {
                    edit = new ReplacementEdit(offset, n, Parts(template, variant, bytes.Length, offset, documentLength));
                    return ReplaceIssue.None;
                }

                OverflowMode mode = options.Overflow;
                if (mode == OverflowMode.Append && !canResize)
                {
                    mode = OverflowMode.Truncate;
                }

                switch (mode)
                {
                    case OverflowMode.Truncate:
                        edit = new ReplacementEdit(offset, available, Parts(template, variant, (int)available, offset, documentLength));
                        return ReplaceIssue.None;
                    case OverflowMode.Append:
                        edit = new ReplacementEdit(offset, available, Parts(template, variant, bytes.Length, offset, documentLength));
                        return ReplaceIssue.None;
                    default:
                        return ReplaceIssue.ExceedsEnd;
                }
        }
    }

    /// <summary>
    /// <paramref name="offset"/> の一致を置換する (1 件ずつの置換。FIND-22)。置換の時点の状態で一致を確かめ直し、一致しなければ
    /// 何もしない (FIND-03 の仕様 5)。1 回の置換は 1 回の Undo で戻せる。
    /// </summary>
    public static ReplaceOneResult ReplaceAt(Document document, SearchPattern pattern, long offset, ReplacementTemplate template,
        ReplaceOptions options, string description = "置換")
    {
        DocumentSnapshot current = document.Current;
        if (!TryVerify(current, pattern, offset, out int length, out int variant))
        {
            return new ReplaceOneResult(ReplaceIssue.NoLongerMatches, offset, 0);
        }

        ReplaceIssue issue = Plan(template, offset, length, variant, options, current.Length, document.CanResize, out ReplacementEdit? edit, current);
        if (issue != ReplaceIssue.None)
        {
            return new ReplaceOneResult(issue, offset, 0);
        }

        document.ApplyReplacements([edit!], description);
        return new ReplaceOneResult(ReplaceIssue.None, offset, edit!.InsertLength);
    }

    /// <summary>
    /// すべて置換の検索 (FIND-23 の仕様 1・2)。範囲の一致を、重ならないように前から順に探す (重なる一致を含めるオプションは無視する)。
    /// 件数の上限はない。キャンセルされた場合は <see cref="OperationCanceledException"/> (何も置換しない。仕様 6)。
    /// </summary>
    public static SearchResults FindForReplaceAll(DocumentSnapshot snapshot, SearchPattern pattern, SearchOptions options,
        LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        var results = new SearchResults(snapshot, pattern, options with { IncludeOverlapping = false, MaxMatches = long.MaxValue });
        try
        {
            SearchEngine.FindAll(results, operation, cancellationToken);
        }
        catch
        {
            results.Dispose();
            throw;
        }

        return results;
    }

    /// <summary>
    /// すべて置換の内容を作る (FIND-23)。検索したスナップショットと今の状態が違う場合は、一致の位置を補正し、今の状態で一致を
    /// 確かめ直す (一致しなくなった箇所は置換しない。FIND-03 の仕様 5)。前の置換と重なる一致 (後ろを上書きした範囲) は飛ばす。
    /// 置換できない一致がある場合 (埋めて長さを保つで長すぎるなど) は、その理由で <see cref="ReplaceException"/> を投げる。
    /// </summary>
    public static IEnumerable<ReplacementEdit> PlanAll(DocumentSnapshot current, SearchResults results, ReplacementTemplate template,
        ReplaceOptions options, bool canResize)
    {
        bool same = ReferenceEquals(current.Tree, results.Snapshot.Tree);
        MatchTracker? tracker = same ? null : new MatchTracker(results.Snapshot, current);
        long previousEnd = 0;
        long total = results.LongCount;
        const int Batch = 65536;
        for (long start = 0; start < total; start += Batch)
        {
            foreach (SearchMatch m in results.GetRange(start, (int)Math.Min(Batch, total - start)))
            {
                long offset = m.Offset;
                long length = m.Length;
                int variant = m.Variant;
                if (tracker is not null)
                {
                    TrackedMatch t = tracker.Track(m);
                    if (t.Status != MatchStatus.Unchanged || !TryVerify(current, results.Pattern, t.Offset, out int len, out variant))
                    {
                        continue;
                    }

                    offset = t.Offset;
                    length = len;
                }

                if (offset < previousEnd)
                {
                    continue;
                }

                ReplaceIssue issue = Plan(template, offset, length, variant, options, current.Length, canResize, out ReplacementEdit? edit, current);
                if (issue == ReplaceIssue.NoLongerMatches)
                {
                    continue;
                }

                if (issue != ReplaceIssue.None)
                {
                    throw new ReplaceException(issue, offset, length, template.ForMatch(current, offset, length)?.LengthFor(variant) ?? 0);
                }

                previousEnd = edit!.Offset + edit.RemoveLength;
                yield return edit;
            }
        }
    }

    /// <summary>
    /// すべての一致で置換が成り立つか、適用の前に確かめる (確認ダイアログの前に「埋めて長さを保つ」の誤りや末尾を超える一致を見つける)。
    /// 最初に見つかった理由を返す。
    /// </summary>
    public static (ReplaceIssue Issue, long Offset) Check(SearchResults results, ReplacementTemplate template, ReplaceOptions options,
        long documentLength, bool canResize, DocumentSnapshot? current = null)
    {
        current ??= results.Snapshot;
        long total = results.LongCount;
        const int Batch = 65536;
        for (long start = 0; start < total; start += Batch)
        {
            foreach (SearchMatch m in results.GetRange(start, (int)Math.Min(Batch, total - start)))
            {
                ReplaceIssue issue = Plan(template, m.Offset, m.Length, m.Variant, options, documentLength, canResize, out _, results.Snapshot);
                if (issue is not (ReplaceIssue.None or ReplaceIssue.NoLongerMatches))
                {
                    return (issue, m.Offset);
                }
            }
        }

        return (ReplaceIssue.None, -1);
    }

    /// <summary>今の状態の <paramref name="offset"/> でパターンが一致するか (一致の長さと種類も返す)。</summary>
    public static bool TryVerify(DocumentSnapshot snapshot, SearchPattern pattern, long offset, out int length, out int variant)
    {
        length = -1;
        variant = 0;
        if (offset < 0 || offset >= snapshot.Length)
        {
            return false;
        }

        if (pattern.Regex is { } regex)
        {
            length = regex.LengthAt(snapshot, offset, 0, snapshot.Length);
            return length > 0;
        }

        int max = (int)Math.Min(pattern.MaxMatchLength, snapshot.Length - offset);
        byte[] buffer = new byte[max];
        if (!snapshot.Read(offset, buffer).IsComplete)
        {
            return false;
        }

        length = pattern.MatchLengthAt(buffer, offset);
        if (length < 0)
        {
            return false;
        }

        variant = pattern.VariantAt(buffer);
        return true;
    }

    /// <summary>
    /// 置換語の先頭 <paramref name="count"/> バイトを部品に分ける。`??` (Keep) の連続は元の範囲のコピー、それ以外の連続はバイト列
    /// (置換語ごとに同じ配列)。
    /// </summary>
    private static List<ReplacementPart> Parts(ReplacementTemplate template, int variant, int count, long offset, long documentLength)
    {
        var parts = new List<ReplacementPart>();
        foreach ((bool keep, int start, int length, byte[]? data) in template.Runs(variant))
        {
            if (start >= count)
            {
                break;
            }

            int n = Math.Min(length, count - start);
            if (keep)
            {
                // 元のバイトを残す。ドキュメントの末尾を超える位置は 00 にする。
                long from = offset + start;
                long copy = Math.Clamp(documentLength - from, 0, n);
                if (copy > 0)
                {
                    parts.Add(ReplacementPart.Copy(from, copy));
                }

                if (copy < n)
                {
                    parts.Add(ReplacementPart.Literal(new byte[n - copy]));
                }
            }
            else
            {
                parts.Add(ReplacementPart.Literal(n == length ? data! : data![..n]));
            }
        }

        return parts;
    }
}

/// <summary>すべて置換で、置換できない一致があった (FIND-24 の「エラー」など)。</summary>
public sealed class ReplaceException(ReplaceIssue issue, long offset, long matchLength, long replacementLength)
    : InvalidOperationException($"{issue} at 0x{offset:X}")
{
    public ReplaceIssue Issue { get; } = issue;

    public long Offset { get; } = offset;

    public long MatchLength { get; } = matchLength;

    public long ReplacementLength { get; } = replacementLength;
}
