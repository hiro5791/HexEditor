using System.Text;

namespace HexEditor.Core.Formats;

/// <summary>
/// テキストの入力 (TOOL-04 の仕様 6): BOM を自動で判別し (UTF-8 / UTF-16 / UTF-32。なければ UTF-8)、改行 (CRLF / LF / CR) をどれでも
/// 受け付ける。行単位 (<see cref="ReadLine"/>) と文字単位 (<see cref="Read"/>) で読め、どちらも行・列を数える。最初に見つかった改行と、
/// 最後の行の後に改行があったかを記録する (元の形式で保存するため。TOOL-11)。
/// </summary>
public sealed class TextInput : IDisposable
{
    /// <summary>1 行の上限 (これを超える行は誤りにする)。</summary>
    public const int MaxLineLength = 1024 * 1024;

    private readonly StreamReader _reader;
    private readonly Stream _stream;
    private readonly char[] _buffer = new char[64 * 1024];
    private int _pos;
    private int _len;
    private bool _pendingCr;
    private char _last = '\n';

    public TextInput(Stream stream, bool leaveOpen = false)
    {
        _stream = stream;
        _reader = new StreamReader(stream, new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true, 64 * 1024, leaveOpen);
    }

    /// <summary>メモリ上のテキスト (テスト・プレビュー用)。</summary>
    public static TextInput FromString(string text) => new(new MemoryStream(new UTF8Encoding(false).GetBytes(text)));

    /// <summary>次に読む文字の行 (1 から)。</summary>
    public int Line { get; private set; } = 1;

    /// <summary>次に読む文字の列 (1 から)。</summary>
    public int Column { get; private set; } = 1;

    /// <summary>最初に見つかった改行。まだなければ null。</summary>
    public string? NewLine { get; private set; }

    /// <summary>最後に読んだ文字が改行か (ファイルの終わりで、最後の行の後に改行があったか)。</summary>
    public bool EndsWithNewLine => _last is '\n' or '\r';

    /// <summary>入力から読んだバイト数 (進捗)。</summary>
    public long BytesRead => _stream.CanSeek ? _stream.Position : 0;

    private bool Fill()
    {
        if (_pos < _len)
        {
            return true;
        }

        _len = _reader.Read(_buffer, 0, _buffer.Length);
        _pos = 0;
        return _len > 0;
    }

    /// <summary>1 文字読む。終わりなら -1。改行は CRLF も 1 つの '\n' として返す。</summary>
    public int ReadChar()
    {
        while (true)
        {
            if (!Fill())
            {
                return -1;
            }

            char c = _buffer[_pos++];
            if (_pendingCr)
            {
                _pendingCr = false;
                if (c == '\n')
                {
                    NewLine ??= "\r\n";
                    _last = c;
                    continue; // CRLF の LF は CR で数え済み
                }

                NewLine ??= "\r";
            }

            _last = c;
            if (c == '\r')
            {
                _pendingCr = true;
                Line++;
                Column = 1;
                return '\n';
            }

            if (c == '\n')
            {
                NewLine ??= "\n";
                Line++;
                Column = 1;
                return '\n';
            }

            Column++;
            return c;
        }
    }

    /// <summary>
    /// 1 行読む (改行は含めない)。終わりなら null。<see cref="MaxLineLength"/> を超える行は先頭だけを返し、<paramref name="tooLong"/> を真にする。
    /// </summary>
    public string? ReadLine(out bool tooLong)
    {
        tooLong = false;
        var sb = new StringBuilder();
        bool any = false;
        while (true)
        {
            int c = ReadChar();
            if (c < 0)
            {
                return any ? sb.ToString() : null;
            }

            any = true;
            if (c == '\n')
            {
                return sb.ToString();
            }

            if (sb.Length < MaxLineLength)
            {
                sb.Append((char)c);
            }
            else
            {
                tooLong = true;
            }
        }
    }

    public string? ReadLine() => ReadLine(out _);

    /// <summary>残りをすべて読む (配列の解釈など、全体を一度に解釈する形式)。</summary>
    public string ReadToEnd()
    {
        var sb = new StringBuilder();
        int c;
        while ((c = ReadChar()) >= 0)
        {
            if (c == '\n')
            {
                sb.Append('\n');
            }
            else
            {
                sb.Append((char)c);
            }
        }

        return sb.ToString();
    }

    public void Dispose() => _reader.Dispose();
}
