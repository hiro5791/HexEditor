using HexEditor.Core.Tests.Engine;
using HexEditor.ManualTests;

namespace HexEditor.Core.Tests.TestSpec;

/// <summary>
/// テストの網羅 (テスト方針 6.2、10 章の 4)。完了したフェーズでは、すべての受け入れ基準にテストケースがあり、すべての「自動」の
/// テストケースに自動テストがあること。フェーズが進んだら <see cref="CompletedPhase"/> を上げる (6.2「フェーズごとに有効にする」)。
/// </summary>
public sealed class CoverageTests
{
    /// <summary>完了したフェーズ。</summary>
    private const int CompletedPhase = 0;

    private static Coverage Load() =>
        Coverage.Load(Path.GetDirectoryName(SourceTests.FindRepoFile("HexEditor.slnx"))!);

    [Fact]
    public void EveryAcceptanceCriterionHasATestCase()
    {
        var missing = Load().UncoveredCriteria.Select(c => $"{c.Feature.Id} の受け入れ基準 {c.Number} ({c.Feature.SourceFile})").ToList();
        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void EveryAutomatedCaseOfCompletedPhasesHasATest()
    {
        var missing = Load().AutomatedWithoutTest.Where(c => c.Phase is >= 0 and <= CompletedPhase).Select(c => c.Id).ToList();
        Assert.True(missing.Count == 0, string.Join(", ", missing));
    }

    [Fact]
    public void CriterionReferencesAreParsed()
    {
        var testCase = new TestCase("TC-X-01-01", "t", "f.md",
            new Dictionary<string, string> { ["確認する基準"] = "ENG-27 の受け入れ基準 1、3、PKG-30 の受け入れ基準 2〜4" }, string.Empty, [], []);
        Assert.Equal(
            [("ENG-27", 1), ("ENG-27", 3), ("PKG-30", 2), ("PKG-30", 3), ("PKG-30", 4)],
            Coverage.CriteriaOf(testCase).ToList());
    }
}
