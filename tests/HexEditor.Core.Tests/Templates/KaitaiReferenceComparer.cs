using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Tests.Templates;

/// <summary>許容リストの 1 行 (tests/fixtures/templates/kaitai/allowlist.tsv)。</summary>
internal sealed record KaitaiAllowEntry(string Format, Regex Path, string Aspect, string Reason)
{
    public bool Matches(string format, string path, string aspect) =>
        Format == format && (Aspect == "all" || Aspect == aspect) && Path.IsMatch(path);
}

/// <summary>期待値との違い 1 件。<see cref="AllowedBy"/> が null でなければ許容リストで認めたもの (失敗にしない)。</summary>
internal sealed record KaitaiMismatch(string Path, string Aspect, string Expected, string Actual, string? AllowedBy = null);

/// <summary>1 つの .ksy の照合結果。</summary>
internal sealed class KaitaiComparison(string format)
{
    public string Format { get; } = format;

    public List<KaitaiMismatch> Mismatches { get; } = [];

    /// <summary>失敗にしない情報 (参照側のエラー、こちらにだけあるフィールドなど)。</summary>
    public List<string> Notes { get; } = [];

    public int Fields { get; set; }

    public int Values { get; set; }

    public int Positions { get; set; }

    public int Extra { get; set; }

    public IEnumerable<KaitaiMismatch> Failures => Mismatches.Where(m => m.AllowedBy is null);

    public bool Passed => !Failures.Any();
}

/// <summary>
/// TC-TPL-33-01 の照合。参照 (Kaitai Struct の公式のコンパイラと JavaScript の実行時ライブラリによる解析。tools/KaitaiReference/dump.js) と、
/// こちらの結果ツリーの JSON (TPL-25 の形式) を比べる。
/// <para>
/// 参照の JSON: <c>{ key, ksy, data, sha256, ksySha256, toolchain, compileError, error, root }</c>。<c>root</c> と各フィールドは
/// <c>{ name, kind, offset?, size?, value?, enum?, unsafe?, instance?, error?, fields? (struct), items? (array) }</c>。<c>kind</c> は
/// <c>struct</c>・<c>array</c>・<c>bytes</c> (値は 16 進の文字列)・<c>number</c>・<c>bool</c>・<c>string</c>・<c>none</c> (値なし)・
/// <c>ref</c>/<c>unknown</c> (比べない)。名前は JavaScript の名前 (lowerCamelCase)。<c>offset</c> はデータの先頭からの位置。
/// </para>
/// <para>
/// 比べるもの: 参照のすべてのフィールド (instances を含む) について、(1) こちらに同じ名前のフィールドがある、(2) エラーがない、
/// (3) オフセットとサイズが同じ (参照に位置がある場合。ビットフィールドとセクションの中は除く)、(4) 値が同じ (整数は値、列挙型は数値で比べ、
/// 名前の表記は比べない。バイト列は 16 進、文字列はそのまま)、(5) 配列の要素の数が同じ。参照側でエラーになったフィールドは比べない (情報に出す)。
/// こちらにだけあるフィールドは失敗にせず数だけ出す。
/// </para>
/// </summary>
internal static partial class KaitaiReferenceComparer
{
    /// <summary>Kaitai Struct の Utils.lowerCamelCase (JavaScript のフィールドの名前)。末尾の <c>_</c> (HXT の予約語の回避) は消える。</summary>
    public static string LowerCamel(string name)
    {
        if (name.StartsWith('_'))
        {
            return "_" + LowerCamel(name[1..]);
        }

        List<string> words = [.. name.Split('_')];
        while (words.Count > 1 && words[^1].Length == 0)
        {
            words.RemoveAt(words.Count - 1);
        }

        var sb = new StringBuilder(words[0]);
        foreach (string w in words.Skip(1))
        {
            if (w.Length > 0)
            {
                sb.Append(char.ToUpperInvariant(w[0])).Append(w.AsSpan(1));
            }
        }

        return sb.ToString();
    }

    /// <summary>許容リスト (タブ区切り: 形式、パス (<c>*</c> は任意の文字列)、対象 (value / offset / size / missing / count / error / all)、理由)。</summary>
    public static IReadOnlyList<KaitaiAllowEntry> LoadAllowList(string path)
    {
        var list = new List<KaitaiAllowEntry>();
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            string[] cols = line.Split('\t');
            if (cols.Length < 4 || cols[3].Trim().Length == 0)
            {
                throw new FormatException($"許容リストの行の形式が違います (形式 TAB パス TAB 対象 TAB 理由): {raw}");
            }

            string pattern = "^" + Regex.Escape(cols[1].Trim()).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
            list.Add(new KaitaiAllowEntry(cols[0].Trim(), new Regex(pattern, RegexOptions.CultureInvariant), cols[2].Trim(), cols[3].Trim()));
        }

