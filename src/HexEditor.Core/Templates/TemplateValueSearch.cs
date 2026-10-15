using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Templates;

/// <summary>テンプレートの値の検索の条件の構文エラーの種類 (FIND-29 の「エラー」)。</summary>
public enum TemplateQueryErrorKind
{
    /// <summary>条件が空。</summary>
    Empty,

    /// <summary><c>name:</c> などの後に値がない、比較の演算子の後に値がない。</summary>
    MissingValue,

    /// <summary>知らない条件の名前 (<c>name</c>、<c>type</c>、<c>value</c> 以外)。</summary>
    UnknownKey,

    /// <summary>比較の演算子がない・正しくない。</summary>
    BadOperator,

    /// <summary>引用符が閉じていない。</summary>
    UnclosedQuote,

    /// <summary>正規表現が正しくない。</summary>
    BadRegex,
}

/// <summary>条件の構文エラー。<see cref="Position"/> は条件の文字列の中の位置 (0 始まり)。</summary>
public sealed class TemplateQueryException(TemplateQueryErrorKind kind, int position, string detail = "")
    : Exception($"{kind} at {position + 1}{(detail.Length > 0 ? ": " + detail : string.Empty)}")
{
    public TemplateQueryErrorKind Kind { get; } = kind;

    public int Position { get; } = position;

    public string Detail { get; } = detail;
}

/// <summary>検索の結果 1 件: フィールドのパス、オフセット、長さ、値 (FIND-29 の仕様 3)。</summary>
public sealed record TemplateValueMatch(string Path, long Offset, long Length, string Value, TemplateNode Node);

/// <summary>値の比較の演算子。</summary>
public enum TemplateValueOperator
{
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,

    /// <summary><c>~</c>: 文字列値の正規表現。</summary>
    Matches,
}

/// <summary>
/// テンプレートの値の検索 (FIND-29)。条件はフィールド名 (<c>name:*size*</c>。ワイルドカードがなければ部分一致)、型名 (<c>type:uint32</c>)、
/// 値の比較 (<c>value&gt;0x1000</c>、<c>value=0</c>、<c>value~"^MZ"</c>) を空白で区切って並べ、すべてを満たすフィールドを返す。接頭辞のない語は
/// フィールド名の部分一致。値は整数・浮動小数点・文字列・列挙値の名前と比べる (仕様 1・2)。
/// </summary>
public sealed class TemplateValueQuery
{
    private readonly List<Func<TemplateNode, bool>> _conditions = [];

    private TemplateValueQuery(string text)
    {
        Text = text;
    }

    public string Text { get; }

    /// <summary>条件を読む。構文エラーなら <see cref="TemplateQueryException"/> (位置付き)。</summary>
    public static TemplateValueQuery Parse(string text)
    {
        var query = new TemplateValueQuery(text);
        int pos = 0;
        while (true)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
            {
                pos++;
            }

            if (pos >= text.Length)
            {
                break;
            }

            query.ParseTerm(text, ref pos);
        }

