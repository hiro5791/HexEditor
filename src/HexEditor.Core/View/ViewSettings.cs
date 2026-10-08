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

    /// <summary>実際に使う中央区切り (VIEW-09 の仕様 3)。グループ化が 8 以上のときは無効。</summary>
    public bool EffectiveMiddleSeparator(int bytesPerRow) =>
        GroupSize < 8 && bytesPerRow >= 16 && (MiddleSeparator ?? true);

    /// <summary>
    /// 実際の行の先頭のずれ a (VIEW-01 の仕様 5)。「行の先頭をアドレスの区切りにそろえる」のときは、アドレスが 1 行のバイト数の
    /// 倍数になる位置で行を区切る。
    /// </summary>
    public int EffectiveRowShift(int bytesPerRow) => bytesPerRow <= 1 ? 0
        : AlignRowsToAddress ? (int)(BaseAddress % (ulong)bytesPerRow)
        : Math.Clamp(RowShift, 0, bytesPerRow - 1);

    /// <summary>1 行のバイト数がそろうべき単位 (グループ化とセル形式の単位の最小公倍数。VIEW-08 の仕様 3・5)。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int RowUnit => Math.Max(1, GroupSize);

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
        int bytesPerRow = BytesPerRow;
        if (bytesPerRow % groupSize != 0)
        {
            bytesPerRow = Math.Min(MaxBytesPerRow, (bytesPerRow + groupSize - 1) / groupSize * groupSize);
            roundedTo = bytesPerRow;
        }

        return this with { GroupSize = groupSize, BytesPerRow = bytesPerRow };
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
    private static readonly string[] DocumentSpecificKeys = ["baseAddress", "rowShift"];

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
        int bytesPerRow = Math.Clamp(BytesPerRow, 1, MaxBytesPerRow);
        if (bytesPerRow % group != 0)
        {
            bytesPerRow = Math.Min(MaxBytesPerRow, (bytesPerRow + group - 1) / group * group);
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
        };
    }
}

/// <summary>1 行のバイト数の指定の誤り (VIEW-08 の仕様 5 と「エラー」)。</summary>
public enum BytesPerRowError
{
    /// <summary>0 以下、または 4,096 を超える。</summary>
    OutOfRange,

    /// <summary>グループ化のバイト数の倍数でない。</summary>
    NotMultipleOfGroup,
}
