using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HexEditor.Core.Hashing;

/// <summary>結果の表示の設定 (コピーの「値のみ」は行の表示形式のまま。ANA-22 の仕様 1)。</summary>
public sealed record HashDisplayOptions(HashValueFormat Format = HashValueFormat.HexUpper, bool LittleEndian = false)
{
    public static readonly HashDisplayOptions Default = new();

    /// <summary>行の値の表示。TTH など Base32 を既定にするアルゴリズムは、Hex 大文字 (既定の形式) のとき Base32 で表示する (ANA-19 の仕様 4)。</summary>
    public string Display(HashResultRow row) =>
        row.Algorithm.PrefersBase32 && Format == HashValueFormat.HexUpper
            ? Base32(row.Value)
            : HashValueFormatter.Format(row.Value, row.IsNumeric, Format, LittleEndian);

    /// <summary>RFC 4648 の Base32 (大文字、パディングなし)。</summary>
    public static string Base32(ReadOnlySpan<byte> data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }
}

/// <summary>結果のコピーと書き出し (ANA-22)。</summary>
public static class HashExport
{
    /// <summary>値のみ (行の表示形式のまま)。</summary>
    public static string Value(HashResultRow row, HashDisplayOptions display) => display.Display(row);

    /// <summary><c>アルゴリズム名: 値</c> を 1 行ずつ。</summary>
    public static string NameValue(IEnumerable<HashResultRow> rows, HashDisplayOptions display) =>
        Lines(rows.Select(r => $"{r.Choice.DisplayName}: {display.Display(r)}"));

    /// <summary>
    /// チェックサムファイル形式 (<c>&lt;値&gt; *&lt;ファイル名&gt;</c>)。値は小文字の Hex (GNU coreutils の <c>sha256sum</c> などと同じ)。
    /// </summary>
    public static string ChecksumLines(IEnumerable<HashResultRow> rows, string fileName) =>
        Lines(rows.Select(r => $"{Convert.ToHexStringLower(r.Value)} *{Path.GetFileName(fileName)}"));

