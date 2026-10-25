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
        await UntilAsync(() => AllText(window).Contains(id, StringComparison.Ordinal) && window.FindFirstDescendant(cf => cf.ByAutomationId("PrepareButton")) is not null ? "ok" : null,
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
        string text = AllText(w);
        Assert.Contains("このテストケースには機材が要ります: 消えてよい USB メモリ", text);
        Assert.Contains("■ 機材: 消えてよい USB メモリ", text);
        Assert.Contains("ないとき:", text);
        Assert.Contains("■ 管理者として実行", text);
        Assert.Null(w.FindFirstDescendant(cf => cf.ByAutomationId("MakeVhd")));
        Assert.Equal("0", File.ReadAllText(Path.Combine(_dir, "hide-equipment.txt")).Trim());

        // 仮想ディスクが要るもの: ボタンと準備の手順が出る (ボタンは押さない。管理者の確認が出るため)。
        await SelectCaseAsync(w, "ENG-28-01", "TC-ENG-28-01");
        Assert.NotNull(w.FindFirstDescendant(cf => cf.ByAutomationId("MakeVhd")));
        Assert.NotNull(w.FindFirstDescendant(cf => cf.ByAutomationId("RemoveVhd")));
        Assert.NotNull(w.FindFirstDescendant(cf => cf.ByAutomationId("BuildInstaller")));
        text = AllText(w);
        Assert.Contains("1. 準備ボタンで自動で行うこと", text);
        Assert.Contains("TD-VHDX-MBR は作らない (「仮想ディスクを作る」で用意する)", text);
        Assert.Contains("■ 仮想ディスク", text);
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
