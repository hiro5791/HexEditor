using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HexEditor.Core.Clipboard;

namespace HexEditor.Core.Formats;

/// <summary>
/// エクスポートの元のデータ。<see cref="Read"/> はドキュメント上のオフセットで読む。<see cref="DataRanges"/> はギャップ (ENG-38 のデータなし) を
/// 除いた範囲を返す (なければ範囲全体)。
/// </summary>
public sealed record ExportSource
{
    public required ByteReader Read { get; init; }

    /// <summary>ドキュメントの長さ。</summary>
    public required long Length { get; init; }

    /// <summary>表示上のベースアドレス (Intel HEX・S-record の既定の開始アドレス)。</summary>
    public long BaseAddress { get; init; }

    /// <summary>[offset, offset + length) の中のデータのある範囲。null なら範囲全体。</summary>
    public Func<long, long, IEnumerable<(long Offset, long Length)>>? DataRanges { get; init; }

    /// <summary>元のファイル名 (先頭のコメント・S0 の既定値)。</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>作成日時 (先頭のコメント。テストで固定する)。</summary>
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// エクスポートの入口 (TOOL-04 の仕様 3)。形式ごとの変換は Copy As (EDIT-25) と共通の部品を使う。ファイルへの書き出しは一時ファイルに書いてから
/// 置き換え、失敗・キャンセルしたら書きかけのファイルを消す (TOOL-04 の「巨大ファイル」「エラー」)。
/// </summary>
public static class Exporter
{
    /// <summary>出力のサイズの確認の閾値 (テキスト形式で 1 GB を超える見込み。TOOL-04)。</summary>
    public const long ConfirmBytes = 1_000_000_000;

    /// <summary>ソースコードの出力の確認の閾値 (100 MB。TOOL-09)。</summary>
    public const long SourceConfirmBytes = 100_000_000;

    /// <summary>HTML・RTF の出力の確認の閾値 (100 MB。TOOL-10)。</summary>
    public const long DocumentConfirmBytes = 100_000_000;

    /// <summary>形式に対応する Copy As の形式 (エンコード形式と配列)。</summary>
    public static CopyFormat? CopyFormatOf(string format) => format switch
    {
        FormatIds.Base64 => CopyFormat.Base64,
        FormatIds.Base32 => CopyFormat.Base32,
        FormatIds.Ascii85 => CopyFormat.Ascii85,
        FormatIds.UUEncode => CopyFormat.UUEncode,
        FormatIds.QuotedPrintable => CopyFormat.QuotedPrintable,
        FormatIds.Url => CopyFormat.HexUrl,
        FormatIds.C => CopyFormat.ArrayC,
        FormatIds.Cpp => CopyFormat.ArrayCpp,
        FormatIds.CSharp => CopyFormat.ArrayCSharp,
        FormatIds.Java => CopyFormat.ArrayJava,
        FormatIds.JavaScript => CopyFormat.ArrayJavaScript,
        FormatIds.Python => CopyFormat.ArrayPython,
        FormatIds.Rust => CopyFormat.ArrayRust,
        FormatIds.Go => CopyFormat.ArrayGo,
        FormatIds.Pascal => CopyFormat.ArrayPascal,
        FormatIds.VisualBasic => CopyFormat.ArrayVisualBasic,
        _ => null,
    };

    /// <summary>テキストの文字コード。</summary>
    public static Encoding EncodingOf(TextFileEncoding encoding) => encoding switch
    {
        TextFileEncoding.Utf8Bom => new UTF8Encoding(true),
        TextFileEncoding.Ascii => Encoding.ASCII,
        _ => new UTF8Encoding(false),
    };

