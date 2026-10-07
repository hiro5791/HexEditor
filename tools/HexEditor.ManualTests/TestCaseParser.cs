using System.Text.RegularExpressions;

namespace HexEditor.ManualTests;

/// <summary>テストケース 1 件 (テスト方針 4.2)。</summary>
public sealed record TestCase(
    string Id,
    string Title,
    string SourceFile,
    IReadOnlyDictionary<string, string> Fields,
    string Precondition,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> Expected)
{
    public string Kind => Fields.GetValueOrDefault("種別", string.Empty);

    public bool IsAutomated => Kind.StartsWith("自動", StringComparison.Ordinal);

    public bool IsManual => !IsAutomated;

    public string Priority => Fields.GetValueOrDefault("優先度", string.Empty);

    public bool IsSmoke => Priority == "高";

    public int Phase => int.TryParse(Fields.GetValueOrDefault("フェーズ"), out int p) ? p : -1;

    /// <summary>所要時間の目安 (分)。手動・半自動のみ。</summary>
    public int Minutes
    {
        get
        {
            Match m = Regex.Match(Fields.GetValueOrDefault("所要時間の目安", string.Empty), @"\d+");
            return m.Success ? int.Parse(m.Value) : 0;
        }
    }

    public IReadOnlyList<string> TestData =>
        Regex.Matches(Fields.GetValueOrDefault("テストデータ", string.Empty), @"TD-[A-Z0-9][A-Z0-9-]*[A-Z0-9]").Select(m => m.Value).ToList();
}

/// <summary>書式の誤り。CI で検査する (テスト方針 8.1)。</summary>
public sealed record FormatProblem(string File, string Id, string Message);

/// <summary>
/// テストケースのファイル (docs/test/cases/*.md) を読む。手動テスト支援ツールと、書式を検査する CI で使う。
/// </summary>
public static partial class TestCaseParser
{
    private static readonly string[] RequiredFields = ["確認する基準", "種別", "優先度", "フェーズ", "テストデータ", "環境"];
    private static readonly string[] ManualFields = ["手動にする理由", "所要時間の目安"];
    private static readonly string[] Kinds =
    [
        "自動: 単体", "自動: 結合", "自動: UI", "自動: 性能", "自動: 国際化", "自動: アクセシビリティ", "自動: ファジング", "自動: 配布",
        "半自動", "手動",
    ];

    [GeneratedRegex(@"^### (TC-[A-Z]+-\d+-\d+) (.+)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\| (.+?) \| (.*) \|$")]
    private static partial Regex TableRow();

    [GeneratedRegex(@"^\d+\. (.+)$")]
    private static partial Regex NumberedItem();

    public static IEnumerable<TestCase> ParseDirectory(string directory) =>
        Directory.GetFiles(directory, "*.md").Order().SelectMany(ParseFile);

    public static IEnumerable<TestCase> ParseFile(string path)
    {
        string[] lines = File.ReadAllLines(path);
        string file = Path.GetFileName(path);
        for (int i = 0; i < lines.Length; i++)
        {
            Match heading = Heading().Match(lines[i]);
            if (!heading.Success)
            {
                continue;
            }

            int end = i + 1;
            while (end < lines.Length && !lines[end].StartsWith("## ", StringComparison.Ordinal) && !lines[end].StartsWith("### ", StringComparison.Ordinal))
            {
                end++;
            }

            yield return ParseBlock(heading.Groups[1].Value, heading.Groups[2].Value, file, lines[(i + 1)..end]);
            i = end - 1;
        }
    }

    private static TestCase ParseBlock(string id, string title, string file, string[] block)
    {
        var fields = new Dictionary<string, string>();
        var steps = new List<string>();
        var expected = new List<string>();
        string precondition = string.Empty;
        string section = string.Empty;
        foreach (string line in block)
        {
            if (TableRow().Match(line) is { Success: true } row && row.Groups[1].Value is not ("項目" or "---"))
            {
                fields[row.Groups[1].Value.Trim()] = row.Groups[2].Value.Trim();
                continue;
            }

            if (line.StartsWith("**前提**:", StringComparison.Ordinal))
            {
                precondition = line["**前提**:".Length..].Trim();
                section = "前提";
            }
            else if (line.StartsWith("**手順**", StringComparison.Ordinal))
            {
                section = "手順";
            }
            else if (line.StartsWith("**期待結果**", StringComparison.Ordinal))
            {
                section = "期待結果";
            }
            else if (section == "手順" && NumberedItem().Match(line) is { Success: true } step)
            {
                steps.Add(step.Groups[1].Value);
            }
            else if (section == "期待結果" && line.StartsWith("- ", StringComparison.Ordinal))
            {
                expected.Add(line[2..]);
            }
        }

        return new TestCase(id, title, file, fields, precondition, steps, expected);
    }

    /// <summary>書式の検査 (テスト方針 4.2・4.3)。</summary>
    public static IEnumerable<FormatProblem> Validate(IEnumerable<TestCase> cases)
    {
        var seen = new HashSet<string>();
        foreach (TestCase c in cases)
        {
            if (!seen.Add(c.Id))
            {
                yield return new(c.SourceFile, c.Id, "ID が重複しています。");
            }

            foreach (string field in RequiredFields.Where(f => !c.Fields.ContainsKey(f)))
            {
                yield return new(c.SourceFile, c.Id, $"「{field}」がありません。");
            }

            if (!Kinds.Contains(c.Kind))
            {
                yield return new(c.SourceFile, c.Id, $"種別「{c.Kind}」は 3 章の種別ではありません。");
            }

            if (c.IsManual)
            {
                foreach (string field in ManualFields.Where(f => !c.Fields.ContainsKey(f)))
                {
                    yield return new(c.SourceFile, c.Id, $"手動・半自動なのに「{field}」がありません。");
                }

                if (c.Steps.Count > 10)
                {
                    yield return new(c.SourceFile, c.Id, $"手順が {c.Steps.Count} 個あります (10 以内。4.3)。");
                }
            }

            if (c.Steps.Count == 0)
            {
                yield return new(c.SourceFile, c.Id, "手順がありません。");
            }

            if (c.Expected.Count == 0)
            {
                yield return new(c.SourceFile, c.Id, "期待結果がありません。");
            }
        }
    }
}
