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

    /// <summary>
    /// 「いま行うこと」の段階 (手動テスト支援ツール): 要るものだけを、行う順に並べる。準備はちょうど 1 回、最後は手順。
    /// </summary>
    [Fact]
    public void GuidePlanListsOnlyTheNeededActionsInOrder()
    {
        PrepBook book = PrepBook.LoadNextTo(CasesDirectory);
        List<TestCase> cases = ManualCases();
        foreach (TestCase c in cases)
        {
            GuidePlan plan = PrepGuide.Plan(book, c);
            Assert.Equal(GuideAction.RunSteps, plan.Steps[^1].Action);
            Assert.Single(plan.Steps, s => s.Action == GuideAction.Prepare);
            Assert.Equal(plan.Steps.Count, plan.Steps.Select(s => s.Key).Distinct().Count());
            Assert.All(plan.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Title)));
        }

        TestCase eng2801Case = cases.Single(c => c.Id == "TC-ENG-28-01");
        GuidePlan eng2801 = PrepGuide.Plan(book, eng2801Case);
        Assert.Equal([GuideAction.MakeVirtualDisks, GuideAction.BuildInstaller, GuideAction.Prepare, GuideAction.RunSteps], eng2801.Steps.Select(s => s.Action));
        Assert.Equal($"下の手順 1〜{eng2801Case.Steps.Count} を順に行い、結果を押してください", eng2801.Steps[^1].Title);
        Assert.Contains(eng2801.Notes, n => n.Name == "一般ユーザーの権限");

        // 「隠すファイル」は、ポータブル版の後・準備の前の自動の段階になる (手で名前を変えない)。
        GuidePlan eng2810 = PrepGuide.Plan(book, cases.Single(c => c.Id == "TC-ENG-28-10"));
        Assert.Equal([GuideAction.BuildPortable, GuideAction.HideFiles, GuideAction.Prepare, GuideAction.RunSteps], eng2810.Steps.Select(s => s.Action));
        Assert.Equal("補助プロセス (HexEditor.Elevated.exe) を一時的に隠します", eng2810.Steps[1].Title);
        Assert.Equal(["HexEditor.Elevated.exe"], book.For("TC-ENG-28-10").HiddenFiles);

        // 機材は最初。「ないとき」の書き方から、飛ばすときの結果とコメントが分かる。
        GuidePlan for2007 = PrepGuide.Plan(book, cases.Single(c => c.Id == "TC-FOR-20-07"));
        Assert.Equal([GuideAction.Hand, GuideAction.Prepare, GuideAction.RunSteps], for2007.Steps.Select(s => s.Action));
        Assert.Equal("機材を用意します: 消えてよい USB メモリ", for2007.Steps[0].Title);
        Assert.Equal("対象外", for2007.Steps[0].SkipOutcome);
        Assert.Equal("消えてよい USB メモリなし", for2007.Steps[0].SkipComment);
        Assert.Contains(for2007.Notes, n => n.Name == PrepBook.AdminNeed);

        // 公開・署名のあとのものは「保留」で飛ばす。
        TestCase publish = cases.First(c => book.EquipmentOf(c).Contains("公開・署名のあと"));
        Assert.Contains(PrepGuide.Plan(book, publish).Steps, s => s.SkipOutcome == "保留");

        // 準備ボタンで作れないテストデータ (TD-FOR-BITLOCKER) は、手で用意する段階になり、対象外で飛ばせる。
        TestCase bitlocker = cases.First(c => c.TestData.Contains("TD-FOR-BITLOCKER"));
        GuideStep data = PrepGuide.Plan(book, bitlocker).Steps.Single(s => s.Key == "data:TD-FOR-BITLOCKER");
        Assert.Equal(("対象外", "Home のため"), (data.SkipOutcome, data.SkipComment));
    }

    [Fact]
    public void HiddenFilesFieldIsParsedAndValidated()
    {
        PrepBook book = PrepBook.Parse(
        [
            "### TC-AAA-01-01",
            "| 項目 | 内容 |",
            "| --- | --- |",
            "| 起動 | ポータブル版 |",
            "| 隠すファイル | a.exe、b.dll |",
            "### TC-AAA-01-02",
            "| 項目 | 内容 |",
            "| --- | --- |",
            "| 起動 | なし |",
            "| 隠すファイル | sub/c.exe |",
        ]);
        Assert.Equal(["a.exe", "b.dll"], book.For("TC-AAA-01-01").HiddenFiles);
        Assert.Empty(book.For("TC-AAA-01-99").HiddenFiles);
        var cases = new[] { "TC-AAA-01-01", "TC-AAA-01-02" }
            .Select(id => new TestCase(id, id, string.Empty, new Dictionary<string, string> { ["種別"] = "手動" }, string.Empty, [], [])).ToList();
        string[] problems = [.. book.Validate(cases).Where(p => p.Contains("隠すファイル", StringComparison.Ordinal))];
        Assert.Equal(2, problems.Length);
        Assert.All(problems, p => Assert.StartsWith("TC-AAA-01-02", p, StringComparison.Ordinal));
        Assert.DoesNotContain(book.Validate(cases), p => p.Contains("項目「隠すファイル」", StringComparison.Ordinal));
    }

    [Fact]
    public void FileHiderHidesAndRestoresIdempotentlyWithoutOverwriting()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "h.exe");
        File.WriteAllText(path, "x");

        // 隠す (2 回目は何もしない)。
        Assert.Null(FileHider.Hide(_dir, "h.exe"));
        Assert.True(FileHider.IsHidden(_dir, "h.exe"));
        Assert.False(File.Exists(path));
        Assert.Null(FileHider.Hide(_dir, "h.exe"));
        Assert.Equal("x", File.ReadAllText(path + ".bak"));

        // 戻す (2 回目は何もしない)。
        Assert.Null(FileHider.Restore(_dir, "h.exe", out bool restored));
        Assert.True(restored);
        Assert.Equal("x", File.ReadAllText(path));
        Assert.Null(FileHider.Restore(_dir, "h.exe", out restored));
        Assert.False(restored);

        // 両方あるときは、どちらも上書きせずに理由を返す。
        File.WriteAllText(path + ".bak", "old");
        Assert.NotNull(FileHider.Hide(_dir, "h.exe"));
        Assert.NotNull(FileHider.Restore(_dir, "h.exe", out restored));
        Assert.False(restored);
        Assert.Equal("x", File.ReadAllText(path));
        Assert.Equal("old", File.ReadAllText(path + ".bak"));

        // ないときは理由を返す。動いているプログラム (開いたまま) は名前を変えられず、理由を返す。
        Assert.NotNull(FileHider.Hide(_dir, "none.exe"));
        File.Delete(path + ".bak");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.NotNull(FileHider.Hide(_dir, "h.exe"));
            Assert.True(File.Exists(path));
        }

        Assert.Null(FileHider.Hide(_dir, "h.exe"));
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
