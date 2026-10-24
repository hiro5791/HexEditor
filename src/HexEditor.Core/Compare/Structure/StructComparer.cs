using System.Diagnostics;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Extensions;
using HexEditor.Core.Templates.Loading;
using HexEditor.Core.Templates.Runtime;

namespace HexEditor.Core.Compare.Structure;

/// <summary>構造の比較の片側 (ANA-31)。<paramref name="RangeStart"/>・<paramref name="RangeLength"/> は比較の範囲 (ANA-01 の 2。「範囲外」の印に使う)。</summary>
public sealed record StructCompareSide(TemplateDataSource Data, long Start, long RangeStart, long RangeLength, string Name, string? Path);

/// <summary>構造の比較の設定 (ANA-31。設定 <c>compare.structure.*</c>)。</summary>
public sealed record StructCompareOptions
{
    public const int DefaultMaxFields = 1_000_000;
    public const int MinMaxFields = 10_000;
    public const int MaxMaxFields = 10_000_000;

    /// <summary>比べるフィールドの数の上限 (左右それぞれ。<c>compare.structure.maxFields</c>)。</summary>
    public int MaxFields { get; init; } = DefaultMaxFields;

    /// <summary><c>hidden</c> のパターンも比べる (<c>compare.structure.includeHidden</c>)。</summary>
    public bool IncludeHidden { get; init; }

    /// <summary>配列ごとの対応付けの方法 (<c>compare.structure.arrayRules</c>)。</summary>
    public StructArrayRules Rules { get; init; } = new();

    /// <summary>文字の既定の文字コード (ドキュメントの文字コード)。</summary>
    public string DefaultEncoding { get; init; } = "latin1";

    public ITemplateExtensionHost? Extensions { get; init; }

    /// <summary>設定「テンプレートの上限の最大値」(TPL-17 の仕様 4。通常の適用と同じ)。</summary>
    public TemplateLimits MaximumLimits { get; init; } = TemplateLimits.DefaultMaximum;

    /// <summary>値の表示の設定 (結果ツリーの値の列と同じ)。</summary>
    public TemplateDisplayContext Display { get; init; } = TemplateDisplayContext.Current;

    /// <summary>記録する一致したフィールドの行の数の上限 (「一致したフィールドも表示」。作業用のメモリを抑える)。</summary>
    public int MaxSameRows { get; init; } = 1_000_000;
}

/// <summary>
/// 構造を理解した比較 (ANA-31)。左右に同じテンプレートを新しく評価し (タブの結果ツリーとは別)、結果ツリーをフィールドの単位で
/// 対応付けて差分を求める。比較は専用のスレッド (評価器のスレッド) で行い、見つかった差分は順に結果の木に加える。
/// </summary>
public static class StructComparer
{
    /// <summary>対応付けの深さの上限 (仕様 5 の 7)。</summary>
    public const int MaxDepth = 64;

    /// <summary>「内容」の対応付けで Myers を使う要素の数の上限 (仕様の「巨大ファイル」)。</summary>
    public const int ContentLimit = 100_000;

    /// <summary>キーの値として持つバイト数。</summary>
    public const int KeyBytes = 64;

    /// <summary>値の表示の長さの上限 (仕様 7)。</summary>
    public const int DisplayLength = 64;

