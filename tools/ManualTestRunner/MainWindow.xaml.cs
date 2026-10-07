using System.Diagnostics;
using HexEditor.ManualTests;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace ManualTestRunner;

/// <summary>一覧に出すテストケース。</summary>
public sealed record CaseItem(TestCase Case, string Header, string Title);

/// <summary>
/// 手動テスト支援ツール (テスト方針 8.1)。手動・半自動のテストケースを 1 件ずつ表示し、準備・記録・報告を行う。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly List<TestCase> _all;
    private readonly RunStore _store = RunStore.LoadOrCreate();
    private TestCase? _current;
    private Process? _hexEditor;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1100, 900));
        _all = TestCaseParser.ParseDirectory(FindCasesDirectory()).Where(c => c.IsManual).ToList();

        // 開発中の確認 (dev-no-activate) では前面に出さない。それ以外は既定で常に手前に表示する。
        bool noActivate = File.Exists(Path.Combine(Path.GetTempPath(), "HexEditor", "dev-no-activate"));
        TopMost.IsChecked = !noActivate;
        Refresh();
    }

    private static string FindCasesDirectory()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("HEXEDITOR_REPO");
        for (DirectoryInfo? dir = new(fromEnv ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "docs", "test", "cases");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("docs/test/cases が見つかりません。環境変数 HEXEDITOR_REPO にリポジトリのパスを設定してください。");
    }

    private IEnumerable<TestCase> Selected()
    {
        int maxPhase = PhaseChoice.SelectedIndex;
        string feature = FeatureFilter.Text.Trim();
        IEnumerable<TestCase> cases = _all.Where(c => c.Phase <= maxPhase);
        if (feature.Length > 0)
        {
            cases = cases.Where(c => c.Id.Contains(feature, StringComparison.OrdinalIgnoreCase));
        }

        return FilterChoice.SelectedIndex switch
        {
            0 => cases.Where(c => c.IsSmoke),
            2 => cases.Where(c => _store.OutcomeOf(c.Id) is Outcome.Fail or Outcome.Hold),
            3 => cases.Where(c => _store.OutcomeOf(c.Id) == Outcome.NotRun),
            _ => cases,
        };
    }

    private void Refresh()
    {
        if (CaseList is null || Summary is null)
        {
            return;
        }

        List<TestCase> cases = Selected().ToList();
        CaseList.ItemsSource = cases
            .Select(c => new CaseItem(c, $"{Mark(_store.OutcomeOf(c.Id))} {c.Id} ({c.Minutes} 分)", c.Title))
            .ToList();
        int remaining = cases.Count(c => _store.OutcomeOf(c.Id) == Outcome.NotRun);
        int remainingMinutes = cases.Where(c => _store.OutcomeOf(c.Id) == Outcome.NotRun).Sum(c => c.Minutes);
        Summary.Text = $"{cases.Count} 件 / 合計 約 {cases.Sum(c => c.Minutes)} 分。残り {remaining} 件 / 約 {remainingMinutes} 分";
    }

    private static string Mark(Outcome outcome) => outcome switch
    {
        Outcome.Pass => "○",
        Outcome.Fail => "×",
        Outcome.Hold => "△",
        Outcome.NotApplicable => "－",
        _ => "・",
    };

    private void Show(TestCase c)
    {
        _current = c;
        Detail.Visibility = ResultPanel.Visibility = Visibility.Visible;
        CaseTitle.Text = $"{c.Id} {c.Title}";
        CaseFields.Text = string.Join("  /  ", c.Fields.Select(f => $"{f.Key}: {f.Value}"));
        Precondition.Text = c.Precondition;
        Steps.Children.Clear();
        for (int i = 0; i < c.Steps.Count; i++)
        {
            Steps.Children.Add(new CheckBox { Content = new TextBlock { Text = $"{i + 1}. {c.Steps[i]}", TextWrapping = TextWrapping.Wrap } });
        }

        Expected.Text = string.Join(Environment.NewLine, c.Expected.Select(e => "・" + e));
        CaseResult? r = _store.ResultOf(c.Id);
        Comment.Text = r?.Comment ?? string.Empty;
        CurrentResult.Text = r is null ? string.Empty : $"記録: {RunStore.Label(r.Outcome)} ({r.At:MM/dd HH:mm})";
        PrepareStatus.Text = string.Empty;
    }

    private void Record(Outcome outcome)
    {
        if (_current is not { } c)
        {
            return;
        }

        // 不合格のときは HexEditor のウィンドウを撮って添付する。
        string? screenshot = outcome == Outcome.Fail ? Preparer.CaptureWindow(_hexEditor, c.Id) : null;
        _store.Record(c.Id, new CaseResult(outcome, Comment.Text, screenshot, DateTimeOffset.Now, EnvironmentInfo.Describe()));
        int index = CaseList.SelectedIndex;
        Refresh();
        CaseList.SelectedIndex = Math.Min(index + 1, CaseList.Items.Count - 1);
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => Refresh();

    private void FeatureFilter_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void CaseList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CaseList.SelectedItem is CaseItem item)
        {
            Show(item.Case);
        }
    }

    private void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        PrepareStatus.Text = "準備しています...";
        _hexEditor = Preparer.Prepare(_current, out IReadOnlyList<string> missing);
        PrepareStatus.Text = (_hexEditor is null ? "HexEditor を起動できませんでした (hexeditor.exe の登録を確認してください)。" : "HexEditor を起動しました。")
            + (missing.Count > 0 ? $" 手で用意するテストデータ: {string.Join("、", missing)}" : string.Empty);
    }

    private void Pass_Click(object sender, RoutedEventArgs e) => Record(Outcome.Pass);

    private void Fail_Click(object sender, RoutedEventArgs e) => Record(Outcome.Fail);

    private void Hold_Click(object sender, RoutedEventArgs e) => Record(Outcome.Hold);

    private void NotApplicable_Click(object sender, RoutedEventArgs e) => Record(Outcome.NotApplicable);

    private async void Report_Click(object sender, RoutedEventArgs e)
    {
        string path = _store.ExportReport(Selected().ToList());
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "報告を書き出しました",
            Content = path,
            CloseButtonText = "閉じる",
        };
        await dialog.ShowAsync();
    }

    private async void NewRun_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "新しく始めますか?",
            Content = "今の記録は日時付きの名前で残し、すべてのテストケースを未実施に戻します。",
            PrimaryButtonText = "新しく始める",
            CloseButtonText = "キャンセル",
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _store.StartNew();
            Refresh();
        }
    }

    private void TopMost_Changed(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = TopMost.IsChecked == true;
        }
    }
}
