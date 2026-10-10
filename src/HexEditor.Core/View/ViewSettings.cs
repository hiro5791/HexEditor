using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Core.View;

/// <summary>オフセットの基数と表示形式 (VIEW-19)。</summary>
public enum OffsetRadix
{
    Hex,
    Decimal,
    Octal,

    /// <summary>セクタ番号 (10 進):セクタ内の位置 (16 進)。</summary>
    Sector,
}

/// <summary>
/// 1 つのビューの表示設定 (VIEW-42 の仕様 1 のうち、フェーズ 1 で持つもの)。変更できない値で、<c>with</c> で変えた組を作る。
/// JSON (ドキュメントごとの設定・全体の既定値・プリセット) に読み書きでき、知らない項目は無視する (VIEW-42 の「エラー」)。
/// </summary>
public sealed record ViewSettings
{
    /// <summary>1 行のバイト数の最大値 (VIEW-08 の仕様 1)。</summary>
    public const int MaxBytesPerRow = 4096;

    /// <summary>組み込みの既定値 (VIEW-42 の仕様 2 の 1)。</summary>
    public static ViewSettings Default { get; } = new();

    /// <summary>グループ化で選べるバイト数 (VIEW-09 の仕様 1)。</summary>
    public static IReadOnlyList<int> GroupSizes { get; } = [1, 2, 4, 8, 16];

    // ---- 列の構成 (VIEW-08、VIEW-09、VIEW-12〜VIEW-16) ----

    /// <summary>1 行のバイト数 (固定のとき。1〜4,096、既定 16)。</summary>
    public int BytesPerRow { get; init; } = 16;

    /// <summary>1 行のバイト数を表示部分の幅に合わせる (VIEW-08 の仕様 3)。</summary>
    public bool AutoBytesPerRow { get; init; }

    /// <summary>自動のとき 2 のべき乗にそろえる (VIEW-08 の仕様 3。既定オフ)。</summary>
    public bool AutoPowerOfTwo { get; init; }

    /// <summary>グループ化のバイト数 (VIEW-09。1、2、4、8、16)。</summary>
    public int GroupSize { get; init; } = 1;

    /// <summary>8 バイトごとの中央区切り (VIEW-09 の仕様 3)。null は既定 (1 行 16 バイト以上のときオン)。</summary>
    public bool? MiddleSeparator { get; init; }

    /// <summary>Hex を小文字で表示 (VIEW-12。既定は大文字)。</summary>
    public bool LowercaseHex { get; init; }

    /// <summary>00 を薄く表示 (VIEW-13。既定オン)。</summary>
    public bool DimZeros { get; init; } = true;

    /// <summary>グループごとに背景を交互に変える (VIEW-14。既定オフ)。</summary>
    public bool AlternateColumns { get; init; }

    /// <summary>テキスト列にも交互色を付ける (VIEW-14 の仕様 4。既定オフ)。</summary>
    public bool AlternateTextColumns { get; init; }

    /// <summary>変更されたバイトを強調 (VIEW-15。既定オン)。</summary>
    public bool HighlightModified { get; init; } = true;

    /// <summary>現在行を強調 (VIEW-06 の仕様 1。既定オン)。</summary>
    public bool HighlightCurrentRow { get; init; } = true;

    /// <summary>列見出し (VIEW-05。既定は表示)。</summary>
    public bool ShowRuler { get; init; } = true;

    public bool ShowOffsetColumn { get; init; } = true;

    public bool ShowHexColumn { get; init; } = true;

    public bool ShowTextColumn { get; init; } = true;

    // ---- オフセット列 (VIEW-19、VIEW-20) ----

    public OffsetRadix Radix { get; init; } = OffsetRadix.Hex;

    /// <summary>16 進で 4 桁ごとに <c>:</c> を入れる (VIEW-19 の仕様 5。既定オフ)。</summary>
    public bool HexDigitSeparator { get; init; }

    /// <summary>ファイルのセクタサイズ (VIEW-19 の仕様 6。既定 512)。ディスクではデータソースの値を使う。</summary>
    public int SectorSize { get; init; } = 512;

    /// <summary>ベースアドレス (VIEW-20 の仕様 1。0〜2^64 − 1)。ドキュメント固有。</summary>
    public ulong BaseAddress { get; init; }

    /// <summary>行の先頭のずれ (VIEW-20 の仕様 5。0〜1 行のバイト数 − 1)。<see cref="AlignRowsToAddress"/> がオフのときに使う。</summary>
    public int RowShift { get; init; }

