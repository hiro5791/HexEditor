using System.Text;

namespace HexEditor.Core.Formats;

/// <summary>
/// エンコード形式のインポート (TOOL-07): Base64 (標準 / URL セーフを自動判別、PEM の囲みを取り除く)、Base32、Ascii85、UUEncode、
/// Quoted-Printable、URL エンコード。入力を 1 文字ずつ読み、結果は一時ファイルに書く (ストリームで変換する)。不正な文字は行・列を
/// 一覧にして飛ばす (「誤りを無視して読み込む」は不正な文字を飛ばした結果になる。仕様 2)。
/// </summary>
public static class EncodingDecoders
{
    private const string Base64Standard = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static readonly sbyte[] Base64Map = BuildBase64Map();

    private static sbyte[] BuildBase64Map()
    {
        var map = new sbyte[128];
        Array.Fill(map, (sbyte)-1);
        for (int i = 0; i < Base64Standard.Length; i++)
        {
            map[Base64Standard[i]] = (sbyte)i;
        }

        map['-'] = 62;
        map['_'] = 63;
        return map;
    }

    /// <summary>形式の ID で選ぶ。</summary>
    public static ImportResult Decode(string format, Stream input, ImportOptions options, string tempDirectory, string displayName,
        ImportProgress? progress = null) => format switch
        {
            FormatIds.Base64 => DecodeBase64(input, options, tempDirectory, displayName, progress),
            FormatIds.Base32 => DecodeBase32(input, options, tempDirectory, displayName, progress),
            FormatIds.Ascii85 => DecodeAscii85(input, tempDirectory, displayName, progress),
            FormatIds.UUEncode => DecodeUu(input, options, tempDirectory, displayName, progress),
            FormatIds.QuotedPrintable => DecodeQuotedPrintable(input, tempDirectory, displayName, progress),
            FormatIds.Url => DecodeUrl(input, options, tempDirectory, displayName, progress),
            _ => throw new ArgumentException(format, nameof(format)),
        };

    /// <summary>結果を順に一時ファイルに書く。</summary>
    private sealed class Output(SparseImageBuilder builder)
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _count;
        private long _address;

        public long Written => _address + _count;

        public void Put(byte b)
        {
            _buffer[_count++] = b;
            if (_count == _buffer.Length)
            {
                Flush();
            }
        }

