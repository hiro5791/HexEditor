using System.Globalization;
using System.Text;
using HexEditor.Core.Templates;

namespace HexEditor.Core.Compare.Structure;

/// <summary>構造の差分の種類 (ANA-31 の仕様 6)。<see cref="None"/> は差分を持たない入れ物、<see cref="Same"/> は一致したフィールド。</summary>
public enum StructDiffKind : byte
{
    None,
    Same,
    Changed,
    Added,
    Removed,
    Moved,
    MovedChanged,
    TypeChanged,
    Unparsed,
}

/// <summary>配列の要素の対応付けの方法 (ANA-31 の仕様 5 の 3)。</summary>
public enum StructMatchMethod
{
    Auto,
    Key,
    Content,
    Index,
}

/// <summary>「解析できない」の理由 (ANA-31 の仕様 5 の 7、仕様 6)。</summary>
public enum StructUnparsedReason : byte
{
    None,

    /// <summary>テンプレートのエラー (TPL-27)。</summary>
    Error,

    /// <summary>上限 (<c>limit.array</c> など) で打ち切った配列の後ろ。</summary>
    Limit,

    /// <summary>対応付けの深さが 64 段を超えた。</summary>
    TooDeep,
}

/// <summary>構造の比較の状態 (ANA-31 の仕様 10、ANA-32 の仕様 8)。</summary>
public enum StructCompareState
{
    Running,
    Completed,

    /// <summary>キャンセルした (それまでの差分は残る)。</summary>
    Cancelled,

    /// <summary>フィールドの数が上限 (<c>compare.structure.maxFields</c>) に達して止めた。</summary>
    FieldLimit,

    /// <summary>テンプレートが決まらない (バイトの比較だけを行う)。</summary>
    NoTemplate,

    /// <summary>テンプレートに構文エラーがある。</summary>
    SyntaxError,

    /// <summary>テンプレートのファイルが見つからない。</summary>
    TemplateMissing,

    /// <summary>比べられない対象 (プロセスメモリのスナップショット)。</summary>
    Unsupported,

    /// <summary>予期しない失敗 (読み込みエラーなど)。</summary>
    Failed,
}

/// <summary>配列の行の対応付けの情報 (「キー: type」「内容」「位置」。ANA-31 の仕様 5 の 3)。</summary>
public sealed class StructArrayInfo
{
    /// <summary>実際に使った方法 (<see cref="StructMatchMethod.Auto"/> にはならない)。</summary>
    public StructMatchMethod Method { get; internal set; }

    /// <summary>キーのフィールドの名前 (<see cref="StructMatchMethod.Key"/> のとき)。</summary>
    public string? KeyField { get; internal set; }

    /// <summary>要素が多いため「内容」をやめて「位置」で対応付けた (100,000 要素を超える。仕様の「巨大ファイル」)。</summary>
    public bool TooManyForContent { get; internal set; }

    /// <summary>方法を利用者が選んだ (<c>compare.structure.arrayRules</c>)。</summary>
    public bool FromRule { get; internal set; }

    /// <summary>配列のパス (添字を除いた形。例: <c>chunks</c>)。対応付けの設定の記録に使う。</summary>
    public string RulePath { get; internal set; } = string.Empty;

    /// <summary>キーに選べるフィールド (要素の型の直接の子の葉の名前)。</summary>
    public IReadOnlyList<string> KeyCandidates { get; internal set; } = [];

    /// <summary>エクスポートの <c>match</c> の値 (<c>key:type</c> / <c>content</c> / <c>index</c>)。</summary>
    public string MatchText => Method switch
    {
        StructMatchMethod.Key => "key:" + KeyField,
        StructMatchMethod.Content => "content",
        _ => "index",
    };
}

/// <summary>
/// 構造の差分の木の 1 行 (ANA-31 の仕様 7、ANA-32 の仕様 1・2)。入れ物 (構造体・配列など) と葉のどちらも表す。差分のある行は
/// <see cref="Kind"/> が <see cref="StructDiffKind.None"/> / <see cref="StructDiffKind.Same"/> 以外。パスは親をたどって作る
/// (100 万行でも行ごとにパスの文字列を持たない)。比較の後は変えない。
/// </summary>
public sealed class StructDiffNode
{
    private readonly string? _name;
    private readonly string? _rightName;

