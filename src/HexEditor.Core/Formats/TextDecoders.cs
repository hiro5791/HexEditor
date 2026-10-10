using System.Globalization;
using System.Text.RegularExpressions;
using HexEditor.Core.Clipboard;

namespace HexEditor.Core.Formats;

/// <summary>
/// Hex テキスト・10 進テキスト (TOOL-08) とソースコードの配列 (TOOL-09) のインポート。
/// </summary>
public static partial class TextDecoders
{
    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static bool IsHex(char c) => HexValue(c) >= 0;

    // ---- Hex テキスト ----

    /// <summary>
    /// Hex テキストを読む (TOOL-08 の仕様 1)。「ダンプからの読み取り: 自動」では、先頭の行からダンプ (行頭のオフセットの列と行末のテキストの列を
    /// 持つ形: このアプリのダンプ、xxd、<c>hexdump -C</c>、<c>od -A x -t x1</c>、<c>certutil -encodehex</c>) かを判別し、ダンプならオフセットと
    /// テキストの列を取り除く。入力はシークできるストリーム (判別のために先頭を読み直す)。
    /// </summary>
    public static ImportResult DecodeHexText(Stream input, ImportOptions options, string tempDirectory, string displayName,
        ImportProgress? progress = null)
    {
        bool dump = false;
        if (options.DumpAuto && input.CanSeek)
        {
            long start = input.Position;
            using (var probe = new TextInput(input, leaveOpen: true))
            {
                var lines = new List<string>();
                while (lines.Count < 32 && probe.ReadLine(out bool tooLong) is { } line)
                {
                    if (tooLong)
                    {
                        break;
                    }

                    if (line.Trim().Length > 0)
                    {
                        lines.Add(line);
                    }
                }

                dump = LooksLikeDump(lines);
            }

            input.Position = start;
        }

        var issues = new ImportIssueList();
        using var builder = new SparseImageBuilder(tempDirectory);
        using var text = new TextInput(input, leaveOpen: true);
        long written = 0;
        var chunk = new List<byte>(64 * 1024);
        void Flush()
        {
            if (chunk.Count > 0)
            {
                builder.Add(written, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(chunk));
                written += chunk.Count;
                chunk.Clear();
            }
        }

        if (dump)
        {
            int lineNo = 0;
            byte[]? last = null;
            bool repeat = false;
            long? expected = null;
            while (text.ReadLine() is { } line)
            {
                lineNo++;
                if (lineNo % 4096 == 0)
                {
                    progress?.CancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(text.BytesRead);
                }

                if (line.Trim().Length == 0)
                {
                    continue;
                }

                if (line.Trim() == "*")
                {
                    // hexdump -C の「前の行と同じ行が続く」。次の行のオフセットまで前の行を繰り返す。
                    repeat = true;
                    continue;
                }

                if (!TryParseDumpLine(line, out long offset, out List<byte> bytes))
                {
                    issues.Add(new ImportIssue(lineNo, 1, ImportIssueKind.UnrecognizedLine, RecordDecoders.Clip(line)));
                    continue;
                }

                if (repeat && last is { Length: > 0 } && expected is { } at)
                {
                    for (long p = at; p + last.Length <= offset; p += last.Length)
                    {
                        chunk.AddRange(last);
                    }
                }

                repeat = false;
                chunk.AddRange(bytes);
                if (bytes.Count > 0)
                {
                    last = [.. bytes];
                }

                expected = offset + bytes.Count;
                if (chunk.Count >= 64 * 1024)
                {
                    Flush();
                }
            }
        }
        else
        {
            DecodePlainHex(text, issues, chunk, Flush, progress);
        }

        Flush();
        progress?.Report(text.BytesRead);
        SparseImage image = builder.BuildContiguous(displayName);
        return new ImportResult(image, issues, null) { Length = image.Length, DataBytes = image.Length };
    }

