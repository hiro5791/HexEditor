namespace HexEditor.Core.Formats;

/// <summary>インポートの誤り・警告の種類 (TOOL-04 の「エラー」、TOOL-05〜09)。</summary>
public enum ImportIssueKind
{
    /// <summary>行の形式の誤り (`:` / `S` で始まらない、長さが足りないなど)。</summary>
    Syntax,

    /// <summary>チェックサムの不一致。</summary>
    Checksum,

    /// <summary>未知のレコード型。</summary>
    UnknownRecord,

    /// <summary>16 進の桁が奇数。</summary>
    OddDigits,

    /// <summary>不正な文字。</summary>
    InvalidCharacter,

    /// <summary>値が範囲外 (10 進テキスト・配列の要素)。</summary>
    OutOfRange,

    /// <summary>同じアドレスに 2 回データがある (後のデータを使う)。</summary>
    Overlap,

    /// <summary>アドレスが上限 (4 GiB) を超える。</summary>
    AddressOverflow,

    /// <summary>レコードのバイト数とデータの長さが合わない。</summary>
    LengthMismatch,

    /// <summary>エンコードの長さ・パディングの誤り。</summary>
    InvalidLength,

    /// <summary>ダンプの行として判別できない。</summary>
    UnrecognizedLine,

    /// <summary>警告: <c>S5</c> / <c>S6</c> のレコード数がデータレコードの数と合わない (読み込みは続ける)。</summary>
    CountMismatch,

    /// <summary>警告: ファイルの終わりのレコード (<c>01</c> / <c>S7〜S9</c>) がない。</summary>
    MissingEnd,

    /// <summary>警告: ファイルの終わりのレコードの後にデータがある。</summary>
    DataAfterEnd,
}

/// <summary>
/// インポートの誤り 1 件 (行・列は 1 から数える。TOOL-04 の「エラー」: 行番号・列・内容)。<see cref="Expected"/> と <see cref="Actual"/> は
/// チェックサムの期待値と実際の値など。
/// </summary>
public sealed record ImportIssue(int Line, int Column, ImportIssueKind Kind, string Content, string? Expected = null, string? Actual = null)
{
    /// <summary>警告 (読み込みは続ける) か。</summary>
    public bool IsWarning => Kind is ImportIssueKind.CountMismatch or ImportIssueKind.MissingEnd or ImportIssueKind.DataAfterEnd;
}

/// <summary>誤りの一覧 (表示は最大 1,000 件。それ以上は件数だけを数える。TOOL-05 の「エラー」)。</summary>
public sealed class ImportIssueList
{
    /// <summary>一覧に持つ件数の上限。</summary>
    public const int MaxListed = 1000;

    private readonly List<ImportIssue> _items = [];

    public IReadOnlyList<ImportIssue> Items => _items;

    public int ErrorCount { get; private set; }

    public int WarningCount { get; private set; }

    public int Count => ErrorCount + WarningCount;

    public void Add(ImportIssue issue)
    {
        if (issue.IsWarning)
        {
            WarningCount++;
        }
        else
        {
            ErrorCount++;
        }

        if (_items.Count < MaxListed)
        {
            _items.Add(issue);
        }
    }
}

/// <summary>アドレスの配置 (TOOL-05 の仕様 2)。</summary>
public enum AddressPlacement
{
    /// <summary>最小のアドレスをオフセット 0 にし、表示上のベースアドレスを最小のアドレスにする (既定)。</summary>
    Lowest,

    /// <summary>アドレスをそのままオフセットにする (アドレス 0 からの位置に置く)。</summary>
    Absolute,
}

/// <summary>インポートの設定 (TOOL-04 の仕様 2 の 3、TOOL-05〜09 のインポートのオプション)。</summary>
public sealed record ImportOptions
{
    /// <summary>形式の ID (<see cref="FormatIds"/>)。</summary>
    public string Format { get; init; } = FormatIds.Binary;

    // ---- Intel HEX・S-record ----
    public AddressPlacement Placement { get; init; } = AddressPlacement.Lowest;

    /// <summary>ギャップを埋める値 (既定 FF)。</summary>
    public byte GapFill { get; init; } = 0xFF;

    /// <summary>重複したアドレスを誤りにせず、後のデータを優先する。</summary>
    public bool PreferLater { get; init; }

    /// <summary>ギャップにブックマークを付ける (既定オフ)。</summary>
    public bool GapBookmarks { get; init; }

    // ---- Base64 ・Base32 ・URL エンコード ----