    /// <summary>
    /// 出力を書く。<paramref name="ranges"/> は出力する範囲 (オフセットの昇順)。複数の範囲は「つなげて 1 つのファイル」として扱い、アドレスを持つ形式では
    /// それぞれのアドレスに、それ以外の形式ではつないだバイト列として書く。IPS はこのメソッドでは書かない (<see cref="IpsPatch"/>)。
    /// </summary>
    public static void Write(ExportSource source, IReadOnlyList<(long Offset, long Length)> ranges, ExportOptions o, Stream output,
        CancellationToken cancellationToken = default, Action<long>? progress = null)
    {
        if (o.Format == FormatIds.Binary)
        {
            WriteBinary(source, ranges, output, cancellationToken, progress);
            return;
        }

        if (o.Format is FormatIds.Ips or FormatIds.Ips32)
        {
            throw new ArgumentException("IPS は IpsPatch で書き出します。", nameof(o));
        }

        // 出力の先頭に BOM を書き、それ以降は BOM なしで書く (StreamWriter に任せると Flush のたびに確かめるため)。
        Encoding encoding = EncodingOf(o.Encoding);
        if (o.Encoding == TextFileEncoding.Utf8Bom && output.Position == 0)
        {
            output.Write(encoding.GetPreamble());
        }

        using var writer = new StreamWriter(output, o.Encoding == TextFileEncoding.Utf8Bom ? new UTF8Encoding(false) : encoding,
            64 * 1024, leaveOpen: true)
        { NewLine = o.NewLine };
        WriteText(source, ranges, o, writer, cancellationToken, progress);
        writer.Flush();
    }

    /// <summary>テキスト形式の出力を <paramref name="writer"/> に書く (プレビューにも使う)。</summary>
    public static void WriteText(ExportSource source, IReadOnlyList<(long Offset, long Length)> ranges, ExportOptions o, TextWriter writer,
        CancellationToken cancellationToken = default, Action<long>? progress = null)
    {
        string nl = o.NewLine;
        long total = ranges.Sum(r => r.Length);
        switch (o.Format)
        {
            case FormatIds.IntelHex or FormatIds.SRecord:
            {
                // ギャップ (データなし) は出力しない (TOOL-11 の仕様 2)。アドレス = オフセット + ベースアドレス (開始アドレスを指定したら、
                // 最初の範囲の先頭をその開始アドレスにする)。
                var data = new List<(long Offset, long Length)>();
                foreach ((long offset, long length) in ranges)
                {
                    data.AddRange(source.DataRanges?.Invoke(offset, length) ?? [(offset, length)]);
                }

                long first = ranges.Count > 0 ? ranges[0].Offset : 0;
                long bias = o.StartAddress is { } start ? start - first : source.BaseAddress;
                RecordExportOptions records = o.Records with
                {
                    Header = o.Format == FormatIds.SRecord && string.IsNullOrEmpty(o.Records.Header) ? source.FileName : o.Records.Header,
                };
                if (o.Format == FormatIds.IntelHex)
                {
                    RecordExporter.WriteIntelHex(writer, source.Read, data, bias, records, nl, o.FinalNewLine, cancellationToken, progress);
                }
                else
                {
                    RecordExporter.WriteSRecord(writer, source.Read, data, bias, records, nl, o.FinalNewLine, cancellationToken, progress);
                }

                return;
            }

            case FormatIds.HexText:
                WriteHexText(source, ranges, o, writer, cancellationToken, progress);
                return;
            case FormatIds.DecimalText:
                WriteDecimalText(source, ranges, o, writer, cancellationToken, progress);
                return;
        }

        if (FormatIds.Dumps.Contains(o.Format))
        {
            long first = ranges.Count > 0 ? ranges[0].Offset : 0;
            var dump = new DumpExporter(writer, o.Format, o.Dump, nl, first, total);
            dump.Begin();
            Feed(source, ranges, cancellationToken, progress, (chunk, _) => dump.Write(chunk));
            dump.End();
            return;
        }

        if (CopyFormatOf(o.Format) is { } copyFormat)
        {
            CopyOptions copy = o.Copy with { NewLine = nl };
            bool array = CopyFormatter.IsArray(copyFormat);
            long rangeStart = ranges.Count > 0 ? ranges[0].Offset : 0;
            if (array)
            {
                WriteSourcePrologue(source, ranges, o, writer, total);
            }

            ByteReader read = Concatenated(source.Read, ranges);
            CopyFormatter.Write(copyFormat, copy, read, 0, total, writer, cancellationToken, progress);
            if (array)
            {
                WriteSourceEpilogue(o, writer, total);
            }
            else if (copyFormat is not CopyFormat.UUEncode && o.FinalNewLine && total > 0)
            {
                writer.Write(nl);
            }

            _ = rangeStart;
            return;
        }

        throw new ArgumentException($"エクスポートできない形式です: {o.Format}", nameof(o));
    }

