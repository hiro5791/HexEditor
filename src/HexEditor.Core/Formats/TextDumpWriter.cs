using System.Globalization;
using System.Text;
using HexEditor.Core.View;

namespace HexEditor.Core.Formats;

/// <summary>コマンドライン <c>hexed dump</c> のテキスト形式 (AUTO-39 の仕様 3)。</summary>
public enum TextDumpStyle
{
    /// <summary>既定: オフセット (8 桁以上の 16 進)、Hex (8 バイトごとに空白 2 つ)、<c>|テキスト|</c>。</summary>
    Default,

    /// <summary><c>xxd</c> 互換 (<c>%08x: </c> の後に 2 バイトずつ空白区切り、空白 2 つの後にテキスト)。</summary>
    Xxd,

    /// <summary><c>hexdump -C</c> 互換 (小文字の 16 進)。</summary>
    HexdumpC,

    /// <summary>Hex だけ (オフセットもテキストもなし)。</summary>
    Plain,
}

/// <summary>
/// ストリームで 1 行ずつ書く Hex ダンプ (AUTO-39)。データは先頭から順に <see cref="Write"/> で渡し、最後に <see cref="Finish"/> を呼ぶ。
/// メモリは 1 行 (と、直前の 1 行) だけを使うため、ファイルの大きさに関係ない (AUTO-39 の「巨大ファイル」)。
/// 改行は <c>\n</c> (AUTO-36: 他のツールと突き合わせられるよう LF)。
/// </summary>
public sealed class TextDumpWriter
{
    private readonly TextWriter _writer;
    private readonly TextDumpStyle _style;
    private readonly int _width;
    private readonly int _group;
    private readonly TextEncoding _encoding;
    private readonly long _displayBase;
    private readonly bool _squeeze;
    private readonly byte[] _row;
    private readonly byte[] _previous;
    private readonly string _newLine;
    private int _count;
    private long _rowOffset;
    private bool _hasPrevious;
    private bool _starPrinted;