        return list;
    }

    /// <summary>参照の JSON (dump.js の 1 ファイル) と、こちらの結果ツリーの JSON (TPL-25。最上位は適用のノード) を比べる。</summary>
    public static KaitaiComparison Compare(string format, JsonNode reference, JsonNode ours, IReadOnlyList<KaitaiAllowEntry> allow)
    {
        var c = new Context(new KaitaiComparison(format), allow);
        if (Str(reference["compileError"]) is { } compileError)
        {
            c.Add("(ksy)", "compile", "公式のコンパイラで変換できる", compileError);
            return c.Result;
        }

        if (Str(reference["error"]) is { } error)
        {
            // 参照側がサンプルを最後まで読めない: サンプルが形式に合っていないため、失敗にして直す
            c.Add("(root)", "reference", "参照の解析がエラーなく終わる", error);
        }

        if (reference["root"] is JsonObject root)
        {
            CompareChildren(c, root["fields"] as JsonArray, ours["children"] as JsonArray, string.Empty);
        }

        return c.Result;
    }

    private static void CompareChildren(Context c, JsonArray? refFields, JsonArray? ourChildren, string parentPath)
    {
        var byName = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (JsonObject child in (ourChildren ?? []).OfType<JsonObject>())
        {
            byName.TryAdd(LowerCamel(Str(child["name"]) ?? string.Empty), child);
        }

        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonObject field in (refFields ?? []).OfType<JsonObject>())
        {
            string name = Str(field["name"]) ?? string.Empty;
            matched.Add(name);
            byName.TryGetValue(name, out JsonObject? our);
            CompareField(c, field, our, parentPath.Length == 0 ? name : parentPath + "." + name);
        }

        foreach ((string name, JsonObject our) in byName)
        {
            if (!matched.Contains(name))
            {
                c.Result.Extra++;
                if (c.Result.Extra <= 20)
                {
                    c.Result.Notes.Add($"こちらにだけあるフィールド: {Str(our["path"]) ?? name}");
                }
            }
        }
    }

    private static void CompareField(Context c, JsonObject r, JsonObject? o, string refPath)
    {
        string path = (o is not null ? Str(o["path"]) : null) ?? refPath;
        string kind = Str(r["kind"]) ?? "unknown";
        c.Result.Fields++;
        if (Str(r["error"]) is { } refError)
        {
            c.Result.Notes.Add($"参照側のエラーのため比べない: {path}: {refError}");
            return;
        }

        if (o is null)
        {
            if (kind != "none")
            {
                c.Add(path, "missing", "フィールドがある", "なし");
            }

            return;
        }

        if (Str(o["error"]) is { } ourError)
        {
            c.Add(path, "error", "エラーなし", ourError);
            return;
        }

        if (r["offset"] is JsonValue && o["section"] is null && o["bit_offset"] is null)
        {
            c.Result.Positions++;
            long refOffset = Long(r["offset"]);
            long ourOffset = Long(o["offset"]);
            if (refOffset != ourOffset)
            {
                c.Add(path, "offset", Hex(refOffset), Hex(ourOffset));
            }

            if (r["size"] is JsonValue rs && o["size"] is JsonValue os && Long(rs) != Long(os))
            {
                c.Add(path, "size", Long(rs).ToString(CultureInfo.InvariantCulture), Long(os).ToString(CultureInfo.InvariantCulture));
            }
        }

        switch (kind)
        {
            case "struct":
                CompareChildren(c, r["fields"] as JsonArray, o["children"] as JsonArray, path);
                break;
            case "array":
                CompareArray(c, (r["items"] as JsonArray) ?? [], o, path);
                break;
            case "bytes" or "number" or "bool" or "string":
                c.Result.Values++;
                CompareValue(c, r, o, path, kind);
                break;
        }
    }

    private static void CompareArray(Context c, JsonArray items, JsonObject o, string path)
    {
        // u1 などの配列は、こちらではバイト列の値 (子なし) になる
        if (o["children"] is not JsonArray children)
        {
            if (Str(o["value"]) is { } hexValue && items.All(i => Str(i?["kind"]) == "number"))
            {
                c.Result.Values++;
                string expected = string.Concat(items.Select(i => Long(i!["value"]).ToString("X2", CultureInfo.InvariantCulture)));
                if (!string.Equals(expected, hexValue, StringComparison.OrdinalIgnoreCase))
                {
                    c.Add(path, "value", Short(expected), Short(hexValue));
                }

                return;
            }

            children = [];
        }

        if (children.Count != items.Count)
        {
            c.Add(path, "count", items.Count.ToString(CultureInfo.InvariantCulture), children.Count.ToString(CultureInfo.InvariantCulture));
        }

        for (int i = 0; i < Math.Min(children.Count, items.Count); i++)
        {
            CompareField(c, (JsonObject)items[i]!, children[i] as JsonObject, $"{path}[{i}]");
        }
    }

    private static void CompareValue(Context c, JsonObject r, JsonObject o, string path, string kind)
    {
        JsonNode? rv = r["value"];
        JsonNode? ov = o["value"];
        string expected = rv?.ToJsonString() ?? "null";
        string actual = ov?.ToJsonString() ?? "(値なし)";
        bool same = kind switch
        {
            "bytes" => ov is null && Long(o["size"]) > Core.Templates.TemplateJsonExporter.MaxInlineBytes
                || string.Equals(Str(rv), Str(ov), StringComparison.OrdinalIgnoreCase),
            "string" => Str(ov) is { } s && s == Str(rv),
            "bool" => ov is JsonValue b && (b.TryGetValue(out bool bv) ? bv == (bool)rv! : NumberText(b) is { } t && (t == "1") == (bool)rv!),
            _ => SameNumber(rv, ov, r["unsafe"] is JsonValue u && (bool)u)
                || (r["enum"] is not null && Str(o["display"]) is { } d && EnumDisplay().Match(d) is { Success: true } m && m.Groups[1].Value == NumberText(rv)),
        };
        if (!same)
        {
            c.Add(path, "value", Short(expected), Short(actual));
        }
    }

    private static bool SameNumber(JsonNode? a, JsonNode? b, bool unsafeInteger)
    {
        if (NumberText(a) is not { } x || NumberText(b) is not { } y)
        {
            return false;
        }

        if (!unsafeInteger && BigInteger.TryParse(x, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger bx)
            && BigInteger.TryParse(y, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger by))
        {
            return bx == by;
        }

        // JavaScript の Number で表せない大きな整数 (u8 / s8) と浮動小数点数は double で比べる
        return x == y || (double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out double dx)
            && double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out double dy) && (dx == dy || (double.IsNaN(dx) && double.IsNaN(dy))));
    }

    /// <summary>数値の JSON の表記 (文字列の "NaN" などはそのまま)。数値でなければ null。</summary>
    private static string? NumberText(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue(out string? s) => s is "NaN" or "Inf" or "-Inf" || double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? s : null,
        JsonValue v when v.TryGetValue(out bool _) => null,
        JsonValue v => v.ToJsonString(),
        _ => null,
    };

    private static long Long(JsonNode? n) =>
        n is null ? 0 : long.Parse(n.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static string Hex(long v) => "0x" + v.ToString("X", CultureInfo.InvariantCulture);

    private static string Short(string s) => s.Length <= 80 ? s : s[..77] + "...";

    /// <summary>照合結果の報告 (Markdown)。CI の成果物にする。</summary>
    public static string ToMarkdown(IReadOnlyList<KaitaiComparison> results, string header)
    {
        var sb = new StringBuilder();
        sb.Append("# TC-TPL-33-01 kaitai_struct_formats の照合\n\n").Append(header).Append("\n\n");
        sb.Append("| 形式 | 結果 | フィールド | 値 | 位置 | 不一致 | 許容 | こちらにだけある |\n| --- | --- | --- | --- | --- | --- | --- | --- |\n");
        foreach (KaitaiComparison r in results)
        {
            sb.Append(CultureInfo.InvariantCulture, $"| {r.Format} | {(r.Passed ? "合格" : "不合格")} | {r.Fields} | {r.Values} | {r.Positions} | ")
                .Append(CultureInfo.InvariantCulture, $"{r.Failures.Count()} | {r.Mismatches.Count(m => m.AllowedBy is not null)} | {r.Extra} |\n");
        }

        foreach (KaitaiComparison r in results)
        {
            sb.Append("\n## ").Append(r.Format).Append("\n\n");
            if (r.Mismatches.Count > 0)
            {
                sb.Append("| パス | 対象 | 期待値 | 結果 | 許容の理由 |\n| --- | --- | --- | --- | --- |\n");
                foreach (KaitaiMismatch m in r.Mismatches.Take(200))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"| `{m.Path}` | {m.Aspect} | {Cell(m.Expected)} | {Cell(m.Actual)} | {Cell(m.AllowedBy ?? string.Empty)} |\n");
                }

                if (r.Mismatches.Count > 200)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"\n(ほか {r.Mismatches.Count - 200} 件)\n");
                }
            }
            else
            {
                sb.Append("不一致なし。\n");
            }

            foreach (string note in r.Notes.Take(50))
            {
                sb.Append("\n- ").Append(note);
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string Cell(string s) => s.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    [GeneratedRegex(@"\((-?\d+)\)\s*$")]
    private static partial Regex EnumDisplay();

    private sealed class Context(KaitaiComparison result, IReadOnlyList<KaitaiAllowEntry> allow)
    {
        public KaitaiComparison Result { get; } = result;

        public void Add(string path, string aspect, string expected, string actual)
        {
            string? reason = allow.FirstOrDefault(a => a.Matches(Result.Format, path, aspect))?.Reason;
            Result.Mismatches.Add(new KaitaiMismatch(path, aspect, expected, actual, reason));
        }
    }
}
