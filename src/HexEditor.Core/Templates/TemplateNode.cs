using System.Collections;
using System.Globalization;
using HexEditor.Core.Templates.Runtime;
using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates;

/// <summary>結果ツリーのノードの種類 (TPL-21 の仕様 2 のアイコンの区別)。</summary>
public enum TemplateNodeKind
{
    /// <summary>適用 (結果ツリーの最上位のノード)。</summary>
    Application,
    Integer,
    Float,
    Bool,
    Char,
    String,
    Enum,
    Flags,
    Struct,
    Union,
    Array,
    Pointer,
    Bitfield,
    BitfieldMember,
    Padding,
    DateTime,
    Guid,
    IpAddress,
    MacAddress,
    Color,
    Bytes,

    /// <summary>セクションの見出し (「セクション: 名前」。TPL-15 の仕様 3)。</summary>
    Section,

    /// <summary><c>[[show]]</c> のローカル変数 (計算値。TPL-11 の仕様 3)。</summary>
    Computed,

    /// <summary>エラーのノード (TPL-27 の仕様 2)。</summary>
    Error,
}

/// <summary>表示用の属性 (TPL-13)。</summary>
public sealed class TemplateNodeAttributes
{
    internal static readonly TemplateNodeAttributes None = new();

    /// <summary><c>color("…")</c> の名前 (<c>accent1</c>〜<c>accent12</c>、名前付きの色、<c>#RRGGBB</c>)。</summary>
    public string? Color { get; internal set; }

    public bool NoColor { get; internal set; }

    public bool Hidden { get; internal set; }

    public bool HighlightHidden { get; internal set; }

    public bool Inline { get; internal set; }

    public bool Sealed { get; internal set; }

    public bool ReadOnly { get; internal set; }

    public bool Lazy { get; internal set; }

    /// <summary><c>export(false)</c> なら false (TPL-25 の仕様 4)。</summary>
    public bool Export { get; internal set; } = true;

    public bool ShowAll { get; internal set; }

    /// <summary><c>bookmark("名前")</c> (TPL-13)。</summary>
    public string? Bookmark { get; internal set; }

    /// <summary><c>visualize("種類", 引数…)</c> の種類と引数 (TPL-16。表示はフェーズ 4)。</summary>
    public string? VisualizeKind { get; internal set; }

    public IReadOnlyList<string> VisualizeArguments { get; internal set; } = [];

    /// <summary><c>docs("…")</c> (型の説明)。</summary>
    public string? Docs { get; internal set; }

    /// <summary><c>encoding("…")</c>。</summary>
    public string? Encoding { get; internal set; }

    internal TemplateNodeAttributes Clone() => (TemplateNodeAttributes)MemberwiseClone();
}

/// <summary>ポインタの情報 (TPL-08)。</summary>
public sealed class TemplatePointerInfo
{
    /// <summary>値が 0 で null として扱った (<c>[[allow_null]]</c> がない場合)。</summary>
    public bool IsNull { get; internal set; }

    /// <summary>指す先のオフセット (指す先のアドレス空間の中)。</summary>
    public long TargetOffset { get; internal set; }

    /// <summary>指す先のセクション (ドキュメントなら null)。</summary>
    public TemplateSection? TargetSection { get; internal set; }

    /// <summary>指す先がデータの範囲外。</summary>
    public bool OutOfRange { get; internal set; }

    /// <summary>同じ位置・同じ型のパターンがすでにある場合、そのパス (「→ 既出」。仕様 4)。</summary>
    public string? DuplicateOf { get; internal set; }

    /// <summary>指す先の型の名前。</summary>
    public string TargetType { get; internal set; } = string.Empty;

    /// <summary>指す先を評価済みか。</summary>
    public bool Resolved { get; internal set; }

    /// <summary>指す位置の計算に失敗した理由 (<c>pointer_map</c> のエラーなど)。</summary>
    public TemplateDiagnostic? Error { get; internal set; }
}