    /// <summary>比較を始める。結果はすぐに返り、<see cref="StructCompareResult.Completion"/> で完了を待てる。</summary>
    public static StructCompareResult Start(TemplateProgram program, StructTemplateChoice choice, StructCompareSide left, StructCompareSide right,
        StructCompareOptions? options = null)
    {
        options ??= new StructCompareOptions();
        int maxFields = Math.Clamp(options.MaxFields, StructCompareOptions.MinMaxFields, StructCompareOptions.MaxMaxFields);
        var result = new StructCompareResult(choice, new StructCompareSideInfo(left.Name, left.Path, left.Start),
            new StructCompareSideInfo(right.Name, right.Path, right.Start), maxFields)
        {
            LeftLength = left.Data.Length,
        };
        result.TemplateName = program.Metadata.GetDisplayName(System.Globalization.CultureInfo.CurrentUICulture, program.Path);
        TemplateThreads.StartDedicated(() =>
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; // バイトの比較と Hex ビューの読み込みを優先する
            }
            catch (Exception ex) when (ex is ThreadStateException or PlatformNotSupportedException)
            {
            }

            Execute(program, choice, left, right, options with { MaxFields = maxFields }, result);
        }, "HexEditor struct compare");
        return result;
    }

    /// <summary>比較して終わるまで待つ (テスト・同期の呼び出し用)。</summary>
    public static StructCompareResult Run(TemplateProgram program, StructTemplateChoice choice, StructCompareSide left, StructCompareSide right,
        StructCompareOptions? options = null, CancellationToken cancellationToken = default)
    {
        StructCompareResult r = Start(program, choice, left, right, options);
        using (cancellationToken.Register(r.Cancel))
        {
            r.Completion.Wait(CancellationToken.None);
        }

        return r;
    }

    internal static string Truncate(string text) => text.Length <= DisplayLength ? text : text[..DisplayLength] + "…";

    private static void Execute(TemplateProgram program, StructTemplateChoice choice, StructCompareSide left, StructCompareSide right,
        StructCompareOptions options, StructCompareResult result)
    {
        long started = Stopwatch.GetTimestamp();
        CancellationToken token = result.CancellationToken;
        try
        {
            if (program.Diagnostics.FirstOrDefault(d => d.Severity == TemplateSeverity.Error) is { } syntax)
            {
                result.State = StructCompareState.SyntaxError;
                result.Message = syntax.ToString();
                return;
            }

            TemplateApplyOptions Apply(long start) => new()
            {
                StartOffset = start,
                DefaultEncoding = options.DefaultEncoding,
                Extensions = options.Extensions,
                MaximumLimits = options.MaximumLimits,
            };

            TemplateResult l = TemplateEngine.Apply(program, left.Data, Apply(left.Start));
            TemplateResult r = TemplateEngine.Apply(program, right.Data, Apply(right.Start));
            result.LeftResult = l;
            result.RightResult = r;
            while (!Task.WaitAll([l.Completion, r.Completion], 50))
            {
                if (token.IsCancellationRequested)
                {
                    l.Cancel();
                    r.Cancel();
                    throw new OperationCanceledException(token);
                }
            }

            if (l.State == TemplateRunState.SyntaxError || r.State == TemplateRunState.SyntaxError)
            {
                result.State = StructCompareState.SyntaxError;
                result.Message = (l.Diagnostics.Concat(r.Diagnostics)).FirstOrDefault(d => d.Severity == TemplateSeverity.Error)?.ToString();
                return;
            }

            new Walker(result, l, r, left, right, choice.TemplatePath, options, token).Run();
            result.State = StructCompareState.Completed;
        }
        catch (OperationCanceledException)
        {
            result.State = StructCompareState.Cancelled;
        }
        catch (FieldLimitException)
        {
            result.State = StructCompareState.FieldLimit;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException
            or ArgumentException or TemplateError)
        {
            result.State = StructCompareState.Failed;
            result.Message = ex.Message;
        }
        finally
        {
            result.Elapsed = Stopwatch.GetElapsedTime(started);
            result.Complete();
        }
    }

    private sealed class FieldLimitException : Exception
    {
    }

    /// <summary>左右の結果ツリーをたどって対応付ける本体 (評価器のスレッドで動く)。</summary>
    private sealed class Walker
    {
        private readonly StructCompareResult _r;
        private readonly TemplateResult _left;
        private readonly TemplateResult _right;
        private readonly StructCompareSide _leftSide;
        private readonly StructCompareSide _rightSide;
        private readonly string _template;
        private readonly StructCompareOptions _options;
        private readonly CancellationToken _token;
        private readonly List<(string Path, TemplateDiagnostic Diagnostic)> _leftErrors;
        private readonly List<(string Path, TemplateDiagnostic Diagnostic)> _rightErrors;
        private readonly HashSet<string> _leftTruncated;
        private readonly HashSet<string> _rightTruncated;
        private readonly Dictionary<object, StructDiffNode> _unparsed = [];
        private readonly HashSet<StructDiffNode> _attached = new(ReferenceEqualityComparer.Instance);
        private readonly byte[] _bufA = new byte[1 << 20];
        private readonly byte[] _bufB = new byte[1 << 20];
        private long _sameRows;
        private int _checks;

        public Walker(StructCompareResult r, TemplateResult left, TemplateResult right, StructCompareSide leftSide, StructCompareSide rightSide,
            string template, StructCompareOptions options, CancellationToken token)
        {
            _r = r;
            _left = left;
            _right = right;
            _leftSide = leftSide;
            _rightSide = rightSide;
            _template = template;
            _options = options;
            _token = token;
            _leftErrors = ErrorsOf(left);
            _rightErrors = ErrorsOf(right);
            _leftTruncated = TruncatedOf(left);
            _rightTruncated = TruncatedOf(right);
            _attached.Add(r.Root);
        }

        public void Run() => CompareContainer(_r.Root, _left.Root, _right.Root, 0);

        /// <summary>
        /// 評価を止めたエラー (評価がエラーで止まった場合の最後のエラー)。止まった位置より後ろのパターンは片側にしかないため、相手側の
        /// パターンを「追加」「削除」にせず「解析できない」にまとめる (仕様 6)。値の表示だけのエラーなど、評価が続いたものは含めない。
        /// </summary>
        private static List<(string, TemplateDiagnostic)> ErrorsOf(TemplateResult r) =>
            r.State == TemplateRunState.Failed && r.Diagnostics.LastOrDefault(d => d.Severity == TemplateSeverity.Error) is { } d
                ? [(d.Path ?? string.Empty, d)] : [];

        private static HashSet<string> TruncatedOf(TemplateResult r) =>
            [.. r.Diagnostics.Where(d => d.Code == TemplateMessages.ArrayTruncated && d.Path is not null).Select(d => d.Path!)];

        // ───────────── 入れ物 (構造体・共用体・セクション・ビットフィールド) ─────────────

        /// <summary>構造体の子 (またはループで宣言した同じ名前のパターンのまとまり)。</summary>
        private sealed record Item(string Key, TemplateNode? Single, List<TemplateNode>? Group);

        private void CompareContainer(StructDiffNode p, TemplateNode a, TemplateNode b, int depth)
        {
            List<Item> la = Items(Kids(a));
            List<Item> lb = Items(Kids(b));
            string pathA = PathOf(a);
            string pathB = PathOf(b);
            (string Path, TemplateDiagnostic Diagnostic)? errA = ErrorInside(_leftErrors, pathA);
            (string Path, TemplateDiagnostic Diagnostic)? errB = ErrorInside(_rightErrors, pathB);
            int cutA = errA is { } ea ? ErrorChild(la, ea.Path) : la.Count;
            int cutB = errB is { } eb ? ErrorChild(lb, eb.Path) : lb.Count;

            var byKey = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int j = 0; j < lb.Count; j++)
            {
                byKey.TryAdd(lb[j].Key, j);
            }

            int[] partnerOfA = new int[la.Count];
            int[] partnerOfB = [.. Enumerable.Repeat(-1, lb.Count)];
            for (int i = 0; i < la.Count; i++)
            {
                partnerOfA[i] = byKey.TryGetValue(la[i].Key, out int j) && partnerOfB[j] < 0 ? j : -1;
                if (partnerOfA[i] >= 0)
                {
                    partnerOfB[partnerOfA[i]] = i;
                }
            }

            // エラーより後ろの片側だけのパターンは「解析できない」にまとめる (仕様 6)
            int foldA = int.MaxValue; // 左だけのパターンのうち、この位置より後ろをまとめる (右にエラー)
            int foldB = int.MaxValue;
            if (errB is not null)
            {
                foldA = -1;
                for (int j = 0; j <= Math.Min(cutB, lb.Count - 1); j++)
                {
                    foldA = Math.Max(foldA, partnerOfB[j]);
                }
            }

            if (errA is not null)
            {
                foldB = -1;
                for (int i = 0; i <= Math.Min(cutA, la.Count - 1); i++)
                {
                    foldB = Math.Max(foldB, partnerOfA[i]);
                }
            }

            foreach ((int i, int j) in MergeOrder(partnerOfA, partnerOfB))
            {
                Check();
                if (i >= 0 && j >= 0)
                {
                    ComparePair(p, la[i], lb[j], depth);
                }
                else if (i >= 0)
                {
                    if (i > foldA)
                    {
                        Fold(p, errB!.Value.Diagnostic, right: true, la[i], b);
                    }
                    else
                    {
                        long anchor = AnchorEnd(lb, LastPartnerBefore(partnerOfA, i), b);
                        foreach (TemplateNode n in Nodes(la[i]))
                        {
                            OneSided(p, n, right: false, n.Name, -1, anchor, null);
                        }
                    }
                }
                else
                {
                    if (j > foldB)
                    {
                        Fold(p, errA!.Value.Diagnostic, right: false, lb[j], a);
                    }
                    else
                    {
                        long anchor = AnchorEnd(la, LastPartnerBefore(partnerOfB, j), a);
                        foreach (TemplateNode n in Nodes(lb[j]))
                        {
                            OneSided(p, n, right: true, n.Name, -1, anchor, null);
                        }
                    }
                }
            }
        }

        private static IEnumerable<TemplateNode> Nodes(Item item) => item.Single is { } s ? [s] : item.Group!;

        private static int LastPartnerBefore(int[] partners, int index)
        {
            for (int k = index - 1; k >= 0; k--)
            {
                if (partners[k] >= 0)
                {
                    return partners[k];
                }
            }

            return -1;
        }

        /// <summary>入るはずの位置: 相手側の、直前に対応付いた兄弟の終わり。兄弟がなければ親の始まり (仕様 7)。</summary>
        private long AnchorEnd(List<Item> items, int index, TemplateNode parent)
        {
            if (index < 0)
            {
                return RangeOf(parent).Offset;
            }

            TemplateNode last = Nodes(items[index]).Last();
            (long off, long size) = RangeOf(last);
            return off + size;
        }

        /// <summary>
        /// 対応付けた組と片側だけのものを並べる順: 左の順に並べ、右だけのものは、右で直前に対応付いたものの後ろに置く。
        /// </summary>
        private static List<(int I, int J)> MergeOrder(int[] partnerOfA, int[] partnerOfB)
        {
            var ops = new List<(long SortA, int Sub, int SortB, int I, int J)>();
            for (int i = 0; i < partnerOfA.Length; i++)
            {
                ops.Add((i, 0, partnerOfA[i], i, partnerOfA[i]));
            }

            int lastLeft = -1;
            for (int j = 0; j < partnerOfB.Length; j++)
            {
                if (partnerOfB[j] >= 0)
                {
                    lastLeft = partnerOfB[j];
                }
                else
                {
                    ops.Add((lastLeft, 1, j, -1, j));
                }
            }

            ops.Sort((x, y) => x.SortA != y.SortA ? x.SortA.CompareTo(y.SortA) : x.Sub != y.Sub ? x.Sub.CompareTo(y.Sub) : x.SortB.CompareTo(y.SortB));
            return [.. ops.Select(o => (o.I, o.J))];
        }

        private void ComparePair(StructDiffNode p, Item a, Item b, int depth)
        {
            if (a.Single is { } sa && b.Single is { } sb)
            {
                CompareNode(p, sa, sb, depth, sa.Name, sb.Name == sa.Name ? null : sb.Name, -1, -1, null, moved: false);
                return;
            }

            if (a.Group is { } ga && b.Group is { } gb)
            {
                // ループで宣言した同じ名前のパターン (section[1] など) は、配列の要素と同じ規則で対応付ける (仕様 5 の 1)
                var info = new StructArrayInfo { RulePath = StructArrayRules.RulePathOf(PathOf(ga[0])) };
                MatchElements(p, new ListElements(ga), new ListElements(gb), null, null, info, depth, named: true);
                return;
            }

            // まとまりと単独のパターン: 型の変更として扱う
            TemplateNode na = Nodes(a).First();
            TemplateNode nb = Nodes(b).First();
            StructDiffNode node = NewNode(p, na, nb, na.Name, nb.Name == na.Name ? null : nb.Name, -1, -1);
            TypeChanged(node, na, nb);
        }

        private List<TemplateNode> Kids(TemplateNode n)
        {
            var list = new List<TemplateNode>();
            foreach (TemplateNode c in n.Children)
            {
                if (c.Kind == TemplateNodeKind.Computed || !c.Attributes.Export || (c.Attributes.Hidden && !_options.IncludeHidden))
                {
                    continue;
                }

                list.Add(c);
            }

            return list;
        }

        private static List<Item> Items(List<TemplateNode> kids)
        {
            var items = new List<Item>();
            var groups = new Dictionary<string, Item>(StringComparer.Ordinal);
            foreach (TemplateNode c in kids)
            {
                if (c.BaseName is { } baseName && c.Kind != TemplateNodeKind.Section)
                {
                    if (!groups.TryGetValue(baseName, out Item? g))
                    {
                        g = new Item("\u0001" + baseName, null, []);
                        groups[baseName] = g;
                        items.Add(g);
                    }

                    g.Group!.Add(c);
                }
                else
                {
                    items.Add(new Item(c.Kind == TemplateNodeKind.Section ? "@" + c.Name : c.Name, c, null));
                }
            }

            return items;
        }

        private static string PathOf(TemplateNode n) => n.Kind == TemplateNodeKind.Application ? string.Empty : n.Path;

        /// <summary>パス <paramref name="path"/> の中 (またはそのもの) で起きたエラー。</summary>
        private static (string Path, TemplateDiagnostic Diagnostic)? ErrorInside(List<(string Path, TemplateDiagnostic Diagnostic)> errors, string path)
        {
            foreach ((string ep, TemplateDiagnostic d) in errors)
            {
                if (Within(ep, path))
                {
                    return (ep, d);
                }
            }

            return null;
        }

        private static bool Within(string errorPath, string path) =>
            path.Length == 0 || errorPath == path || (errorPath.Length > path.Length && errorPath.StartsWith(path, StringComparison.Ordinal)
                && errorPath[path.Length] is '.' or '[');

        private static int ErrorChild(List<Item> items, string errorPath)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (Nodes(items[i]).Any(n => Within(errorPath, PathOf(n)) && PathOf(n).Length > 0))
                {
                    return i;
                }
            }

            return items.Count;
        }

        // ───────────── 1 組のパターン ─────────────

        private StructDiffNode? CompareNode(StructDiffNode p, TemplateNode a, TemplateNode b, int depth, string? name, string? rightName, long li, long ri,
            string? keyText, bool moved)
        {
            Check();
            StructDiffNode node = NewNode(p, a, b, name, rightName, li, ri);
            node.KeyText = keyText;
            if (depth > MaxDepth)
            {
                node.Kind = StructDiffKind.Unparsed;
                node.Reason = StructUnparsedReason.TooDeep;
                Attach(node);
                return node;
            }

            if (a.Kind == TemplateNodeKind.Error || b.Kind == TemplateNodeKind.Error)
            {
                Unparsed(node, a, b);
                return node;
            }

            bool leafA = IsLeaf(a);
            bool leafB = IsLeaf(b);
            if (leafA != leafB || BaseType(a.TypeName) != BaseType(b.TypeName))
            {
                TypeChanged(node, a, b);
                return node;
            }

            long before = _r.DiffCount;
            if (leafA)
            {
                CompareLeaf(node, a, b);
            }
            else
            {
                node.IsContainer = true;
                if (a.Size == b.Size && a.Size > 0 && Plain(a) && Plain(b) && BytesEqual(a, b))
                {
                    // 固定長の型で左右のバイト列が同じ: 子を評価せずに一致とする (「巨大ファイル」の方針)
                    Fields(1, 1);
                    Cover(a, b);
                    node.Kind = StructDiffKind.Same;
                    AttachSame(node);
                }
                else
                {
                    switch (a.Kind)
                    {
                        case TemplateNodeKind.Array:
                            CompareArray(node, a, b, depth);
                            break;
                        case TemplateNodeKind.Pointer:
                            ComparePointer(node, a, b, depth);
                            break;
                        default:
                            CompareContainer(node, a, b, depth + 1);
                            break;
                    }
                }
            }

            if (moved)
            {
                bool changed = node.Kind == StructDiffKind.Changed || _r.DiffCount > before;
                Attach(node);
                _r.SetKind(node, changed ? StructDiffKind.MovedChanged : StructDiffKind.Moved);
            }

            return node;
        }

        private StructDiffNode NewNode(StructDiffNode p, TemplateNode? a, TemplateNode? b, string? name, string? rightName, long li, long ri)
        {
            var node = new StructDiffNode(p, name, rightName, li, ri)
            {
                HasLeft = a is not null,
                HasRight = b is not null,
                TypeName = (a ?? b)!.TypeName,
            };
            if (a is not null)
            {
                (node.LeftOffset, node.LeftSize) = RangeOf(a);
            }

            if (b is not null)
            {
                (node.RightOffset, node.RightSize) = RangeOf(b);
            }

            node.OutsideRange = (node.HasLeft && !Overlaps(node.LeftOffset, node.LeftSize, _leftSide))
                || (node.HasRight && !Overlaps(node.RightOffset, node.RightSize, _rightSide));
            return node;
        }

        private static bool Overlaps(long offset, long size, StructCompareSide side) =>
            offset < side.RangeStart + side.RangeLength && offset + Math.Max(1, size) > side.RangeStart;

        private void TypeChanged(StructDiffNode node, TemplateNode a, TemplateNode b)
        {
            Fields(1, 1);
            node.Kind = StructDiffKind.TypeChanged;
            node.RightTypeName = b.TypeName;
            node.LeftDisplay = Display(a);
            node.RightDisplay = Display(b);
            Cover(a, b);
            Attach(node);
        }

        private void Unparsed(StructDiffNode node, TemplateNode? a, TemplateNode? b)
        {
            node.Kind = StructDiffKind.Unparsed;
            node.Reason = StructUnparsedReason.Error;
            node.ReasonLeft = a is { } x && (x.Kind == TemplateNodeKind.Error || x.HasError);
            node.ReasonRight = b is { } y && (y.Kind == TemplateNodeKind.Error || y.HasError);
            TemplateNode? source = node.ReasonRight ? b : a;
            TemplateDiagnostic? d = source?.Diagnostics.FirstOrDefault(x => x.Severity == TemplateSeverity.Error)
                ?? (source is null ? null : ErrorInside(node.ReasonRight ? _rightErrors : _leftErrors, PathOf(source))?.Diagnostic);
            node.Diagnostic = d;
            if (d is not null)
            {
                _unparsed.TryAdd(d, node);
            }

            Attach(node);
        }

        /// <summary>エラーより後ろの、相手側にしかないパターンを「解析できない」の行にまとめる (仕様 6)。</summary>
        private void Fold(StructDiffNode p, TemplateDiagnostic diagnostic, bool right, Item other, TemplateNode errorParent)
        {
            foreach (TemplateNode n in Nodes(other))
            {
                FoldNode(p, diagnostic, StructUnparsedReason.Error, right, n, n.Name, -1, -1, RangeOf(errorParent) is var (o, s) ? o + s : 0);
            }
        }

        private void FoldNode(StructDiffNode p, object key, StructUnparsedReason reason, bool errorOnRight, TemplateNode other, string? name, long li, long ri,
            long errorSideOffset)
        {
            if (!_unparsed.TryGetValue(key, out StructDiffNode? row))
            {
                row = new StructDiffNode(p, name, null, li, ri)
                {
                    Kind = StructDiffKind.Unparsed,
                    Reason = reason,
                    ReasonLeft = !errorOnRight,
                    ReasonRight = errorOnRight,
                    Diagnostic = key as TemplateDiagnostic,
                    TypeName = other.TypeName,
                    HasLeft = errorOnRight,
                    HasRight = !errorOnRight,
                };
                (long o, long s) = RangeOf(other);
                if (errorOnRight)
                {
                    (row.LeftOffset, row.LeftSize) = (o, s);
                    row.RightOffset = errorSideOffset;
                }
                else
                {
                    (row.RightOffset, row.RightSize) = (o, s);
                    row.LeftOffset = errorSideOffset;
                }

                _unparsed[key] = row;
                Attach(row);
            }
            else
            {
                (long o, long s) = RangeOf(other);
                lock (_r.SyncRoot)
                {
                    if (errorOnRight && row.HasLeft)
                    {
                        long end = Math.Max(row.LeftOffset + row.LeftSize, o + s);
                        row.LeftOffset = Math.Min(row.LeftOffset, o);
                        row.LeftSize = end - row.LeftOffset;
                    }
                    else if (!errorOnRight && row.HasRight)
                    {
                        long end = Math.Max(row.RightOffset + row.RightSize, o + s);
                        row.RightOffset = Math.Min(row.RightOffset, o);
                        row.RightSize = end - row.RightOffset;
                    }
                }
            }

            row.FoldedCount++;
            _r.Touch();
        }

        // ───────────── 葉 ─────────────

        private static bool IsLeaf(TemplateNode n) =>
            n.IsByteOrCharArray || n.Attributes.Sealed || n.Kind is not (TemplateNodeKind.Struct or TemplateNodeKind.Union or TemplateNodeKind.Array
                or TemplateNodeKind.Pointer or TemplateNodeKind.Bitfield or TemplateNodeKind.Section or TemplateNodeKind.Application);

        /// <summary>型の名前から配列の長さを除いたもの (<c>char[12]</c> と <c>char[10]</c> は同じ型)。</summary>
        private static string BaseType(string typeName)
        {
            int open = typeName.LastIndexOf('[');
            return open > 0 && typeName.EndsWith(']') ? typeName[..open] : typeName;
        }

        private void CompareLeaf(StructDiffNode node, TemplateNode a, TemplateNode b)
        {
            Fields(1, 1);
            if (a.HasError || b.HasError)
            {
                Unparsed(node, a, b);
                return;
            }

            node.IsPadding = a.Kind == TemplateNodeKind.Padding;
            Cover(a, b);
            if (LeafEqual(a, b))
            {
                node.Kind = StructDiffKind.Same;
                AttachSame(node);
                return;
            }

            node.Kind = StructDiffKind.Changed;
            node.LeftDisplay = Display(a);
            node.RightDisplay = Display(b);
            Attach(node);
        }

        private bool LeafEqual(TemplateNode a, TemplateNode b)
        {
            if (a.Kind == TemplateNodeKind.BitfieldMember || HasRead(a) || HasRead(b))
            {
                return ValueEquals(a.Value, b.Value);
            }

            return a.Size == b.Size && BytesEqual(a, b);
        }

        private static bool HasRead(TemplateNode n) => n.Result.Invoke(() => Evaluator.HasReadConversion(n));

        private static bool ValueEquals(object? x, object? y) => x switch
        {
            byte[] bx => y is byte[] by && bx.AsSpan().SequenceEqual(by),
            null => y is null,
            _ => x.Equals(y),
        };

        private string Display(TemplateNode n)
        {
            try
            {
                return Truncate(n.GetDisplay(_options.Display));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return string.Empty;
            }
        }

        // ───────────── ポインタ ─────────────

        private void ComparePointer(StructDiffNode node, TemplateNode a, TemplateNode b, int depth)
        {
            Fields(1, 1);
            Cover(a, b);
            if (!(a.Size == b.Size && BytesEqual(a, b)))
            {
                node.LeftDisplay = Display(a);
                node.RightDisplay = Display(b);
                Attach(node);
                _r.SetKind(node, StructDiffKind.Changed);
            }

            // 指す先がすでに比べたパターンと同じ (既出) なら比べない (仕様 5 の 5)
            if (a.Pointer is { DuplicateOf: null } && b.Pointer is { DuplicateOf: null } && a.PointerTarget is { } ta && b.PointerTarget is { } tb)
            {
                CompareNode(node, ta, tb, depth + 1, ta.Name, tb.Name == ta.Name ? null : tb.Name, -1, -1, null, moved: false);
            }
        }

        // ───────────── 配列 ─────────────

        private interface IElements
        {
            long Count { get; }

            TemplateNode Get(long index);
        }

        private sealed class ArrayElements(TemplateNode array) : IElements
        {
            private readonly long _count = array.ChildCount;

            public long Count => _count;

            public TemplateNode Get(long index) => array.GetChild(index);
        }

        private sealed class ListElements(List<TemplateNode> items) : IElements
        {
            public long Count => items.Count;

            public TemplateNode Get(long index) => items[(int)index];
        }

        private void CompareArray(StructDiffNode node, TemplateNode a, TemplateNode b, int depth)
        {
            long? sizeA = a.Result.Invoke(() => a.Result.Evaluator.LeafArrayElementSize(a));
            long? sizeB = b.Result.Invoke(() => b.Result.Evaluator.LeafArrayElementSize(b));
            if (sizeA is { } ea && sizeA == sizeB)
            {
                node.Array = new StructArrayInfo { Method = StructMatchMethod.Index, RulePath = StructArrayRules.RulePathOf(PathOf(a)) };
                CompareLeafArray(node, a, b, ea);
                return;
            }

            var info = new StructArrayInfo { RulePath = StructArrayRules.RulePathOf(PathOf(a)) };
            node.Array = info;
            Attach(node);
            MatchElements(node, new ArrayElements(a), new ArrayElements(b), a, b, info, depth, named: false);
        }

        private enum OpKind : byte
        {
            Pair,
            Removed,
            Added,
        }

        private readonly record struct Op(OpKind Kind, long I, long J, bool Moved);

        private void MatchElements(StructDiffNode container, IElements ea, IElements eb, TemplateNode? arrayA, TemplateNode? arrayB, StructArrayInfo info,
            int depth, bool named)
        {
            long nA = ea.Count;
            long nB = eb.Count;
            string pathA = arrayA is null ? string.Empty : PathOf(arrayA);
            string pathB = arrayB is null ? string.Empty : PathOf(arrayB);

            // 対応付けの方法 (仕様 5 の 3)
            TemplateNode? first = nA > 0 ? ea.Get(0) : nB > 0 ? eb.Get(0) : null;
            bool structs = first is { Kind: TemplateNodeKind.Struct or TemplateNodeKind.Union } && !IsLeaf(first);
            List<TemplateNode> firstKids = structs ? Kids(first!) : [];
            info.KeyCandidates = [.. firstKids.Where(IsLeaf).Select(c => c.Name)];
            (string?[] KeysA, string?[] KeysB)? keys = null;
            StructArrayRule? rule = _options.Rules.Find(_template, info.RulePath);
            if (rule is { Method: StructMatchMethod.Key, Key: { } rk } && info.KeyCandidates.Contains(rk))
            {
                info.Method = StructMatchMethod.Key;
                info.KeyField = rk;
                info.FromRule = true;
            }
            else if (rule is { Method: StructMatchMethod.Content or StructMatchMethod.Index })
            {
                info.Method = rule.Method;
                info.FromRule = true;
            }
            else
            {
                string? diffKey = arrayA is null ? null : arrayA.Result.Invoke(() => arrayA.Result.Evaluator.DiffKeyOf(arrayA));
                diffKey ??= arrayB is null ? null : arrayB.Result.Invoke(() => arrayB.Result.Evaluator.DiffKeyOf(arrayB));
                if (diffKey is not null && info.KeyCandidates.Contains(diffKey))
                {
                    info.Method = StructMatchMethod.Key;
                    info.KeyField = diffKey;
                }
                else
                {
                    foreach (TemplateNode c in firstKids.Where(IsAutoKey))
                    {
                        (string?[], string?[]) k = (KeysOf(ea, c.Name), KeysOf(eb, c.Name));
                        if (!AllSame(k.Item1) && !AllSame(k.Item2))
                        {
                            info.Method = StructMatchMethod.Key;
                            info.KeyField = c.Name;
                            keys = k;
                            break;
                        }
                    }

                    if (info.KeyField is null)
                    {
                        info.Method = structs ? StructMatchMethod.Content : StructMatchMethod.Index;
                    }
                }
            }

            if (info.Method == StructMatchMethod.Content && (nA > ContentLimit || nB > ContentLimit))
            {
                info.Method = StructMatchMethod.Index;
                info.TooManyForContent = true;
            }

            _r.Touch();
            List<Op> ops = info.Method switch
            {
                StructMatchMethod.Key => KeyOps(keys ?? (KeysOf(ea, info.KeyField!), KeysOf(eb, info.KeyField!))),
                StructMatchMethod.Content => ContentOps(ea, eb),
                _ => IndexOps(nA, nB),
            };

            // エラー・上限より後ろの、相手側にしかない要素は「解析できない」にまとめる (仕様 6)
            (string Path, TemplateDiagnostic Diagnostic)? errA = arrayA is null ? null : ErrorInside(_leftErrors, pathA);
            (string Path, TemplateDiagnostic Diagnostic)? errB = arrayB is null ? null : ErrorInside(_rightErrors, pathB);
            bool truncA = _leftTruncated.Contains(pathA) && arrayA is not null;
            bool truncB = _rightTruncated.Contains(pathB) && arrayB is not null;
            long maxPairedA = -1;
            long maxPairedB = -1;
            foreach (Op op in ops)
            {
                if (op.Kind == OpKind.Pair)
                {
                    maxPairedA = Math.Max(maxPairedA, op.I);
                    maxPairedB = Math.Max(maxPairedB, op.J);
                }
            }

            // 入るはずの位置 (仕様 7) を求めるための、直前に対応付いた要素
            long lastPairedJ = -1;
            long lastPairedI = -1;
            var pairOfI = new Dictionary<long, long>();
            foreach (Op op in ops)
            {
                if (op.Kind == OpKind.Pair)
                {
                    pairOfI[op.I] = op.J;
                }
            }

            string? keyField = info.Method == StructMatchMethod.Key ? info.KeyField : null;
            foreach (Op op in ops)
            {
                Check();
                switch (op.Kind)
                {
                    case OpKind.Pair:
                    {
                        TemplateNode x = ea.Get(op.I);
                        TemplateNode y = eb.Get(op.J);
                        SetPosition(x);
                        CompareNode(container, x, y, depth + 1, named ? x.Name : null, named && y.Name != x.Name ? y.Name : null, op.I, op.J,
                            keyField is null ? null : KeyTextOf(x, keyField), op.Moved);
                        if (!op.Moved)
                        {
                            lastPairedI = op.I;
                            lastPairedJ = op.J;
                        }

                        break;
                    }

                    case OpKind.Removed:
                    {
                        TemplateNode x = ea.Get(op.I);
                        if ((errB is not null || truncB) && op.I > maxPairedA)
                        {
                            FoldNode(container, errB is { } e ? e.Diagnostic : "limit:" + pathB, errB is null ? StructUnparsedReason.Limit : StructUnparsedReason.Error,
                                errorOnRight: true, x, named ? x.Name : null, op.I, nB, EndOf(arrayB));
                            break;
                        }

                        long anchor = AnchorOf(eb, PrecedingPair(pairOfI, op.I), arrayB);
                        OneSided(container, x, right: false, named ? x.Name : null, op.I, anchor, keyField is null ? null : KeyTextOf(x, keyField));
                        break;
                    }

                    default:
                    {
                        TemplateNode y = eb.Get(op.J);
                        if ((errA is not null || truncA) && op.J > maxPairedB)
                        {
                            FoldNode(container, errA is { } e ? e.Diagnostic : "limit:" + pathA, errA is null ? StructUnparsedReason.Limit : StructUnparsedReason.Error,
                                errorOnRight: false, y, named ? y.Name : null, nA, op.J, EndOf(arrayA));
                            break;
                        }

                        long anchor = AnchorOf(ea, lastPairedI, arrayA);
                        OneSided(container, y, right: true, named ? y.Name : null, op.J, anchor, keyField is null ? null : KeyTextOf(y, keyField));
                        break;
                    }
                }
            }
        }

        private long EndOf(TemplateNode? n)
        {
            if (n is null)
            {
                return 0;
            }

            (long o, long s) = RangeOf(n);
            return o + s;
        }

        private static long PrecedingPair(Dictionary<long, long> pairOfI, long i)
        {
            // 左の順で直前に対応付いた要素の、右の添字 (要素の数は小さいとは限らないので、近い順に探す)
            for (long k = i - 1; k >= 0 && i - k <= 4096; k--)
            {
                if (pairOfI.TryGetValue(k, out long j))
                {
                    return j;
                }
            }

            return pairOfI.Keys.Where(k => k < i).DefaultIfEmpty(-1).Max() is long best and >= 0 ? pairOfI[best] : -1;
        }

        private long AnchorOf(IElements elements, long index, TemplateNode? array)
        {
            if (index >= 0 && index < elements.Count)
            {
                TemplateNode n = elements.Get(index);
                (long o, long s) = RangeOf(n);
                return o + s;
            }

            return array is null ? 0 : RangeOf(array).Offset;
        }

        private void OneSided(StructDiffNode p, TemplateNode n, bool right, string? name, long index, long anchor, string? keyText)
        {
            Fields(right ? 0 : 1, right ? 1 : 0);
            StructDiffNode node = right ? NewNode(p, null, n, name, null, -1, index) : NewNode(p, n, null, name, null, index, -1);
            node.KeyText = keyText;
            node.IsContainer = !IsLeaf(n);
            node.IsPadding = n.Kind == TemplateNodeKind.Padding;
            if (right)
            {
                node.LeftOffset = anchor;
                node.RightDisplay = IsLeaf(n) ? Display(n) : null;
                (right ? _r.RightCoverage : _r.LeftCoverage).Add(node.RightOffset, n.Section is null ? node.RightSize : 0);
            }
            else
            {
                node.RightOffset = anchor;
                node.LeftDisplay = IsLeaf(n) ? Display(n) : null;
                _r.LeftCoverage.Add(node.LeftOffset, n.Section is null ? node.LeftSize : 0);
            }

            node.Kind = right ? StructDiffKind.Added : StructDiffKind.Removed;
            Attach(node);
        }

        private static List<Op> IndexOps(long nA, long nB)
        {
            long n = Math.Min(nA, nB);
            var ops = new List<Op>((int)Math.Min(int.MaxValue / 2, Math.Max(nA, nB)));
            for (long i = 0; i < n; i++)
            {
                ops.Add(new Op(OpKind.Pair, i, i, false));
            }

            for (long i = n; i < nA; i++)
            {
                ops.Add(new Op(OpKind.Removed, i, -1, false));
            }

            for (long j = n; j < nB; j++)
            {
                ops.Add(new Op(OpKind.Added, -1, j, false));
            }

            return ops;
        }

        /// <summary>キーの値と、同じ値の中での出現の順で対応付ける。並び順が違うもの (最長増加部分列に入らない要素) は移動 (仕様 5 の 4)。</summary>
        private static List<Op> KeyOps((string?[] A, string?[] B) keys)
        {
            (string?[] ka, string?[] kb) = keys;
            var queues = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
            for (int j = 0; j < kb.Length; j++)
            {
                string k = kb[j] ?? "\u0000";
                if (!queues.TryGetValue(k, out Queue<int>? q))
                {
                    q = new Queue<int>();
                    queues[k] = q;
                }

                q.Enqueue(j);
            }

            int[] partner = new int[ka.Length];
            bool[] usedB = new bool[kb.Length];
            for (int i = 0; i < ka.Length; i++)
            {
                partner[i] = queues.TryGetValue(ka[i] ?? "\u0000", out Queue<int>? q) && q.Count > 0 ? q.Dequeue() : -1;
                if (partner[i] >= 0)
                {
                    usedB[partner[i]] = true;
                }
            }

            // 最長増加部分列 (左の順に並べたときの右の添字)
            var pairedI = new List<int>();
            for (int i = 0; i < ka.Length; i++)
            {
                if (partner[i] >= 0)
                {
                    pairedI.Add(i);
                }
            }

            HashSet<int> inLis = Lis(pairedI.Select(i => partner[i]).ToArray()).Select(x => pairedI[x]).ToHashSet();
            return BuildOps(partner, usedB, i => !inLis.Contains(i));
        }

        /// <summary>左の順に、対応付いた組と削除を、右だけのものは右で直前に対応付いた要素の後ろに並べる。</summary>
        private static List<Op> BuildOps(int[] partner, bool[] usedB, Func<int, bool> moved)
        {
            var ops = new List<(long SortA, int Sub, long SortB, Op Op)>();
            int[] partnerOfB = [.. Enumerable.Repeat(-1, usedB.Length)];
            for (int i = 0; i < partner.Length; i++)
            {
                if (partner[i] >= 0)
                {
                    partnerOfB[partner[i]] = i;
                    ops.Add((i, 0, partner[i], new Op(OpKind.Pair, i, partner[i], moved(i))));
                }
                else
                {
                    ops.Add((i, 0, -1, new Op(OpKind.Removed, i, -1, false)));
                }
            }

            long lastLeft = -1;
            for (int j = 0; j < usedB.Length; j++)
            {
                if (partnerOfB[j] >= 0)
                {
                    if (!moved(partnerOfB[j]))
                    {
                        lastLeft = partnerOfB[j];
                    }
                }
                else
                {
                    ops.Add((lastLeft, 1, j, new Op(OpKind.Added, -1, j, false)));
                }
            }

            ops.Sort((x, y) => x.SortA != y.SortA ? x.SortA.CompareTo(y.SortA) : x.Sub != y.Sub ? x.Sub.CompareTo(y.Sub) : x.SortB.CompareTo(y.SortB));
            return [.. ops.Select(o => o.Op)];
        }

        /// <summary>最長増加部分列に入る位置 (<paramref name="values"/> の添字)。</summary>
        private static IEnumerable<int> Lis(int[] values)
        {
            int n = values.Length;
            int[] tails = new int[n];
            int[] prev = new int[n];
            int length = 0;
            for (int i = 0; i < n; i++)
            {
                int lo = 0;
                int hi = length;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (values[tails[mid]] < values[i])
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                prev[i] = lo > 0 ? tails[lo - 1] : -1;
                tails[lo] = i;
                length = Math.Max(length, lo + 1);
            }

            var result = new List<int>(length);
            for (int k = length > 0 ? tails[length - 1] : -1; k >= 0; k = prev[k])
            {
                result.Add(k);
            }

            return result;
        }

        /// <summary>
        /// 要素の範囲のバイト列のハッシュの並びを Myers の差分で対応付ける。一致しない要素の連続は、同じ位置のものを組にして「変更」、
        /// 余りを追加・削除にする。削除と追加のうちバイト列が同じものの組は移動 (仕様 5 の 3・4)。
        /// </summary>
        private List<Op> ContentOps(IElements ea, IElements eb)
        {
            int nA = (int)ea.Count;
            int nB = (int)eb.Count;
            ulong[] ha = new ulong[nA];
            ulong[] hb = new ulong[nB];
            for (int i = 0; i < nA; i++)
            {
                Check();
                ha[i] = HashOf(ea.Get(i));
            }

            for (int j = 0; j < nB; j++)
            {
                Check();
                hb[j] = HashOf(eb.Get(j));
            }

            // 共通の先頭・末尾を除いてから Myers にかける。編集が多すぎる (打ち切った) 場合は、全体を 1 つの不一致の連続として扱う。
            int prefix = 0;
            while (prefix < nA && prefix < nB && ha[prefix] == hb[prefix])
            {
                prefix++;
            }

            int suffix = 0;
            while (suffix < nA - prefix && suffix < nB - prefix && ha[nA - 1 - suffix] == hb[nB - 1 - suffix])
            {
                suffix++;
            }

            var matches = new List<(int A, int B, int Length)> { (0, 0, prefix) };
            int midA = nA - prefix - suffix;
            int midB = nB - prefix - suffix;
            if (midA > 0 && midB > 0)
            {
                var eq = new HashEq(ha, hb, prefix);
                int maxD = Math.Min(midA + midB, 20_000);
                List<MyersDiff.Match>? found = MyersDiff.Matches(eq, midA, midB, maxD, Stopwatch.GetTimestamp() + (Stopwatch.Frequency * 5), _token);
                if (found is not null)
                {
                    matches.AddRange(found.Select(m => (m.A + prefix, m.B + prefix, m.Length)));
                }
            }

            matches.Add((nA - suffix, nB - suffix, suffix));

            int[] partner = [.. Enumerable.Repeat(-1, nA)];
            bool[] usedB = new bool[nB];
            var removed = new List<int>();
            var added = new List<int>();
            int pa = 0;
            int pb = 0;
            foreach ((int ma, int mb, int len) in matches)
            {
                // 不一致の連続: 同じ位置のものを組にする
                int ga = ma - pa;
                int gb = mb - pb;
                int pairs = Math.Min(ga, gb);
                for (int k = 0; k < pairs; k++)
                {
                    partner[pa + k] = pb + k;
                    usedB[pb + k] = true;
                }

                for (int k = pairs; k < ga; k++)
                {
                    removed.Add(pa + k);
                }

                for (int k = pairs; k < gb; k++)
                {
                    added.Add(pb + k);
                }

                for (int k = 0; k < len; k++)
                {
                    partner[ma + k] = mb + k;
                    usedB[mb + k] = true;
                }

                pa = ma + len;
                pb = mb + len;
            }

            // 削除と追加のうち、バイト列が同じものの組は移動
            var movedSet = new HashSet<int>();
            if (removed.Count > 0 && added.Count > 0)
            {
                var byHash = new Dictionary<ulong, Queue<int>>();
                foreach (int j in added)
                {
                    if (!byHash.TryGetValue(hb[j], out Queue<int>? q))
                    {
                        q = new Queue<int>();
                        byHash[hb[j]] = q;
                    }

                    q.Enqueue(j);
                }

                foreach (int i in removed)
                {
                    if (byHash.TryGetValue(ha[i], out Queue<int>? q) && q.Count > 0)
                    {
                        int j = q.Dequeue();
                        partner[i] = j;
                        usedB[j] = true;
                        movedSet.Add(i);
                    }
                }
            }

            return BuildOps(partner, usedB, movedSet.Contains);
        }

        private readonly struct HashEq(ulong[] a, ulong[] b, int offset) : ISymbolComparer
        {
            public bool Equal(int x, int y) => a[x + offset] == b[y + offset];
        }

        private ulong HashOf(TemplateNode n)
        {
            var hash = new HexEditor.Core.Hashing.XxHash64Hasher(0);
            (long offset, long size) = (n.Offset, n.Size);
            TemplateResult r = n.Result;
            for (long done = 0; done < size;)
            {
                int chunk = (int)Math.Min(_bufA.Length, size - done);
                ReadAt(r, n, offset + done, _bufA.AsSpan(0, chunk));
                hash.Append(_bufA.AsSpan(0, chunk));
                done += chunk;
            }

            return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(hash.Finish()) ^ (ulong)size;
        }

        private static bool IsAutoKey(TemplateNode c) =>
            c.Kind is TemplateNodeKind.String or TemplateNodeKind.Enum or TemplateNodeKind.Guid
            || (c.IsByteOrCharArray && c.Size <= KeyBytes && c.Result.Invoke(() => c.Type is PrimType { IsChar: true }));

        private string?[] KeysOf(IElements elements, string field)
        {
            long n = elements.Count;
            string?[] keys = new string?[n];
            byte[] buf = new byte[KeyBytes];
            for (long i = 0; i < n; i++)
            {
                Check();
                TemplateNode e = elements.Get(i);
                TemplateNode? k = e.FindChild(field);
                if (k is null || k.Kind == TemplateNodeKind.Error)
                {
                    continue;
                }

                int len = (int)Math.Min(KeyBytes, k.Size);
                ReadAt(e.Result, k, k.Offset, buf.AsSpan(0, len));
                keys[i] = Convert.ToHexString(buf, 0, len);
            }

            return keys;
        }

        private static bool AllSame(string?[] keys) => keys.Length >= 2 && keys.All(k => k == keys[0]);

        /// <summary>表示用のパスに付けるキー (<c>type = "tEXt"</c>。仕様 7)。文字列は引用符で囲む。</summary>
        private string? KeyTextOf(TemplateNode element, string field)
        {
            if (element.FindChild(field) is not { } k)
            {
                return null;
            }

            string display = Display(k);
            bool text = k.Kind == TemplateNodeKind.String || (k.IsByteOrCharArray && k.Result.Invoke(() => k.Type is PrimType { IsChar: true }));
            return field + " = " + (text && !display.StartsWith('"') ? "\"" + display + "\"" : display);
        }

        /// <summary>
        /// 固定長の葉の要素の配列: 要素を 1 つずつ評価せず、左右の範囲のバイト列を 1 MiB 単位で比べ、違う要素の連続を 1 件の差分にまとめる。
        /// 比べたフィールドの数には差分の件数だけを数える (仕様の「巨大ファイル」)。
        /// </summary>
        private void CompareLeafArray(StructDiffNode node, TemplateNode a, TemplateNode b, long elementSize)
        {
            long nA = a.Size / elementSize;
            long nB = b.Size / elementSize;
            long n = Math.Min(nA, nB);
            int chunkElements = (int)Math.Max(1, _bufA.Length / elementSize);
            long runStart = -1;
            long diffs = 0;
            Cover(a, b);
            for (long i = 0; i < n; i += chunkElements)
            {
                Check(force: true);
                int count = (int)Math.Min(chunkElements, n - i);
                int bytes = (int)(count * elementSize);
                ReadAt(a.Result, a, a.Offset + (i * elementSize), _bufA.AsSpan(0, bytes));
                ReadAt(b.Result, b, b.Offset + (i * elementSize), _bufB.AsSpan(0, bytes));
                _r.SetPosition(a.Offset + (i * elementSize));
                if (_bufA.AsSpan(0, bytes).SequenceEqual(_bufB.AsSpan(0, bytes)))
                {
                    if (runStart >= 0)
                    {
                        Run(node, a, b, runStart, i - 1, elementSize);
                        diffs++;
                        runStart = -1;
                    }

                    continue;
                }

                for (int k = 0; k < count; k++)
                {
                    bool same = _bufA.AsSpan((int)(k * elementSize), (int)elementSize).SequenceEqual(_bufB.AsSpan((int)(k * elementSize), (int)elementSize));
                    if (!same && runStart < 0)
                    {
                        runStart = i + k;
                    }
                    else if (same && runStart >= 0)
                    {
                        Run(node, a, b, runStart, i + k - 1, elementSize);
                        diffs++;
                        runStart = -1;
                    }
                }
            }

            if (runStart >= 0)
            {
                Run(node, a, b, runStart, n - 1, elementSize);
                diffs++;
            }

            if (nA != nB)
            {
                // 長さの違い: 余りの要素を 1 件の追加・削除にまとめる
                bool right = nB > nA;
                Fields(right ? 0 : 1, right ? 1 : 0);
                long from = n;
                long to = Math.Max(nA, nB) - 1;
                var tail = new StructDiffNode(node, null, null, right ? -1 : from, right ? from : -1)
                {
                    TypeName = a.TypeName[..Math.Max(0, a.TypeName.LastIndexOf('['))],
                    HasLeft = !right,
                    HasRight = right,
                    LeftIndexEnd = right ? -1 : to,
                    RightIndexEnd = right ? to : -1,
                    Kind = right ? StructDiffKind.Added : StructDiffKind.Removed,
                };
                (long ao, long asz) = RangeOf(a);
                (long bo, long bsz) = RangeOf(b);
                tail.LeftOffset = right ? ao + asz : ao + (from * elementSize);
                tail.LeftSize = right ? 0 : (to - from + 1) * elementSize;
                tail.RightOffset = right ? bo + (from * elementSize) : bo + bsz;
                tail.RightSize = right ? (to - from + 1) * elementSize : 0;
                Attach(tail);
                diffs++;
            }

            if (diffs == 0)
            {
                Fields(1, 1);
                node.Kind = StructDiffKind.Same;
                AttachSame(node);
            }
        }

        private void Run(StructDiffNode array, TemplateNode a, TemplateNode b, long from, long to, long elementSize)
        {
            Fields(1, 1);
            (long ao, _) = RangeOf(a);
            (long bo, _) = RangeOf(b);
            bool inSection = a.Section is not null;
            var node = new StructDiffNode(array, null, null, from, from)
            {
                TypeName = a.TypeName[..Math.Max(0, a.TypeName.LastIndexOf('['))],
                HasLeft = true,
                HasRight = true,
                LeftIndexEnd = to > from ? to : -1,
                RightIndexEnd = to > from ? to : -1,
                LeftOffset = inSection ? ao : ao + (from * elementSize),
                LeftSize = inSection ? 0 : (to - from + 1) * elementSize,
                RightOffset = b.Section is not null ? bo : bo + (from * elementSize),
                RightSize = b.Section is not null ? 0 : (to - from + 1) * elementSize,
                Kind = StructDiffKind.Changed,
            };
            string more = to > from ? " …" : string.Empty;
            node.LeftDisplay = Display(a.GetChild(from)) + more;
            node.RightDisplay = Display(b.GetChild(from)) + more;
            node.OutsideRange = !Overlaps(node.LeftOffset, node.LeftSize, _leftSide) || !Overlaps(node.RightOffset, node.RightSize, _rightSide);
            Attach(node);
        }

        // ───────────── 共通 ─────────────

        private bool Plain(TemplateNode n) => n.Result.Invoke(() => n.Result.Evaluator.IsPlainFixed(n.Type));

        /// <summary>差分の範囲 (ドキュメントの先頭から)。セクションの中のパターンは、セクションを作った元の範囲 (仕様 5 の 6)。</summary>
        private static (long Offset, long Size) RangeOf(TemplateNode n)
        {
            if (n.Kind == TemplateNodeKind.Application)
            {
                return (n.Offset, n.Result.DataLength - n.Offset);
            }

            if (n.Section is { } s)
            {
                return s.SourceRange ?? (0, 0);
            }

            return (n.Offset, n.Size);
        }

        private void Cover(TemplateNode a, TemplateNode b)
        {
            (long ao, long asz) = RangeOf(a);
            (long bo, long bsz) = RangeOf(b);
            _r.LeftCoverage.Add(ao, a.Section is null ? asz : 0);
            _r.RightCoverage.Add(bo, b.Section is null ? bsz : 0);
        }

        private void SetPosition(TemplateNode n)
        {
            if (n.Section is null)
            {
                _r.SetPosition(n.Offset);
            }
        }

        private bool BytesEqual(TemplateNode a, TemplateNode b)
        {
            long size = a.Size;
            for (long done = 0; done < size;)
            {
                int chunk = (int)Math.Min(_bufA.Length, size - done);
                ReadAt(a.Result, a, a.Offset + done, _bufA.AsSpan(0, chunk));
                ReadAt(b.Result, b, b.Offset + done, _bufB.AsSpan(0, chunk));
                if (!_bufA.AsSpan(0, chunk).SequenceEqual(_bufB.AsSpan(0, chunk)))
                {
                    return false;
                }

                done += chunk;
                if (size > _bufA.Length)
                {
                    Check(force: true);
                }
            }

            return true;
        }

        /// <summary>パターンのアドレス空間 (ドキュメントかセクション) から読む。読めない・範囲外の部分は 0 にする。</summary>
        private static void ReadAt(TemplateResult r, TemplateNode n, long offset, Span<byte> destination)
        {
            int done = 0;
            while (done < destination.Length)
            {
                int got = n.Section is { } s ? s.Read(offset + done, destination[done..]) : r.Data.Read(offset + done, destination[done..], out _);
                if (got <= 0)
                {
                    destination[done..].Clear();
                    return;
                }

                done += got;
            }
        }

        private void Fields(long left, long right)
        {
            if (_r.LeftFields + left > _options.MaxFields || _r.RightFields + right > _options.MaxFields)
            {
                throw new FieldLimitException();
            }

            _r.AddFields(left, right);
        }

        private void Check(bool force = false)
        {
            if (force || (++_checks & 0x3F) == 0)
            {
                _token.ThrowIfCancellationRequested();
            }
        }

        /// <summary>行を木に加える (まだ加えていない祖先も先に加える)。</summary>
        private void Attach(StructDiffNode node)
        {
            if (_attached.Contains(node))
            {
                return;
            }

            if (node.Parent is { } parent)
            {
                Attach(parent);
                _attached.Add(node);
                _r.Add(parent, node);
            }
        }

        /// <summary>一致した行を加える (記録する数の上限まで)。</summary>
        private void AttachSame(StructDiffNode node)
        {
            if (_sameRows >= _options.MaxSameRows)
            {
                _r.SameRowsTruncated = true;
                return;
            }

            _sameRows++;
            Attach(node);
        }
    }
}
