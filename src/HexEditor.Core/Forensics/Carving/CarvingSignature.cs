using System.Globalization;
using HexEditor.Core.Expressions;

namespace HexEditor.Core.Forensics.Carving;

/// <summary>Hex バイト列の書式の誤り (00 の 6.3)。</summary>
public enum HexPatternError
{
    None,
    Empty,

    /// <summary>奇数桁 (最後のニブルを補完しない)。</summary>
    OddDigits,

    /// <summary>16 進数と <c>?</c> 以外の文字。</summary>
    BadCharacter,

    /// <summary>固定のバイトが 1 つもない (<c>??</c> だけ)。</summary>
    NoFixedByte,
}

/// <summary>
/// ワイルドカード付きの Hex バイト列 (<c>CA FE ?? BE</c>、<c>A?</c>)。<c>??</c> は任意の 1 バイト、<c>?</c> は任意の 1 ニブル。
/// 区切りは空白・カンマ・改行・<c>0x</c>・<c>\x</c> (00 の 6.3)。
/// </summary>
public sealed class HexMaskPattern
{
    private HexMaskPattern(byte[] value, byte[] mask)
    {
        Value = value;
        Mask = mask;
        (AnchorOffset, AnchorLength) = LongestFixedRun(mask);
    }

    /// <summary>各バイトの値 (ワイルドカードの部分は 0)。</summary>
    public byte[] Value { get; }

    /// <summary>各バイトの比べるビット (0xFF = 固定、0xF0 / 0x0F = 片方のニブルだけ、0x00 = 任意)。</summary>
    public byte[] Mask { get; }

    public int Length => Value.Length;

    /// <summary>固定のバイトが最も長く続く部分の位置と長さ (検索の足がかり)。</summary>
    public int AnchorOffset { get; }

    public int AnchorLength { get; }

    public ReadOnlySpan<byte> Anchor => Value.AsSpan(AnchorOffset, AnchorLength);

    public static HexPatternError TryParse(string? text, out HexMaskPattern? pattern)
    {
        pattern = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return HexPatternError.Empty;
        }

        string s = text.Replace("\\x", " ", StringComparison.OrdinalIgnoreCase);
        var nibbles = new List<(int Value, bool Wild)>();
        foreach (string token in s.Split([' ', ',', '\t', '\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            string t = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;
            foreach (char c in t)
            {
                if (c == '?')
                {
                    nibbles.Add((0, true));
                }
                else if (Uri.IsHexDigit(c))
                {
                    nibbles.Add((Convert.ToInt32(c.ToString(), 16), false));
                }
                else
                {
                    return HexPatternError.BadCharacter;
                }
            }
        }

        if (nibbles.Count == 0)
        {
            return HexPatternError.Empty;
        }

        if (nibbles.Count % 2 != 0)
        {
            return HexPatternError.OddDigits;
        }

        byte[] value = new byte[nibbles.Count / 2], mask = new byte[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            (int hi, bool hiWild) = nibbles[i * 2];
            (int lo, bool loWild) = nibbles[i * 2 + 1];
            value[i] = (byte)((hiWild ? 0 : hi << 4) | (loWild ? 0 : lo));
            mask[i] = (byte)((hiWild ? 0 : 0xF0) | (loWild ? 0 : 0x0F));
        }

        if (!mask.Contains((byte)0xFF))
        {
            return HexPatternError.NoFixedByte;
        }

        pattern = new HexMaskPattern(value, mask);
        return HexPatternError.None;
    }

    /// <summary>正しい書式であることを前提に作る (組み込みの定義用)。</summary>
    public static HexMaskPattern Parse(string text) =>
        TryParse(text, out HexMaskPattern? p) == HexPatternError.None ? p! : throw new FormatException($"Invalid hex pattern: {text}");

    private static (int Offset, int Length) LongestFixedRun(byte[] mask)
    {
        int bestOffset = 0, bestLength = 0, run = 0;
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0xFF)
            {
                run++;
                if (run > bestLength)
                {
                    bestLength = run;
                    bestOffset = i - run + 1;
                }
            }
            else
            {
                run = 0;
            }
        }