/// <summary>値の表示の設定 (TPL-14 の仕様 3。日時と <c>size</c> だけが地域設定に従う)。</summary>
public sealed record TemplateDisplayContext(CultureInfo Culture, TimeZoneInfo TimeZone)
{
    public static TemplateDisplayContext Current => new(CultureInfo.CurrentCulture, TimeZoneInfo.Local);

    /// <summary>JSON の期待値の比較に使う設定 (en-US、UTC)。</summary>
    public static TemplateDisplayContext Invariant { get; } = new(CultureInfo.GetCultureInfo("en-US"), TimeZoneInfo.Utc);
}

/// <summary>編集できない理由 (TPL-22 の仕様 6)。</summary>
public enum TemplateEditBlock
{
    None,
    ReadOnlyAttribute,
    InSection,
    Computed,
    NotAValue,
    Error,
}

/// <summary>
/// 結果ツリーのノード (TPL-21)。名前・型・オフセット・サイズは評価済みの値で、子・値・表示は必要になったときに評価する
/// (遅延評価。TPL-28)。子を評価するメソッドは評価器のスレッドで実行し、呼び出し元を待たせる。UI スレッドからは
/// <see cref="TemplateResult.InvokeAsync{T}(Func{T})"/> で呼ぶ。
/// </summary>
public sealed class TemplateNode
{
    internal TemplateNode(TemplateResult result, TemplateNodeKind kind, string name, string typeName, long offset, long size)
    {
        Result = result;
        Kind = kind;
        Name = name;
        TypeName = typeName;
        Offset = offset;
        Size = size;
    }

    public TemplateResult Result { get; }

    /// <summary>結果ツリーでの親 (<c>inline</c> の構造体は飛ばす。セクションの中のパターンはセクションの見出し)。</summary>
    public TemplateNode? Parent => DisplayParent();

    public TemplateNodeKind Kind { get; internal set; }

    /// <summary>識別子 (配列の要素は <c>[3]</c>。ループで宣言した同じ名前のパターンは <c>section[1]</c>)。</summary>
    public string Name
    {
        get => _name;
        internal set
        {
            _name = value;
            _path = null;
        }
    }

    private string _name = string.Empty;

    /// <summary>ループで宣言して名前に添字を付けたパターンの元の名前。</summary>
    internal string? BaseName { get; set; }

    /// <summary>結果ツリーに出す名前 (<c>name("…")</c> 属性があればそれ)。</summary>
    public string DisplayName => DisplayNameAttr ?? Name;

    public string TypeName { get; internal set; }

    /// <summary>開始オフセット (アドレス空間 <see cref="Section"/> の中。ドキュメントは先頭からの絶対オフセット)。</summary>
    public long Offset { get; internal set; }

    /// <summary>サイズ (バイト)。ビットフィールドの要素は、そのビットを含むバイトの数。</summary>
    public long Size { get; internal set; }

    /// <summary>終わりのオフセット (<c>endof</c> と同じ。この位置のバイトを含まない)。</summary>
    public long End => Offset + Size;

    /// <summary>ビットフィールドの要素の、<see cref="Offset"/> のバイトの中のビット位置 (TPL-06 の仕様 6)。</summary>
    public int? BitOffset { get; internal set; }

    public int? BitSize { get; internal set; }

    /// <summary>セクションの中のパターンならそのセクション (TPL-15)。</summary>
    public TemplateSection? Section { get; internal set; }

    public bool BigEndian { get; internal set; }

    public TemplateNodeAttributes Attributes { get; internal set; } = TemplateNodeAttributes.None;

    /// <summary><c>///</c> のドキュメントコメント (TPL-02 の仕様 1)。</summary>
    public string? DocComment { get; internal set; }

    /// <summary>パターンを宣言した位置 (「定義へ移動」。TPL-24)。</summary>
    public TemplateSourceLocation? Declaration { get; internal set; }