    internal StructDiffNode(StructDiffNode? parent, string? name, string? rightName, long leftIndex, long rightIndex)
    {
        Parent = parent;
        _name = name;
        _rightName = rightName;
        LeftIndex = leftIndex;
        RightIndex = rightIndex;
        Depth = parent is null ? -1 : parent.Depth + 1;
    }

    public StructDiffNode? Parent { get; }

    /// <summary>根からの深さ (根は -1、最上位のフィールドが 0)。</summary>
    public int Depth { get; }

    public StructDiffNode? FirstChild { get; private set; }

    public StructDiffNode? NextSibling { get; private set; }

    internal StructDiffNode? LastChild { get; private set; }

    /// <summary>表示の計算 (<see cref="StructDiffView"/>) での前順の番号。</summary>
    internal int ViewIndex { get; set; } = -1;

    /// <summary>結果の木に加えた (比較の中だけで使う)。</summary>
    internal bool Attached { get; set; }

    public bool HasChildren => FirstChild is not null;

    public IEnumerable<StructDiffNode> Children
    {
        get
        {
            for (StructDiffNode? c = FirstChild; c is not null; c = c.NextSibling)
            {
                yield return c;
            }
        }
    }

    public StructDiffKind Kind { get; internal set; }

    /// <summary>入れ物 (構造体・共用体・配列・ポインタ・セクション) の行か。</summary>
    public bool IsContainer { get; internal set; }

    /// <summary>配列の行の対応付けの情報 (配列でなければ null)。</summary>
    public StructArrayInfo? Array { get; internal set; }

    /// <summary>配列の要素の左右の添字 (要素でなければ -1)。</summary>
    public long LeftIndex { get; }

    public long RightIndex { get; }

    /// <summary>葉の要素の配列の、違う要素の連続 (<c>a[10 … 25]</c>) の最後の添字 (連続でなければ -1)。</summary>
    public long LeftIndexEnd { get; internal set; } = -1;

    public long RightIndexEnd { get; internal set; } = -1;

    public string TypeName { get; internal set; } = string.Empty;

    public string? RightTypeName { get; internal set; }

    public bool HasLeft { get; internal set; }

    public bool HasRight { get; internal set; }

    /// <summary>左の開始オフセット (ドキュメントの先頭から)。追加の行は入るはずの位置 (仕様 7)。</summary>
    public long LeftOffset { get; internal set; }

    public long LeftSize { get; internal set; }

    public long RightOffset { get; internal set; }

    public long RightSize { get; internal set; }

    /// <summary>左右の値の表示 (結果ツリーの値の列と同じ。先頭 64 文字)。一致した行は表示するときに求める (<see cref="StructCompareResult.DisplayOf"/>)。</summary>
    public string? LeftDisplay { get; internal set; }

    public string? RightDisplay { get; internal set; }

    /// <summary>キーで対応付けた要素のキー (<c>type = "tEXt"</c>)。</summary>
    public string? KeyText { get; internal set; }

    /// <summary>埋め草 (<c>padding</c>) の行。</summary>
    public bool IsPadding { get; internal set; }

    /// <summary>比較の範囲 (ANA-01 の 2) の外のフィールド。</summary>
    public bool OutsideRange { get; internal set; }

    public StructUnparsedReason Reason { get; internal set; }

    /// <summary>「解析できない」の原因の側 (左 / 右。両方なら左右とも)。</summary>
    public bool ReasonLeft { get; internal set; }

    public bool ReasonRight { get; internal set; }

    /// <summary>「解析できない」のテンプレートのエラー (TPL-27)。</summary>
    public TemplateDiagnostic? Diagnostic { get; internal set; }

    /// <summary>「解析できない」の行にまとめた、相手側のパターンの数。</summary>
    public long FoldedCount { get; internal set; }

    /// <summary>差分の行 (一致・入れ物でない)。</summary>
    public bool IsDiff => Kind is not (StructDiffKind.None or StructDiffKind.Same);