    /// <summary>行の先頭をアドレスの区切りにそろえる (VIEW-20 の仕様 5。既定オン)。</summary>
    public bool AlignRowsToAddress { get; init; } = true;

    // ---- テキスト列 (VIEW-21、VIEW-22) ----

    /// <summary>テキスト列の文字コード (<see cref="TextEncoding.FromId"/> の名前。既定 ascii)。</summary>
    public string Encoding { get; init; } = "ascii";

    /// <summary>UTF-16 の開始位置の偶奇 (VIEW-22 の仕様 4。0 = 偶数、1 = 奇数)。</summary>
    public int Utf16Phase { get; init; }

    /// <summary>UTF-32 の開始位置 (オフセットを 4 で割った余り。VIEW-22 の仕様 4)。</summary>
    public int Utf32Phase { get; init; }

    /// <summary>文字の範囲の残りのセルに続きの記号 `·` を薄く表示する (VIEW-22 の仕様 2。既定オフ)。</summary>
    public bool ShowContinuation { get; init; }

    /// <summary>
    /// 2 列目以降のテキスト列 (VIEW-24。最大 4 列)。<see cref="TextColumnSpec.Serialize"/> の形 (列ごとに改行で区切り、文字コード・UTF-16 の
    /// 開始位置・UTF-32 の開始位置をタブで区切る)。1 列目は <see cref="Encoding"/> などで持つ。値で比べられるよう文字列で持つ。
    /// </summary>
    public string ExtraTextColumns { get; init; } = string.Empty;

    // ---- セルの表示形式とエンディアン (VIEW-10、VIEW-11) ----

    /// <summary>Hex 列のセルの表示形式 (VIEW-10。既定は Hex)。</summary>
    public CellFormat CellFormat { get; init; } = CellFormat.Hex;

    /// <summary>10 進・8 進を 0 ではなく空白で埋める (VIEW-10 の仕様 2。既定オフ)。</summary>
    public bool SpacePadding { get; init; }

    /// <summary>ドキュメントのエンディアン (VIEW-11 の仕様 1。既定はリトル)。ドキュメント固有。</summary>
    public bool BigEndian { get; init; }

    /// <summary>グループ内のバイトを逆順に表示する (VIEW-11 の仕様 4。既定オフ)。データは変えない。</summary>
    public bool ReverseGroups { get; init; }

    // ---- レコード表示 (VIEW-18) ----

    /// <summary>レコード表示 (VIEW-18。既定オフ)。</summary>
    public bool RecordView { get; init; }

    /// <summary>レコード長 (VIEW-18 の仕様 1。1〜2^31 − 1、既定 16)。</summary>
    public int RecordLength { get; init; } = 16;

    /// <summary>最初のレコードの開始オフセット (VIEW-18 の仕様 1。既定 0)。</summary>
    public long RecordStart { get; init; }

    /// <summary>1 行を 1 レコードにする (VIEW-18 の仕様 4。レコード長が 4,096 以下のときだけ)。</summary>
    public bool RecordPerRow { get; init; }

    /// <summary>オフセット列にレコード番号を表示 (VIEW-18 の仕様 5)。</summary>
    public bool RecordNumbers { get; init; }

    // ---- 区切り線とページ表示 (VIEW-33) ----

    /// <summary>区切り線 (VIEW-33 の仕様 1。既定なし)。</summary>
    public SeparatorKind Separator { get; init; } = SeparatorKind.None;

    /// <summary>「任意の長さ」の区切りの長さ (VIEW-33 の仕様 1)。</summary>
    public long SeparatorLength { get; init; } = SectionLayout.PageSize;

    /// <summary>区切り線の見出しを表示 (VIEW-33 の仕様 3。既定オン)。</summary>
    public bool SeparatorLabels { get; init; } = true;

    /// <summary>ページ単位で表示 (VIEW-33 の仕様 4。既定オフ)。</summary>
    public bool PageView { get; init; }

    /// <summary>テキスト列の数の上限 (VIEW-24 の仕様 1)。</summary>
    public const int MaxTextColumns = 5;

    /// <summary>すべてのテキスト列 (1 列目を含む。VIEW-24)。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<TextColumnSpec> TextColumns =>
        [new TextColumnSpec(Encoding, Utf16Phase, Utf32Phase), .. TextColumnSpec.Parse(ExtraTextColumns)];

    /// <summary>テキスト列の数 (1〜5)。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int TextColumnCount => 1 + TextColumnSpec.Count(ExtraTextColumns);