    /// <summary>範囲をつないだバイト列を、位置 0 からのオフセットで読む。</summary>
    private static ByteReader Concatenated(ByteReader read, IReadOnlyList<(long Offset, long Length)> ranges)
    {
        if (ranges.Count == 1)
        {
            long start = ranges[0].Offset;
            return (offset, destination) => read(start + offset, destination);
        }

        return (offset, destination) =>
        {
            long at = offset;
            int done = 0;
            long passed = 0;
            foreach ((long o, long l) in ranges)
            {
                if (done >= destination.Length)
                {
                    break;
                }

                if (at < passed + l)
                {
                    long within = at - passed;
                    int n = (int)Math.Min(destination.Length - done, l - within);
                    read(o + within, destination.Slice(done, n));
                    done += n;
                    at += n;
                }

                passed += l;
            }
        };
    }

    private static void Feed(ExportSource source, IReadOnlyList<(long Offset, long Length)> ranges, CancellationToken cancellationToken,
        Action<long>? progress, Action<ReadOnlySpan<byte>, long> sink)
    {
        byte[] buffer = new byte[CopyFormatter.ChunkSize];
        long done = 0;
        foreach ((long offset, long length) in ranges)
        {
            for (long pos = 0; pos < length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buffer.Length, length - pos);
                source.Read(offset + pos, buffer.AsSpan(0, n));
                sink(buffer.AsSpan(0, n), offset + pos);
                pos += n;
                done += n;
                progress?.Invoke(done);
            }
        }
    }

    private static void WriteBinary(ExportSource source, IReadOnlyList<(long Offset, long Length)> ranges, Stream output,
        CancellationToken cancellationToken, Action<long>? progress) =>
        Feed(source, ranges, cancellationToken, progress, (chunk, _) => output.Write(chunk));

    // ---- Hex テキスト・10 進テキスト (TOOL-08 の仕様 3・4) ----

    private static void WriteHexText(ExportSource source, IReadOnlyList<(long Offset, long Length)> ranges, ExportOptions o, TextWriter w,
        CancellationToken cancellationToken, Action<long>? progress)
    {
        int perLine = o.HexBytesPerLine is >= 1 and <= 1024 ? o.HexBytesPerLine : 0;
        string format = o.HexUpperCase ? "X2" : "x2";
        long index = 0;
        long lastOffset = ranges.Count > 0 ? ranges[^1].Offset + ranges[^1].Length - 1 : 0;
        int offsetDigits = Math.Max(8, (lastOffset + source.BaseAddress).ToString("X", CultureInfo.InvariantCulture).Length);
        var sb = new StringBuilder();
        Feed(source, ranges, cancellationToken, progress, (chunk, at) =>
        {
            sb.Clear();
            for (int i = 0; i < chunk.Length; i++)
            {
                bool lineStart = perLine > 0 ? index % perLine == 0 : index == 0;
                if (lineStart)
                {
                    if (index > 0)
                    {
                        sb.Append(o.NewLine);
                    }

                    if (o.HexOffsets)
                    {
                        sb.Append((at + i + source.BaseAddress).ToString(o.HexUpperCase ? "X" : "x", CultureInfo.InvariantCulture).PadLeft(offsetDigits, '0'));
                        sb.Append(": ");
                    }
                }
                else
                {
                    sb.Append(o.HexSeparator);
                }

                sb.Append(o.HexPrefix).Append(chunk[i].ToString(format, CultureInfo.InvariantCulture));
                index++;
            }

            w.Write(sb);
        });
        if (index > 0)
        {
            w.Write(o.NewLine);
        }
    }