    /// <summary>00 の 6.3 の書式: 空白・カンマ・<c>0x</c>・<c>\x</c>・改行を区切りとし、区切りの間の桁数は偶数。</summary>
    private static void DecodePlainHex(TextInput text, ImportIssueList issues, List<byte> chunk, Action flush, ImportProgress? progress)
    {
        int pending = -1; // 前の桁 (まとまりの中の奇数番目の桁)
        int pendingLine = 0, pendingColumn = 0;
        int previous = -1;
        long count = 0;
        while (true)
        {
            int line = text.Line, column = text.Column;
            int c = text.ReadChar();
            if (++count % (1 << 20) == 0)
            {
                progress?.CancellationToken.ThrowIfCancellationRequested();
                progress?.Report(text.BytesRead);
            }

            bool separator = c < 0 || char.IsWhiteSpace((char)c) || c is ',' or ';';
            if (!separator && c is 'x' or 'X' && previous is '0' or '\\' && (previous == '\\' || pending == 0))
            {
                // 0x / \x の接頭辞。0 は前の桁として読んでいるので取り消す。
                pending = -1;
                previous = c;
                continue;
            }

            if (!separator && c == '\\')
            {
                if (pending >= 0)
                {
                    issues.Add(new ImportIssue(pendingLine, pendingColumn, ImportIssueKind.OddDigits, string.Empty));
                    pending = -1;
                }

                previous = c;
                continue;
            }

            if (separator)
            {
                if (pending >= 0)
                {
                    issues.Add(new ImportIssue(pendingLine, pendingColumn, ImportIssueKind.OddDigits, string.Empty));
                    pending = -1;
                }

                if (c < 0)
                {
                    break;
                }

                previous = c;
                continue;
            }

            int v = HexValue((char)c);
            if (v < 0)
            {
                issues.Add(new ImportIssue(line, column, ImportIssueKind.InvalidCharacter, ((char)c).ToString()));
                previous = c;
                continue;
            }

            if (pending < 0)
            {
                pending = v;
                pendingLine = line;
                pendingColumn = column;
            }
            else
            {
                chunk.Add((byte)(pending << 4 | v));
                pending = -1;
                if (chunk.Count >= 64 * 1024)
                {
                    flush();
                }
            }

            previous = c;
        }
    }

    /// <summary>先頭の行がダンプの形か: 行頭にオフセットがあり、オフセットが前の行のバイト数だけ進んでいる。</summary>
    internal static bool LooksLikeDump(IReadOnlyList<string> lines)
    {
        var parsed = new List<(long Offset, int Count)>();
        foreach (string line in lines)
        {
            if (line.Trim() == "*")
            {
                continue;
            }

            if (!TryParseDumpLine(line, out long offset, out List<byte> bytes))
            {
                return false;
            }

            parsed.Add((offset, bytes.Count));
        }

        if (parsed.Count == 0)
        {
            return false;
        }

        if (parsed.Count == 1)
        {
            return parsed[0].Offset == 0 && parsed[0].Count > 0;
        }

        int consistent = 0;
        for (int i = 1; i < parsed.Count; i++)
        {
            if (parsed[i].Offset == parsed[i - 1].Offset + parsed[i - 1].Count)
            {
                consistent++;
            }
            else if (parsed[i].Offset < parsed[i - 1].Offset)
            {
                return false;
            }
        }

        return consistent >= (parsed.Count - 1 + 1) / 2;
    }

    [GeneratedRegex(@"^@?([0-9A-Fa-f]{4,16}):?$")]
    private static partial Regex OffsetToken();