    /// <summary>
    /// JSON (<c>[{"algorithm": "SHA-256", "value": "…", "parameters": {…}, "range": {"start": 0, "length": 1024}}]</c>)。
    /// 値は行の表示形式。マルチ選択を連結した結果は <c>ranges</c> に各範囲も入れる。
    /// </summary>
    public static string Json(IEnumerable<HashResultRow> rows, HashDisplayOptions display)
    {
        var array = new JsonArray();
        foreach (HashResultRow row in rows)
        {
            var parameters = new JsonObject();
            if (row.Algorithm.Parameters.HasFlag(HashParameterKinds.Seed))
            {
                parameters["seed"] = row.Choice.Parameters.Seed;
            }

            if (row.Algorithm.Parameters.HasFlag(HashParameterKinds.Complement))
            {
                parameters["complement"] = row.Choice.Parameters.Complement.ToString().ToLowerInvariant();
            }

            HashParameters p = row.Choice.Parameters;
            if (row.Algorithm.Parameters.HasFlag(HashParameterKinds.Endian) && p.BigEndian is bool be)
            {
                parameters["bigEndian"] = be;
            }

            if (row.Algorithm.Parameters.HasFlag(HashParameterKinds.Signed))
            {
                parameters["signed"] = p.Signed;
            }

            if (row.Algorithm.Parameters.HasFlag(HashParameterKinds.Key) && p.KeyHex is { Length: > 0 } key)
            {
                parameters["key"] = key;
            }

            if (row.Algorithm.Parameters.HasFlag(HashParameterKinds.OutputLength))
            {
                parameters["outputBits"] = row.Algorithm.BitsFor(p);
            }

            if (row.Algorithm.Parameters.HasFlag(HashParameterKinds.Ed2kMode))
            {
                parameters["ed2k"] = p.Ed2k.ToString().ToLowerInvariant();
            }

            var item = new JsonObject
            {
                ["algorithm"] = row.Algorithm.Name,
                ["value"] = display.Display(row),
                ["parameters"] = parameters,
                ["range"] = new JsonObject { ["start"] = row.Start, ["length"] = row.Length },
            };
            if (row.Ranges.Count > 1)
            {
                item["ranges"] = new JsonArray([.. row.Ranges.Select(r => (JsonNode?)new JsonObject { ["start"] = r.Offset, ["length"] = r.Length })]);
            }

            array.Add(item);
        }

        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>CSV (アルゴリズム, 値, 開始, 長さ)。1 行目は見出し。</summary>
    public static string Csv(IEnumerable<HashResultRow> rows, HashDisplayOptions display)
    {
        var lines = new List<string> { "algorithm,value,start,length" };
        lines.AddRange(rows.Select(r => string.Join(',',
            CsvField(r.Choice.DisplayName),
            CsvField(display.Display(r)),
            r.Start.ToString(CultureInfo.InvariantCulture),
            r.Length.ToString(CultureInfo.InvariantCulture))));
        return Lines(lines);
    }

    /// <summary>
    /// チェックサムファイルとして保存する (ANA-22 の仕様 2。UTF-8 (BOM なし)、改行は LF)。一時ファイルに書いてから置き換える。
    /// </summary>
    public static void WriteChecksumFile(string path, IEnumerable<HashResultRow> rows, string documentFileName)
    {
        string temp = path + ".tmp";

        // .sfv は「ファイル名 CRC」の形 (値は大文字の Hex)。それ以外は GNU coreutils の形。
        string content = string.Equals(Path.GetExtension(path), ".sfv", StringComparison.OrdinalIgnoreCase)
            ? Lines(rows.Select(r => $"{Path.GetFileName(documentFileName)} {Convert.ToHexString(r.Value)}"))
            : ChecksumLines(rows, documentFileName);
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>保存するチェックサムファイルの拡張子 (アルゴリズムから。<c>.sha256</c> など)。</summary>
    public static string ChecksumExtension(HashAlgorithmInfo algorithm) => algorithm.Id switch
    {
        "crc32" => ".sfv",
        _ => "." + algorithm.Id.Replace("-", string.Empty, StringComparison.Ordinal),
    };

    private static string Lines(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (string line in lines)
        {
            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    private static string CsvField(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
}

/// <summary>「カーソル位置に書き込む」(ANA-22 の仕様 3・4)。</summary>
public static class HashWriteBack
{
    /// <summary>書き込むバイト列 (64 bit 以下の値は <paramref name="littleEndian"/> で下位のバイトから)。</summary>
    public static byte[] Bytes(HashResultRow row, bool littleEndian) =>
        HashValueFormatter.Arrange(row.Value, row.IsNumeric, littleEndian);

    /// <summary>
    /// 書き込み先が計算の対象範囲に含まれるか (警告を出すか)。除外範囲に含まれる部分は数えない (ANA-22 の仕様 4)。
    /// </summary>
    public static bool OverlapsTarget(long offset, int length, IReadOnlyList<HashRange> targets, IReadOnlyList<HashRange> exclusions)
    {
        long end = offset + length;
        for (long p = offset; p < end;)
        {
            HashRange? excluded = exclusions.Where(e => e.Offset <= p && p < e.End).Cast<HashRange?>().FirstOrDefault();
            if (excluded is { } e)
            {
                p = e.End;
                continue;
            }

            long next = exclusions.Where(x => x.Offset > p).Select(x => x.Offset).DefaultIfEmpty(end).Min();
            long to = Math.Min(next, end);
            if (targets.Any(t => t.Offset < to && p < t.End))
            {
                return true;
            }

            p = to;
        }

        return false;
    }

    /// <summary>末尾を超える場合の不足のバイト数 (0 なら書ける。ANA-22 の「エラー」)。</summary>
    public static long Shortage(long offset, int length, long documentLength) => Math.Max(0, offset + length - documentLength);
}