    /// <summary>型を定義した位置。</summary>
    public TemplateSourceLocation? TypeDeclaration { get; internal set; }

    public TemplatePointerInfo? Pointer { get; internal set; }

    /// <summary>書式・変換の関数のエラー (値の列は <c>&lt;エラー: 理由&gt;</c>。TPL-14 の仕様 7)。</summary>
    public TemplateDiagnostic? DisplayError { get; internal set; }

    /// <summary>配列の要素なら添字。</summary>
    public long ArrayIndex { get; internal set; } = -1;

    /// <summary>列挙型の値に名前がない (警告アイコンと「定義されていない値」。警告としては記録しない。TPL-05 の仕様 3)。</summary>
    public bool IsUndefinedEnumValue => Result.Run(() => Result.Evaluator.IsUndefinedEnum(this));

    /// <summary>このノードの警告・エラー。</summary>
    public IReadOnlyList<TemplateDiagnostic> Diagnostics
    {
        get
        {
            lock (Result.SyncRoot)
            {
                return DiagnosticsList is null ? [] : [.. DiagnosticsList];
            }
        }
    }

    public bool HasError => Kind == TemplateNodeKind.Error || Diagnostics.Any(d => d.Severity == TemplateSeverity.Error);

    public bool HasWarning => Diagnostics.Any(d => d.Severity == TemplateSeverity.Warning);

    /// <summary>パス (例: <c>chunks[3].header.width</c>。TPL-21 の仕様 6)。</summary>
    public string Path
    {
        get
        {
            if (_path is not null)
            {
                return _path;
            }

            TemplateNode? p = Parent;
            string path;
            if (Kind == TemplateNodeKind.Section)
            {
                path = "@" + Name;
            }
            else if (p is null || p.Kind == TemplateNodeKind.Application)
            {
                path = Name;
            }
            else if (Name.StartsWith('['))
            {
                path = p.Path + Name;
            }
            else
            {
                path = p.Path + "." + Name;
            }

            _path = path;
            return path;
        }
    }

    /// <summary>`u8` / `char` の配列 (結果ツリーで要素を展開せず、値の列に Hex または文字列を表示する。TPL-07 の仕様 6)。</summary>
    public bool IsByteOrCharArray => Kind == TemplateNodeKind.Array && Type is Runtime.PrimType { Size: 1 or 2 or 4 } p
        && (p.IsChar || (p.Size == 1 && p.Kind is Runtime.PrimKind.UInt or Runtime.PrimKind.SInt));

    /// <summary>子を持つ (構造体・共用体・配列・ポインタ・ビットフィールド・セクション)。評価はしない。</summary>
    public bool HasChildren => Children0 is not null && (Kind != TemplateNodeKind.Pointer || (Pointer is { IsNull: false, OutOfRange: false }));

    /// <summary>子を評価済みか。</summary>
    public bool IsEvaluated => Children0 is null || Children0.IsMaterialized;

    /// <summary>子の数 (評価していない構造体は評価する)。</summary>
    public long ChildCount => Children0 is null ? 0 : Result.Run(() => Result.Evaluator.ChildCount(this));

    /// <summary>評価せずに分かる子の数 (配列の要素数など)。分からなければ null。</summary>
    public long? KnownChildCount => Children0?.KnownCount;

    /// <summary>
    /// 子の一覧。配列は仮想的な一覧で、要素は参照したときに作る (1 億要素でもメモリを使わない)。<c>inline</c> の構造体は、その子を
    /// 直接並べる。
    /// </summary>
    public IReadOnlyList<TemplateNode> Children => Children0 is null ? [] : new ChildList(this);

    public TemplateNode GetChild(long index) => Result.Run(() => Result.Evaluator.GetChild(this, index));

    /// <summary>名前で子を探す (<c>inline</c> の構造体の子も探す)。</summary>
    public TemplateNode? FindChild(string name) => Result.Run(() => Result.Evaluator.FindChild(this, name));