    /// <summary>
    /// ダンプの 1 行を読む: 先頭のオフセットの列、続く Hex の並び (2・4・8 桁のまとまり)、残りのテキストの列 (`|` で囲んだもの、または
    /// 2 つ以上の空白の後のバイト数以下の文字列) に分ける。オフセットだけの行 (最後のオフセット) はバイト数 0 として受け付ける。
    /// </summary>
    internal static bool TryParseDumpLine(string line, out long offset, out List<byte> bytes)
    {
        bytes = [];
        offset = 0;
        string trimmed = line.TrimEnd();
        int i = 0;
        // 先頭の空白を飛ばす。
        while (i < trimmed.Length && char.IsWhiteSpace(trimmed[i]))
        {
            i++;
        }

        int tokenStart = i;
        while (i < trimmed.Length && !char.IsWhiteSpace(trimmed[i]))
        {
            i++;
        }

        string first = trimmed[tokenStart..i];
        if (!OffsetToken().IsMatch(first))
        {
            return false;
        }

        offset = long.Parse(first.TrimStart('@').TrimEnd(':'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        while (i < trimmed.Length)
        {
            int gapStart = i;
            while (i < trimmed.Length && char.IsWhiteSpace(trimmed[i]))
            {
                i++;
            }

            if (i >= trimmed.Length)
            {
                break;
            }

            int gap = 0;
            for (int k = gapStart; k < i; k++)
            {
                gap += trimmed[k] == '\t' ? 4 : 1;
            }

            int start = i;
            while (i < trimmed.Length && !char.IsWhiteSpace(trimmed[i]))
            {
                i++;
            }

            string token = trimmed[start..i];
            if (token == "|")
            {
                // このアプリのダンプの縦線の区切り。Hex の後の縦線の後はテキストの列。
                if (bytes.Count > 0)
                {
                    break;
                }

                continue;
            }

            if (token[0] == '|')
            {
                break; // hexdump -C のテキストの列
            }

            bool hexToken = token.Length is 2 or 4 or 8 && token.All(IsHex);
            int remaining = trimmed.Length - start;
            if (!hexToken || (gap >= 2 && bytes.Count > 0 && remaining <= bytes.Count))
            {
                break; // テキストの列
            }

            for (int k = 0; k < token.Length; k += 2)
            {
                bytes.Add((byte)(HexValue(token[k]) << 4 | HexValue(token[k + 1])));
            }
        }

        return true;
    }

    // ---- 10 進テキスト ----

    /// <summary>
    /// 10 進テキストを読む (TOOL-08 の仕様 2): 空白・カンマ・セミコロン・改行で区切った数値。1 つの値のサイズ・符号・エンディアンに従って
    /// バイト列にする。範囲外の値は誤り。
    /// </summary>
    public static ImportResult DecodeDecimalText(Stream input, ImportOptions options, string tempDirectory, string displayName,
        ImportProgress? progress = null)
    {
        var issues = new ImportIssueList();
        using var builder = new SparseImageBuilder(tempDirectory);
        using var text = new TextInput(input, leaveOpen: true);
        int size = options.ValueSize is 1 or 2 or 4 or 8 ? options.ValueSize : 1;
        long written = 0;
        var chunk = new List<byte>(64 * 1024);
        var token = new System.Text.StringBuilder();
        int tokenLine = 0, tokenColumn = 0;
        long count = 0;
        void EndToken()
        {
            if (token.Length == 0)
            {
                return;
            }

            string s = token.ToString();
            token.Clear();
            if (!TryDecimal(s, size, options.Signed, out ulong bits))
            {
                bool syntax = s.Any(ch => !char.IsAsciiDigit(ch) && ch is not ('-' or '+'));
                issues.Add(new ImportIssue(tokenLine, tokenColumn, syntax ? ImportIssueKind.InvalidCharacter : ImportIssueKind.OutOfRange, s));
                return;
            }

            for (int k = 0; k < size; k++)
            {
                int shift = options.BigEndian ? (size - 1 - k) * 8 : k * 8;
                chunk.Add((byte)(bits >> shift));
            }

            if (chunk.Count >= 64 * 1024)
            {
                builder.Add(written, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(chunk));
                written += chunk.Count;
                chunk.Clear();
            }
        }

        while (true)
        {
            int line = text.Line, column = text.Column;
            int c = text.ReadChar();
            if (++count % (1 << 20) == 0)
            {
                progress?.CancellationToken.ThrowIfCancellationRequested();
                progress?.Report(text.BytesRead);
            }

            if (c < 0 || char.IsWhiteSpace((char)c) || c is ',' or ';')
            {
                EndToken();
                if (c < 0)
                {
                    break;
                }

                continue;
            }

            if (token.Length == 0)
            {
                tokenLine = line;
                tokenColumn = column;
            }

            token.Append((char)c);
        }

        if (chunk.Count > 0)
        {
            builder.Add(written, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(chunk));
        }

        progress?.Report(text.BytesRead);
        SparseImage image = builder.BuildContiguous(displayName);
        return new ImportResult(image, issues, null) { Length = image.Length, DataBytes = image.Length };
    }

    /// <summary>10 進の数値を、サイズと符号の範囲で確かめてビット列にする。</summary>
    private static bool TryDecimal(string s, int size, bool signed, out ulong bits)
    {
        bits = 0;
        int bitCount = size * 8;
        if (signed)
        {
            if (!long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v))
            {
                return false;
            }

            long min = size == 8 ? long.MinValue : -(1L << (bitCount - 1));
            long max = size == 8 ? long.MaxValue : (1L << (bitCount - 1)) - 1;
            if (v < min || v > max)
            {
                return false;
            }

            bits = unchecked((ulong)v);
            return true;
        }

        if (!ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ulong u))
        {
            return false;
        }

        if (size < 8 && u >= 1UL << bitCount)
        {
            return false;
        }

        bits = u;
        return true;
    }

    // ---- ソースコードの配列 ----