        return (bestOffset, bestLength);
    }

    /// <summary><paramref name="data"/> の先頭がこのパターンに一致するか。</summary>
    public bool MatchesAt(ReadOnlySpan<byte> data)
    {
        if (data.Length < Value.Length)
        {
            return false;
        }

        for (int i = 0; i < Value.Length; i++)
        {
            if ((data[i] & Mask[i]) != Value[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>16 進の文字列 (<c>CA FE ?? BE</c>)。</summary>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Value.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(Mask[i] switch
            {
                0xFF => Value[i].ToString("X2", CultureInfo.InvariantCulture),
                0x00 => "??",
                0xF0 => (Value[i] >> 4).ToString("X", CultureInfo.InvariantCulture) + "?",
                _ => "?" + (Value[i] & 0xF).ToString("X", CultureInfo.InvariantCulture),
            });
        }

        return sb.ToString();
    }
}

/// <summary>サイズの取得の規則 (FOR-18 の仕様 2): ヘッダー内の値からファイルの長さを求める。長さ = 値 + <see cref="Add"/>。</summary>
public sealed record SizeFieldRule
{
    /// <summary>ファイルの先頭からの値のオフセット。</summary>
    public long Offset { get; init; }

    /// <summary>値の幅 (8、16、24、32、40、48、56、64 bit)。</summary>
    public int Bits { get; init; } = 32;

    public bool BigEndian { get; init; }

    /// <summary>値に加える数 (ヘッダーの分など。負でもよい)。</summary>
    public long Add { get; init; }

    public bool IsValid => Offset >= 0 && Bits is 8 or 16 or 24 or 32 or 40 or 48 or 56 or 64;
}

/// <summary>定義の誤り (名前付きの理由コード)。UI は <see cref="Code"/> を説明文のリソースにする。</summary>
public sealed record SignatureError(string Field, string Code, string Detail = "");

/// <summary>カービング用のファイル形式の定義 1 つ (FOR-18 の仕様 2)。</summary>
public sealed record CarvingSignature
{
    public const long DefaultMaxSize = 10L * 1024 * 1024;

    public string Name { get; init; } = string.Empty;

    /// <summary>書き出すときの拡張子 (ドットなし)。</summary>
    public string Extension { get; init; } = string.Empty;

    /// <summary>ヘッダーの Hex バイト列 (<c>??</c> / <c>?</c> のワイルドカード可)。複数指定可。</summary>
    public IReadOnlyList<string> Headers { get; init; } = [];

    /// <summary>ファイルの先頭からヘッダーまでのオフセット (MP4 の <c>ftyp</c> は 4)。</summary>
    public long HeaderOffset { get; init; }

    public string? Footer { get; init; }

    /// <summary>フッターの先頭から何バイトを含めるか (フッターを含めるバイト数)。null ならフッターの長さ。</summary>
    public long? FooterInclude { get; init; }

    public long MaxSize { get; init; } = DefaultMaxSize;

    public long MinSize { get; init; }

    public SizeFieldRule? SizeField { get; init; }

    /// <summary>構造の検証の名前 (組み込みの検証または構造体テンプレートの名前)。</summary>
    public string? Structure { get; init; }

    public bool SectorAligned { get; init; } = true;

    public bool Enabled { get; init; } = true;

    /// <summary>組み込みの定義か (保存しない。編集できず、無効にすることと複製だけができる)。</summary>
    public bool BuiltIn { get; init; }

    /// <summary>書き出し先のフォルダ名 (<c>jpeg/…</c>)。名前を小文字にして、英数字以外を <c>-</c> にしたもの。</summary>
    public string Folder
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in Name.ToLowerInvariant())
            {
                sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
            }

            string folder = sb.ToString().Trim('-');
            while (folder.Contains("--", StringComparison.Ordinal))
            {
                folder = folder.Replace("--", "-", StringComparison.Ordinal);
            }

            return folder.Length == 0 ? "unknown" : folder;
        }
    }

    /// <summary>フッターを含める長さ。</summary>
    public long FooterIncludeLength(HexMaskPattern footer) => FooterInclude ?? footer.Length;

    /// <summary>定義の誤り。なければ空。</summary>
    public IReadOnlyList<SignatureError> Validate()
    {
        var errors = new List<SignatureError>();
        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add(new("Name", "NameEmpty"));
        }

        string ext = Extension.TrimStart('.');
        if (ext.Length == 0)
        {
            errors.Add(new("Extension", "ExtensionEmpty"));
        }
        else if (ext.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-')) || ext.Length > 16)
        {
            errors.Add(new("Extension", "ExtensionInvalid", ext));
        }

        if (Headers.Count == 0 || Headers.All(string.IsNullOrWhiteSpace))
        {
            errors.Add(new("Headers", "HeaderEmpty"));
        }
        else
        {
            foreach (string header in Headers)
            {
                HexPatternError e = HexMaskPattern.TryParse(header, out _);
                if (e != HexPatternError.None)
                {
                    errors.Add(new("Headers", e == HexPatternError.NoFixedByte ? "HeaderNoFixedByte" : e == HexPatternError.Empty ? "HeaderEmpty" : "HeaderFormat", header));
                }
            }
        }

        if (HeaderOffset < 0)
        {
            errors.Add(new("HeaderOffset", "HeaderOffsetNegative"));
        }

        if (!string.IsNullOrWhiteSpace(Footer))
        {
            HexPatternError e = HexMaskPattern.TryParse(Footer, out _);
            if (e != HexPatternError.None)
            {
                errors.Add(new("Footer", "FooterFormat", Footer));
            }
        }

        if (FooterInclude is < 0)
        {
            errors.Add(new("FooterInclude", "FooterIncludeNegative"));
        }

        if (MaxSize < 1)
        {
            errors.Add(new("MaxSize", "MaxSizeInvalid"));
        }

        if (MinSize < 0)
        {
            errors.Add(new("MinSize", "MinSizeInvalid"));
        }

        if (MaxSize >= 1 && MinSize >= 0 && MaxSize < MinSize)
        {
            errors.Add(new("MaxSize", "MaxSizeBelowMin"));
        }

        if (SizeField is { IsValid: false })
        {
            errors.Add(new("SizeField", "SizeFieldInvalid"));
        }

        return errors;
    }

    /// <summary>解析済みのヘッダー (正しい定義だけ)。</summary>
    public IReadOnlyList<HexMaskPattern> ParsedHeaders() =>
        [.. Headers.Select(h => HexMaskPattern.TryParse(h, out HexMaskPattern? p) == HexPatternError.None ? p! : throw new FormatException(h))];

    public HexMaskPattern? ParsedFooter() =>
        !string.IsNullOrWhiteSpace(Footer) && HexMaskPattern.TryParse(Footer, out HexMaskPattern? p) == HexPatternError.None ? p : null;
}

/// <summary>サイズの入力 (<c>1M</c>、<c>0x100</c>、<c>10 MiB</c>)。00 の 6.1 の入力式。接頭辞のない数値は 10 進。</summary>
public static class SizeInput
{
    private sealed class NoContext : IExpressionContext
    {
        public long Cursor => 0;

        public long Length => 0;

        public long SelectionStart => 0;

        public long SelectionLength => 0;

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination) => false;
    }

    public static bool TryParse(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // "10 MiB" のような単位の書き方も受け付ける (K / M / G / T の別名)。
        string t = text.Trim();
        foreach ((string suffix, string replacement) in new[] { ("TiB", "T"), ("GiB", "G"), ("MiB", "M"), ("KiB", "K") })
        {
            if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                t = t[..^suffix.Length].TrimEnd() + replacement;
                break;
            }
        }

        return ExpressionEvaluator.TryEvaluate(t, new NoContext(), out value, out _, DefaultRadix.Decimal);
    }
}
