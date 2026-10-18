using System.Globalization;
using System.Text;
using HexEditor.Core.View;

namespace HexEditor.Core.Tools;

public enum CharacterCellKind
{
    /// <summary>文字が割り当てられていないバイト (斜線で表示する)。</summary>
    Undefined,

    Character,

    /// <summary>複数バイトの文字コードの先行バイト。</summary>
    Lead,
}

/// <summary>
/// 表の 1 セル。<see cref="Bytes"/> は選んだときに入力するバイト列 (先行バイトのセルでは先行バイトだけ)、<see cref="Label"/> はセルに出す文字
/// (制御文字は略称)、<see cref="Text"/> は文字そのもの (制御文字・未定義では空)。
/// </summary>
public sealed record CharacterCell(CharacterCellKind Kind, byte[] Bytes, int CodePoint, string Text, string Label, string? Name)
{
    /// <summary>スクリーンリーダーが読む名前 (例: <c>0x41、A、LATIN CAPITAL LETTER A</c>)。</summary>
    public string SpokenText
    {
        get
        {
            string hex = "0x" + Convert.ToHexString(Bytes);
            return Kind switch
            {
                CharacterCellKind.Undefined => hex,
                CharacterCellKind.Lead => hex,
                _ => Name is null ? $"{hex}, {Label}" : $"{hex}, {Label}, {Name}",
            };
        }
    }
}

/// <summary>詳細欄の内容 (TOOL-26 の仕様 4)。</summary>
public sealed record CharacterDetail(
    string ByteHex,
    string ByteDecimal,
    string ByteOctal,
    string ByteBinary,
    string Character,
    string CodePointText,
    string? Name,
    string Utf8,
    string Utf16);

/// <summary>一覧表示の 1 行 (仕様 5。列: Hex、10 進、8 進、文字、コードポイント、名前)。</summary>
public sealed record CharacterListRow(string Hex, string Decimal, string Octal, string Character, string CodePoint, string Name);

/// <summary>
/// 文字コード表 (TOOL-26)。選んだ文字コードの 256 のバイト値と文字の対応。複数バイトの文字コード (Shift_JIS、EUC-JP、GBK、Big5、UHC など) は
/// 先行バイトの表と、先行バイトごとの 2 バイト目の表で示す。
/// </summary>
public sealed class CharacterTable
{
    private readonly Encoding _strict;
    private readonly Dictionary<byte, CharacterCell> _singleCache = [];
    private bool[]? _lead;