    /// <summary>ポインタの指す先のノード (null、範囲外、既出の場合は null)。</summary>
    public TemplateNode? PointerTarget => Kind == TemplateNodeKind.Pointer ? Result.Run(() => Result.Evaluator.PointerTarget(this)) : null;

    /// <summary>
    /// 値 (TPL-25 の <c>value</c>)。整数は <see cref="long"/> / <see cref="ulong"/> (64 bit を超えるものは <see cref="Int128"/> /
    /// <see cref="UInt128"/>)、浮動小数点は <see cref="double"/>、文字列は <see cref="string"/>、バイト列は <see cref="byte"/>[]、
    /// 構造体・配列は null。<c>read</c> の変換があれば変換後の値。
    /// </summary>
    public object? Value => Result.Run(() => Result.Evaluator.PublicValue(this, converted: true));

    /// <summary>変換前の生の値 (<c>std::raw</c>)。</summary>
    public object? RawValue => Result.Run(() => Result.Evaluator.PublicValue(this, converted: false));

    /// <summary>値の列の文字列 (現在の地域設定)。</summary>
    public string DisplayValue => GetDisplay(TemplateDisplayContext.Current);

    public string GetDisplay(TemplateDisplayContext context) => Result.Run(() => Result.Evaluator.Display(this, context));

    /// <summary>コメント列 (<c>comment</c> 属性。表示するときに評価する)。</summary>
    public string? Comment => Result.Run(() => Result.Evaluator.Comment(this));

    /// <summary>日時の型の値 (UTC)。</summary>
    public DateTime? DateTimeUtc => Kind == TemplateNodeKind.DateTime ? Result.Run(() => Result.Evaluator.DateTimeOf(this)) : null;

    /// <summary>色の型の値 (0xAARRGGBB)。</summary>
    public uint? ColorArgb => Kind == TemplateNodeKind.Color ? Result.Run(() => Result.Evaluator.ColorOf(this)) : null;

