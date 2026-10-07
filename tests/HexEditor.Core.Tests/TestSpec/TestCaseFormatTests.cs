using HexEditor.Core.Tests.Engine;
using HexEditor.ManualTests;

namespace HexEditor.Core.Tests.TestSpec;

/// <summary>テストケースのファイルの書式を検査する (テスト方針 8.1: 書式の誤りは CI で検査する)。</summary>
public sealed class TestCaseFormatTests
{
    private static string CasesDirectory => Path.GetDirectoryName(SourceTests.FindRepoFile("docs/test/cases/01-engine-and-sources.md"))!;

    [Fact]
    public void AllTestCasesAreWellFormed()
    {
        List<TestCase> cases = TestCaseParser.ParseDirectory(CasesDirectory).ToList();
        Assert.True(cases.Count > 1700, $"テストケースが {cases.Count} 件しかありません。");
        List<FormatProblem> problems = TestCaseParser.Validate(cases).ToList();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(50).Select(p => $"{p.File} {p.Id}: {p.Message}")));
    }

    [Fact]
    public void ManualSmokeRunFitsInOneHour()
    {
        List<TestCase> cases = TestCaseParser.ParseDirectory(CasesDirectory).ToList();
        int smokeMinutes = cases.Where(c => c.IsManual && c.IsSmoke).Sum(c => c.Minutes);
        Assert.InRange(smokeMinutes, 1, 60);
    }
}
