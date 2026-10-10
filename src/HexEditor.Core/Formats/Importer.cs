using System.Text;
using HexEditor.Core.Clipboard;

namespace HexEditor.Core.Formats;

/// <summary>
/// インポートの入口 (TOOL-04 の仕様 2)。形式の ID で各形式の変換を呼ぶ。変換はストリームで行い、結果は一時ファイルに置く
/// (TOOL-04 の「巨大ファイル」)。
/// </summary>
public static class Importer
{
    /// <summary>形式の自動判定に使う先頭の長さ (64 KB。TOOL-04 の仕様 2 の 2)。</summary>
    public const int DetectionBytes = 64 * 1024;

    /// <summary>形式で変換する。IPS は「パッチの適用」なのでここでは扱わない (<see cref="IpsPatch"/>)。</summary>
    public static ImportResult Decode(Stream input, ImportOptions options, string tempDirectory, string displayName, ImportProgress? progress = null)
    {
        string format = options.Format;
        return format switch
        {
            FormatIds.IntelHex => RecordDecoders.DecodeIntelHex(input, options, tempDirectory, displayName, progress),
            FormatIds.SRecord => RecordDecoders.DecodeSRecord(input, options, tempDirectory, displayName, progress),
            FormatIds.HexText => TextDecoders.DecodeHexText(input, options, tempDirectory, displayName, progress),
            FormatIds.DecimalText => TextDecoders.DecodeDecimalText(input, options, tempDirectory, displayName, progress),
            FormatIds.Binary => DecodeBinary(input, tempDirectory, displayName, progress),
            _ when FormatIds.Encodings.Contains(format) => EncodingDecoders.Decode(format, input, options, tempDirectory, displayName, progress),
            _ when FormatIds.SourceArrays.Contains(format) => TextDecoders.DecodeSourceArray(input, options, tempDirectory, displayName, progress),
            _ => throw new ArgumentException($"インポートできない形式です: {format}", nameof(options)),
        };
    }

    /// <summary>ファイルを読んで変換する。</summary>
    public static ImportResult DecodeFile(string path, ImportOptions options, string tempDirectory, ImportProgress? progress = null)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.SequentialScan);
        return Decode(stream, options, tempDirectory, Path.GetFileName(path), progress);
    }

    /// <summary>バイナリ (ファイルの挿入): 内容をそのまま一時ファイルに写す。</summary>
    private static ImportResult DecodeBinary(Stream input, string tempDirectory, string displayName, ImportProgress? progress)
    {
        using var builder = new SparseImageBuilder(tempDirectory);
        byte[] buffer = new byte[1024 * 1024];
        long at = 0;
        int n;
        while ((n = input.Read(buffer)) > 0)
        {
            progress?.CancellationToken.ThrowIfCancellationRequested();
            builder.Add(at, buffer.AsSpan(0, n));
            at += n;
            progress?.Report(at);
        }

        SparseImage image = builder.BuildContiguous(displayName);
        return new ImportResult(image, new ImportIssueList(), null) { Length = image.Length, DataBytes = image.Length };
    }

    // ---- 形式の自動判定 ----

    /// <summary>ファイルの先頭 64 KB から形式を判定する (TOOL-04 の仕様 2 の 2)。拡張子には頼らない。</summary>
    public static string DetectFile(string path)
    {
        byte[] head = new byte[DetectionBytes];
        int count;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            count = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }

        return Detect(head.AsSpan(0, count), complete: count < head.Length);
    }

    /// <summary>
    /// 内容から形式を判定する。<paramref name="complete"/> は内容がファイル全体か (偽なら最後の行は途中で切れているため除く)。
    /// テキストとして判別できない内容は「バイナリ」。判別は「形式を選択して貼り付け」(EDIT-26) の自動判別と共通にする。
    /// </summary>
    public static string Detect(ReadOnlySpan<byte> head, bool complete = true)
    {
        if (head.IsEmpty)
        {
            return FormatIds.Binary;
        }

        string? text = AsText(head);
        if (text is null)
        {
            return FormatIds.Binary;
        }

        if (!complete)
        {
            int cut = text.LastIndexOf('\n');
            if (cut > 0)
            {
                text = text[..cut];
            }
        }

        // PEM の囲みがあれば Base64 (TOOL-07 の仕様 3)。
        if (text.Contains("-----BEGIN ", StringComparison.Ordinal))
        {
            return FormatIds.Base64;
        }

        if (text.TrimStart().StartsWith("begin ", StringComparison.Ordinal))
        {
            return FormatIds.UUEncode;
        }

        IReadOnlyList<PasteCandidate> candidates = PasteDetector.Detect(text);
        foreach (PasteCandidate candidate in candidates)
        {
            if (!candidate.IsValid && candidate.Format is not (PasteFormat.IntelHex or PasteFormat.SRecord))
            {
                continue;
            }

            // チェックサムの誤りなどで解釈できない場合も、構造のある形式はその形式として判定する (誤りはインポートで一覧にする)。
            string? id = candidate.Format switch
            {
                PasteFormat.IntelHex => FormatIds.IntelHex,
                PasteFormat.SRecord => FormatIds.SRecord,
                PasteFormat.UUEncode => FormatIds.UUEncode,
                PasteFormat.ScreenDump or PasteFormat.HexString => FormatIds.HexText,
                PasteFormat.Array or PasteFormat.Escape => FormatIds.C,
                PasteFormat.Ascii85 => FormatIds.Ascii85,
                PasteFormat.Base32 => FormatIds.Base32,
                PasteFormat.Base64 => FormatIds.Base64,
                PasteFormat.QuotedPrintable => FormatIds.QuotedPrintable,
                PasteFormat.UrlEncoded => FormatIds.Url,
                PasteFormat.Decimal => FormatIds.DecimalText,
                _ => null,
            };
            if (id is not null)
            {
                return id;
            }
        }

        return FormatIds.Binary;
    }

    /// <summary>テキストとして読めればその文字列 (BOM を判別)。NUL や制御文字が多ければ null。</summary>
    private static string? AsText(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(new byte[] { 0xFF, 0xFE }))
        {
            return Encoding.Unicode.GetString(head[2..]);
        }

        if (head.StartsWith(new byte[] { 0xFE, 0xFF }))
        {
            return Encoding.BigEndianUnicode.GetString(head[2..]);
        }

        if (head.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            head = head[3..];
        }

        int control = 0;
        foreach (byte b in head)
        {
            if (b == 0)
            {
                return null;
            }

            if (b < 0x20 && b is not (0x09 or 0x0A or 0x0D or 0x0C))
            {
                control++;
            }
        }

        if (control > head.Length / 100)
        {
            return null;
        }

        return new UTF8Encoding(false, false).GetString(head);
    }
}