    /// <param name="writer">出力先。</param>
    /// <param name="style">形式。</param>
    /// <param name="width">1 行のバイト数 (1〜1024)。</param>
    /// <param name="group">グループのバイト数 (0 でグループなし)。</param>
    /// <param name="encoding">テキストの文字コード (<see cref="TextDumpStyle.Default"/> と <see cref="TextDumpStyle.HexdumpC"/> の列。xxd は ASCII)。</param>
    /// <param name="displayBase">最初のバイトに表示するオフセット (<c>--base-address</c> と <c>--range</c> の開始位置を足したもの)。</param>
    /// <param name="squeeze">同じ内容の行が続くとき <c>*</c> 1 行にまとめる。</param>
    public TextDumpWriter(TextWriter writer, TextDumpStyle style, int width, int group, TextEncoding? encoding, long displayBase, bool squeeze,
        string newLine = "\n")
    {
        if (width is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        _writer = writer;
        _style = style;
        _width = width;
        _group = Math.Max(0, group);
        _encoding = encoding ?? TextEncoding.Ascii;
        _displayBase = displayBase;
        _squeeze = squeeze;
        _newLine = newLine;
        _row = new byte[width];
        _previous = new byte[width];
        _rowOffset = displayBase;
    }

    /// <summary>形式ごとの既定のグループのバイト数。</summary>
    public static int DefaultGroup(TextDumpStyle style) => style switch
    {
        TextDumpStyle.Xxd => 2,
        TextDumpStyle.Plain => 0,
        _ => 8,
    };

    /// <summary>データを渡す (先頭から順に、何回に分けてもよい)。</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            int take = Math.Min(_width - _count, data.Length);
            data[..take].CopyTo(_row.AsSpan(_count));
            _count += take;
            data = data[take..];
            if (_count == _width)
            {
                EmitRow(complete: true);
            }
        }
    }

    /// <summary>残りの行を書き、必要なら終端のオフセットの行 (<c>hexdump -C</c> 形式) を書く。</summary>
    public void Finish()
    {
        if (_count > 0)
        {
            EmitRow(complete: false);
        }

        if (_style == TextDumpStyle.HexdumpC)
        {
            _writer.Write(Offset(_rowOffset));
            _writer.Write(_newLine);
        }
    }

    private string Offset(long value) => value.ToString("x8", CultureInfo.InvariantCulture);

    private void EmitRow(bool complete)
    {
        int n = _count;
        if (_squeeze && complete && _hasPrevious && IsRepeat())
        {
            // xxd -a は 00 だけの行を省く。それ以外の形式は直前の行と同じ内容の行を省く。
            if (!_starPrinted)
            {
                _writer.Write('*');
                _writer.Write(_newLine);
                _starPrinted = true;
            }

            Advance(n);
            return;
        }

        _starPrinted = false;
        _writer.Write(Format(_row.AsSpan(0, n)));
        _writer.Write(_newLine);
        if (complete)
        {
            _row.CopyTo(_previous, 0);
            _hasPrevious = true;
        }
        else
        {
            _hasPrevious = false;
        }

        Advance(n);
    }

    private bool IsRepeat() => _style == TextDumpStyle.Xxd
        ? _row.AsSpan().IndexOfAnyExcept((byte)0) < 0 && _previous.AsSpan().IndexOfAnyExcept((byte)0) < 0
        : _row.AsSpan().SequenceEqual(_previous);

    private void Advance(int n)
    {
        _rowOffset += n;
        _count = 0;
    }

    private string Format(ReadOnlySpan<byte> row) => _style switch
    {
        TextDumpStyle.Xxd => FormatXxd(row),
        TextDumpStyle.HexdumpC => FormatHexdump(row, lower: true),
        TextDumpStyle.Plain => FormatPlain(row),
        _ => FormatHexdump(row, lower: false),
    };

    private char TextChar(byte b)
    {
        if (_style == TextDumpStyle.Xxd)
        {
            return b is >= 0x20 and <= 0x7E ? (char)b : '.';
        }

        char c = _encoding.DisplayChar(b);
        return char.IsControl(c) || char.IsSurrogate(c) || c == '�' ? '.' : c;
    }

    /// <summary>xxd: <c>%08x: </c>、グループごとの Hex (グループの後ろに空白 1 つ)、空白 1 つ、テキスト。</summary>
    private string FormatXxd(ReadOnlySpan<byte> row)
    {
        var sb = new StringBuilder(80);
        sb.Append(Offset(_rowOffset)).Append(": ");
        for (int i = 0; i < _width; i++)
        {
            if (i < row.Length)
            {
                sb.Append(row[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append("  ");
            }

            if (_group > 0 && (i + 1) % _group == 0)
            {
                sb.Append(' ');
            }
        }

        sb.Append(' ');
        foreach (byte b in row)
        {
            sb.Append(TextChar(b));
        }

        return sb.ToString();
    }

    /// <summary>
    /// hexdump -C: オフセット、空白 2 つ、Hex (各バイトの後ろに空白 1 つ、グループの終わりにもう 1 つ)、空白 1 つ、<c>|テキスト|</c>。
    /// 既定の形式は Hex を大文字にし、オフセットは小文字のまま (AUTO-39 の受け入れ基準の例のとおり)。
    /// </summary>
    private string FormatHexdump(ReadOnlySpan<byte> row, bool lower)
    {
        var sb = new StringBuilder(96);
        sb.Append(Offset(_rowOffset)).Append("  ");
        string format = lower ? "x2" : "X2";
        for (int i = 0; i < _width; i++)
        {
            if (i < row.Length)
            {
                sb.Append(row[i].ToString(format, CultureInfo.InvariantCulture)).Append(' ');
            }
            else
            {
                sb.Append("   ");
            }

            if (_group > 0 && (i + 1) % _group == 0 && i + 1 < _width)
            {
                sb.Append(' ');
            }
        }

        sb.Append(" |");
        foreach (byte b in row)
        {
            sb.Append(TextChar(b));
        }

        return sb.Append('|').ToString();
    }

    private string FormatPlain(ReadOnlySpan<byte> row)
    {
        var sb = new StringBuilder(row.Length * 3);
        for (int i = 0; i < row.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(_group > 0 && i % _group == 0 ? "  " : " ");
            }

            sb.Append(row[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