    /// <summary>ツールチップの日時の表示 (UTC と地域の時刻の両方。TPL-03 の仕様 5)。</summary>
    public (string Utc, string Local)? DateTimeTooltip(TemplateDisplayContext context)
    {
        DateTime? utc = DateTimeUtc;
        if (utc is null)
        {
            return null;
        }

        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utc.Value, context.TimeZone);
        return (utc.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC",
            local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    /// <summary>列挙型の名前と値の一覧 (編集のドロップダウン。TPL-22)。</summary>
    public IReadOnlyList<KeyValuePair<string, Int128>> EnumMembers => Result.Run(() => Result.Evaluator.EnumMembers(this));

    /// <summary>編集できるか、できない理由 (TPL-22 の仕様 6)。</summary>
    public TemplateEditBlock EditBlock => Result.Evaluator.EditBlockOf(this);

    /// <summary><c>read</c> の変換だけがあり <c>write</c> がない (変換後の値では編集できず、生の値の編集になる。TPL-14 の仕様 5)。</summary>
    public bool EditsRawValueOnly => Result.Evaluator.EditsRawOnly(this);

    /// <summary>
    /// 入力した値を、このパターンの範囲に書き込むバイト列にする (TPL-22。<c>write</c> の変換を通す)。書き込みは呼び出し側が
    /// <see cref="Offset"/> に行う。<paramref name="raw"/> が true なら <c>read</c> の変換の前の値として扱う。
    /// </summary>
    public TemplateEditResult EncodeValue(string input, bool raw = false) =>
        Result.Run(() => Result.Evaluator.EncodeEdit(this, input, raw));

    /// <summary>
    /// <see cref="EncodeValue(string, bool)"/> と同じ。文字列が範囲より短い場合に、残りを 0 で埋めるか (<paramref name="padWithZeros"/>)、
    /// 入力の分だけを書く (残りは元のまま) かを選べる (TPL-22 の仕様 3)。
    /// </summary>
    public TemplateEditResult EncodeValue(string input, bool raw, bool padWithZeros) =>
        Result.Run(() => Result.Evaluator.EncodeEdit(this, input, raw, padWithZeros));

    public override string ToString() => $"{Path} : {TypeName} @0x{Offset:X} ({Size})";

    // ───────────── 内部 ─────────────

    private string? _path;

    internal string? DisplayNameAttr { get; set; }

    internal TemplateNode? Owner { get; set; }

    internal TemplateNode? DisplayParentOverride { get; set; }

    internal ChildSource? Children0 { get; set; }

    internal List<TemplateDiagnostic>? DiagnosticsList { get; set; }

    internal RtType? Type { get; set; }

    internal Frame? Context { get; set; }

    internal List<AttributeNode> AttributeNodes { get; set; } = [];

    internal Variable? ComputedVariable { get; set; }

    internal object? ValueCache { get; set; }

    internal object? ConvertedCache { get; set; }

    internal TemplateNode? InstanceTarget { get; set; }

    internal bool ValueCached { get; set; }

    internal bool IsComputed => Kind == TemplateNodeKind.Computed;

    /// <summary>ビットフィールドの要素の値 (読み込み済み)。</summary>
    internal ulong BitValue { get; set; }

    internal void AddDiagnostic(TemplateDiagnostic d)
    {
        lock (Result.SyncRoot)
        {
            DiagnosticsList ??= [];
            DiagnosticsList.Add(d);
        }
    }

    private TemplateNode? DisplayParent()
    {
        if (DisplayParentOverride is not null)
        {
            return DisplayParentOverride;
        }

        TemplateNode? p = Owner;
        while (p is not null && p.Attributes.Inline && p.Kind is TemplateNodeKind.Struct or TemplateNodeKind.Union && p.ArrayIndex < 0
            && p.Owner is not null)
        {
            p = p.Owner;
        }

        return p;
    }

    private sealed class ChildList(TemplateNode node) : IReadOnlyList<TemplateNode>
    {
        private IReadOnlyList<TemplateNode>? _flat;
        private long _count = -1;

        public TemplateNode this[int index] => Flat is { } f ? f[index] : node.GetChild(index);

        public int Count
        {
            get
            {
                if (Flat is { } f)
                {
                    return f.Count;
                }

                if (_count < 0)
                {
                    _count = node.ChildCount;
                }

                return (int)Math.Min(int.MaxValue, _count);
            }
        }

        /// <summary>構造体の子 (inline を展開した一覧)。配列は null (要素を必要なときに作る)。</summary>
        private IReadOnlyList<TemplateNode>? Flat
        {
            get
            {
                if (_flat is null && node.Children0 is not OnDemandChildren)
                {
                    _flat = node.Result.Run(() => node.Result.Evaluator.DisplayChildren(node));
                }

                return _flat;
            }
        }

        public IEnumerator<TemplateNode> GetEnumerator()
        {
            int n = Count;
            for (int i = 0; i < n; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>値の書き込みの失敗の種類 (画面の文言を選ぶ。TPL-22 の「エラー」)。</summary>
public enum TemplateEditError
{
    None,

    /// <summary>入力を読めない。</summary>
    Invalid,

    /// <summary>型の範囲外。</summary>
    OutOfRange,

    /// <summary>文字列が範囲より長い (TPL-22 の仕様 3)。</summary>
    TooLong,

    /// <summary>編集できないパターン (<see cref="TemplateEditBlock"/>)。</summary>
    Blocked,

    /// <summary><c>write</c> の変換の関数がエラーになった。</summary>
    WriteFunctionFailed,
}

/// <summary>値の書き込みの結果 (TPL-22)。</summary>
public sealed record TemplateEditResult(byte[]? Bytes, string? Error)
{
    /// <summary>書き込む位置 (ビットフィールドの要素は、そのビットを含むまとまりの先頭)。</summary>
    public long Offset { get; init; }

    public TemplateEditError Code { get; init; }

    public bool Success => Bytes is not null;
}