    /// <summary>行の名前 (表示用のパスの最後の要素。ANA-32 の仕様 2)。</summary>
    public string Name => Segment(HasLeft || !HasRight ? Side.Left : Side.Right, withKey: false);

    /// <summary>左のパス (左にないときは空)。</summary>
    public string LeftPath => HasLeft || Kind == StructDiffKind.Unparsed ? BuildPath(Side.Left, false) : string.Empty;

    /// <summary>右のパス (右にないときは空)。</summary>
    public string RightPath => HasRight || Kind == StructDiffKind.Unparsed ? BuildPath(Side.Right, false) : string.Empty;

    /// <summary>表示用のパス: 左のパス (追加では右のパス)。キーで対応付けた要素にはキーを付ける (仕様 7)。</summary>
    public string DisplayPath => BuildPath(HasLeft || !HasRight ? Side.Left : Side.Right, true);

    /// <summary>テンプレートの結果ツリーのパス (左右の評価結果でノードを探すのに使う)。</summary>
    internal string TreePath(bool right) => BuildPath(right ? Side.Right : Side.Left, false);

    internal void Append(StructDiffNode child)
    {
        if (LastChild is null)
        {
            FirstChild = child;
        }
        else
        {
            LastChild.NextSibling = child;
        }

        LastChild = child;
    }

    private enum Side
    {
        Left,
        Right,
    }

    private string Segment(Side side, bool withKey)
    {
        string text;
        if (_name is not null)
        {
            text = side == Side.Right && _rightName is not null ? _rightName : _name;
        }
        else
        {
            bool left = side == Side.Left ? LeftIndex >= 0 : RightIndex < 0;
            long index = left ? LeftIndex : RightIndex;
            long end = left ? LeftIndexEnd : RightIndexEnd;
            text = end > index
                ? "[" + index.ToString(CultureInfo.InvariantCulture) + " … " + end.ToString(CultureInfo.InvariantCulture) + "]"
                : "[" + index.ToString(CultureInfo.InvariantCulture) + "]";
        }

        return withKey && KeyText is not null ? text + " (" + KeyText + ")" : text;
    }

    private string BuildPath(Side side, bool withKey)
    {
        var parts = new List<string>();
        for (StructDiffNode? n = this; n is not null && n.Parent is not null; n = n.Parent)
        {
            parts.Add(n.Segment(side, withKey));
        }

        var sb = new StringBuilder();
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            string p = parts[i];
            if (sb.Length > 0 && !p.StartsWith('['))
            {
                sb.Append('.');
            }

            sb.Append(p);
        }

        return sb.ToString();
    }

    public override string ToString() => $"{Kind} {DisplayPath}";
}

/// <summary>構造の比較に使うテンプレートを決めた理由 (ANA-31 の仕様 2)。</summary>
public enum StructTemplateReason
{
    /// <summary>決まらない。</summary>
    None,

    /// <summary>利用者が「構造の比較に使うテンプレートを選ぶ」で選んだ。</summary>
    UserChoice,

    /// <summary>左右に同じテンプレートの固定の適用がある。</summary>
    SameApplication,

    /// <summary>左右の自動適用の判定で、同じテンプレートのマジックが両方で一致した。</summary>
    MagicMatch,
}

/// <summary>構造の比較に使うテンプレートと左右の開始オフセット (ANA-31 の仕様 2)。</summary>
public sealed record StructTemplateChoice(string TemplatePath, long LeftStart, long RightStart, StructTemplateReason Reason)
{
    /// <summary>決まらなかった。</summary>
    public static StructTemplateChoice None { get; } = new(string.Empty, 0, 0, StructTemplateReason.None);

    public bool IsNone => Reason == StructTemplateReason.None;
}

/// <summary>構造の比較の片側の情報 (エクスポートの <c>left</c> / <c>right</c>)。</summary>
public sealed record StructCompareSideInfo(string Name, string? Path, long Start);

/// <summary>構造の外の差分 (ANA-31 の仕様 8): バイトの差分のうち、どの葉のパターンの範囲とも重ならない部分の件数とバイト数。</summary>
public readonly record struct StructOutsideDiffs(long Count, long Bytes);
