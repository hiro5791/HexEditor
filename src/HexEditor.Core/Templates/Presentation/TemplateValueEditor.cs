using System.Globalization;
using HexEditor.Core.Expressions;

namespace HexEditor.Core.Templates.Presentation;

/// <summary>結果ツリーの値の編集欄の種類 (TPL-22 の仕様 1 の表)。</summary>
public enum TemplateEditorKind
{
    Integer,
    Float,
    Enum,
    Flags,
    Bool,
    String,
    DateTime,

    /// <summary>GUID / IP / MAC (それぞれの書式のテキスト)。</summary>
    Text,

    /// <summary>ビットフィールドの要素 (ビット数の範囲で検証する整数)。</summary>
    Bitfield,
    Color,
}

/// <summary>列挙型・フラグ型の 1 項目。</summary>
public sealed record TemplateEditChoice(string Name, Int128 Value, bool IsChecked);

/// <summary>書き込む内容 (位置とバイト列)。失敗なら <see cref="Error"/>。</summary>
public sealed record TemplateEditPlan(long Offset, byte[]? Bytes, TemplateEditError Error, string? Detail)
{
    public bool Success => Bytes is not null && Error == TemplateEditError.None;
}

/// <summary>
/// 結果ツリーでの値の編集 (TPL-22)。型に応じた編集欄の種類と初期値を決め、入力を検証して書き込むバイト列にする
/// (<c>write</c> の変換を通す。<c>read</c> だけのパターンは生の値の編集。TPL-14 の仕様 5)。書き込みはパターンの範囲の上書きだけで、
/// 挿入モードでも長さを変えない (仕様 2)。書き込み自体は呼び出し側がドキュメントの Undo 1 回分として行う (仕様 5)。
/// </summary>
public sealed class TemplateValueEditSession
{
    private readonly TemplateNode _node;
    private readonly Int128 _originalFlags;

    private TemplateValueEditSession(TemplateNode node, TemplateEditorKind kind, string initialText, IReadOnlyList<TemplateEditChoice> choices,
        Int128 originalFlags)
    {
        _node = node;
        Kind = kind;
        InitialText = initialText;
        Choices = choices;
        _originalFlags = originalFlags;
    }

    public TemplateNode Node => _node;

    public TemplateEditorKind Kind { get; }

    /// <summary>編集欄の初期値 (生の値の編集では生の値。TC-TPL-14-02)。</summary>
    public string InitialText { get; }

    /// <summary>列挙型の名前の一覧、フラグ型のビットの一覧 (今のチェックの状態つき)。</summary>
    public IReadOnlyList<TemplateEditChoice> Choices { get; }

    /// <summary><c>read</c> だけの変換があり、生の値を編集する (編集欄の付近に説明を出す)。</summary>
    public bool EditsRawValue { get; private init; }

    /// <summary>文字列の最大のバイト数 (パターンの範囲)。</summary>
    public long MaxBytes => _node.Size;

    /// <summary>ビットフィールドの要素のビット数。</summary>
    public int? BitSize => _node.BitSize;

    public bool BoolValue { get; private init; }

    /// <summary>
    /// 編集を始める (評価器のスレッドで呼ぶ)。編集できないパターン (TPL-22 の仕様 6) なら null で、理由は <paramref name="block"/>。
    /// <paramref name="documentReadOnly"/> は読み取り専用のドキュメント。
    /// </summary>
    public static TemplateValueEditSession? Create(TemplateNode node, bool documentReadOnly, out TemplateEditBlock block)
    {
        block = node.EditBlock;
        if (block != TemplateEditBlock.None)
        {
            return null;
        }

        if (documentReadOnly)
        {
            block = TemplateEditBlock.ReadOnlyAttribute;
            return null;
        }

        bool raw = node.EditsRawValueOnly;
        object? value = raw ? node.RawValue : node.Value;
        TemplateEditorKind kind = node.Kind switch
        {
            TemplateNodeKind.Enum => TemplateEditorKind.Enum,
            TemplateNodeKind.Flags => TemplateEditorKind.Flags,
            TemplateNodeKind.Bool => TemplateEditorKind.Bool,
            TemplateNodeKind.String => TemplateEditorKind.String,
            TemplateNodeKind.Array => TemplateEditorKind.String,
            TemplateNodeKind.DateTime => TemplateEditorKind.DateTime,
            TemplateNodeKind.Guid or TemplateNodeKind.IpAddress or TemplateNodeKind.MacAddress => TemplateEditorKind.Text,
            TemplateNodeKind.BitfieldMember => TemplateEditorKind.Bitfield,
            TemplateNodeKind.Color => TemplateEditorKind.Color,
            TemplateNodeKind.Float => TemplateEditorKind.Float,
            _ => TemplateEditorKind.Integer,
        };
        if (raw && kind is TemplateEditorKind.Float or TemplateEditorKind.Enum or TemplateEditorKind.Flags or TemplateEditorKind.Color)
        {
            kind = value is double ? TemplateEditorKind.Float : TemplateEditorKind.Integer;
        }

        Int128 current = ToInt(value);
        IReadOnlyList<TemplateEditChoice> choices = kind switch
        {
            TemplateEditorKind.Enum => [.. node.EnumMembers.Select(m => new TemplateEditChoice(m.Key, m.Value, m.Value == current))],
            TemplateEditorKind.Flags => [.. node.EnumMembers.Where(m => m.Value != 0).Select(m => new TemplateEditChoice(m.Key, m.Value, (current & m.Value) == m.Value))],
            _ => [],
        };
        string initial = kind switch
        {
            TemplateEditorKind.String => value as string ?? node.GetDisplay(TemplateDisplayContext.Invariant).Trim('"'),
            TemplateEditorKind.DateTime => node.DateTimeUtc is { } dt ? dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : string.Empty,
            TemplateEditorKind.Color => node.ColorArgb is { } argb ? "#" + (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture) : string.Empty,
            TemplateEditorKind.Text => node.GetDisplay(TemplateDisplayContext.Invariant),
            TemplateEditorKind.Float => value is double d ? d.ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            TemplateEditorKind.Enum => choices.FirstOrDefault(c => c.IsChecked)?.Name ?? current.ToString(CultureInfo.InvariantCulture),
            TemplateEditorKind.Bool => value is bool b && b ? "true" : "false",
            _ => current.ToString(CultureInfo.InvariantCulture),
        };
        return new TemplateValueEditSession(node, kind, initial, choices, current)
        {
            EditsRawValue = raw,
            BoolValue = value is bool bv ? bv : current != 0,
        };
    }

