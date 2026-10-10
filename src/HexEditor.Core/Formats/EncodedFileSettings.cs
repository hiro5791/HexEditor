namespace HexEditor.Core.Formats;

/// <summary>Intel HEX のアドレスの形式 (TOOL-05 の仕様 3)。</summary>
public enum IntelHexAddressMode
{
    /// <summary>64 KB 以下なら I8HEX、超えるなら I32HEX。</summary>
    Auto,

    /// <summary>16 bit (拡張なし)。</summary>
    I8Hex,

    /// <summary>セグメント (レコード型 02)。</summary>
    I16Hex,

    /// <summary>リニア (レコード型 04)。</summary>
    I32Hex,
}

/// <summary>S-record のアドレスの形式 (TOOL-06 の仕様 3)。</summary>
public enum SRecordAddressMode
{
    /// <summary>範囲の最大アドレスが入る最小の形式。</summary>
    Auto,
    S1,
    S2,
    S3,
}

/// <summary>
/// エンコード形式のファイルを元の形式で保存するための設定 (TOOL-11 の仕様 1)。開いたときに元のファイルから読み取り
/// (ENG-38 の仕様 2)、「形式の設定...」で変えられる。
/// </summary>
public sealed record EncodedFileSettings
{
    /// <summary>形式の ID (ihex / srec / base64)。</summary>
    public required string Format { get; init; }

    /// <summary>改行 (元のファイルで最初に使われていたもの。既定 CRLF)。</summary>
    public string NewLine { get; init; } = "\r\n";

    /// <summary>最後の行の後に改行があったか。</summary>
    public bool FinalNewLine { get; init; } = true;

    /// <summary>
    /// データとして読めない行 (コメント行・未知のレコード型の行) があった (保存で失われるため、最初の保存で確かめる。TOOL-11 の仕様 4)。
    /// </summary>
    public bool HasForeignLines { get; init; }

    // ---- Intel HEX・S-record ----

    /// <summary>1 レコードのデータ長 (最も多く使われていた値。判定できなければ 16)。</summary>
    public int RecordLength { get; init; } = 16;

    public IntelHexAddressMode IntelMode { get; init; } = IntelHexAddressMode.Auto;

    public SRecordAddressMode SRecordMode { get; init; } = SRecordAddressMode.Auto;

    /// <summary>実行開始アドレス (<c>03</c> / <c>05</c>、<c>S7〜S9</c>)。なければ null。</summary>
    public long? StartAddress { get; init; }

    /// <summary>
    /// Intel HEX の <c>03</c> (開始セグメントアドレス) を使っていた (偽なら <c>05</c>)。
    /// </summary>
    public bool StartIsSegment { get; init; }

    /// <summary><c>S0</c> の文字列。</summary>
    public string? Header { get; init; }

    /// <summary>レコード数 (<c>S5</c> / <c>S6</c>) を出力する。</summary>
    public bool WriteCount { get; init; } = true;

    /// <summary>16 進の大文字。</summary>
    public bool UpperCase { get; init; } = true;

    /// <summary>最初のデータレコードの前に、値が 0 の拡張アドレスのレコードがあった (同じ形で書き戻すため)。</summary>
    public bool LeadingExtendedRecord { get; init; }

    // ---- Base64 ----

    /// <summary>1 行の文字数 (0 は改行なし)。</summary>
    public int LineLength { get; init; } = 76;

    public bool UrlSafe { get; init; }

    public bool Padding { get; init; } = true;

    /// <summary>デコードしたときの隙間の塗りつぶしの値 (ENG-38 の仕様 3。保存には使わない。復旧でデコードし直すときに使う)。</summary>
    public byte GapFill { get; init; } = EncodedFile.DefaultGapFill;

    /// <summary>形式の表示名のキー (ステータスバー。「Intel HEX」など)。</summary>
    public string DisplayName => Format switch
    {
        FormatIds.IntelHex => "Intel HEX",
        FormatIds.SRecord => "S-record",
        FormatIds.Base64 => "Base64",
        _ => Format,
    };
}
