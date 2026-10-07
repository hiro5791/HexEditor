using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HexEditor.ManualTests;

/// <summary>機能仕様書の機能 1 つ (受け入れ基準の数とフェーズ)。</summary>
public sealed record SpecFeature(string Id, string Title, string SourceFile, string Roadmap, int CriteriaCount)
{
    /// <summary>
    /// ロードマップの欄から読んだフェーズ (F1-05 → 1、「追加 (フェーズ 0 提案)」→ 0)。複数あれば最も早いもの。読めなければ null。
    /// </summary>
    public int? Phase
    {
        get
        {
            var phases = Regex.Matches(Roadmap, @"F(\d)-").Select(m => int.Parse(m.Groups[1].Value))
                .Concat(Regex.Matches(Roadmap, @"フェーズ (\d)").Select(m => int.Parse(m.Groups[1].Value)))
                .ToList();
            return phases.Count > 0 ? phases.Min() : null;
        }
    }
}

/// <summary>受け入れ基準 1 つと、それを確認するテストケース。</summary>
public sealed record CriterionCoverage(SpecFeature Feature, int Number, IReadOnlyList<TestCase> Cases);

/// <summary>
/// テストの網羅の一覧 (テスト方針 6.2 の test-coverage.md、10 章の 4): 受け入れ基準とテストケースの対応、自動テストのない
/// 「自動」のテストケース、失敗したテストケース。
/// </summary>
public sealed partial class Coverage
{
    private Coverage(IReadOnlyList<SpecFeature> features, IReadOnlyList<TestCase> cases, IReadOnlySet<string> automated,
        IReadOnlySet<string> failed)
    {
        Features = features;
        Cases = cases;
        Automated = automated;
        Failed = failed;
        var byCriterion = new Dictionary<(string, int), List<TestCase>>();
        foreach (TestCase c in cases)
        {
            foreach ((string feature, int number) in CriteriaOf(c))
            {
                if (!byCriterion.TryGetValue((feature, number), out List<TestCase>? list))
                {
                    byCriterion[(feature, number)] = list = [];
                }

                list.Add(c);
            }
        }

        Criteria = [.. features.SelectMany(f => Enumerable.Range(1, f.CriteriaCount)
            .Select(n => new CriterionCoverage(f, n, byCriterion.GetValueOrDefault((f.Id, n)) ?? [])))];
    }

    public IReadOnlyList<SpecFeature> Features { get; }

    public IReadOnlyList<TestCase> Cases { get; }

    /// <summary>自動テスト (xUnit の Trait、build/tests の Invoke-TestCase) があるテストケースの ID。</summary>
    public IReadOnlySet<string> Automated { get; }

    /// <summary>テストの結果 (trx) で失敗したテストケースの ID。</summary>
    public IReadOnlySet<string> Failed { get; }

    public IReadOnlyList<CriterionCoverage> Criteria { get; }

    /// <summary>どのテストケースにも対応しない受け入れ基準。</summary>
    public IEnumerable<CriterionCoverage> UncoveredCriteria => Criteria.Where(c => c.Cases.Count == 0);

    /// <summary>種別が「自動」なのに、自動テストがないテストケース。</summary>
    public IEnumerable<TestCase> AutomatedWithoutTest => Cases.Where(c => c.IsAutomated && !Automated.Contains(c.Id));

    /// <summary>リポジトリから作る。<paramref name="resultsDirectory"/> があれば、その下の *.trx から失敗を読む。</summary>
    public static Coverage Load(string repositoryRoot, string? resultsDirectory = null)
    {
        var features = new List<SpecFeature>();
        foreach (string file in Directory.GetFiles(Path.Combine(repositoryRoot, "docs", "spec"), "*.md").Order())
        {
            features.AddRange(ParseSpec(file));
        }

        List<TestCase> cases = [.. TestCaseParser.ParseDirectory(Path.Combine(repositoryRoot, "docs", "test", "cases"))];
        (HashSet<string> automated, Dictionary<string, HashSet<string>> byMethod) = ScanTests(repositoryRoot);
        var failed = new HashSet<string>(StringComparer.Ordinal);
        if (resultsDirectory is not null && Directory.Exists(resultsDirectory))
        {
            foreach (string trx in Directory.EnumerateFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories))
            {
                failed.UnionWith(FailedCases(trx, byMethod));
            }
        }