        return query._conditions.Count == 0 ? throw new TemplateQueryException(TemplateQueryErrorKind.Empty, 0) : query;
    }

    /// <summary>フィールドが条件をすべて満たすか。</summary>
    public bool Matches(TemplateNode node) => _conditions.All(c => c(node));

    private void ParseTerm(string text, ref int pos)
    {
        int start = pos;
        int keyEnd = pos;
        while (keyEnd < text.Length && (char.IsAsciiLetter(text[keyEnd])))
        {
            keyEnd++;
        }

        string key = text[start..keyEnd].ToLowerInvariant();
        if (keyEnd < text.Length && text[keyEnd] == ':' && key.Length > 0)
        {
            pos = keyEnd + 1;
            string pattern = ReadValue(text, ref pos, out _);
            switch (key)
            {
                case "name":
                    Regex name = Wildcard(pattern);
                    _conditions.Add(n => name.IsMatch(n.DisplayName) || name.IsMatch(n.Name));
                    return;
                case "type":
                    Regex type = Wildcard(pattern, partial: false);
                    _conditions.Add(n => type.IsMatch(n.TypeName));
                    return;
                default:
                    throw new TemplateQueryException(TemplateQueryErrorKind.UnknownKey, start, key);
            }
        }

        if (key == "value" && keyEnd < text.Length && text[keyEnd] is '=' or '!' or '<' or '>' or '~')
        {
            pos = keyEnd;
            TemplateValueOperator op = ReadOperator(text, ref pos);
            int valueAt = pos;
            string literal = ReadValue(text, ref pos, out bool quoted);
            _conditions.Add(Compare(op, literal, quoted, valueAt));
            return;
        }

        if (key == "value")
        {
            throw new TemplateQueryException(TemplateQueryErrorKind.BadOperator, keyEnd);
        }

        // 接頭辞のない語: フィールド名の部分一致。
        pos = start;
        Regex bare = Wildcard(ReadValue(text, ref pos, out _));
        _conditions.Add(n => bare.IsMatch(n.DisplayName) || bare.IsMatch(n.Name));
    }

    private static TemplateValueOperator ReadOperator(string text, ref int pos)
    {
        string two = pos + 1 < text.Length ? text.Substring(pos, 2) : string.Empty;
        (TemplateValueOperator op, int length) = two switch
        {
            "<=" => (TemplateValueOperator.LessOrEqual, 2),
            ">=" => (TemplateValueOperator.GreaterOrEqual, 2),
            "!=" => (TemplateValueOperator.NotEqual, 2),
            "==" => (TemplateValueOperator.Equal, 2),
            _ => text[pos] switch
            {
                '<' => (TemplateValueOperator.Less, 1),
                '>' => (TemplateValueOperator.Greater, 1),
                '=' => (TemplateValueOperator.Equal, 1),
                '~' => (TemplateValueOperator.Matches, 1),
                _ => throw new TemplateQueryException(TemplateQueryErrorKind.BadOperator, pos),
            },
        };
        pos += length;
        return op;
    }

    /// <summary>値 (引用符で囲んだ文字列、または空白までの語) を読む。なければ構文エラー。</summary>
    private static string ReadValue(string text, ref int pos, out bool quoted)
    {
        quoted = false;
        if (pos >= text.Length || char.IsWhiteSpace(text[pos]))
        {
            throw new TemplateQueryException(TemplateQueryErrorKind.MissingValue, pos);
        }

        if (text[pos] == '"')
        {
            quoted = true;
            int open = pos;
            var sb = new StringBuilder();
            pos++;
            while (pos < text.Length && text[pos] != '"')
            {
                if (text[pos] == '\\' && pos + 1 < text.Length && text[pos + 1] is '"' or '\\')
                {
                    pos++;
                }

                sb.Append(text[pos++]);
            }

            if (pos >= text.Length)
            {
                throw new TemplateQueryException(TemplateQueryErrorKind.UnclosedQuote, open);
            }

            pos++;
            return sb.ToString();
        }

        int start = pos;
        while (pos < text.Length && !char.IsWhiteSpace(text[pos]))
        {
            pos++;
        }

        return text[start..pos];
    }

    /// <summary>ワイルドカード (<c>*</c>、<c>?</c>) の照合。ワイルドカードがなく <paramref name="partial"/> なら部分一致。大文字・小文字を区別しない。</summary>
    private static Regex Wildcard(string pattern, bool partial = true)
    {
        bool wild = pattern.Contains('*') || pattern.Contains('?');
        string body = Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".");
        string regex = wild || !partial ? "^" + body + "$" : body;
        return new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static Func<TemplateNode, bool> Compare(TemplateValueOperator op, string literal, bool quoted, int at)
    {
        if (op == TemplateValueOperator.Matches)
        {
            Regex regex;
            try
            {
                regex = new Regex(literal, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                throw new TemplateQueryException(TemplateQueryErrorKind.BadRegex, at, ex.Message);
            }

            return n => StringValue(n) is { } s && regex.IsMatch(s);
        }

        if (!quoted && TryNumber(literal, out BigInteger integer, out double number, out bool isInteger))
        {
            return n => NumericValue(n) is { } v && Test(op, isInteger && v.IsInteger ? v.Integer.CompareTo(integer) : v.Number.CompareTo(isInteger ? (double)integer : number));
        }

        // 文字列か列挙値の名前。等しい・等しくないは大文字・小文字を区別しない。大小は序数で比べる。
        return n =>
        {
            if (EnumName(n) is { } name && op is TemplateValueOperator.Equal or TemplateValueOperator.NotEqual)
            {
                return Test(op, string.Equals(name, literal, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
            }

            return StringValue(n) is { } s
                && Test(op, op is TemplateValueOperator.Equal or TemplateValueOperator.NotEqual
                    ? (string.Equals(s, literal, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    : string.CompareOrdinal(s, literal));
        };
    }

    private static bool Test(TemplateValueOperator op, int compare) => op switch
    {
        TemplateValueOperator.Equal => compare == 0,
        TemplateValueOperator.NotEqual => compare != 0,
        TemplateValueOperator.Less => compare < 0,
        TemplateValueOperator.LessOrEqual => compare <= 0,
        TemplateValueOperator.Greater => compare > 0,
        _ => compare >= 0,
    };

    private static bool TryNumber(string text, out BigInteger integer, out double number, out bool isInteger)
    {
        integer = 0;
        number = 0;
        isInteger = true;
        bool negative = text.StartsWith('-');
        string body = negative ? text[1..] : text;
        if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && BigInteger.TryParse("0" + body[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out integer))
        {
            integer = negative ? -integer : integer;
            return body.Length > 2;
        }

        if (BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out integer))
        {
            return true;
        }

        isInteger = false;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private readonly record struct Numeric(bool IsInteger, BigInteger Integer, double Number);

    private static Numeric? NumericValue(TemplateNode node)
    {
        if (node.Kind is TemplateNodeKind.Struct or TemplateNodeKind.Union or TemplateNodeKind.Array or TemplateNodeKind.Application
            or TemplateNodeKind.Section or TemplateNodeKind.Error)
        {
            return null;
        }

        return node.Value switch
        {
            long l => new Numeric(true, l, l),
            ulong u => new Numeric(true, u, u),
            Int128 i => new Numeric(true, (BigInteger)i, (double)i),
            BigInteger b => new Numeric(true, b, (double)b),
            double d => new Numeric(false, 0, d),
            float f => new Numeric(false, 0, f),
            bool flag => new Numeric(true, flag ? 1 : 0, flag ? 1 : 0),
            _ => null,
        };
    }

    private static string? StringValue(TemplateNode node) => node.Kind switch
    {
        TemplateNodeKind.Struct or TemplateNodeKind.Union or TemplateNodeKind.Array or TemplateNodeKind.Application or TemplateNodeKind.Section => null,
        _ => node.Value as string ?? node.DisplayValue,
    };

    /// <summary>列挙値の名前 (表示の先頭の語)。列挙でなければ null。</summary>
    private static string? EnumName(TemplateNode node)
    {
        if (node.Kind is not (TemplateNodeKind.Enum or TemplateNodeKind.Flags))
        {
            return null;
        }

        string display = node.DisplayValue;
        int end = 0;
        while (end < display.Length && (char.IsLetterOrDigit(display[end]) || display[end] is '_' or ':'))
        {
            end++;
        }

        return end > 0 ? display[..end] : null;
    }
}

/// <summary>
/// テンプレートの結果から条件に合うフィールドを集める (FIND-29 の仕様 3・4)。対象はすでに解析されたフィールドだけ。<c>evaluateAll</c> なら
/// 先に遅延して解析される部分もすべて解析する (長時間処理として呼び出し側がキャンセルできるようにする)。
/// </summary>
public static class TemplateValueSearch
{
    /// <summary>結果の件数の上限 (共通の結果一覧に出す数)。</summary>
    public const int MaxMatches = 100_000;

    public static IReadOnlyList<TemplateValueMatch> Find(TemplateResult result, TemplateValueQuery query, bool evaluateAll = false,
        CancellationToken cancellationToken = default)
    {
        if (evaluateAll)
        {
            result.EvaluateAll(null, cancellationToken);
        }

        var matches = new List<TemplateValueMatch>();
        var stack = new Stack<TemplateNode>();
        Push(stack, result.Root, evaluateAll);
        while (stack.Count > 0 && matches.Count < MaxMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TemplateNode node = stack.Pop();
            if (node.Kind is not (TemplateNodeKind.Application or TemplateNodeKind.Section or TemplateNodeKind.Error) && query.Matches(node))
            {
                matches.Add(new TemplateValueMatch(node.Path, node.Offset, node.Size, node.DisplayValue, node));
            }

            Push(stack, node, evaluateAll);
        }

        return matches;
    }

    /// <summary>
    /// 子を、ツリーの順に取り出せるよう逆順に積む。「すべて解析してから検索」でなければ、まだ解析していない子 (遅延して解析される配列など) は
    /// 解析しない (仕様 4)。
    /// </summary>
    private static void Push(Stack<TemplateNode> stack, TemplateNode node, bool all)
    {
        if (!node.HasChildren || !all && !node.IsEvaluated || node.Kind == TemplateNodeKind.Pointer && node.PointerTarget is null)
        {
            return;
        }

        IReadOnlyList<TemplateNode> children = node.Children;
        for (int i = children.Count - 1; i >= 0; i--)
        {
            stack.Push(children[i]);
        }
    }
}