    /// <summary>空白・改行を無視する (Base64。既定オン)。</summary>
    public bool IgnoreWhitespace { get; init; } = true;

    /// <summary>パディングなしを許す (Base64・Base32。既定オン)。</summary>
    public bool AllowMissingPadding { get; init; } = true;

    /// <summary>URL エンコードで <c>+</c> を空白として扱う。</summary>
    public bool PlusAsSpace { get; init; }

    /// <summary>UUEncode でファイルが複数ある場合に読むファイル (0 から数える)。</summary>
    public int UuFileIndex { get; init; }

    // ---- Hex テキスト ----

    /// <summary>ダンプからの読み取り (行頭のオフセット・行末のテキストを取り除く。既定: 自動)。</summary>
    public bool DumpAuto { get; init; } = true;

    // ---- 10 進テキスト・配列 ----

    /// <summary>1 つの値のサイズ (1 / 2 / 4 / 8。配列では 0 で型名から推定)。</summary>
    public int ValueSize { get; init; } = 1;

    public bool Signed { get; init; }

    public bool BigEndian { get; init; }
}

/// <summary>
/// インポートの結果 (TOOL-04 の仕様 2 の 5 のプレビュー)。デコードした内容 (<see cref="Image"/>) は、ドキュメントに渡すまでこの結果が持つ。
/// </summary>
public sealed class ImportResult : IDisposable
{
    private SparseImage? _image;

    internal ImportResult(SparseImage image, ImportIssueList issues, EncodedFileSettings? settings)
    {
        _image = image;
        Issues = issues;
        Settings = settings;
    }

    /// <summary>デコードした内容。<see cref="TakeImage"/> で渡した後は null。</summary>
    public SparseImage? Image => _image;

    public ImportIssueList Issues { get; }

    /// <summary>元の形式で保存するための設定 (TOOL-11 の仕様 1)。対応しない形式では null。</summary>
    public EncodedFileSettings? Settings { get; }

    /// <summary>変換後のサイズ (ドキュメントの長さ)。</summary>
    public long Length { get; init; }

    /// <summary>データのバイト数 (ギャップを除く)。</summary>
    public long DataBytes { get; init; }

    /// <summary>ギャップのバイト数。</summary>
    public long GapBytes => Length - DataBytes;

    /// <summary>最小・最大のアドレス (アドレスを持つ形式。データがなければ null)。</summary>
    public (long First, long Last)? AddressRange { get; init; }

    /// <summary>実行開始アドレス (<c>03</c> / <c>05</c>、<c>S7〜S9</c>)。</summary>
    public long? StartAddress { get; init; }

    /// <summary><c>S0</c> の文字列。</summary>
    public string? Header { get; init; }

    /// <summary>配列のインポートで、型名から推定した要素のサイズ (TOOL-09 の仕様 4)。</summary>
    public int? InferredValueSize { get; init; }

    /// <summary>UUEncode のファイルの名前の一覧 (複数あれば選ばせる)。</summary>
    public IReadOnlyList<string> UuFiles { get; init; } = [];

    /// <summary>表示上のベースアドレス (アドレスの配置が「最小アドレス」なら最小のアドレス)。</summary>
    public long BaseAddress => _image?.Origin ?? 0;

    /// <summary>ギャップ (オフセットの範囲。ブックマークを付けるため)。</summary>
    public IEnumerable<(long Offset, long Length)> Gaps => _image is { } image ? image.GapsIn(0, image.Length) : [];

    /// <summary>先頭 <paramref name="count"/> バイト (プレビュー)。</summary>
    public byte[] Preview(int count)
    {
        if (_image is not { } image)
        {
            return [];
        }

        byte[] bytes = new byte[(int)Math.Min(count, image.Length)];
        image.Read(0, bytes);
        return bytes;
    }

    /// <summary>誤り (警告を除く) があるか。</summary>
    public bool HasErrors => Issues.ErrorCount > 0;

    /// <summary>デコードした内容を受け取る (以後この結果は持たない)。</summary>
    public SparseImage TakeImage()
    {
        SparseImage image = _image ?? throw new ObjectDisposedException(nameof(ImportResult));
        _image = null;
        return image;
    }

    public void Dispose()
    {
        _image?.Dispose();
        _image = null;
    }
}

/// <summary>読み込みのたびの進捗とキャンセル (長時間処理。ENG-09)。</summary>
public sealed class ImportProgress(CancellationToken cancellationToken = default, Action<long>? report = null)
{
    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>入力を読んだバイト数を知らせる。</summary>
    public void Report(long bytes) => report?.Invoke(bytes);
}