        return new Coverage(features, cases, automated, failed);
    }

    /// <summary>機能仕様書の 1 ファイルから、機能と受け入れ基準の数を読む。</summary>
    public static IEnumerable<SpecFeature> ParseSpec(string path)
    {
        string text = File.ReadAllText(path);
        foreach (Match m in FeatureSection().Matches(text))
        {
            string body = m.Groups[3].Value;
            string roadmap = RoadmapRow().Match(body) is { Success: true } r ? r.Groups[1].Value.Trim() : string.Empty;
            int criteria = AcceptanceBlock().Match(body) is { Success: true } a ? Checkbox().Matches(a.Groups[1].Value).Count : 0;
            yield return new SpecFeature(m.Groups[1].Value, m.Groups[2].Value.Trim(), Path.GetFileName(path), roadmap, criteria);
        }
    }

    /// <summary>テストケースの「確認する基準」から、(機能 ID, 受け入れ基準の番号) を読む。「1、3」「2〜4」の形も読む。</summary>
    public static IEnumerable<(string Feature, int Number)> CriteriaOf(TestCase testCase)
    {
        string text = testCase.Fields.GetValueOrDefault("確認する基準", string.Empty);
        foreach (Match m in CriterionReference().Matches(text))
        {
            foreach (string part in Regex.Split(m.Groups[2].Value.Trim(), @"[、,・\s]+").Where(p => p.Length > 0))
            {
                string[] range = Regex.Split(part, "[〜-]");
                if (range.Length == 2 && int.TryParse(range[0], out int from) && int.TryParse(range[1], out int to))
                {
                    for (int n = from; n <= to; n++)
                    {
                        yield return (m.Groups[1].Value, n);
                    }
                }
                else if (int.TryParse(part, out int n))
                {
                    yield return (m.Groups[1].Value, n);
                }
            }
        }
    }

    /// <summary>
    /// 自動テストのある テストケース ID を集める: tests/ の C# (Trait の値) と build/tests の PowerShell (Invoke-TestCase)。
    /// あわせて、C# のテストメソッド名 → テストケース ID の対応を作る (trx の結果との突き合わせに使う)。
    /// </summary>
    private static (HashSet<string> All, Dictionary<string, HashSet<string>> ByMethod) ScanTests(string root)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        var byMethod = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var pending = new List<string>();
            foreach (string line in File.ReadLines(file))
            {
                if (TraitLine().Match(line) is { Success: true } trait)
                {
                    pending.AddRange(TestCaseId().Matches(trait.Value).Select(x => x.Value));
                    continue;
                }

                if (pending.Count > 0 && MethodLine().Match(line) is { Success: true } method)
                {
                    all.UnionWith(pending);
                    if (!byMethod.TryGetValue(method.Groups[1].Value, out HashSet<string>? ids))
                    {
                        byMethod[method.Groups[1].Value] = ids = [];
                    }

                    ids.UnionWith(pending);
                    pending.Clear();
                }
            }
        }

        string scripts = Path.Combine(root, "build", "tests");
        if (Directory.Exists(scripts))
        {
            foreach (string file in Directory.EnumerateFiles(scripts, "*.ps1"))
            {
                foreach (Match m in InvokeTestCase().Matches(File.ReadAllText(file)))
                {
                    all.Add(m.Groups[1].Value);
                }
            }
        }

        return (all, byMethod);
    }

    /// <summary>trx (Visual Studio のテスト結果) から、失敗したテストのテストケース ID を読む。</summary>
    private static IEnumerable<string> FailedCases(string trx, Dictionary<string, HashSet<string>> byMethod)
    {
        XDocument doc = XDocument.Load(trx);
        XNamespace ns = doc.Root!.Name.Namespace;
        foreach (XElement result in doc.Descendants(ns + "UnitTestResult").Where(r => (string?)r.Attribute("outcome") == "Failed"))
        {
            // testName は「名前空間.クラス.メソッド(引数)」。メソッド名で突き合わせる。
            string name = ((string?)result.Attribute("testName") ?? string.Empty).Split('(')[0];
            string method = name[(name.LastIndexOf('.') + 1)..];
            if (byMethod.TryGetValue(method, out HashSet<string>? ids))
            {
                foreach (string id in ids)
                {
                    yield return id;
                }
            }
        }
    }

    /// <summary>Markdown の一覧 (test-coverage.md)。</summary>
    public string ToMarkdown(string? generatedBy = null)
    {
        var md = new StringBuilder();
        md.AppendLine("# テストの網羅 (test-coverage.md)");
        md.AppendLine();
        md.AppendLine("テスト方針 (00-test-strategy.md) の 6.2 と 10 章の 4 の一覧。`tools/TestCoverage` が機能仕様書・テストケース・自動テストのソースから作る。手で編集しない。");
        if (generatedBy is not null)
        {
            md.AppendLine();
            md.AppendLine(generatedBy);
        }

        md.AppendLine();
        md.AppendLine("## フェーズごとの集計");
        md.AppendLine();
        md.AppendLine("| フェーズ | 機能 | 受け入れ基準 | テストケースのない基準 | テストケース | 自動 | 自動テストあり | 自動テストのない「自動」 | 手動・半自動 | 失敗 |");
        md.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (int phase in Enumerable.Range(0, 6))
        {
            var features = Features.Where(f => f.Phase == phase).ToList();
            var criteria = Criteria.Where(c => c.Feature.Phase == phase).ToList();
            var cases = Cases.Where(c => c.Phase == phase).ToList();
            int auto = cases.Count(c => c.IsAutomated);
            int withTest = cases.Count(c => c.IsAutomated && Automated.Contains(c.Id));
            md.AppendLine($"| {phase} | {features.Count} | {criteria.Count} | {criteria.Count(c => c.Cases.Count == 0)} | {cases.Count} | {auto} | {withTest} | {auto - withTest} | {cases.Count - auto} | {cases.Count(c => Failed.Contains(c.Id))} |");
        }

        md.AppendLine();
        md.AppendLine("## テストケースのない受け入れ基準");
        md.AppendLine();
        var uncovered = UncoveredCriteria.ToList();
        md.AppendLine(uncovered.Count == 0 ? "なし。" : string.Join(Environment.NewLine,
            uncovered.Select(c => $"- {c.Feature.Id} の受け入れ基準 {c.Number} (フェーズ {c.Feature.Phase?.ToString() ?? "?"}、{c.Feature.SourceFile})")));

        md.AppendLine();
        md.AppendLine("## 自動テストのない「自動」のテストケース");
        md.AppendLine();
        var missing = AutomatedWithoutTest.OrderBy(c => c.Phase).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        md.AppendLine(missing.Count == 0 ? "なし。" : string.Join(Environment.NewLine,
            missing.GroupBy(c => c.Phase).Select(g => $"- フェーズ {g.Key}: {g.Count()} 件 ({string.Join("、", g.Take(30).Select(c => c.Id))}{(g.Count() > 30 ? " ほか" : string.Empty)})")));

        md.AppendLine();
        md.AppendLine("## 失敗したテストケース");
        md.AppendLine();
        md.AppendLine(Failed.Count == 0 ? "なし (テストの結果を渡していない場合も「なし」)。" : string.Join(Environment.NewLine, Failed.Order().Select(id => $"- {id}")));

        md.AppendLine();
        md.AppendLine("## 受け入れ基準ごとのテストケース");
        foreach (IGrouping<string, SpecFeature> file in Features.Where(f => f.CriteriaCount > 0).GroupBy(f => f.SourceFile))
        {
            md.AppendLine();
            md.AppendLine($"### {file.Key}");
            md.AppendLine();
            md.AppendLine("| 機能 | フェーズ | 基準 | テストケース |");
            md.AppendLine("| --- | --- | --- | --- |");
            foreach (SpecFeature feature in file)
            {
                foreach (CriterionCoverage c in Criteria.Where(c => c.Feature == feature))
                {
                    string cases = c.Cases.Count == 0 ? "**なし**" : string.Join(", ", c.Cases.Select(Describe));
                    md.AppendLine($"| {feature.Id} {feature.Title} | {feature.Phase?.ToString() ?? "?"} | {c.Number} | {cases} |");
                }
            }
        }

        return md.ToString();
    }

    /// <summary>「TC-ENG-01-01 (自動 ✓)」「TC-UI-02-07 (手動)」「TC-X (自動 ✗)」。✗ は自動テストがない、! は失敗。</summary>
    private string Describe(TestCase c)
    {
        string mark = !c.IsAutomated ? c.Kind : Failed.Contains(c.Id) ? "自動 !" : Automated.Contains(c.Id) ? "自動 ✓" : "自動 ✗";
        return $"{c.Id} ({mark})";
    }

    [GeneratedRegex(@"^### ([A-Z]+-\d+) ([^\n]*)\n(.*?)(?=^### |^## |\z)", RegexOptions.Singleline | RegexOptions.Multiline)]
    private static partial Regex FeatureSection();

    [GeneratedRegex(@"\| ロードマップ \| ([^|]*)\|")]
    private static partial Regex RoadmapRow();

    [GeneratedRegex(@"\*\*受け入れ基準\*\*:?\s*\n(.*?)(?=\n\*\*|\z)", RegexOptions.Singleline)]
    private static partial Regex AcceptanceBlock();

    [GeneratedRegex(@"^- \[ \]", RegexOptions.Multiline)]
    private static partial Regex Checkbox();

    [GeneratedRegex(@"([A-Z]+-\d+) の受け入れ基準 ([0-9、,・〜\-\s]+)")]
    private static partial Regex CriterionReference();

    [GeneratedRegex(@"Trait\([^)]*""TC-[A-Z]+-\d+-\d+""[^)]*\)")]
    private static partial Regex TraitLine();

    [GeneratedRegex(@"TC-[A-Z]+-\d+-\d+")]
    private static partial Regex TestCaseId();

    [GeneratedRegex(@"\b(?:public|private|internal)\s+(?:static\s+)?(?:async\s+)?[\w<>\[\],?\s]+?\s+(\w+)\s*\(")]
    private static partial Regex MethodLine();

    [GeneratedRegex(@"Invoke-TestCase\s+'(TC-[A-Z]+-\d+-\d+)'")]
    private static partial Regex InvokeTestCase();
}
