using System.Numerics;

namespace HexEditor.Sdk;

/// <summary>
/// A byte range: start offset and length. The end (<see cref="End"/>) is exclusive.
/// <ja>バイトの範囲 (開始と長さ)。終了 (<see cref="End"/>) は含まない。</ja>
/// </summary>
/// <param name="Start">Start offset. <ja>開始オフセット。</ja></param>
/// <param name="Length">Length in bytes. <ja>長さ (バイト)。</ja></param>
public readonly record struct ByteRange(long Start, long Length)
{
    /// <summary>The offset just after the range. <ja>範囲の直後のオフセット。</ja></summary>
    public long End => Start + Length;
}

/// <summary>
/// A range argument: either a <see cref="ByteRange"/> or a text in the input expression syntax
/// (for example <c>"sel"</c>, <c>"0x100:0x20"</c> (start:length) or <c>"0x100..0x120"</c> (start..end, end exclusive)).
/// <ja>範囲の引数。<see cref="ByteRange"/> か入力式の文字列 (例: <c>"sel"</c>、<c>"0x100:0x20"</c> (開始:長さ)、<c>"0x100..0x120"</c> (開始..終了。終了は含まない))。</ja>
/// </summary>
[TsType("ByteRange | string")]
public readonly struct RangeArg
{
    private RangeArg(ByteRange? range, string? expression)
    {
        Range = range;
        Expression = expression;
    }

    /// <summary>The range when given as numbers. <ja>数値で与えた範囲。</ja></summary>
    public ByteRange? Range { get; }

    /// <summary>The range when given as an input expression. <ja>入力式で与えた範囲。</ja></summary>
    public string? Expression { get; }

    /// <summary>Converts a range. <ja>範囲から変換する。</ja></summary>
    public static implicit operator RangeArg(ByteRange range) => new(range, null);

    /// <summary>Converts an input expression. <ja>入力式から変換する。</ja></summary>
    public static implicit operator RangeArg(string expression) => new(null, expression);

    /// <summary>Text for messages. <ja>メッセージ用の文字列。</ja></summary>
    public override string ToString() => Expression ?? Range?.ToString() ?? "";
}

/// <summary>
/// A numeric argument (offset, length): either a number or a text in the input expression syntax (for example <c>"end-0x10"</c>).
/// <ja>数値の引数 (オフセット・長さ)。数値か入力式の文字列 (例: <c>"end-0x10"</c>)。</ja>
/// </summary>
[TsType("number | string")]
public readonly struct OffsetArg
{
    private OffsetArg(long? value, string? expression)
    {
        Value = value;
        Expression = expression;
    }

    /// <summary>The value when given as a number. <ja>数値で与えた値。</ja></summary>
    public long? Value { get; }

    /// <summary>The value when given as an input expression. <ja>入力式で与えた値。</ja></summary>
    public string? Expression { get; }

    /// <summary>Converts a number. <ja>数値から変換する。</ja></summary>
    public static implicit operator OffsetArg(long value) => new(value, null);

    /// <summary>Converts an input expression. <ja>入力式から変換する。</ja></summary>
    public static implicit operator OffsetArg(string expression) => new(null, expression);

    /// <summary>Text for messages. <ja>メッセージ用の文字列。</ja></summary>
    public override string ToString() => Expression ?? Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
}

/// <summary>
/// A byte sequence argument: bytes (JavaScript: <c>Uint8Array</c> or an array of numbers) or a Hex string (for example <c>"DE AD"</c>).
/// Some arguments also accept the word <c>"random"</c>.
/// <ja>バイト列の引数。バイト列 (JavaScript では <c>Uint8Array</c> か数値の配列) か Hex 文字列 (例: <c>"DE AD"</c>)。一部の引数は <c>"random"</c> も受け付ける。</ja>
/// </summary>
[TsType("Uint8Array | number[] | string")]
public readonly struct BytePattern
{
    private BytePattern(byte[]? bytes, string? text)
    {
        Bytes = bytes;
        Text = text;
    }

    /// <summary>The bytes when given as bytes. <ja>バイト列で与えた値。</ja></summary>
    public byte[]? Bytes { get; }

    /// <summary>The text when given as a string (Hex or a keyword). <ja>文字列で与えた値 (Hex かキーワード)。</ja></summary>
    public string? Text { get; }

    /// <summary>Converts bytes. <ja>バイト列から変換する。</ja></summary>
    public static implicit operator BytePattern(byte[] bytes) => new(bytes, null);

    /// <summary>Converts a Hex string or keyword. <ja>Hex 文字列またはキーワードから変換する。</ja></summary>
    public static implicit operator BytePattern(string text) => new(null, text);
}

