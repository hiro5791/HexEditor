using System.Text.Json.Nodes;
using HexEditor.Core.Files;
using HexEditor.Core.Tests.Engine;
using HexEditor.ManualTests;
using Xunit.Abstractions;

namespace HexEditor.Core.Tests.TestSpec;

/// <summary>
/// 手動テストの準備の手順 (docs/test/manual-test-prepare.md) の検査 (テスト方針 8 章: 手動テスト支援ツールの画面だけで手動テストを行えること)。
/// </summary>
public sealed class ManualPrepTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HexEditorManualPrepTests", Guid.NewGuid().ToString("N"));

    private static string CasesDirectory => Path.GetDirectoryName(SourceTests.FindRepoFile("docs/test/cases/01-engine-and-sources.md"))!;

    private static List<TestCase> ManualCases() => TestCaseParser.ParseDirectory(CasesDirectory).Where(c => c.IsManual).ToList();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            foreach (string file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void EveryManualCaseHasPreparationSteps()
    {
        List<TestCase> cases = ManualCases();
        PrepBook book = PrepBook.LoadNextTo(CasesDirectory);
        List<string> problems = book.Validate(cases).ToList();
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(50)));

        List<TestCase> equipment = cases.Where(book.NeedsEquipment).ToList();
        output.WriteLine($"手動・半自動 {cases.Count} 件: 機材が必要 {equipment.Count} 件、この PC だけでできる {cases.Count - equipment.Count} 件");
        foreach (IGrouping<string, TestCase> g in equipment.GroupBy(c => string.Join("、", book.EquipmentOf(c))).OrderBy(g => g.Key))
        {
            output.WriteLine($"  {g.Key}: {string.Join(" ", g.Select(c => c.Id))}");
        }

        // 機材のない PC で行えるテストが残る (機材のない利用者も、一覧の多くを行える)。
        Assert.InRange(equipment.Count, 1, cases.Count - 1);
    }

    [Fact]
    public void VirtualDiskCasesAreNotEquipment()
    {
        PrepBook book = PrepBook.LoadNextTo(CasesDirectory);
        TestCase c = ManualCases().Single(c => c.Id == "TC-ENG-28-01");
        Assert.True(book.NeedsVirtualDisks(c));
        Assert.False(book.NeedsEquipment(c));
        Assert.Contains(book.NeedsOf(c), n => n.Name == PrepBook.InstallerNeed);
    }

    [Fact]
    public void SetupPreparesBookmarksSettingsScriptsAndHooks()
    {
        PrepBook book = PrepBook.LoadNextTo(CasesDirectory);
        Dictionary<string, TestCase> cases = ManualCases().ToDictionary(c => c.Id);
        string data = Path.Combine(_dir, "data");

        // TC-INSP-30-03: TD-SEQ-1M にブックマーク 5 件 (付随データ)。
        SetupResult bm = CaseSetup.Run(cases["TC-INSP-30-03"], book.For("TC-INSP-30-03"), data, Path.Combine(_dir, "p1"));
        string seq = bm.Files["TD-SEQ-1M"];
        string sidecar = new DocumentDataStore(Path.Combine(bm.Profile, "documents")).PathFor(seq, "bookmarks");
        Assert.True(File.Exists(sidecar));
        Assert.Equal(5, JsonNode.Parse(File.ReadAllText(sidecar))!["bookmarks"]!.AsArray().Count);
        Assert.Equal(seq, bm.Arguments[^1]);
        Assert.Empty(bm.Missing);

        // TC-UI-51-03: 設定フォルダのブックマーク、読み込みエラーのフック、--offset。
        SetupResult a11y = CaseSetup.Run(cases["TC-UI-51-03"], book.For("TC-UI-51-03"), data, Path.Combine(_dir, "p2"));
        Assert.NotNull(a11y.HooksPath);
        JsonNode hooks = JsonNode.Parse(File.ReadAllText(a11y.HooksPath!))!;
        Assert.False(hooks["noActivate"]!.GetValue<bool>());
        Assert.Equal(a11y.Files["TD-ZERO-1M"], hooks["fileSources"]![0]!["match"]!.GetValue<string>());
        Assert.Equal(["--offset", "0x10", a11y.Files["TD-ZERO-1M"]], a11y.Arguments[^3..]);

        // TC-UI-08-06: 画面全体の拡大 400%。
        SetupResult zoom = CaseSetup.Run(cases["TC-UI-08-06"], book.For("TC-UI-08-06"), data, Path.Combine(_dir, "p3"));
        Assert.Equal(400, JsonNode.Parse(File.ReadAllText(Path.Combine(zoom.Profile, "settings.json")))!["view.zoom.ui"]!.GetValue<int>());

        // TC-AUTO-20-06: スクリプトを設定フォルダに置く。
        SetupResult script = CaseSetup.Run(cases["TC-AUTO-20-06"], book.For("TC-AUTO-20-06"), data, Path.Combine(_dir, "p4"));
        Assert.StartsWith("// narrator test", File.ReadAllText(Path.Combine(script.Profile, "scripts", "narrator.js")));

        // TC-TPL-21-05: 名前のテンプレートは 1 つの引数のまま。
        SetupResult png = CaseSetup.Run(cases["TC-TPL-21-05"], book.For("TC-TPL-21-05"), data, Path.Combine(_dir, "p5"));
        Assert.Equal(["--template", "PNG image", png.Files["TD-PNG"]], png.Arguments[^3..]);

        // TC-ENG-28-01: 仮想ディスクは準備ボタンでは作らない (専用のボタン)。
        SetupResult vhd = CaseSetup.Run(cases["TC-ENG-28-01"], book.For("TC-ENG-28-01"), data, Path.Combine(_dir, "p6"));
        Assert.Contains("TD-VHDX-MBR", vhd.Missing);
    }

    [Fact]
    public void ReadOnlyTestDataHasTheAttribute()
    {
        string path = CaseSetup.Generate("TD-READONLY", _dir);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        Assert.Equal(1024, new FileInfo(path).Length);

        // 2 回目も作り直せる。
        Assert.Equal(path, CaseSetup.Generate("TD-READONLY", _dir));
    }
}