    /// <summary>
    /// ソースコードの配列の定義を読む (TOOL-09 の仕様 4)。解釈は「形式を選択して貼り付け」の配列表記 (EDIT-26) と共通で、文字リテラル・
    /// 文字列リテラル・8 進も読む。<see cref="ImportOptions.ValueSize"/> が 0 なら、要素のサイズを型名から推定する。
    /// </summary>
    public static ImportResult DecodeSourceArray(Stream input, ImportOptions options, string tempDirectory, string displayName,
        ImportProgress? progress = null)
    {
        using var text = new TextInput(input, leaveOpen: true);
        string source = StripLanguageComments(options.Format, text.ReadToEnd());
        progress?.Report(text.BytesRead);
        int size = options.ValueSize is 1 or 2 or 4 or 8 ? options.ValueSize : InferElementSize(source);
        PasteCandidate parsed = PasteDetector.Parse(PasteFormat.Array, source,
            new PasteOptions { ElementSize = size, BigEndian = options.BigEndian, SourceLiterals = true, CollectErrors = true });
        var issues = new ImportIssueList();
        using var builder = new SparseImageBuilder(tempDirectory);
        string[] lines = source.Split('\n');
        void AddIssue(PasteError e) => issues.Add(new ImportIssue(e.Line, e.Column, e.Reason switch
        {
            "range" => ImportIssueKind.OutOfRange,
            "char" => ImportIssueKind.InvalidCharacter,
            _ => ImportIssueKind.Syntax,
        }, e.Line - 1 < lines.Length ? lines[e.Line - 1].TrimEnd('\r') : string.Empty));

        if (parsed.Error is { } error)
        {
            AddIssue(error);
        }
        else
        {
            // 数値として解釈できない部分はすべて位置を一覧にする (TOOL-09 の「エラー」)。「誤りを無視して読み込む」では、その部分を飛ばす。
            foreach (PasteError skipped in parsed.SkippedErrors.OrderBy(e => e.Line).ThenBy(e => e.Column))
            {
                AddIssue(skipped);
            }

            if (parsed.Bytes is { Length: > 0 } bytes)
            {
                builder.Add(0, bytes);
            }
        }

        SparseImage image = builder.BuildContiguous(displayName);
        return new ImportResult(image, issues, null) { Length = image.Length, DataBytes = image.Length, InferredValueSize = size };
    }

    /// <summary>
    /// 言語ごとのコメントのうち、配列表記の解釈 (<c>//</c>・<c>/* */</c>・<c>#</c> を無視する) で扱えないものを取り除く: VB.NET の <c>'</c> と
    /// <c>REM</c>、Pascal の <c>{ }</c> と <c>(* *)</c> (行頭のもの)。
    /// </summary>
    private static string StripLanguageComments(string format, string source)
    {
        if (format == FormatIds.VisualBasic)
        {
            return VbComment().Replace(source, string.Empty);
        }

        if (format == FormatIds.Pascal)
        {
            return PascalComment().Replace(source, string.Empty);
        }

        return source;
    }

    [GeneratedRegex(@"^[ \t]*('|REM\b).*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex VbComment();

    [GeneratedRegex(@"^[ \t]*(\{[^}]*\}|\(\*.*?\*\))", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex PascalComment();

    /// <summary>型名から要素のサイズを推定する (uint16_t・short・u32 など。分からなければ 1)。</summary>
    public static int InferElementSize(string source)
    {
        // 最初の { / [ / ( (配列の値) より前を見る。
        int end = source.IndexOfAny(['{', '=']);
        string head = end >= 0 ? source[..end] : source;
        foreach (Match m in TypeName().Matches(head))
        {
            string t = m.Value.ToLowerInvariant();
            int size = t switch
            {
                "uint8_t" or "int8_t" or "char" or "byte" or "sbyte" or "u8" or "i8" or "uint8array" or "int8array" or "bytes" => 1,
                "uint16_t" or "int16_t" or "short" or "ushort" or "u16" or "i16" or "word" or "uint16" or "int16" or "uint16array"
                    or "int16array" => 2,
                "uint32_t" or "int32_t" or "int" or "uint" or "u32" or "i32" or "longword" or "dword" or "uint32" or "int32"
                    or "uinteger" or "integer" or "uint32array" or "int32array" => 4,
                "uint64_t" or "int64_t" or "long" or "ulong" or "u64" or "i64" or "uint64" or "int64" or "qword" or "biguint64array"
                    or "bigint64array" => 8,
                _ => 0,
            };
            if (size > 0)
            {
                return size;
            }
        }

        return 1;
    }

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex TypeName();
}
