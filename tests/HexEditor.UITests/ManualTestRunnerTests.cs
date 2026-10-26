using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using HexEditor.ManualTests;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>ビルドした手動テスト支援ツール (tools/ManualTestRunner) があるときだけ実行する。</summary>
public sealed class ManualTestRunnerFactAttribute : FactAttribute
{
    public ManualTestRunnerFactAttribute()
    {
        if (ManualTestRunnerTests.FindExe() is null)
        {
            Skip = "手動テスト支援ツールがビルドされていない (dotnet build tools/ManualTestRunner -p:Platform=x64)。";
        }
    }
}

/// <summary>
/// 手動テスト支援ツール自身の UI テスト (テスト方針 8 章: ツールの画面だけで手動テストを行えること)。印 %TEMP%\HexEditor\dev-no-activate を置いて
/// 起動し、ウィンドウが前面に出ない・常に手前にならないことも確かめる。記録の置き場所は一時フォルダ (HEXEDITOR_MANUALTESTS_DIR) にし、
/// 利用者の記録には触れない。管理者の確認が出るボタン (仮想ディスク) と、クリップボードに書くボタン (すべてコピー) は押さない。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ManualTestRunnerTests : IDisposable
{
    private static readonly TimeSpan Wait = UiTest.Scaled(TimeSpan.FromSeconds(60));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HexEditorManualRunnerTests", Guid.NewGuid().ToString("N"));
    private readonly List<Process> _started = [];

    public ManualTestRunnerTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
        File.WriteAllText(Marker, string.Empty);
    }

    private static string Marker => Path.Combine(Path.GetTempPath(), "HexEditor", "dev-no-activate");

    private static string CasesDirectory => Path.Combine(AppLocator.RepositoryRoot, "docs", "test", "cases");

    public void Dispose()
    {
        foreach (Process p in _started)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(10_000);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        // 印は消す (手動テストでは、ツールと HexEditor が普通に前面に出る必要がある)。
        File.Delete(Marker);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>ビルドした ManualTestRunner.exe (この PC のアーキテクチャのもの)。</summary>
    public static string? FindExe()
    {
        string bin = Path.Combine(AppLocator.RepositoryRoot, "tools", "ManualTestRunner", "bin");
        if (!Directory.Exists(bin))
        {
            return null;
        }

        string rid = "win-" + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        return new DirectoryInfo(bin).EnumerateFiles("ManualTestRunner.exe", SearchOption.AllDirectories)
            .Where(f => f.DirectoryName!.Contains(rid, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    private sealed record Runner(Process Process, Window Window);

    private async Task<Runner> StartAsync()
    {
        var info = new ProcessStartInfo(FindExe()!) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(FindExe())! };
        info.Environment["HEXEDITOR_MANUALTESTS_DIR"] = _dir;
        info.Environment["HEXEDITOR_REPO"] = AppLocator.RepositoryRoot;
        Process process = Process.Start(info) ?? throw new InvalidOperationException("ManualTestRunner を起動できません。");
        _started.Add(process);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < Wait)
        {
            Assert.False(process.HasExited, $"ManualTestRunner が終了しました (終了コード {(process.HasExited ? process.ExitCode : 0)})。");
            if (NativeMethods.TopLevelWindows(process.Id, "WinUIDesktopWin32WindowClass") is [var hwnd, ..])
            {
                Window window = AppSession.Automation.FromHandle(hwnd).AsWindow();
                if (window.FindFirstDescendant(cf => cf.ByAutomationId("CaseList")) is not null)
                {
                    AssertNotInFront(process);
                    return new Runner(process, window);
                }
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("ManualTestRunner のウィンドウが出ません。");
    }

    private static void AssertNotInFront(Process process)
    {
        Assert.NotEqual(process.Id, NativeMethods.ForegroundProcessId());
        nint hwnd = NativeMethods.TopLevelWindows(process.Id, "WinUIDesktopWin32WindowClass")[0];
        long exStyle = GetWindowLongPtr(hwnd, -20);
        Assert.True((exStyle & 0x08000000) != 0, "WS_EX_NOACTIVATE が付いていません (dev-no-activate の印があるのに)。");
        Assert.True((exStyle & 0x00000008) == 0, "常に手前に表示 (WS_EX_TOPMOST) になっています (dev-no-activate の印があるのに)。");
    }

    private static async Task CloseAsync(Runner runner)
    {
        runner.Process.CloseMainWindow();
        if (!runner.Process.WaitForExit(10_000))
        {
            runner.Process.Kill();
        }

        await runner.Process.WaitForExitAsync();
    }

    private static AutomationElement ById(Window window, string id) =>
        window.FindFirstDescendant(cf => cf.ByAutomationId(id)) ?? throw new Xunit.Sdk.XunitException($"{id} がありません。");

    private static async Task<T> UntilAsync<T>(Func<T?> probe, string what) where T : class
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < Wait)
        {
            if (probe() is { } value)
            {
                return value;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException(what);
    }

    private static string NameOf(AutomationElement e) => e.Properties.Name.ValueOrDefault ?? string.Empty;

    private static List<AutomationElement> ListItems(Window window) =>
        [.. ById(window, "CaseList").FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))];

    /// <summary>右側の欄の文字 (準備の手順など) をすべてつなげる。</summary>
    private static string AllText(Window window) =>
        string.Join("\n", window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).Select(NameOf));

    private static async Task SelectCaseAsync(Window window, string feature, string id)
    {
        ById(window, "FeatureFilter").AsTextBox().Text = feature;
        AutomationElement item = await UntilAsync(() => ListItems(window).FirstOrDefault(i => NameOf(i).Contains(id, StringComparison.Ordinal)
            || i.FindAllDescendants().Any(d => NameOf(d).Contains(id, StringComparison.Ordinal))), $"{id} が一覧に出ません。");
        item.Patterns.SelectionItem.Pattern.Select();
        await UntilAsync(() => AllText(window).Contains(id, StringComparison.Ordinal) && window.FindFirstDescendant(cf => cf.ByAutomationId("GuideButton")) is not null ? "ok" : null,
            $"{id} の内容が出ません。");
    }

    [ManualTestRunnerFact]
    public async Task StartsBehindOtherWindowsAndListsEveryPhase()
    {
        List<TestCase> manual = TestCaseParser.ParseDirectory(CasesDirectory).Where(c => c.IsManual).ToList();
        int lastPhase = manual.Max(c => c.Phase);
        Runner runner = await StartAsync();

        ComboBox phase = ById(runner.Window, "PhaseChoice").AsComboBox();
        phase.Expand();
        string[] items = await UntilAsync(() => phase.Items is { Length: > 0 } list ? list.Select(i => i.Name).ToArray() : null, "フェーズの選択肢が出ません。");
        string selected = phase.SelectedItem?.Name ?? string.Empty;
        phase.Collapse();
        Assert.Equal(Enumerable.Range(0, lastPhase + 1).Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)), items);
        Assert.Equal(lastPhase.ToString(System.Globalization.CultureInfo.InvariantCulture), selected);

        AssertNotInFront(runner.Process);
        await CloseAsync(runner);
    }

    [ManualTestRunnerFact]
    public async Task RemembersTheSelectionAndHidesEquipmentCases()
    {
        List<TestCase> manual = TestCaseParser.ParseDirectory(CasesDirectory).Where(c => c.IsManual).ToList();
        PrepBook book = PrepBook.LoadNextTo(CasesDirectory);
        int equipment = manual.Count(book.NeedsEquipment);

        // 前回「リリース前」を選んだ記録。
        File.WriteAllText(Path.Combine(_dir, "filter-choice.txt"), "1");
        Runner runner = await StartAsync();
        Window w = runner.Window;

        // 既定で「機材が必要なテストを除く」がオンで、除いた件数が出る。
        Assert.Equal(ToggleState.On, ById(w, "HideEquipment").AsCheckBox().ToggleState);
        Assert.Equal($"({equipment} 件を除いています)", NameOf(ById(w, "HiddenCount")));
        Assert.Equal(manual.Count - equipment, SummaryCount(w));

        // 機材が要るもの (USB メモリ) は出ない。外すと出て、準備の手順に機材の用意のしかたが出る。
        ById(w, "FeatureFilter").AsTextBox().Text = "FOR-20-07";
        await UntilAsync(() => ListItems(w).Count == 0 ? "ok" : null, "機材が必要な TC-FOR-20-07 が一覧に出ています。");
        ById(w, "HideEquipment").AsCheckBox().Toggle();
        await SelectCaseAsync(w, "FOR-20-07", "TC-FOR-20-07");
        Assert.Equal("済んだ", NameOf(ById(w, "GuideButton")));
        ById(w, "DetailsExpander").Patterns.ExpandCollapse.Pattern.Expand();
        await UntilAsync(() => AllText(w).Contains("■ 機材: 消えてよい USB メモリ", StringComparison.Ordinal) ? "ok" : null, "詳しい説明が開きません。");
        string text = AllText(w);
        Assert.Contains("このテストケースには機材が要ります: 消えてよい USB メモリ", text);
        Assert.Contains("■ 機材: 消えてよい USB メモリ", text);
        Assert.Contains("ないとき:", text);
        Assert.Contains("■ 管理者として実行", text);
        Assert.Equal("0", File.ReadAllText(Path.Combine(_dir, "hide-equipment.txt")).Trim());

        // 仮想ディスクが要るもの: いま行うことに「仮想ディスクを作る」が出て、詳しい説明に準備の手順が出る (ボタンは押さない。管理者の確認が出るため)。
        await SelectCaseAsync(w, "ENG-28-01", "TC-ENG-28-01");
        Assert.Equal("仮想ディスクを作る", NameOf(ById(w, "GuideButton")));
        text = AllText(w);
        Assert.Contains("1. 準備ボタンで自動で行うこと", text);
        Assert.Contains("TD-VHDX-MBR は作らない (「仮想ディスクを作る」で用意する)", text);
        Assert.Contains("■ 仮想ディスク", text);
        Assert.Contains("3. 用意のしかた", text);
        Assert.Contains("■ インストーラ版", text);
        Assert.Contains("2. テストケースの前提", text);

        // テストケースの文は選択できる (TextPattern で選択を扱える)。「すべてコピー」はある (押さない)。
        AutomationElement title = ById(w, "CaseTitle");
        Assert.True(title.Patterns.Text.IsSupported, "テストケースの名前の文字を選べません。");
        Assert.NotEqual(SupportedTextSelection.None, title.Patterns.Text.Pattern.SupportedTextSelection);
        AutomationElement prepLine = w.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).First(t => NameOf(t) == "2. テストケースの前提");
        Assert.True(prepLine.Patterns.Text.IsSupported, "準備の手順の文字を選べません。");
        Assert.NotNull(w.FindFirstDescendant(cf => cf.ByAutomationId("CopyAll")));

        // 選び方を変えると覚え、次の起動で戻る。
        ComboBox filter = ById(w, "FilterChoice").AsComboBox();
        filter.Select(3);
        await UntilAsync(() => File.ReadAllText(Path.Combine(_dir, "filter-choice.txt")).Trim() == "3" ? "ok" : null, "選び方が保存されません。");
        AssertNotInFront(runner.Process);
        await CloseAsync(runner);

        Runner again = await StartAsync();
        ComboBox restored = ById(again.Window, "FilterChoice").AsComboBox();
        restored.Expand();
        string selected = await UntilAsync(() => restored.SelectedItem?.Name, "選び方が読めません。");
        restored.Collapse();
        Assert.Equal("未実施のもの", selected);
        Assert.Equal(ToggleState.Off, ById(again.Window, "HideEquipment").AsCheckBox().ToggleState);
        Assert.Equal(string.Empty, NameOf(ById(again.Window, "HiddenCount")));
        AssertNotInFront(again.Process);
        await CloseAsync(again);
    }

    /// <summary>
    /// 「いま行うこと」は 1 つだけ出し、そのボタンはスクロールしなくても見える。段階が済むと (ファイルができる・[済んだ] を押す) 次の段階に進む。
    /// 管理者の確認が出るボタン・インストールするボタン・準備ボタンは押さない。
    /// </summary>
    [ManualTestRunnerFact]
    public async Task GuidesOneActionAtATime()
    {
        List<TestCase> manual = TestCaseParser.ParseDirectory(CasesDirectory).Where(c => c.IsManual).ToList();
        PrepBook book = PrepBook.LoadNextTo(CasesDirectory);
        File.WriteAllText(Path.Combine(_dir, "filter-choice.txt"), "1");
        File.WriteAllText(Path.Combine(_dir, "hide-equipment.txt"), "0");
        Runner runner = await StartAsync();
        Window w = runner.Window;

        // 既定の大きさ (1100 × 900)。
        Assert.InRange(w.BoundingRectangle.Width, 1098, 1102);
        Assert.InRange(w.BoundingRectangle.Height, 898, 902);

        // TC-ENG-28-01: 1 番目は「仮想ディスクを作る」(ログは一時フォルダにあるので、この PC の仮想ディスクにかかわらず未済)。
        TestCase eng2801 = manual.Single(c => c.Id == "TC-ENG-28-01");
        GuidePlan plan = PrepGuide.Plan(book, eng2801);
        Assert.Equal([GuideAction.MakeVirtualDisks, GuideAction.BuildInstaller, GuideAction.Prepare, GuideAction.RunSteps], plan.Steps.Select(s => s.Action));
        await SelectCaseAsync(w, "ENG-28-01", "TC-ENG-28-01");
        await UntilTitleAsync(w, "1/4  仮想ディスクを作ります");
        AutomationElement button = ById(w, "GuideButton");
        Assert.Equal("仮想ディスクを作る", NameOf(button));
        AssertVisibleWithoutScrolling(w, button);
        Assert.Contains("▶ 1. 仮想ディスクを作ります  ← いまここ", AllText(w));
        Assert.Contains("手順 (準備が終わってから行います。いまはまだ行いません)", AllText(w));
        Shot(w, "TC-ENG-28-01");

        // TC-ENG-28-10: ポータブル版ができていれば (ここでは一時フォルダに置いた空の exe) 1 番目は自動で済み、補足の段階から。[済んだ] で準備に進む。
        string portable = Path.Combine(_dir, "portable", "HexEditor", "HexEditor.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(portable)!);
        File.WriteAllBytes(portable, []);
        await SelectCaseAsync(w, "ENG-28-10", "TC-ENG-28-10");
        await UntilTitleAsync(w, "2/4  準備の前に、下のことを行います");
        Assert.Contains("✓ 1. ポータブル版を作ります  (済み)", AllText(w));
        button = ById(w, "GuideButton");
        Assert.Equal("済んだ", NameOf(button));
        AssertVisibleWithoutScrolling(w, button);
        Shot(w, "TC-ENG-28-10-step2");
        button.Patterns.Invoke.Pattern.Invoke();
        await UntilTitleAsync(w, "3/4  「準備」を押して、HexEditor を起動します");
        button = ById(w, "GuideButton");
        Assert.Equal("準備", NameOf(button));
        AssertVisibleWithoutScrolling(w, button);
        Shot(w, "TC-ENG-28-10-step3");

        // 準備の要らないテストケース (起動するだけ): 1/2 が「準備」で、手順がすぐ下に見える。
        TestCase plain = manual.First(c => !book.NeedsEquipment(c) && c.Steps.Count > 0 && book.For(c.Id).Launch == LaunchKind.Dev
            && PrepGuide.Plan(book, c) is { Steps.Count: 2, Notes.Count: 0 });
        await SelectCaseAsync(w, plain.Id["TC-".Length..], plain.Id);
        await UntilTitleAsync(w, "1/2  「準備」を押して、HexEditor を起動します");
        AssertVisibleWithoutScrolling(w, ById(w, "GuideButton"));
        string firstStep = $"1. {plain.Steps[0]}";
        AutomationElement step = w.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)).First(t => NameOf(t) == firstStep);
        AssertVisibleWithoutScrolling(w, step);
        Shot(w, plain.Id);

        // HexEditor を起動しないテストケース (別の PC で行う): 準備を押してもテストデータを作るだけなので、最後の段階まで進めて確かめる。
        TestCase remote = manual.Single(c => c.Id == "TC-UI-02-07");
        CasePrep remotePrep = book.For(remote.Id);
        Assert.True(remotePrep is { Launch: LaunchKind.None, AdminCommand: null, CopyTo: null }, "TC-UI-02-07 の準備が、起動しないものではなくなりました。");
        GuidePlan remotePlan = PrepGuide.Plan(book, remote);
        Assert.Equal([GuideAction.Hand, GuideAction.BuildPortable, GuideAction.Prepare, GuideAction.RunSteps], remotePlan.Steps.Select(s => s.Action));
        await SelectCaseAsync(w, "UI-02-07", "TC-UI-02-07");
        await UntilTitleAsync(w, "1/4  機材を用意します: Windows 10 の仮想マシン");
        ById(w, "GuideButton").Patterns.Invoke.Pattern.Invoke();
        await UntilTitleAsync(w, "3/4  「準備」を押して、テストデータを作ります");
        ById(w, "GuideButton").Patterns.Invoke.Pattern.Invoke();
        await UntilTitleAsync(w, "4/4  " + remotePlan.Steps[^1].Title);
        Assert.Contains("✓ 準備はすべて済みました (3 件)", AllText(w));
        Assert.Contains("✓ 準備が終わりました。", AllText(w));
        Assert.Equal("手順", NameOf(ById(w, "StepsHeading")));
        Assert.NotNull(w.FindFirstDescendant(cf => cf.ByAutomationId("RedoPrepare")));
        Assert.Null(w.FindFirstDescendant(cf => cf.ByAutomationId("GuideButton")));
        Shot(w, "TC-UI-02-07-final");

        // 機材が要るもの: 1 番目は機材の用意。「ない (対象外にする)」で、コメント付きの対象外を記録して次へ進む。
        await SelectCaseAsync(w, "FOR-20-07", "TC-FOR-20-07");
        await UntilTitleAsync(w, "1/3  機材を用意します: 消えてよい USB メモリ");
        AutomationElement skip = ById(w, "SkipButton");
        Assert.Equal("ない (対象外にする)", NameOf(skip));
        AssertVisibleWithoutScrolling(w, skip);
        Shot(w, "TC-FOR-20-07");
        skip.Patterns.Invoke.Pattern.Invoke();
        string run = Path.Combine(_dir, "current-run.json");
        await UntilAsync(() => File.Exists(run) && File.ReadAllText(run).Contains("TC-FOR-20-07", StringComparison.Ordinal) ? "ok" : null, "対象外が記録されません。");
        using (var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(run)))
        {
            System.Text.Json.JsonElement result = json.RootElement.GetProperty("TC-FOR-20-07");
            Assert.Equal(4, result.GetProperty("Outcome").GetInt32());
            Assert.Equal("消えてよい USB メモリなし", result.GetProperty("Comment").GetString());
        }

        AssertNotInFront(runner.Process);
        await CloseAsync(runner);
    }

    private static async Task UntilTitleAsync(Window window, string title)
    {
        try
        {
            await UntilAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId("GuideTitle")) is { } t && NameOf(t) == title ? "ok" : null, title);
        }
        catch (TimeoutException)
        {
            string now = window.FindFirstDescendant(cf => cf.ByAutomationId("GuideTitle")) is { } t ? NameOf(t) : "なし";
            throw new Xunit.Sdk.XunitException($"いま行うことが「{title}」になりません (今: 「{now}」)。");
        }
    }

    /// <summary>右側の欄 (スクロールする所) の、スクロールしないで見える範囲にある。</summary>
    private static void AssertVisibleWithoutScrolling(Window window, AutomationElement element)
    {
        System.Drawing.Rectangle view = ById(window, "DetailScroll").BoundingRectangle;
        System.Drawing.Rectangle r = element.BoundingRectangle;
        Assert.False(r.IsEmpty, $"{NameOf(element)} の位置が取れません。");
        Assert.True(view.Contains(r), $"{NameOf(element)} ({r}) が、スクロールしないで見える範囲 ({view}) の外にあります。");
    }

    /// <summary>環境変数 HEXEDITOR_RUNNER_SHOTS があれば、ウィンドウを前面に出さずに撮って、そのフォルダに置く (見た目の確認用)。</summary>
    private static void Shot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("HEXEDITOR_RUNNER_SHOTS") is { Length: > 0 } folder)
        {
            WindowImage.Capture(window.Properties.NativeWindowHandle.Value).Save(Path.Combine(folder, name + ".bmp"));
        }
    }

    /// <summary>一覧の下の「N 件 / 合計…」の N。</summary>
    private static int SummaryCount(Window window)
    {
        string summary = NameOf(ById(window, "Summary"));
        Match m = Regex.Match(summary, @"^(\d+) 件");
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
    }

    /// <summary>MainWindow.xaml の名前の付いた TextBlock は、すべて文字を選べる (コピーできる)。</summary>
    [Fact]
    public void NamedTextBlocksAreSelectable()
    {
        string xaml = File.ReadAllText(Path.Combine(AppLocator.RepositoryRoot, "tools", "ManualTestRunner", "MainWindow.xaml"));
        var blocks = Regex.Matches(xaml, @"<TextBlock\b[^>]*?x:Name=""(\w+)""[^>]*?/?>", RegexOptions.Singleline).ToList();
        Assert.NotEmpty(blocks);
        foreach (Match m in blocks)
        {
            Assert.True(m.Value.Contains("IsTextSelectionEnabled=\"True\"", StringComparison.Ordinal), $"{m.Groups[1].Value} の文字を選べません。");
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtr(nint hWnd, int index);
}