    /// <summary>テキスト列を置き換える (1 列目は <see cref="Encoding"/> などに入れる)。1〜5 列。</summary>
    public ViewSettings WithTextColumns(IReadOnlyList<TextColumnSpec> columns)
    {
        if (columns.Count == 0)
        {
            return this;
        }

        TextColumnSpec first = columns[0];
        return this with
        {
            Encoding = first.Encoding,
            Utf16Phase = first.Utf16Phase & 1,
            Utf32Phase = first.Utf32Phase & 3,
            ExtraTextColumns = TextColumnSpec.Serialize(columns.Skip(1).Take(MaxTextColumns - 1)),
        };
    }

    /// <summary>
    /// テキスト列を左右に移す (VIEW-24 の仕様 3)。<paramref name="column"/> の列と、<paramref name="delta"/> (-1 で左、+1 で右) の隣の列を入れ替える。
    /// 移せない (端の列・範囲外) 場合は null。
    /// </summary>
    public ViewSettings? WithTextColumnMoved(int column, int delta)
    {
        List<TextColumnSpec> columns = [.. TextColumns];
        int target = column + Math.Sign(delta);
        if (delta == 0 || column < 0 || column >= columns.Count || target < 0 || target >= columns.Count)
        {
            return null;
        }

        (columns[target], columns[column]) = (columns[column], columns[target]);
        return WithTextColumns(columns);
    }

    /// <summary>テキスト列を削除する (VIEW-24)。最後の 1 列・範囲外は null。</summary>
    public ViewSettings? WithoutTextColumn(int column)
    {
        List<TextColumnSpec> columns = [.. TextColumns];
        if (columns.Count <= 1 || column < 0 || column >= columns.Count)
        {
            return null;
        }

        columns.RemoveAt(column);
        return WithTextColumns(columns);
    }

    /// <summary>セルの単位 (VIEW-10 の仕様 1)。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int CellUnit => CellFormatter.Unit(CellFormat);

    /// <summary>実際に使うグループ化のバイト数。セルの単位以上に引き上げる (VIEW-10 の仕様 3)。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int EffectiveGroupSize => Math.Max(Math.Max(1, GroupSize), CellUnit);

    /// <summary>「1 行を 1 レコードにする」が働いているか (レコード長が 4,096 以下のときだけ。VIEW-18 の仕様 1・4)。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool RecordRowsActive => RecordView && RecordPerRow && RecordLength <= MaxBytesPerRow;

    /// <summary>
    /// 実際の 1 行のバイト数。「1 行を 1 レコードにする」ならレコード長 (VIEW-18 の仕様 4)、自動なら <paramref name="auto"/>、
    /// それ以外は <see cref="BytesPerRow"/>。
    /// </summary>
    public int EffectiveBytesPerRow(int? auto) => RecordRowsActive ? RecordLength : AutoBytesPerRow && auto is { } a ? a : BytesPerRow;

    /// <summary>実際に使う中央区切り (VIEW-09 の仕様 3)。グループ化が 8 以上のときは無効。</summary>
    public bool EffectiveMiddleSeparator(int bytesPerRow) =>
        CellFormat == CellFormat.Hex && GroupSize < 8 && bytesPerRow >= 16 && (MiddleSeparator ?? true);

    /// <summary>
    /// 実際の行の先頭のずれ a (VIEW-01 の仕様 5)。「行の先頭をアドレスの区切りにそろえる」のときは、アドレスが 1 行のバイト数の
    /// 倍数になる位置で行を区切る。
    /// </summary>
    public int EffectiveRowShift(int bytesPerRow) => bytesPerRow <= 1 ? 0
        : RecordRowsActive && bytesPerRow == RecordLength ? (int)((bytesPerRow - RecordStart % bytesPerRow) % bytesPerRow)
        : AlignRowsToAddress ? (int)(BaseAddress % (ulong)bytesPerRow)
        : Math.Clamp(RowShift, 0, bytesPerRow - 1);

    /// <summary>1 行のバイト数がそろうべき単位 (グループ化とセル形式の単位の最小公倍数。VIEW-08 の仕様 3・5)。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int RowUnit => EffectiveGroupSize;

    /// <summary>
    /// 1 行のバイト数の指定を確かめる (VIEW-08 の仕様 1・5)。問題がなければ null、あればその種類。
    /// </summary>
    public BytesPerRowError? ValidateBytesPerRow(long value)
    {
        if (value < 1 || value > MaxBytesPerRow)
        {
            return BytesPerRowError.OutOfRange;
        }

        return value % RowUnit != 0 ? BytesPerRowError.NotMultipleOfGroup : null;
    }

