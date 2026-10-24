using System.Globalization;

namespace HexEditor.Core.Templates.Inference;

/// <summary>テンプレートの推定のエンディアンの指定 (TPL-38 の仕様 1)。</summary>
public enum InferenceEndian
{
    Auto,
    Little,
    Big,
}

/// <summary>推定したフィールドの種類 (TPL-39 の仕様 2 の表)。</summary>
public enum InferredKind
{
    Padding,
    Magic,
    Constant,
    Counter,
    Time,
    Offset,
    Length,
    Float,
    AsciiString,
    Utf16String,
    Flags,
    Enum,
    Integer,
    Unknown,
}

/// <summary>確度 (TPL-39 の仕様 2 の表の「確度」の列)。値が大きいほど確か。</summary>
public enum InferenceConfidence
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>
/// 画面・下書きに出す文 1 つ (リソースのキーと引数)。文は App がリソースから作り、数値は地域設定で書式化する (00 の 5.4)。
/// 引数の整数 (int・long) は <see cref="Text.MessageFormat"/> で桁区切りを付けて出す。
/// </summary>
public sealed record InferenceMessage(string Key, params object[] Args)
{
    public override string ToString() => Key + "(" + string.Join(", ", Args.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture))) + ")";
}

/// <summary>推定を始める入力 (TPL-38 の仕様 1)。</summary>
public sealed record TemplateInferenceRequest
{
    /// <summary>範囲の開始 (ドキュメント上のオフセット。レコードの先頭とする)。</summary>
    public long Start { get; init; }

    /// <summary>範囲の長さ。レコード長と数の両方を指定したときは「レコード長 × 数」に置き換える。</summary>
    public long Length { get; init; }

    /// <summary>レコード長 (null は「自動」)。1〜65,536。</summary>
    public int? RecordLength { get; init; }

    /// <summary>レコードの数 (null は「範囲いっぱい」)。</summary>
    public long? RecordCount { get; init; }

    public InferenceEndian Endian { get; init; } = InferenceEndian.Auto;

    /// <summary>レコード長の最大値 (TPL-38 の仕様 1)。</summary>
    public const int MaxRecordLength = 65_536;

    /// <summary>範囲の最小の長さ (TPL-38 の仕様 2)。</summary>
    public const long MinRangeLength = 32;

    /// <summary>範囲に入るべきレコードの最小の数 (TPL-38 の仕様 2)。</summary>
    public const int MinRecords = 4;

    /// <summary>実際に使う範囲の長さ (レコード長と数の両方を指定したら「レコード長 × 数」。ドキュメントの末尾で切る)。</summary>
    public long EffectiveLength(long documentLength)
    {
        long available = Math.Max(0, documentLength - Start);
        long length = RecordLength is int k && RecordCount is long n ? (long)k * n : Length;
        return Math.Clamp(length, 0, available);
    }

    /// <summary>範囲の条件 (TPL-38 の仕様 2) を満たさない理由 (満たせば null)。「推定」ボタンを無効にして出す。</summary>
    public InferenceMessage? Validate(long documentLength)
    {
        if (Start < 0 || Start > documentLength)
        {
            return new InferenceMessage("TplInfer_Block_Start");
        }

        if (RecordLength is int k && (k < 1 || k > MaxRecordLength))
        {
            return new InferenceMessage("TplInfer_Block_RecordLength", 1, MaxRecordLength);
        }

        if (RecordCount is long c && c < MinRecords)
        {
            return new InferenceMessage("TplInfer_Block_FourRecords");
        }

        long length = EffectiveLength(documentLength);
        if (length < MinRangeLength)
        {
            return new InferenceMessage("TplInfer_Block_MinLength", (int)MinRangeLength);
        }

        // レコード長が自動なら、最小の候補 (2 バイト) でも 4 個入ること。指定なら、その長さで 4 個入ること。
        long records = RecordLength is int len ? length / len : length / 2;
        return records < MinRecords ? new InferenceMessage("TplInfer_Block_FourRecords") : null;
    }
}

