using System.Globalization;
using System.Text;

namespace HexEditor.Core.Text;

/// <summary>
/// 文字列のリソースの書式 (UI-42 の仕様 4)。<c>{0}</c> などの位置の引数 (string.Format と同じ書式指定も使える) に加え、ICU MessageFormat の
/// 複数形 <c>{0, plural, one {# match} other {# matches}}</c> を扱う。<c>#</c> は数値 (地域設定で書式化)、<c>=0</c> などは値そのものの指定。
/// </summary>
public static class MessageFormat
{
    /// <summary><paramref name="pattern"/> を書式化する。複数形の規則は <paramref name="language"/> (表示言語)、数値の書式は <paramref name="culture"/>。</summary>
    public static string Format(string pattern, CultureInfo culture, string language, params object?[] args)
    {
        if (!pattern.Contains(", plural,", StringComparison.Ordinal))
        {
            // 書式指定のない {0} の整数は、桁区切りを付ける (複数形を使わない訳でも、件数の表示を同じにする)。
            return string.Format(culture, pattern, [.. args.Select(a => a is int or long or uint or ulong or short or ushort ? new GroupedInteger(Convert.ToDecimal(a, CultureInfo.InvariantCulture)) : a)]);
        }

        var result = new StringBuilder();
        int i = 0;
        while (i < pattern.Length)
        {
            char c = pattern[i];
            if (c == '{' && TryReadPlural(pattern, i, out int index, out Dictionary<string, string>? cases, out int end))
            {
                object? value = index < args.Length ? args[index] : null;
                decimal number = Convert.ToDecimal(value ?? 0, CultureInfo.InvariantCulture);
                string body = cases.TryGetValue("=" + number.ToString(CultureInfo.InvariantCulture), out string? exact) ? exact
                    : cases.TryGetValue(PluralRules.Category(language, number), out string? selected) ? selected
                    : cases.GetValueOrDefault("other", string.Empty);
                string formattedNumber = value is IFormattable f ? f.ToString("N0", culture) : number.ToString("N0", culture);
                result.Append(Format(body.Replace("#", formattedNumber, StringComparison.Ordinal), culture, language, args));
                i = end;
                continue;
            }

            if (c == '{')
            {
                int close = pattern.IndexOf('}', i);
                if (close < 0)
                {
                    throw new FormatException($"閉じていない {{: {pattern}");
                }

                result.Append(string.Format(culture, pattern[i..(close + 1)], args));
                i = close + 1;
                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }

    /// <summary><c>{n, plural, 選択子 {本文} ...}</c> を読む。<paramref name="end"/> は閉じ括弧の次の位置。</summary>
    private static bool TryReadPlural(string s, int start, out int index, out Dictionary<string, string> cases, out int end)
    {
        index = 0;
        cases = new Dictionary<string, string>(StringComparer.Ordinal);
        end = start;
        int comma = s.IndexOf(',', start);
        int close = s.IndexOf('}', start);
        if (comma < 0 || (close >= 0 && close < comma) || !int.TryParse(s.AsSpan(start + 1, comma - start - 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out index))
        {
            return false;
        }

        int pos = comma + 1;
        int second = s.IndexOf(',', pos);
        if (second < 0 || s[pos..second].Trim() != "plural")
        {
            return false;
        }

        pos = second + 1;
        while (pos < s.Length)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos]))
            {
                pos++;
            }

            if (pos < s.Length && s[pos] == '}')
            {
                end = pos + 1;
                return true;
            }

            int open = s.IndexOf('{', pos);
            if (open < 0)
            {
                return false;
            }

            string selector = s[pos..open].Trim();
            int depth = 0;
            int j = open;
            for (; j < s.Length; j++)
            {
                if (s[j] == '{')
                {
                    depth++;
                }
                else if (s[j] == '}' && --depth == 0)
                {
                    break;
                }
            }

            if (j >= s.Length)
            {
                return false;
            }

            cases[selector] = s[(open + 1)..j];
            pos = j + 1;
        }

        return false;
    }
}

/// <summary>書式指定がなければ桁区切り付きで出す整数 (書式指定があればそれに従う)。</summary>
internal readonly record struct GroupedInteger(decimal Value) : IFormattable
{
    public string ToString(string? format, IFormatProvider? formatProvider) =>
        string.IsNullOrEmpty(format) ? Value.ToString("N0", formatProvider) : ((long)Value).ToString(format, formatProvider);

    public override string ToString() => ToString(null, CultureInfo.CurrentCulture);
}

/// <summary>
/// 複数形の区分 (CLDR の基数の規則のうち、整数について。00-overview.md 5.1 の 23 言語)。
/// </summary>
public static class PluralRules
{
    public static string Category(string language, decimal number)
    {
        string lang = language.Split('-')[0].ToLowerInvariant();
        bool integer = number == decimal.Truncate(number);
        long n = integer ? (long)Math.Abs(number) : -1;
        switch (lang)
        {
            case "ja" or "zh" or "ko" or "vi" or "th" or "id":
                return "other";
            case "fr" or "pt" when integer:
                return n is 0 or 1 ? "one" : "other";
            case "ru" or "uk" when integer:
                return (n % 10 == 1 && n % 100 != 11) ? "one"
                    : (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) ? "few" : "many";
            case "pl" when integer:
                return n == 1 ? "one" : (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) ? "few" : "many";
            case "cs" when integer:
                return n == 1 ? "one" : n is >= 2 and <= 4 ? "few" : "other";
            case "ro" when integer:
                return n == 1 ? "one" : (n == 0 || n % 100 is >= 2 and <= 19) ? "few" : "other";
            case "ar" when integer:
                return n switch
                {
                    0 => "zero",
                    1 => "one",
                    2 => "two",
                    _ when n % 100 is >= 3 and <= 10 => "few",
                    _ when n % 100 is >= 11 and <= 99 => "many",
                    _ => "other",
                };
            case "fa" when integer:
                return n is 0 or 1 ? "one" : "other";
            default:
                // en、de、es、it、hu、el、tr など: 1 が one。
                return integer && n == 1 ? "one" : "other";
        }
    }
}