    /// <summary>
    /// グループ化を変える (VIEW-09 の仕様 4)。1 行のバイト数が倍数でなくなる場合は倍数に切り上げる。<paramref name="roundedTo"/> に
    /// 切り上げた後の値 (切り上げなければ null)。
    /// </summary>
    public ViewSettings WithGroupSize(int groupSize, out int? roundedTo)
    {
        groupSize = GroupSizes.Contains(groupSize) ? groupSize : 1;
        roundedTo = null;
        int unit = Math.Max(groupSize, CellUnit);
        int bytesPerRow = BytesPerRow;
        if (bytesPerRow % unit != 0)
        {
            bytesPerRow = Math.Min(MaxBytesPerRow, (bytesPerRow + unit - 1) / unit * unit);
            roundedTo = bytesPerRow;
        }

        return this with { GroupSize = groupSize, BytesPerRow = bytesPerRow };
    }

    /// <summary>
    /// セルの表示形式を変える (VIEW-10)。1 行のバイト数が単位の倍数でなくなる場合は倍数に切り上げる。<paramref name="roundedTo"/> に
    /// 切り上げた後の値 (切り上げなければ null)。
    /// </summary>
    public ViewSettings WithCellFormat(CellFormat format, out int? roundedTo)
    {
        roundedTo = null;
        int unit = Math.Max(Math.Max(1, GroupSize), CellFormatter.Unit(format));
        int bytesPerRow = BytesPerRow;
        if (bytesPerRow % unit != 0)
        {
            bytesPerRow = Math.Min(MaxBytesPerRow, (bytesPerRow + unit - 1) / unit * unit);
            roundedTo = bytesPerRow;
        }

        return this with { CellFormat = format, BytesPerRow = bytesPerRow };
    }

    /// <summary>
    /// 自動のときの 1 行のバイト数 (VIEW-08 の仕様 3)。<paramref name="fits"/> は「このバイト数の行が表示部分の幅に収まるか」。
    /// 単位の倍数で収まる最大の値。最小は単位 1 つ分、最大は 4,096。
    /// </summary>
    public int AutoFit(Func<int, bool> fits)
    {
        int unit = RowUnit;
        int best = unit;
        if (AutoPowerOfTwo)
        {
            for (int n = unit; n <= MaxBytesPerRow; n *= 2)
            {
                if (!fits(n))
                {
                    break;
                }

                best = n;
            }

            return best;
        }

        // 幅は単調に増えるので二分探索する (単位の個数で探す)。
        int lo = 1;
        int hi = MaxBytesPerRow / unit;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (fits(mid * unit))
            {
                best = mid * unit;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best;
    }

    // ---- JSON ----

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>ドキュメント固有の項目 (「既定として保存」に含めない。VIEW-42 の仕様 4)。</summary>
    private static readonly string[] DocumentSpecificKeys =
        ["baseAddress", "rowShift", "bigEndian", "recordView", "recordLength", "recordStart", "recordPerRow", "recordNumbers"];

    /// <summary>JSON にする。<paramref name="includeDocumentSpecific"/> が偽ならドキュメント固有の項目を除く。</summary>
    public JsonObject ToJson(bool includeDocumentSpecific = true)
    {
        var node = (JsonObject)JsonSerializer.SerializeToNode(this, JsonOptions)!;
        if (!includeDocumentSpecific)
        {
            foreach (string key in DocumentSpecificKeys)
            {
                node.Remove(key);
            }
        }

        return node;
    }

    /// <summary>
    /// JSON の項目を <paramref name="baseSettings"/> に重ねる (後のものが優先。VIEW-42 の仕様 2)。知らない項目と、型の合わない項目は無視する。
    /// </summary>
    public static ViewSettings FromJson(JsonObject? json, ViewSettings? baseSettings = null)
    {
        ViewSettings result = baseSettings ?? Default;
        if (json is null)
        {
            return result;
        }

        JsonObject merged = result.ToJson();
        foreach ((string key, JsonNode? value) in json)
        {
            if (merged.ContainsKey(key) && value is not null)
            {
                merged[key] = value.DeepClone();
            }
        }

        try
        {
            return (JsonSerializer.Deserialize<ViewSettings>(merged, JsonOptions) ?? result).Normalize();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            // 型の合わない項目があれば、1 つずつ重ねて読める項目だけを使う。
            foreach ((string key, JsonNode? value) in json)
            {
                if (!merged.ContainsKey(key) || value is null)
                {
                    continue;
                }

                JsonObject one = result.ToJson();
                one[key] = value.DeepClone();
                try
                {
                    result = JsonSerializer.Deserialize<ViewSettings>(one, JsonOptions) ?? result;
                }
                catch (Exception inner) when (inner is JsonException or FormatException or InvalidOperationException or OverflowException)
                {
                }
            }

            return result.Normalize();
        }
    }

    /// <summary>範囲外の値を直す。</summary>
    public ViewSettings Normalize()
    {
        int group = GroupSizes.Contains(GroupSize) ? GroupSize : 1;
        CellFormat format = Enum.IsDefined(CellFormat) ? CellFormat : CellFormat.Hex;
        int unit = Math.Max(group, CellFormatter.Unit(format));
        int bytesPerRow = Math.Clamp(BytesPerRow, 1, MaxBytesPerRow);
        if (bytesPerRow % unit != 0)
        {
            bytesPerRow = Math.Min(MaxBytesPerRow, (bytesPerRow + unit - 1) / unit * unit);
        }

        bool anyData = ShowHexColumn || ShowTextColumn;
        return this with
        {
            GroupSize = group,
            BytesPerRow = bytesPerRow,
            SectorSize = SectorSize > 0 ? SectorSize : 512,
            RowShift = Math.Max(0, RowShift),
            Utf16Phase = Utf16Phase & 1,
            Utf32Phase = Utf32Phase & 3,
            ShowHexColumn = anyData ? ShowHexColumn : true,
            Encoding = string.IsNullOrWhiteSpace(Encoding) ? "ascii" : Encoding,
            CellFormat = format,
            RecordLength = Math.Max(1, RecordLength),
            RecordStart = Math.Max(0, RecordStart),
            SeparatorLength = Math.Clamp(SeparatorLength, 1, int.MaxValue),
            ExtraTextColumns = TextColumnSpec.Serialize(TextColumnSpec.Parse(ExtraTextColumns).Take(MaxTextColumns - 1)),
        };
    }
}

/// <summary>区切り線の種類 (VIEW-33 の仕様 1)。</summary>
public enum SeparatorKind
{
    None,