/// <summary>レコード長の候補 1 つ (TPL-38 の画面の一覧: k、得点、レコードの数)。</summary>
public sealed record RecordLengthCandidate(int Length, double Score, long RecordCount);

/// <summary>
/// フィールドの型 (下書きの型)。<see cref="Id"/> は <c>u8</c>〜<c>u64</c>、<c>s8</c>〜<c>s64</c>、<c>f32</c>、<c>f64</c>、<c>time32_t</c>、
/// <c>time64_t</c>、<c>filetime</c>、<c>dosdatetime</c>、<c>char</c> (ASCII の文字列)、<c>char16</c> (UTF-16 の文字列)、<c>bytes</c> (<c>u8</c> の配列)、
/// <c>padding</c>、<c>enum</c>、<c>flags</c>。<see cref="Size"/> はフィールドのバイト数。
/// </summary>
public sealed record InferenceType(string Id, int Size)
{
    public bool IsArray => Id is "char" or "char16" or "bytes" or "padding";

    public bool IsEnumOrFlags => Id is "enum" or "flags";

    /// <summary>数値として読む型か (エンディアンの指定が意味を持つ)。</summary>
    public bool IsEndianSensitive => Size > 1 && Id is not ("char" or "bytes" or "padding");

    /// <summary>配列の要素の数 (文字列は文字数)。</summary>
    public int ArrayLength => Id == "char16" ? Size / 2 : Size;

    /// <summary>整数の基底型 (<c>u16</c> など。列挙・フラグの基底、整数の型)。</summary>
    public static string UnsignedOf(int size) => "u" + (size * 8).ToString(CultureInfo.InvariantCulture);

    /// <summary>画面の「型」の列の表示 (<c>u32</c>、<c>char[16]</c>、<c>Kind_18 : u16</c>)。</summary>
    public string Display(string? typeName) => Id switch
    {
        "char" or "char16" => Id + "[" + ArrayLength.ToString(CultureInfo.InvariantCulture) + "]",
        "bytes" => "u8[" + Size.ToString(CultureInfo.InvariantCulture) + "]",
        "padding" => "padding[" + Size.ToString(CultureInfo.InvariantCulture) + "]",
        "enum" or "flags" => (typeName ?? Id) + " : " + UnsignedOf(Size),
        _ => Id,
    };

    public override string ToString() => Display(null);
}

/// <summary>フィールドに当たった規則 1 つ (種類・確度・下書きの型・理由)。</summary>
public sealed record InferredRule(InferredKind Kind, InferenceConfidence Confidence, InferenceType Type, bool? BigEndian, InferenceMessage Reason)
{
    /// <summary>列挙の値 (小さい順)、フラグのビットの値 (小さい順)。</summary>
    public IReadOnlyList<ulong> Values { get; init; } = [];

    /// <summary>連番の a と d (理由と試験用)。</summary>
    public (long Start, long Step)? Counter { get; init; }

    /// <summary>値の範囲 (最大 − 最小。エンディアンの比べ方。TPL-39 の仕様 4)。</summary>
    public ulong Spread { get; init; }
}

/// <summary>推定したフィールド 1 つ (TPL-39)。</summary>
public sealed record InferredField
{
    /// <summary>レコードの先頭からのオフセット (列)。</summary>
    public required int Offset { get; init; }

    public required int Size { get; init; }

    /// <summary>採った規則 (候補の 1 位)。</summary>
    public required InferredRule Rule { get; init; }

    /// <summary>当たった規則 (確度の順に最大 3 つ。先頭が <see cref="Rule"/>。TPL-39 の仕様 6)。</summary>
    public IReadOnlyList<InferredRule> Candidates { get; init; } = [];

    /// <summary>当たらなかった規則のうち最も近いもの (TPL-39 の仕様 8)。</summary>
    public InferenceMessage? NearMiss { get; init; }

    /// <summary>名前 (TPL-39 の仕様 7)。埋め草は null。</summary>
    public string? Name { get; init; }

    public InferredKind Kind => Rule.Kind;

    public InferenceConfidence Confidence => Rule.Confidence;

    public InferenceType Type => Rule.Type;

    public int End => Offset + Size;
}
