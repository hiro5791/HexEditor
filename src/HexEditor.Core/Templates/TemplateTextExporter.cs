using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Templates;

/// <summary>パスがフィールドの指定にどう当たるか。</summary>
public enum FieldMatch
{
    /// <summary>出力しない。</summary>
    None,

    /// <summary>指定したフィールドの祖先 (子をさらに絞り込む)。</summary>
    Ancestor,

    /// <summary>指定したフィールド (またはその子孫)。部分木をすべて出力する。</summary>
    All,
}

/// <summary>
/// 出力するフィールドの指定 (AUTO-42 の仕様 4)。パスは結果ツリーのパス (<c>header.e_lfanew</c>、<c>sections[3].name</c>) で、
/// 添字の <c>[*]</c> はすべての要素に当たる。大文字・小文字は区別しない (<c>sections[*].name</c> で <c>Name</c> に当たる)。
/// </summary>
public sealed class TemplateFieldFilter
{
    private readonly List<Regex> _full = [];
    private readonly List<Regex> _prefixes = [];

    public TemplateFieldFilter(IEnumerable<string> fields)
    {
        foreach (string field in fields.Select(f => f.Trim()).Where(f => f.Length > 0))
        {
            Fields.Add(field);
            List<string> tokens = Tokenize(field);
            var sb = new StringBuilder();
            for (int i = 0; i < tokens.Count; i++)
            {
                string token = tokens[i];
                if (token.StartsWith('['))
                {
                    sb.Append(token == "[*]" ? @"\[\d+\]" : Regex.Escape(token));
                }
                else
                {
                    if (i > 0)
                    {
                        sb.Append(@"\.");
                    }

                    sb.Append(Regex.Escape(token));
                }

                var regex = new Regex("^" + sb + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                (i == tokens.Count - 1 ? _full : _prefixes).Add(regex);
            }
        }
    }

    public List<string> Fields { get; } = [];

    /// <summary>ノードのパスの当たり方。子孫は呼び出し側が <see cref="FieldMatch.All"/> を引き継ぐ。</summary>
    public FieldMatch Match(string path) =>
        _full.Any(r => r.IsMatch(path)) ? FieldMatch.All : _prefixes.Any(r => r.IsMatch(path)) ? FieldMatch.Ancestor : FieldMatch.None;

    /// <summary><c>a.b[*].c</c> → <c>a</c>、<c>b</c>、<c>[*]</c>、<c>c</c>。</summary>
    private static List<string> Tokenize(string field)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < field.Length)
        {
            char c = field[i];
            if (c == '.')
            {
                i++;
            }
            else if (c == '[')
            {
                int end = field.IndexOf(']', i);
                end = end < 0 ? field.Length - 1 : end;
                tokens.Add(field[i..(end + 1)].Replace(" ", string.Empty, StringComparison.Ordinal));
                i = end + 1;
            }
            else
            {
                int end = field.IndexOfAny(['.', '['], i);
                end = end < 0 ? field.Length : end;
                tokens.Add(field[i..end]);
                i = end;
            }
        }

        return tokens;
    }
}

/// <summary>
/// 結果ツリーの CSV とインデント付きのテキスト (コマンドラインの <c>template --format csv|text</c>。AUTO-42 の仕様 3)。
/// JSON と同じく、未評価の部分は出力のために評価し、ストリームに書く。
/// </summary>
public static class TemplateTextExporter
{
    /// <summary>CSV: 見出しの行のあと、1 行 1 フィールド (パス・型・開始・長さ・値)。フィールドは葉のノード。</summary>
    public static void WriteCsv(TemplateResult result, TextWriter output, TemplateExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new TemplateExportOptions();
        output.WriteLine("path,type,offset,size,value");
        result.Invoke(() =>
        {
            Walk(result.Root, options, 0, options.Fields is null ? FieldMatch.All : FieldMatch.Ancestor, cancellationToken, (node, depth, leaf, match) =>
            {
                if (leaf && match == FieldMatch.All)
                {
                    output.WriteLine(string.Join(',',
                        Csv(node.Path), Csv(node.TypeName), Number(node.Offset, options), Number(node.Size, options), Csv(ValueText(node, options))));
                }
            });
            return 0;
        });
    }

    /// <summary>テキスト: 1 行 1 ノードのインデント付きのツリー (<c>名前 : 型 @ 開始 (長さ) = 表示値</c>)。</summary>
    public static void WriteText(TemplateResult result, TextWriter output, TemplateExportOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new TemplateExportOptions();
        result.Invoke(() =>
        {
            Walk(result.Root, options, 0, options.Fields is null ? FieldMatch.All : FieldMatch.Ancestor, cancellationToken, (node, depth, leaf, match) =>
            {
                if (depth == 0)
                {
                    output.WriteLine(node.DisplayName);
                    return;
                }

                var line = new StringBuilder();
                line.Append(' ', (depth - 1) * 2).Append(node.DisplayName).Append(" : ").Append(node.TypeName)
                    .Append(" @ ").Append(Number(node.Offset, options)).Append(" (").Append(Number(node.Size, options)).Append(')');
                if (node.Kind is not (TemplateNodeKind.Struct or TemplateNodeKind.Union or TemplateNodeKind.Section) && (leaf || node.IsByteOrCharArray))
                {
                    line.Append(" = ").Append(node.GetDisplay(options.Display));
                }

                output.WriteLine(line.ToString());
            });
            return 0;
        });
    }

    private static void Walk(TemplateNode node, TemplateExportOptions o, int depth, FieldMatch match, CancellationToken ct,
        Action<TemplateNode, int, bool, FieldMatch> visit)
    {
        ct.ThrowIfCancellationRequested();
        bool expand = node.HasChildren && (o.MaxDepth is null || depth < o.MaxDepth) && !node.Attributes.Sealed && !node.IsByteOrCharArray;
        visit(node, depth, !expand, match);
        if (!expand)
        {
            return;
        }

        long count = node.ChildCount;
        long limit = node.Kind == TemplateNodeKind.Array && o.MaxArrayElements is { } m ? Math.Min(m, count) : count;
        for (long i = 0; i < limit; i++)
        {
            TemplateNode child = node.GetChild(i);
            if (!child.Attributes.Export || (child.Attributes.Hidden && !o.IncludeHidden))
            {
                continue;
            }

            FieldMatch childMatch = match == FieldMatch.All ? FieldMatch.All : o.Fields!.Match(child.Path);
            if (childMatch != FieldMatch.None)
            {
                Walk(child, o, depth + 1, childMatch, ct, visit);
            }
        }
    }

    private static string ValueText(TemplateNode node, TemplateExportOptions o)
    {
        object? value = node.Kind is TemplateNodeKind.Struct or TemplateNodeKind.Union or TemplateNodeKind.Section ? null : node.Value;
        return value switch
        {
            null => node.GetDisplay(o.Display),
            byte[] bytes => Convert.ToHexString(bytes),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
            bool b => b ? "true" : "false",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    private static string Number(long value, TemplateExportOptions o) =>
        o.HexOffsets ? "0x" + value.ToString("X", CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>RFC 4180 の引用 (区切り・引用符・改行を含む場合)。</summary>
    private static string Csv(string text) =>
        text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
}