    /// <summary>セクタ (VIEW-32 のセクタサイズ)。</summary>
    Sector,

    /// <summary>ページ (4,096 バイト)。</summary>
    Page,

    /// <summary>任意の長さ (<see cref="ViewSettings.SeparatorLength"/>)。</summary>
    Custom,
}

/// <summary>テキスト列 1 つの文字コードと開始位置 (VIEW-24 の仕様 2)。</summary>
public readonly record struct TextColumnSpec(string Encoding, int Utf16Phase = 0, int Utf32Phase = 0)
{
    private const char EntrySeparator = '\n';
    private const char FieldSeparator = '\t';

    /// <summary><see cref="ViewSettings.ExtraTextColumns"/> の文字列から読む。読めない項目は飛ばす。</summary>
    public static IReadOnlyList<TextColumnSpec> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var result = new List<TextColumnSpec>();
        foreach (string line in text.Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(FieldSeparator);
            if (parts[0].Trim().Length == 0)
            {
                continue;
            }

            int p16 = parts.Length > 1 && int.TryParse(parts[1], out int a) ? a & 1 : 0;
            int p32 = parts.Length > 2 && int.TryParse(parts[2], out int b) ? b & 3 : 0;
            result.Add(new TextColumnSpec(parts[0].Trim(), p16, p32));
        }

        return result;
    }

    /// <summary>項目の数 (文字列を作らずに数える)。</summary>
    public static int Count(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int n = 0;
        foreach (string line in text.Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Split(FieldSeparator)[0].Trim().Length > 0)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary><see cref="ViewSettings.ExtraTextColumns"/> の文字列にする。</summary>
    public static string Serialize(IEnumerable<TextColumnSpec> columns) =>
        string.Join(EntrySeparator, columns.Select(c => c.Encoding + FieldSeparator + (c.Utf16Phase & 1) + FieldSeparator + (c.Utf32Phase & 3)));
}

/// <summary>1 行のバイト数の指定の誤り (VIEW-08 の仕様 5 と「エラー」)。</summary>
public enum BytesPerRowError
{
    /// <summary>0 以下、または 4,096 を超える。</summary>
    OutOfRange,

    /// <summary>グループ化のバイト数の倍数でない。</summary>
    NotMultipleOfGroup,
}