        public void Flush()
        {
            if (_count > 0)
            {
                builder.Add(_address, _buffer.AsSpan(0, _count));
                _address += _count;
                _count = 0;
            }
        }
    }

    /// <summary>入力を 1 文字ずつ読む共通部分 (行・列、進捗、キャンセル)。</summary>
    private sealed class Reader(TextInput text, ImportProgress? progress)
    {
        private long _chars;

        public int Line { get; private set; } = 1;

        public int Column { get; private set; } = 1;

        /// <summary>行頭か (PEM の囲みの行を見つけるため)。</summary>
        public bool AtLineStart => Column == 1;

        public int Next()
        {
            Line = text.Line;
            Column = text.Column;
            if (++_chars % (1 << 20) == 0)
            {
                progress?.CancellationToken.ThrowIfCancellationRequested();
                progress?.Report(text.BytesRead);
            }

            return text.ReadChar();
        }

        /// <summary>行の残りを読む (改行は読み捨てる)。</summary>
        public string RestOfLine()
        {
            var sb = new StringBuilder();
            int c;
            while ((c = text.ReadChar()) >= 0 && c != '\n')
            {
                sb.Append((char)c);
            }

            return sb.ToString();
        }
    }

    private static ImportResult Finish(SparseImageBuilder builder, Output output, ImportIssueList issues, EncodedFileSettings? settings,
        string displayName, TextInput text, ImportProgress? progress, IReadOnlyList<string>? uuFiles = null)
    {
        output.Flush();
        progress?.Report(text.BytesRead);
        SparseImage image = builder.BuildContiguous(displayName);
        return new ImportResult(image, issues, settings) { Length = image.Length, DataBytes = image.Length, UuFiles = uuFiles ?? [] };
    }

    // ---- Base64 ----

    public static ImportResult DecodeBase64(Stream input, ImportOptions options, string tempDirectory, string displayName,
        ImportProgress? progress = null)
    {
        var issues = new ImportIssueList();
        using var builder = new SparseImageBuilder(tempDirectory);
        using var text = new TextInput(input, leaveOpen: true);
        var output = new Output(builder);
        var r = new Reader(text, progress);

        // デコード中も、書き終えた先頭から表示できるようにする (ENG-38 の仕様 2)。
        progress?.Attach(builder);
        int bits = 0, quantum = 0;
        bool urlSafe = false, padded = false, sawData = false;
        int firstLineLength = 0, lineLength = 0, dataLines = 0;
        bool afterPadding = false;
        void Handle(int c, int line, int column)
        {
            if (c is ' ' or '\t')
            {
                if (!options.IgnoreWhitespace)
                {
                    issues.Add(new ImportIssue(line, column, ImportIssueKind.InvalidCharacter, ((char)c).ToString()));
                }

                return;
            }

            if (c == '=')
            {
                lineLength++;
                padded = true;
                if (quantum is 0 or 1 && !afterPadding)
                {
                    issues.Add(new ImportIssue(line, column, ImportIssueKind.InvalidLength, "="));
                }
                else if (quantum is 2 or 3)
                {
                    // パディングの前の 2 文字 → 1 バイト、3 文字 → 2 バイト。
                    output.Put((byte)(quantum == 2 ? bits >> 4 : bits >> 10));
                    if (quantum == 3)
                    {
                        output.Put((byte)(bits >> 2));
                    }
                }

                quantum = 0;
                bits = 0;
                afterPadding = true;
                return;
            }

            int v = c < 128 ? Base64Map[c] : -1;
            if (v < 0)
            {
                issues.Add(new ImportIssue(line, column, ImportIssueKind.InvalidCharacter, ((char)c).ToString()));
                return;
            }

            afterPadding = false;
            urlSafe |= c is '-' or '_';
            sawData = true;
            lineLength++;
            bits = bits << 6 | v;
            quantum++;
            if (quantum == 4)
            {
                output.Put((byte)(bits >> 16));
                output.Put((byte)(bits >> 8));
                output.Put((byte)bits);
                quantum = 0;
                bits = 0;
            }
        }

        int c;
        while ((c = r.Next()) >= 0)
        {
            if (c == '\n')
            {
                if (lineLength > 0)
                {
                    dataLines++;
                    if (dataLines == 1)
                    {
                        firstLineLength = lineLength;
                    }
                }

                lineLength = 0;
                continue;
            }

            if (r.AtLineStart && c == '-')
            {
                // PEM の囲み (-----BEGIN ...----- / -----END ...-----) の行を取り除く (仕様 3)。URL セーフの '-' で始まる行は普通に読む。
                int line = r.Line;
                string rest = r.RestOfLine();
                if (rest.StartsWith("----", StringComparison.Ordinal))
                {
                    continue;
                }

                string whole = "-" + rest;
                for (int i = 0; i < whole.Length; i++)
                {
                    Handle(whole[i], line, i + 1);
                }

                if (lineLength > 0)
                {
                    dataLines++;
                    if (dataLines == 1)
                    {
                        firstLineLength = lineLength;
                    }
                }

                lineLength = 0;
                continue;
            }

            Handle(c, r.Line, r.Column);
        }

        if (lineLength > 0)
        {
            dataLines++;
            if (dataLines == 1)
            {
                firstLineLength = lineLength;
            }
        }

        FlushQuantum(quantum, bits, output, issues, options.AllowMissingPadding, r, padded);
        var settings = new EncodedFileSettings
        {
            Format = FormatIds.Base64,
            NewLine = text.NewLine ?? "\r\n",
            FinalNewLine = text.EndsWithNewLine,
            LineLength = dataLines > 1 ? firstLineLength : 0,
            UrlSafe = urlSafe,
            Padding = padded || !sawData || output.Written % 3 == 0,
        };
        return Finish(builder, output, issues, settings, displayName, text, progress);
    }

    /// <summary>パディングなしで終わった Base64 の残り (2 文字 → 1 バイト、3 文字 → 2 バイト)。</summary>
    private static void FlushQuantum(int quantum, int bits, Output output, ImportIssueList issues, bool allowMissing, Reader r, bool padded)
    {
        if (quantum == 0)
        {
            return;
        }

        if (quantum == 1)
        {
            issues.Add(new ImportIssue(r.Line, r.Column, ImportIssueKind.InvalidLength, string.Empty));
            return;
        }

        if (!allowMissing && !padded)
        {
            issues.Add(new ImportIssue(r.Line, r.Column, ImportIssueKind.InvalidLength, string.Empty));
        }

        if (quantum == 2)
        {
            output.Put((byte)(bits >> 4));
        }
        else
        {
            output.Put((byte)(bits >> 10));
            output.Put((byte)(bits >> 2));
        }
    }

    // ---- Base32 ----

    public static ImportResult DecodeBase32(Stream input, ImportOptions options, string tempDirectory, string displayName,
        ImportProgress? progress = null)
    {
        var issues = new ImportIssueList();
        using var builder = new SparseImageBuilder(tempDirectory);
        using var text = new TextInput(input, leaveOpen: true);
        var output = new Output(builder);
        var r = new Reader(text, progress);
        ulong bits = 0;
        int count = 0;
        bool padded = false;
        int c;
        void FlushGroup()
        {
            if (count == 0)
            {
                return;
            }

            int bytes = count switch { 2 => 1, 4 => 2, 5 => 3, 7 => 4, 8 => 5, _ => -1 };
            if (bytes < 0)
            {
                issues.Add(new ImportIssue(r.Line, r.Column, ImportIssueKind.InvalidLength, string.Empty));
            }
            else
            {
                ulong v = bits << (5 * (8 - count));
                for (int i = 0; i < bytes; i++)
                {
                    output.Put((byte)(v >> (32 - i * 8)));
                }
            }

            bits = 0;
            count = 0;
        }

        while ((c = r.Next()) >= 0)
        {
            if (c is '\n' or ' ' or '\t')
            {
                continue;
            }

            if (c == '=')
            {
                padded = true;
                FlushGroup();
                continue;
            }

            int v = Base32Alphabet.IndexOf(char.ToUpperInvariant((char)c));
            if (v < 0)
            {
                issues.Add(new ImportIssue(r.Line, r.Column, ImportIssueKind.InvalidCharacter, ((char)c).ToString()));
                continue;
            }

            bits = bits << 5 | (uint)v;
            if (++count == 8)
            {
                FlushGroup();
            }
        }

        if (count > 0 && !padded && !options.AllowMissingPadding)
        {
            issues.Add(new ImportIssue(r.Line, r.Column, ImportIssueKind.InvalidLength, string.Empty));
        }

        FlushGroup();
        return Finish(builder, output, issues, null, displayName, text, progress);
    }

    // ---- Ascii85 ----

    public static ImportResult DecodeAscii85(Stream input, string tempDirectory, string displayName, ImportProgress? progress = null)
    {
        var issues = new ImportIssueList();
        using var builder = new SparseImageBuilder(tempDirectory);
        using var text = new TextInput(input, leaveOpen: true);
        var output = new Output(builder);
        var r = new Reader(text, progress);
        ulong v = 0;
        int count = 0;
        bool started = false, ended = false;
        int previous = -1;
        int c;
        while ((c = r.Next()) >= 0)
        {
            if (ended || c is '\n' or ' ' or '\t' or '\f' or '\0')
            {
                continue;
            }

            if (!started && c == '<')
            {
                // <~ の有無を自動で判別する。
                previous = c;
                continue;
            }

            if (previous == '<')
            {
                previous = -1;
                if (c == '~')
                {
                    started = true;
                    continue;
                }

                issues.Add(new ImportIssue(r.Line, r.Column - 1, ImportIssueKind.InvalidCharacter, "<"));
            }

            started = true;
            if (c == '~')
            {
                ended = true; // ~>
                continue;
            }

            if (c == 'z' && count == 0)
            {
                for (int i = 0; i < 4; i++)
                {
                    output.Put(0);
                }

                continue;
            }

            if (c is < '!' or > 'u')
            {
                issues.Add(new ImportIssue(r.Line, r.Column, ImportIssueKind.InvalidCharacter, ((char)c).ToString()));
                continue;
            }

            v = v * 85 + (ulong)(c - '!');
            if (++count == 5)
            {
                if (v > uint.MaxValue)
                {
                    issues.Add(new ImportIssue(r.Line, r.Column, ImportIssueKind.OutOfRange, string.Empty));
                }

                for (int i = 3; i >= 0; i--)
                {
                    output.Put((byte)(v >> (i * 8)));
                }

                v = 0;
                count = 0;
            }
        }

        if (count == 1)
        {
            issues.Add(new ImportIssue(r.Line, r.Column, ImportIssueKind.InvalidLength, string.Empty));
        }
        else if (count > 1)
        {
            int bytes = count - 1;
            for (int i = count; i < 5; i++)
            {
                v = v * 85 + 84;
            }

            for (int i = 0; i < bytes; i++)
            {
                output.Put((byte)(v >> (24 - i * 8)));
            }
        }

        return Finish(builder, output, issues, null, displayName, text, progress);
    }

    // ---- UUEncode ----

    public static ImportResult DecodeUu(Stream input, ImportOptions options, string tempDirectory, string displayName,
        ImportProgress? progress = null)
    {
        var issues = new ImportIssueList();
        using var builder = new SparseImageBuilder(tempDirectory);
        using var text = new TextInput(input, leaveOpen: true);
        var output = new Output(builder);
        var files = new List<string>();
        int fileIndex = -1;
        bool inFile = false, done = false;
        int lineNo = 0;
        while (text.ReadLine() is { } raw)
        {
            lineNo++;
            string line = raw.TrimEnd('\r');
            if (!inFile)
            {
                if (line.StartsWith("begin ", StringComparison.Ordinal))
                {
                    string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    files.Add(parts.Length >= 3 ? parts[2] : string.Empty);
                    fileIndex++;
                    inFile = true;
                }

                continue;
            }

            if (line == "end")
            {
                inFile = false;
                done |= fileIndex == options.UuFileIndex;
                continue;
            }

            if (fileIndex != options.UuFileIndex || line.Length == 0)
            {
                continue;
            }

            int length = (line[0] - 32) & 63;
            int at = 1;
            int written = 0;
            while (written < length)
            {
                if (at >= line.Length)
                {
                    issues.Add(new ImportIssue(lineNo, at + 1, ImportIssueKind.InvalidLength, RecordDecoders.Clip(line)));
                    break;
                }

                int[] q = new int[4];
                bool bad = false;
                for (int k = 0; k < 4; k++)
                {
                    char ch = at + k < line.Length ? line[at + k] : '`';
                    if (ch is < ' ' or > '`')
                    {
                        issues.Add(new ImportIssue(lineNo, at + k + 1, ImportIssueKind.InvalidCharacter, ch.ToString()));
                        bad = true;
                    }

                    q[k] = (ch - 32) & 63;
                }

                if (bad)
                {
                    break;
                }

                int b0 = q[0] << 2 | q[1] >> 4, b1 = (q[1] & 15) << 4 | q[2] >> 2, b2 = (q[2] & 3) << 6 | q[3];
                foreach (int b in new[] { b0, b1, b2 })
                {
                    if (written < length)
                    {
                        output.Put((byte)b);
                        written++;
                    }
                }

                at += 4;
            }
        }

        if (files.Count == 0 || !done && fileIndex < options.UuFileIndex)
        {
            issues.Add(new ImportIssue(Math.Max(1, lineNo), 1, ImportIssueKind.Syntax, string.Empty, "begin"));
        }

        return Finish(builder, output, issues, null, displayName, text, progress, files);
    }

    // ---- Quoted-Printable ----

    public static ImportResult DecodeQuotedPrintable(Stream input, string tempDirectory, string displayName, ImportProgress? progress = null)
    {
        var issues = new ImportIssueList();
        using var builder = new SparseImageBuilder(tempDirectory);
        using var text = new TextInput(input, leaveOpen: true);
        var output = new Output(builder);
        int lineNo = 0;
        bool first = true;
        bool hardBreakPending = false;
        while (text.ReadLine() is { } raw)
        {
            lineNo++;
            if (lineNo % 4096 == 0)
            {
                progress?.CancellationToken.ThrowIfCancellationRequested();
                progress?.Report(text.BytesRead);
            }

            if (hardBreakPending)
            {
                // ハードな改行は CRLF を表す (RFC 2045)。最後の行の後の改行は含めない。
                output.Put(0x0D);
                output.Put(0x0A);
                hardBreakPending = false;
            }

            string line = raw.TrimEnd(' ', '\t');
            bool soft = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c != '=')
                {
                    if (c > 0x7E)
                    {
                        issues.Add(new ImportIssue(lineNo, i + 1, ImportIssueKind.InvalidCharacter, c.ToString()));
                        continue;
                    }

                    output.Put((byte)c);
                    continue;
                }

                if (i == line.Length - 1)
                {
                    soft = true;
                    break;
                }

                if (i + 2 < line.Length + 0 && Uri.IsHexDigit(line[i + 1]) && Uri.IsHexDigit(line[i + 2]))
                {
                    output.Put(Convert.ToByte(line.Substring(i + 1, 2), 16));
                    i += 2;
                    continue;
                }

                issues.Add(new ImportIssue(lineNo, i + 1, ImportIssueKind.InvalidCharacter, "="));
            }

            hardBreakPending = !soft;
            first = false;
        }

        _ = first;
        return Finish(builder, output, issues, null, displayName, text, progress);
    }

    // ---- URL エンコード ----

    public static ImportResult DecodeUrl(Stream input, ImportOptions options, string tempDirectory, string displayName,
        ImportProgress? progress = null)
    {
        var issues = new ImportIssueList();
        using var builder = new SparseImageBuilder(tempDirectory);
        using var text = new TextInput(input, leaveOpen: true);
        var output = new Output(builder);
        var r = new Reader(text, progress);
        Span<byte> utf8 = stackalloc byte[4];
        int c;
        while ((c = r.Next()) >= 0)
        {
            if (c == '\n')
            {
                continue;
            }

            if (c == '%')
            {
                int line = r.Line, column = r.Column;
                int h1 = r.Next();
                int h2 = h1 >= 0 ? r.Next() : -1;
                if (h1 < 0 || h2 < 0 || !Uri.IsHexDigit((char)h1) || !Uri.IsHexDigit((char)h2))
                {
                    issues.Add(new ImportIssue(line, column, ImportIssueKind.InvalidCharacter, "%"));
                    continue;
                }

                output.Put((byte)(Convert.ToInt32(((char)h1).ToString(), 16) << 4 | Convert.ToInt32(((char)h2).ToString(), 16)));
                continue;
            }

            if (c == '+' && options.PlusAsSpace)
            {
                output.Put(0x20);
                continue;
            }

            int n = Encoding.UTF8.GetBytes([(char)c], utf8);
            foreach (byte b in utf8[..n])
            {
                output.Put(b);
            }
        }

        return Finish(builder, output, issues, null, displayName, text, progress);
    }
}