    private static Int128 ToInt(object? value) => value switch
    {
        long l => l,
        ulong u => u,
        int i => i,
        Int128 i => i,
        UInt128 u => (Int128)u,
        bool b => b ? 1 : 0,
        double d when double.IsFinite(d) && Math.Abs(d) < 9.2e18 => (Int128)Math.Round(d),
        _ => 0,
    };

    /// <summary>
    /// 入力 (整数は入力式。接頭辞のない数値は 10 進。00 の 6 章) を検証し、書き込む内容にする。
    /// <paramref name="padWithZeros"/>: 文字列が範囲より短い場合に残りを 0 で埋める (既定。TPL-22 の仕様 3)。
    /// <paramref name="localTime"/>: 日時の入力を地域の時刻として読む (false は UTC)。
    /// </summary>
    public TemplateEditPlan Plan(string input, bool padWithZeros = true, bool localTime = false)
    {
        string text = input.Trim();
        switch (Kind)
        {
            case TemplateEditorKind.Integer:
            case TemplateEditorKind.Bitfield:
                if (!ExpressionEvaluator.TryEvaluateWide(text, NoContext.Instance, out Int128 value, out ExpressionException? error, DefaultRadix.Decimal))
                {
                    return new TemplateEditPlan(0, null, error?.Error == ExpressionError.Overflow ? TemplateEditError.OutOfRange : TemplateEditError.Invalid,
                        error?.Message);
                }

                return Encode(value.ToString(CultureInfo.InvariantCulture), padWithZeros);
            case TemplateEditorKind.Enum:
            {
                TemplateEditChoice? byName = Choices.FirstOrDefault(c => string.Equals(c.Name, text, StringComparison.Ordinal));
                if (byName is not null)
                {
                    return Encode(byName.Value.ToString(CultureInfo.InvariantCulture), padWithZeros);
                }

                // 「数値で入力」
                if (!ExpressionEvaluator.TryEvaluateWide(text, NoContext.Instance, out Int128 number, out ExpressionException? err, DefaultRadix.Decimal))
                {
                    return new TemplateEditPlan(0, null, TemplateEditError.Invalid, err?.Message);
                }

                return Encode(number.ToString(CultureInfo.InvariantCulture), padWithZeros);
            }

            case TemplateEditorKind.DateTime:
            {
                if (!System.DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out DateTime parsed)
                    && !System.DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
                {
                    return new TemplateEditPlan(0, null, TemplateEditError.Invalid, null);
                }

                DateTime utc = localTime ? TimeZoneInfo.ConvertTimeToUtc(System.DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), TimeZoneInfo.Local)
                    : System.DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
                return Encode(utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture), padWithZeros);
            }

            case TemplateEditorKind.Bool:
                return Encode(text is "1" or "true" or "True" ? "1" : "0", padWithZeros);
            default:
                return Encode(text, padWithZeros);
        }
    }

    /// <summary>フラグ型: チェックした名前の一覧から書き込む内容を作る (名前のないビットは元のまま。TC-TPL-22-01)。</summary>
    public TemplateEditPlan PlanFlags(IEnumerable<string> checkedNames)
    {
        var set = new HashSet<string>(checkedNames, StringComparer.Ordinal);
        Int128 known = 0;
        Int128 value = 0;
        foreach (TemplateEditChoice c in Choices)
        {
            known |= c.Value;
            if (set.Contains(c.Name))
            {
                value |= c.Value;
            }
        }

        value |= _originalFlags & ~known;
        return Encode(value.ToString(CultureInfo.InvariantCulture), padWithZeros: true);
    }

    /// <summary>bool: チェックボックスの状態から書き込む内容を作る。</summary>
    public TemplateEditPlan PlanBool(bool value) => Encode(value ? "1" : "0", padWithZeros: true);

    private TemplateEditPlan Encode(string text, bool padWithZeros)
    {
        TemplateEditResult r = _node.EncodeValue(text, EditsRawValue, padWithZeros);
        if (!r.Success)
        {
            return new TemplateEditPlan(r.Offset, null, r.Code == TemplateEditError.None ? TemplateEditError.Invalid : r.Code, r.Error);
        }

        return new TemplateEditPlan(r.Offset, r.Bytes, TemplateEditError.None, null);
    }

    /// <summary>名前と読み取り関数を使わない入力式の文脈 (テンプレートの値の入力)。</summary>
    private sealed class NoContext : IExpressionContext
    {
        public static readonly NoContext Instance = new();

        public long Cursor => 0;

        public long Length => 0;

        public long SelectionStart => 0;

        public long SelectionLength => 0;

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination) => false;
    }
}