    private static void WriteDecimalText(ExportSource source, IReadOnlyList<(long Offset, long Length)> ranges, ExportOptions o, TextWriter w,
        CancellationToken cancellationToken, Action<long>? progress)
    {
        int size = o.DecimalValueSize is 1 or 2 or 4 or 8 ? o.DecimalValueSize : 1;
        int perLine = Math.Max(1, o.DecimalPerLine);
        byte[] pending = new byte[8];
        int pendingCount = 0;
        long index = 0;
        var sb = new StringBuilder();
        void Emit()
        {
            ulong bits = 0;
            for (int k = 0; k < size; k++)
            {
                int shift = o.DecimalBigEndian ? (size - 1 - k) * 8 : k * 8;
                bits |= (ulong)pending[k] << shift;
            }

            string text;
            if (o.DecimalSigned)
            {
                long v = size == 8 ? unchecked((long)bits) : (long)(bits << (64 - size * 8)) >> (64 - size * 8);
                text = v.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                text = bits.ToString(CultureInfo.InvariantCulture);
            }

            if (index > 0)
            {
                sb.Append(index % perLine == 0 ? o.NewLine : o.DecimalSeparator);
            }

            sb.Append(text);
            index++;
            pendingCount = 0;
        }

        Feed(source, ranges, cancellationToken, progress, (chunk, _) =>
        {
            sb.Clear();
            foreach (byte b in chunk)
            {
                pending[pendingCount++] = b;
                if (pendingCount == size)
                {
                    Emit();
                }
            }

            w.Write(sb);
        });

        sb.Clear();
        if (pendingCount > 0)
        {
            // 端数は 0 で埋める (EDIT-25 の仕様 5 と同じ)。
            Array.Clear(pending, pendingCount, size - pendingCount);
            Emit();
        }

        if (index > 0)
        {
            sb.Append(o.NewLine);
        }

        w.Write(sb);
    }

    // ---- ソースコードの配列 (TOOL-09 の仕様 3) ----

    private static string Guard(string variable) => variable.ToUpperInvariant() + "_H";

    private static void WriteSourcePrologue(ExportSource source, IReadOnlyList<(long Offset, long Length)> ranges, ExportOptions o, TextWriter w,
        long total)
    {
        string nl = o.NewLine;
        bool cLike = o.Format is FormatIds.C or FormatIds.Cpp;
        if (o.SourceComment)
        {
            string range = ranges.Count == 0 ? "0x0-0x0"
                : string.Join(", ", ranges.Select(r => $"0x{r.Offset:X}-0x{r.Offset + Math.Max(0, r.Length - 1):X}"));
            string body = $"{(source.FileName.Length > 0 ? source.FileName : "(untitled)")} [{range}] ({total.ToString(CultureInfo.InvariantCulture)} bytes), "
                + source.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
            string comment = o.Format switch
            {
                FormatIds.Python => "# " + body,
                FormatIds.Pascal => "{ " + body.Replace("}", ")") + " }",
                FormatIds.VisualBasic => "' " + body,
                _ => "// " + body,
            };
            w.Write(comment + nl);
        }

        if (cLike && o.HeaderFile)
        {
            string guard = Guard(o.Copy.VariableName);
            w.Write($"#ifndef {guard}{nl}#define {guard}{nl}{nl}");
            w.Write(o.Format == FormatIds.Cpp ? $"#include <array>{nl}#include <cstdint>{nl}{nl}" : $"#include <stdint.h>{nl}{nl}");
        }

        if (cLike && o.LengthConstant)
        {
            string name = o.Copy.VariableName.ToUpperInvariant() + "_LEN";
            w.Write(o.Format == FormatIds.C
                ? $"#define {name} {total.ToString(CultureInfo.InvariantCulture)}{nl}"
                : $"constexpr std::size_t {name} = {total.ToString(CultureInfo.InvariantCulture)};{nl}");
        }
    }

