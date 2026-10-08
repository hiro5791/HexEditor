using HexEditor.Core.View;

namespace HexEditor.Core.Clipboard;

/// <summary>「形式を選択して貼り付け」で解釈を試みる形式 (EDIT-26 の仕様 2)。</summary>
public enum PasteFormat
{
    /// <summary>クリップボードのバイナリ形式 (`HexEditor.Binary`、他のエディタの形式)。</summary>
    Binary,

    /// <summary>エクスプローラーでコピーしたファイルの内容 (EDIT-30 と同じ)。</summary>
    FileContent,

    IntelHex,
    SRecord,
    UUEncode,
    XXEncode,
    Json,
    ScreenDump,
    Array,
    Escape,

    /// <summary>8 桁ちょうどの 2 進の数値列 (EDIT-25 の 2 進の出力)。</summary>
    BinaryNumbers,
    HexString,
    Base32,
    Base32Hex,
    Base64,
    Ascii85,
    Z85,
    QuotedPrintable,
    UrlEncoded,
    Decimal,
    Octal,
    Text,
}

/// <summary>解釈できなかった理由と位置 (「エラー: 3 行目 12 文字目」。行・文字は 1 から数える)。</summary>
public sealed record PasteError(int Line, int Column, string Reason);

/// <summary>アドレスを持つ形式 (Intel HEX・S-record) の 1 つの連続した範囲。</summary>
public sealed record AddressedSegment(long Address, byte[] Data);

/// <summary>改行の変換 (テキストの形式)。</summary>
public enum NewLineConversion
{
    Keep,
    CrLf,
    Lf,
}

/// <summary>判別の設定。</summary>
public sealed record PasteOptions
{
    /// <summary>テキストの形式の文字コード (既定は現在の表示文字コード)。</summary>
    public TextEncoding Encoding { get; init; } = TextEncoding.Ascii;

    public NewLineConversion NewLines { get; init; } = NewLineConversion.Keep;

    /// <summary>テキストの末尾に NUL を付ける。</summary>
    public bool AppendNul { get; init; }

    /// <summary>配列表記の要素の大きさ (1 / 2 / 4 / 8)。</summary>
    public int ElementSize { get; init; } = 1;

    public bool BigEndian { get; init; }

    /// <summary>
    /// 最後に使った形式 (設定「前回の形式を優先する」がオンのとき。仕様 6)。同じ順位の候補の中で先頭にする。
    /// </summary>
    public PasteFormat? Preferred { get; init; }

    /// <summary>Intel HEX・S-record の「カーソル位置に貼る」でレコード間の隙間を埋める値 (既定 FF。ENG-38 と同じ)。</summary>
    public byte GapFill { get; init; } = 0xFF;
}

/// <summary>1 つの形式で解釈した結果。</summary>
public sealed class PasteCandidate
{
    internal PasteCandidate(PasteFormat format, byte[]? bytes, PasteError? error, IReadOnlyList<AddressedSegment>? segments = null,
        int checksumErrors = 0)
    {
        Format = format;
        Bytes = bytes;
        Error = error;
        Segments = segments;
        ChecksumErrors = checksumErrors;
    }

    public PasteFormat Format { get; }

    /// <summary>確からしさの順位 (小さいほど上。仕様 3)。</summary>
    public int Rank => PasteDetector.RankOf(Format);

    /// <summary>結果のバイト列 (アドレスを持つ形式は「カーソル位置に貼る」の場合)。解釈できなければ null。</summary>
    public byte[]? Bytes { get; }

    public long Length => Bytes?.LongLength ?? 0;

    /// <summary>解釈できなかった理由。null なら選べる。</summary>
    public PasteError? Error { get; }

    public bool IsValid => Error is null && Bytes is not null;

    /// <summary>Intel HEX・S-record のレコードの範囲 (「レコードのアドレスに書く」で使う)。</summary>
    public IReadOnlyList<AddressedSegment>? Segments { get; }

    /// <summary>チェックサムが一致しない行の数 (Intel HEX・S-record)。0 でなければ確認ダイアログを出す。</summary>
    public int ChecksumErrors { get; }

    public bool HasAddresses => Segments is not null;

    public override string ToString() => IsValid ? $"{Format} ({Length} bytes)" : $"{Format} (error {Error})";
}