/// <summary>
/// A text encoding argument: a code page number (for example <c>932</c>) or a name (for example <c>"shift_jis"</c>, <c>"utf-16le"</c>).
/// <ja>文字コードの引数。コードページ番号 (例: <c>932</c>) か名前 (例: <c>"shift_jis"</c>、<c>"utf-16le"</c>)。</ja>
/// </summary>
[TsType("number | string")]
public readonly struct EncodingArg
{
    private EncodingArg(int? codePage, string? name)
    {
        CodePage = codePage;
        Name = name;
    }

    /// <summary>The code page number. <ja>コードページ番号。</ja></summary>
    public int? CodePage { get; }

    /// <summary>The encoding name. <ja>文字コードの名前。</ja></summary>
    public string? Name { get; }

    /// <summary>Converts a code page number. <ja>コードページ番号から変換する。</ja></summary>
    public static implicit operator EncodingArg(int codePage) => new(codePage, null);

    /// <summary>Converts a name. <ja>名前から変換する。</ja></summary>
    public static implicit operator EncodingArg(string name) => new(null, name);

    /// <summary>Text for messages. <ja>メッセージ用の文字列。</ja></summary>
    public override string ToString() => Name ?? CodePage?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
}

/// <summary>
/// The operand of a data operation: a number (JavaScript: <c>number</c> or <c>bigint</c>) or a Hex string used as a repeating key.
/// <ja>データ演算のオペランド。数値 (JavaScript では <c>number</c> か <c>bigint</c>) か、繰り返しのキーとして使う Hex 文字列。</ja>
/// </summary>
[TsType("number | bigint | string")]
public readonly struct TransformOperand
{
    private TransformOperand(BigInteger? value, string? key)
    {
        Value = value;
        HexKey = key;
    }

    /// <summary>The numeric operand. <ja>数値のオペランド。</ja></summary>
    public BigInteger? Value { get; }

    /// <summary>The repeating key as a Hex string. <ja>繰り返しのキー (Hex 文字列)。</ja></summary>
    public string? HexKey { get; }

    /// <summary>Converts a number. <ja>数値から変換する。</ja></summary>
    public static implicit operator TransformOperand(long value) => new(value, null);

    /// <summary>Converts a large number. <ja>大きな数値から変換する。</ja></summary>
    public static implicit operator TransformOperand(BigInteger value) => new(value, null);

    /// <summary>Converts a Hex key. <ja>Hex のキーから変換する。</ja></summary>
    public static implicit operator TransformOperand(string hexKey) => new(null, hexKey);
}

/// <summary>
/// A hash algorithm: an ID (for example <c>"sha256"</c>) or a custom CRC (<see cref="CustomCrc"/>).
/// <ja>ハッシュのアルゴリズム。ID (例: <c>"sha256"</c>) かカスタム CRC (<see cref="CustomCrc"/>)。</ja>
/// </summary>
[TsType("string | CustomCrc")]
public readonly struct HashAlgorithmSpec
{
    private HashAlgorithmSpec(string? id, CustomCrc? crc)
    {
        Id = id;
        Crc = crc;
    }

    /// <summary>The algorithm ID. <ja>アルゴリズムの ID。</ja></summary>
    public string? Id { get; }

    /// <summary>The custom CRC parameters. <ja>カスタム CRC のパラメータ。</ja></summary>
    public CustomCrc? Crc { get; }

    /// <summary>Converts an ID. <ja>ID から変換する。</ja></summary>
    public static implicit operator HashAlgorithmSpec(string id) => new(id, null);

    /// <summary>Converts a custom CRC. <ja>カスタム CRC から変換する。</ja></summary>
    public static implicit operator HashAlgorithmSpec(CustomCrc crc) => new(null, crc);

    /// <summary>Text for messages. <ja>メッセージ用の文字列。</ja></summary>
    public override string ToString() => Id ?? "crc";
}

/// <summary>
/// Custom CRC parameters (the Rocksoft model). <c>id</c> is always <c>"crc"</c>.
/// <ja>カスタム CRC のパラメータ (Rocksoft のモデル)。<c>id</c> は常に <c>"crc"</c>。</ja>
/// </summary>
public sealed record CustomCrc
{
    /// <summary>Always <c>"crc"</c>. <ja>常に <c>"crc"</c>。</ja></summary>
    public string Id { get; init; } = "crc";

    /// <summary>Width in bits (1-64). <ja>ビット幅 (1〜64)。</ja></summary>
    public int Width { get; init; }

    /// <summary>Polynomial. <ja>多項式。</ja></summary>
    public ulong Poly { get; init; }

    /// <summary>Initial value. <ja>初期値。</ja></summary>
    public ulong Init { get; init; }

    /// <summary>Reflect input bytes. <ja>入力のビットを反転する。</ja></summary>
    public bool RefIn { get; init; }

    /// <summary>Reflect the output. <ja>出力のビットを反転する。</ja></summary>
    public bool RefOut { get; init; }

    /// <summary>Value XORed with the output. <ja>出力に XOR する値。</ja></summary>
    public ulong XorOut { get; init; }
}