    public CharacterTable(EncodingEntry entry)
    {
        Entry = entry;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int codePage = entry.CodePage switch
        {
            0 => CultureInfo.CurrentCulture.TextInfo.ANSICodePage,
            1 => CultureInfo.CurrentCulture.TextInfo.OEMCodePage,
            _ => entry.CodePage,
        };
        CodePage = codePage;
        _strict = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    public EncodingEntry Entry { get; }

    public int CodePage { get; }

    /// <summary>表で示せる文字コードか (UTF-16 / UTF-32 / UTF-7 は 1 バイトごとの表にならない)。</summary>
    public static bool IsSupported(EncodingEntry entry) =>
        entry.Id is not ("utf-16le" or "utf-16be" or "utf-32le" or "utf-32be" or "cp65000") && entry.CodePage != 12000 && entry.Group != EncodingGroup.Custom
        && !entry.Id.StartsWith("table:", StringComparison.OrdinalIgnoreCase);

    /// <summary>この文字コードの 1 バイトの表を作れるか (この環境でコードページを提供するか)。</summary>
    public static CharacterTable? TryCreate(EncodingEntry entry)
    {
        try
        {
            return IsSupported(entry) ? new CharacterTable(entry) : null;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>複数バイトの文字コードか (先行バイトがあるか)。</summary>
    public bool IsMultiByte => Leads.Any(l => l);

    /// <summary>各バイトが先行バイトか。</summary>
    public IReadOnlyList<bool> Leads => _lead ??= ComputeLeads();

    private bool[] ComputeLeads()
    {
        var lead = new bool[256];
        for (int b = 0; b < 256; b++)
        {
            if (DecodeSingle((byte)b) is not null)
            {
                continue;
            }

            // 単独では文字にならないバイトに、続けて文字になるバイトがあれば先行バイト。
            for (int t = 0; t < 256 && !lead[b]; t++)
            {
                lead[b] = DecodePair((byte)b, (byte)t) is not null;
            }
        }

        return lead;
    }

    private string? DecodeSingle(byte value)
    {
        try
        {
            string text = _strict.GetString([value]);
            return text.Length > 0 && text != "�" ? text : null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private string? DecodePair(byte lead, byte trail)
    {
        try
        {
            string text = _strict.GetString([lead, trail]);
            return text.Length > 0 && text != "�" && !text.Contains('�') && char.ConvertToUtf32(text, 0) is var cp && text.Length == (cp > 0xFFFF ? 2 : 1)
                ? text
                : null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>最初の表のセル (1 バイト)。</summary>
    public CharacterCell Cell(byte value)
    {
        if (_singleCache.TryGetValue(value, out CharacterCell? cached))
        {
            return cached;
        }

        CharacterCell cell;
        if (DecodeSingle(value) is { } text)
        {
            cell = MakeCell([value], text);
        }
        else if (Leads[value])
        {
            cell = new CharacterCell(CharacterCellKind.Lead, [value], -1, string.Empty, "»", null);
        }
        else
        {
            cell = new CharacterCell(CharacterCellKind.Undefined, [value], -1, string.Empty, string.Empty, null);
        }

        _singleCache[value] = cell;
        return cell;
    }

    /// <summary>先行バイト <paramref name="lead"/> の 2 バイト目の表のセル。</summary>
    public CharacterCell TrailCell(byte lead, byte trail) =>
        DecodePair(lead, trail) is { } text
            ? MakeCell([lead, trail], text)
            : new CharacterCell(CharacterCellKind.Undefined, [lead, trail], -1, string.Empty, string.Empty, null);

    private static CharacterCell MakeCell(byte[] bytes, string text)
    {
        int cp = char.ConvertToUtf32(text, 0);
        string? name = UnicodeNames.Get(cp);
        string? control = UnicodeNames.ControlAbbreviation(cp);
        string label;
        if (control is not null)
        {
            label = control;
        }
        else if (cp == 0x20)
        {
            label = "SP";
        }
        else if (cp == 0xA0)
        {
            label = "NBSP";
        }
        else if (cp == 0xAD)
        {
            label = "SHY";
        }
        else
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(text, 0);
            label = category switch
            {
                UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark => "◌" + text,
                UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator
                    or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned => "U+" + cp.ToString("X4", CultureInfo.InvariantCulture),
                _ => text,
            };
        }

        return new CharacterCell(CharacterCellKind.Character, bytes, cp, control is null ? text : string.Empty, label, name);
    }

    /// <summary>詳細欄の内容。UTF-16 のバイト列は <paramref name="utf16BigEndian"/> でビッグエンディアン (既定はリトルエンディアン)。</summary>
    public static CharacterDetail Detail(CharacterCell cell, bool utf16BigEndian)
    {
        byte[] b = cell.Bytes;
        ulong value = 0;
        foreach (byte x in b)
        {
            value = (value << 8) | x;
        }

        string hex = "0x" + Convert.ToHexString(b);
        string binary = string.Join(' ', b.Select(x => Convert.ToString(x, 2).PadLeft(8, '0')));
        string octal = string.Join(' ', b.Select(x => Convert.ToString(x, 8).PadLeft(3, '0')));
        if (cell.Kind != CharacterCellKind.Character)
        {
            return new CharacterDetail(hex, value.ToString(CultureInfo.InvariantCulture), octal, binary, string.Empty, string.Empty, null, string.Empty, string.Empty);
        }

        string text = char.ConvertFromUtf32(cell.CodePoint);
        string utf8 = Convert.ToHexString(Encoding.UTF8.GetBytes(text)).Chunk(2).Select(c => new string(c)).Aggregate((a, c) => a + " " + c);
        byte[] utf16Bytes = (utf16BigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetBytes(text);
        string utf16 = string.Join(' ', utf16Bytes.Select(x => x.ToString("X2", CultureInfo.InvariantCulture)));
        return new CharacterDetail(
            hex,
            value.ToString(CultureInfo.InvariantCulture),
            octal,
            binary,
            cell.Text.Length > 0 ? cell.Text : cell.Label,
            "U+" + cell.CodePoint.ToString("X4", CultureInfo.InvariantCulture),
            cell.Name,
            utf8,
            utf16);
    }

    /// <summary>一覧表示 (仕様 5): 1 行 1 バイト、256 行。先行バイトを指定すると、その 2 バイト目の表 (256 行)。</summary>
    public IReadOnlyList<CharacterListRow> List(byte? lead = null)
    {
        var rows = new List<CharacterListRow>(256);
        for (int i = 0; i < 256; i++)
        {
            CharacterCell cell = lead is { } l ? TrailCell(l, (byte)i) : Cell((byte)i);
            rows.Add(new CharacterListRow(
                i.ToString("X2", CultureInfo.InvariantCulture),
                i.ToString(CultureInfo.InvariantCulture),
                Convert.ToString(i, 8).PadLeft(3, '0'),
                cell.Kind == CharacterCellKind.Character ? (cell.Text.Length > 0 ? cell.Text : cell.Label) : string.Empty,
                cell.Kind == CharacterCellKind.Character ? "U+" + cell.CodePoint.ToString("X4", CultureInfo.InvariantCulture) : string.Empty,
                cell.Name ?? string.Empty));
        }

        return rows;
    }

    // ---- 検索 (仕様 6) ----

    /// <summary>文字・名前・コードポイントで検索して、最初に見つかったセルの位置を返す。先行バイトがあれば 2 バイト目の表のセル。</summary>
    public (byte? Lead, byte Value)? Find(string query)
    {
        string q = query.Trim();
        if (q.Length == 0)
        {
            return null;
        }

        // コードポイント: U+3042 / 0x3042 / 3042 (16 進)。
        string hexText = q.StartsWith("U+", StringComparison.OrdinalIgnoreCase) ? q[2..] : q.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? q[2..] : q;
        if (hexText.Length is >= 2 and <= 6 && hexText.All(char.IsAsciiHexDigit) && (q.StartsWith("U+", StringComparison.OrdinalIgnoreCase) || q.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || q.Length >= 4)
            && int.TryParse(hexText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int codePoint) && codePoint <= 0x10FFFF
            && !char.IsSurrogate((char)Math.Min(codePoint, 0xFFFF)))
        {
            if (FindCodePoint(codePoint) is { } byCode)
            {
                return byCode;
            }
        }

        // 文字そのもの。
        if (q.Length <= 2 && !q.Any(char.IsAsciiHexDigit) || q.Length == 1 || (q.Length == 2 && char.IsHighSurrogate(q[0])))
        {
            if (FindCodePoint(char.ConvertToUtf32(q, 0)) is { } byChar)
            {
                return byChar;
            }
        }

        // 名前 (部分一致、大文字・小文字を区別しない)。
        for (int b = 0; b < 256; b++)
        {
            CharacterCell cell = Cell((byte)b);
            if (cell.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
            {
                return (null, (byte)b);
            }
        }

        for (int l = 0; l < 256; l++)
        {
            if (!Leads[l])
            {
                continue;
            }

            for (int t = 0; t < 256; t++)
            {
                if (TrailCell((byte)l, (byte)t).Name?.Contains(q, StringComparison.OrdinalIgnoreCase) == true)
                {
                    return ((byte)l, (byte)t);
                }
            }
        }

        return null;
    }

    private (byte? Lead, byte Value)? FindCodePoint(int codePoint)
    {
        byte[] bytes;
        try
        {
            bytes = _strict.GetBytes(char.ConvertFromUtf32(codePoint));
        }
        catch (Exception ex) when (ex is EncoderFallbackException or ArgumentOutOfRangeException)
        {
            return null;
        }

        return bytes.Length switch
        {
            1 => (null, bytes[0]),
            2 => (bytes[0], bytes[1]),
            _ => null,
        };
    }
}
