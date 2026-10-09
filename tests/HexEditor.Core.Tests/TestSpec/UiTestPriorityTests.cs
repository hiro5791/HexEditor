using System.Text.RegularExpressions;
using HexEditor.Core.Tests.Engine;
using HexEditor.ManualTests;

namespace HexEditor.Core.Tests.TestSpec;

/// <summary>
/// UI テストの優先度の印 (<c>[Trait(UiTest.Priority, UiTest.High)]</c>) がテストケースの文書の優先度と合っている (テスト方針 9 章:
/// プルリクエストでは優先度「高」の UI テストだけを動かす)。合わなければ build/Update-UiTestPriority.ps1 で付け直す。
/// </summary>
public sealed partial class UiTestPriorityTests
{
    [GeneratedRegex(@"Trait\(UiTest\.TC, ""(TC-[^""]+)""\)")]
    private static partial Regex TestCaseTrait();

    [GeneratedRegex(@"^\s*public (async )?(Task|void)\s+(\w+)")]
    private static partial Regex TestMethod();

    [Fact]
    public void Priority_traits_follow_the_test_case_documents()
    {
        string root = Path.GetDirectoryName(SourceTests.FindRepoFile("HexEditor.slnx"))!;
        Dictionary<string, string> priority = Coverage.Load(root).Cases.ToDictionary(c => c.Id, c => c.Priority);
        var wrong = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "tests", "HexEditor.UITests"), "*.cs"))
        {
            var block = new List<string>();
            foreach (string line in File.ReadLines(file))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith('[') || trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    block.Add(trimmed);
                    continue;
                }

                if (TestMethod().Match(line) is { Success: true } method && block.Count > 0)
                {
                    string[] ids = [.. block.SelectMany(b => TestCaseTrait().Matches(b)).Select(m => m.Groups[1].Value)];
                    bool high = ids.Any(id => priority.GetValueOrDefault(id) == "高");
                    bool marked = block.Contains("[Trait(UiTest.Priority, UiTest.High)]");
                    if (high != marked)
                    {
                        wrong.Add($"{Path.GetFileName(file)} {method.Groups[3].Value}: {(high ? "missing" : "extra")} priority mark ({string.Join(", ", ids)})");
                    }
                }

                block.Clear();
            }
        }

        Assert.True(wrong.Count == 0, "run build/Update-UiTestPriority.ps1:\n" + string.Join("\n", wrong));
    }
}