    private static void WriteSourceEpilogue(ExportOptions o, TextWriter w, long total)
    {
        string nl = o.NewLine;
        w.Write(nl);
        if (o.Format is FormatIds.C or FormatIds.Cpp && o.HeaderFile)
        {
            w.Write($"{nl}#endif /* {Guard(o.Copy.VariableName)} */{nl}");
        }

        _ = total;
    }

    // ---- ファイルへの書き出し ----

    /// <summary>
    /// <paramref name="path"/> に書き出す (TOOL-04 の「巨大ファイル」: 同じフォルダの一時ファイルに書いてから置き換える。置き換えは安全な保存
    /// (ENG-22) と同じく元のファイルの属性・ACL・作成日時を引き継ぐ)。失敗・キャンセルしたら一時ファイルを消し、出力先は変えない。
    /// </summary>
    public static void WriteFile(string path, Action<Stream> write) => Saving.SafeFileWriter.Write(path, write);

    /// <summary>出力のおおよそのサイズ (バイト。先頭の最大 4 KiB を変換して見積もる。TOOL-04 の仕様 3 の 3)。</summary>
    public static long EstimateSize(ExportSource source, IReadOnlyList<(long Offset, long Length)> ranges, ExportOptions o)
    {
        long total = ranges.Sum(r => r.Length);
        if (o.Format is FormatIds.Binary)
        {
            return total;
        }

        if (o.Format is FormatIds.Ips or FormatIds.Ips32)
        {
            return 0;
        }

        long sample = Math.Min(total, 4096);
        var first = new List<(long, long)>();
        long left = sample;
        foreach ((long offset, long length) in ranges)
        {
            if (left <= 0)
            {
                break;
            }

            long n = Math.Min(left, length);
            first.Add((offset, n));
            left -= n;
        }

        var sw = new StringWriter(CultureInfo.InvariantCulture);
        try
        {
            WriteText(source, first, o, sw);
        }
        catch (FormatLimitException)
        {
            return 0;
        }

        long chars = sw.GetStringBuilder().Length;
        return sample == 0 ? chars : (long)Math.Ceiling(chars * ((double)total / sample));
    }

    /// <summary>出力の先頭 <paramref name="lines"/> 行 (プレビュー。TOOL-04 の仕様 3 の 3)。</summary>
    public static string Preview(ExportSource source, IReadOnlyList<(long Offset, long Length)> ranges, ExportOptions o, int lines = 20)
    {
        if (o.Format is FormatIds.Binary or FormatIds.Ips or FormatIds.Ips32)
        {
            return string.Empty;
        }

        // 20 行に足りる量 (1 行 1,024 バイトまで) だけを変換する。
        long budget = 20L * Math.Max(1024, o.HexBytesPerLine) + 8192;
        var head = new List<(long, long)>();
        long left = budget;
        foreach ((long offset, long length) in ranges)
        {
            if (left <= 0)
            {
                break;
            }

            long n = Math.Min(left, length);
            head.Add((offset, n));
            left -= n;
        }

        var sw = new StringWriter(CultureInfo.InvariantCulture);
        WriteText(source, head, o, sw);
        string text = sw.ToString();
        int at = 0;
        for (int i = 0; i < lines && at >= 0; i++)
        {
            at = text.IndexOf('\n', at + (i == 0 ? 0 : 1));
        }

        return at < 0 ? text : text[..at].TrimEnd('\r');
    }
}
