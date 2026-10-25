using System.Diagnostics;
using System.Text;
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
/// 準備の手順は docs/test/manual-test-prepare.md (<see cref="PrepBook"/>) から作り、このツールの画面だけでテストを行えるようにする。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly List<TestCase> _all;
    private readonly Dictionary<string, TestCase> _everyCase;
    private readonly PrepBook _book;
    private readonly RunStore _store = RunStore.LoadOrCreate();
    private readonly bool _restored;
    private TestCase? _current;
    private Process? _hexEditor;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1100, 900));
        string casesDirectory = FindCasesDirectory();
        Preparer.RepoRoot = Path.GetFullPath(Path.Combine(casesDirectory, "..", "..", ".."));
        List<TestCase> every = TestCaseParser.ParseDirectory(casesDirectory).ToList();
        _everyCase = every.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        _all = every.Where(c => c.IsManual).ToList();
        _book = PrepBook.LoadNextTo(casesDirectory);

        // フェーズの選択肢は、テストケースにある最大のフェーズまで作る (フェーズが増えてもツールを直さなくてよいように)。
        int lastPhase = _all.Count == 0 ? 0 : _all.Max(c => c.Phase);
        for (int phase = 0; phase <= lastPhase; phase++)
        {
            PhaseChoice.Items.Add(phase.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        PhaseChoice.SelectedIndex = lastPhase;

        // 前回選んだ「テストの選び方」と「機材が必要なテストを除く」を使う (起動のたびに選び直さなくてよいように)。
        FilterChoice.SelectedIndex = LoadChoice(FilterChoicePath, 0, FilterChoice.Items.Count);
        HideEquipment.IsChecked = LoadChoice(HideEquipmentPath, 1, 2) == 1;
        _restored = true;

        // 開発中の確認 (dev-no-activate) では前面に出さない。それ以外は既定で常に手前に表示する。
        TopMost.IsChecked = !DevOptions.NoActivate;
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

    /// <summary>機材の絞り込みの前の一覧 (選び方・フェーズ・機能 ID)。</summary>
    private IEnumerable<TestCase> SelectedBeforeEquipment()
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

    private bool HidesEquipment => HideEquipment.IsChecked == true;

    private IEnumerable<TestCase> Selected() =>
        HidesEquipment ? SelectedBeforeEquipment().Where(c => !_book.NeedsEquipment(c)) : SelectedBeforeEquipment();

    /// <summary>「機材が必要なテストを除く」で外したもの。</summary>
    private List<TestCase> HiddenByEquipment() =>
        HidesEquipment ? SelectedBeforeEquipment().Where(_book.NeedsEquipment).ToList() : [];

    private void Refresh()
    {
        if (CaseList is null || Summary is null || _book is null || !_restored)
        {
            return;
        }

        List<TestCase> cases = Selected().ToList();
        CaseList.ItemsSource = cases
            .Select(c => new CaseItem(c, $"{Mark(_store.OutcomeOf(c.Id))} {c.Id} ({c.Minutes} 分)", c.Title))
            .ToList();
        int hidden = HiddenByEquipment().Count;
        HiddenCount.Text = HidesEquipment ? $"({hidden} 件を除いています)" : string.Empty;
        int remaining = cases.Count(c => _store.OutcomeOf(c.Id) == Outcome.NotRun);
        int remainingMinutes = cases.Where(c => _store.OutcomeOf(c.Id) == Outcome.NotRun).Sum(c => c.Minutes);
        string text = $"{cases.Count} 件 / 合計 約 {cases.Sum(c => c.Minutes)} 分。残り {remaining} 件 / 約 {remainingMinutes} 分";
        if (cases.Count > 0 && remaining == 0)
        {
            text += "。すべて結果を付けました。「報告を書き出す」を押してください。";
            if (Preparer.VirtualDisksExist)
            {
                text += " 仮想ディスクが残っています (報告を書き出すときに外せます)。";
            }
        }

        if (DevOptions.NoActivate)
        {
            text += $"{Environment.NewLine}注意: 開発中の確認用の印 {DevOptions.NoActivateMarker} があるため、このツールと HexEditor が前面に出ません。手動テストの前に、このファイルを消してください。";
        }

        Summary.Text = text;
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
        CasePrep prep = _book.For(c.Id);
        bool vhd = _book.NeedsVirtualDisks(c);
        MakeVhd.Visibility = RemoveVhd.Visibility = vhd ? Visibility.Visible : Visibility.Collapsed;
        BuildPortable.Visibility = prep.IsPortable || _book.NeedsOf(c).Any(n => n.Name.Contains("仮想マシン", StringComparison.Ordinal))
            ? Visibility.Visible : Visibility.Collapsed;
        BuildInstaller.Visibility = prep.Launch == LaunchKind.Installer ? Visibility.Visible : Visibility.Collapsed;
        BuildPrepPanel(c, null);
        Steps.Children.Clear();
        for (int i = 0; i < c.Steps.Count; i++)
        {
            // チェックボックスの中の文字は選べないので、チェックボックスの右に選べる文字を置く (手順をコピーできるように)。
            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string stepText = $"{i + 1}. {c.Steps[i]}";
            var check = new CheckBox { MinWidth = 0 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(check, stepText);
            var text = new TextBlock { Text = stepText, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            row.Children.Add(check);
            row.Children.Add(text);
            Steps.Children.Add(row);
        }

        Expected.Text = string.Join(Environment.NewLine, c.Expected.Select(e => "・" + e));
        CaseResult? r = _store.ResultOf(c.Id);
        Comment.Text = r?.Comment ?? string.Empty;
        CurrentResult.Text = r is null ? string.Empty : $"記録: {RunStore.Label(r.Outcome)} ({r.At:MM/dd HH:mm})";
        PrepareStatus.Text = Preparer.ToolIsElevated
            ? "注意: このツールが管理者として動いているため、起動する HexEditor も管理者になります。一般ユーザーの権限のテストでは、ツールを普通に起動し直してください。"
            : string.Empty;
        ToolStatus.Text = vhd && Preparer.VirtualDisksExist && File.Exists(Preparer.VhdLog)
            ? "前回の仮想ディスクの結果:" + Environment.NewLine + Preparer.DescribeVirtualDiskLog(File.ReadAllText(Preparer.VhdLog))
            : string.Empty;
    }

    // ---- 準備の手順 ----

    /// <summary>
    /// 準備の手順の欄を作る。<paramref name="outcome"/> が null なら準備ボタンで行うことを「・」で、準備の後は行ったことを「✔」で示す。
    /// </summary>
    private void BuildPrepPanel(TestCase c, PrepareOutcome? outcome)
    {
        PrepPanel.Children.Clear();
        CasePrep prep = _book.For(c.Id);
        IReadOnlyList<PrepNeed> needs = _book.NeedsOf(c);
        IReadOnlyList<string> equipment = _book.EquipmentOf(c);
        if (equipment.Count > 0)
        {
            bool publish = equipment.Contains("公開・署名のあと");
            AddText($"このテストケースには機材が要ります: {string.Join("、", equipment)}。"
                + (publish ? "公開・署名がまだなら「保留」を押してください。" : "なければ「対象外」を押してください (下の「あなたが行うこと」に書き方があります)。"), strong: true);
        }

        AddHeading(outcome is null ? "1. 準備ボタンで自動で行うこと" : "1. 準備ボタンが行ったこと");
        if (outcome is null)
        {
            foreach (string line in PlannedSteps(c, prep))
            {
                AddText("・" + line);
            }
        }
        else
        {
            foreach (string line in outcome.Done)
            {
                AddText("✔ " + line);
            }

            foreach (string line in outcome.Problems)
            {
                AddText("✖ " + line, strong: true);
            }
        }

        AddHeading("2. テストケースの前提");
        AddText(c.Precondition.Length > 0 ? c.Precondition : "(なし)");

        AddHeading("3. あなたが行うこと");
        bool any = false;
        foreach (PrepNeed need in needs)
        {
            AddText((need.IsEquipment ? "■ 機材: " : "■ ") + need.Name, strong: true);
            foreach (string line in need.Lines)
            {
                AddText(line);
            }

            any = true;
        }

        foreach (string id in c.TestData.Concat(prep.ArgumentData).Distinct().Where(id => _book.Data.ContainsKey(id) && !id.StartsWith("TD-VHDX", StringComparison.Ordinal)))
        {
            AddText("■ テストデータ " + id, strong: true);
            foreach (string line in _book.Data[id].Lines)
            {
                AddText(line);
            }

            any = true;
        }

        if (prep.Steps.Count > 0)
        {
            AddText("■ このテストケースの補足", strong: true);
            foreach (string line in prep.Steps)
            {
                AddText(line);
            }

            any = true;
        }

        if (!any)
        {
            AddText("準備ボタンを押すだけで、前提の状態になります。");
        }

        IReadOnlyList<string> references = PrepBook.ReferencedCases(c);
        if (references.Count > 0)
        {
            AddHeading("4. 参照するテストケース (前提に出てくるもの)");
            foreach (string id in references)
            {
                if (!_everyCase.TryGetValue(id, out TestCase? other))
                {
                    continue;
                }

                AddText($"{other.Id} {other.Title} ({other.Kind})", strong: true);
                if (other.Precondition.Length > 0)
                {
                    AddText("前提: " + other.Precondition);
                }

                for (int i = 0; i < other.Steps.Count; i++)
                {
                    AddText($"手順 {i + 1}. {other.Steps[i]}");
                }
            }
        }
    }

    private IEnumerable<string> PlannedSteps(TestCase c, CasePrep prep)
    {
        yield return "テスト用の新しい設定フォルダを作る (普段の設定は変わらない)";
        List<string> data = c.TestData.Concat(prep.ArgumentData).Distinct().ToList();
        List<string> made = data.Where(CaseSetup.CanPrepare).ToList();
        if (made.Count > 0)
        {
            yield return "テストデータを作る: " + string.Join("、", made);
        }

        foreach (string id in data.Where(id => !CaseSetup.CanPrepare(id)))
        {
            yield return id.StartsWith("TD-VHDX", StringComparison.Ordinal)
                ? $"{id} は作らない (「仮想ディスクを作る」で用意する)"
                : $"{id} は作れない (「あなたが行うこと」を見る)";
        }

        if (prep.Settings is { } settings)
        {
            yield return "テスト用の設定を書く: " + settings;
        }

        if (prep.CopyTo == "デスクトップ")
        {
            yield return "テストデータをデスクトップに置く";
        }

        if (prep.AdminCommand is { } command)
        {
            yield return $"管理者の PowerShell で「{command}」を実行する (管理者の確認が出る)";
        }

        if (prep.Launch == LaunchKind.None)
        {
            yield return "HexEditor は起動しない (このテストケースは別の PC などで行う)";
        }
        else
        {
            string open = prep.OpensNothing ? "ファイルを開かずに"
                : prep.Arguments is { } a ? $"引数「{a}」で"
                : c.TestData.FirstOrDefault(CaseSetup.CanPrepare) is { } first && first != "TD-INSP-BM-SET" ? $"{first} を開いて"
                : "ファイルを開かずに";
            yield return $"{Preparer.LaunchLabel(prep.Launch)}{open}起動する" + (prep.IsAdmin ? " (管理者の確認が出る)" : string.Empty);
        }
    }

    private void AddHeading(string text) =>
        PrepPanel.Children.Add(new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

    private void AddText(string text, bool strong = false) =>
        PrepPanel.Children.Add(new TextBlock
        {
            Text = text,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        });

    private string PrepPanelText() =>
        string.Join(Environment.NewLine, PrepPanel.Children.OfType<TextBlock>().Select(t => t.Text));

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

    /// <summary>今のテストケースの内容をまとめてクリップボードへ (利用者がボタンを押したときだけ)。</summary>
    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } c)
        {
            return;
        }

        var text = new StringBuilder();
        text.AppendLine(CaseTitle.Text);
        text.AppendLine(CaseFields.Text);
        text.AppendLine();
        text.AppendLine("準備の手順:");
        text.AppendLine(PrepPanelText());
        text.AppendLine();
        text.AppendLine("手順:");
        for (int i = 0; i < c.Steps.Count; i++)
        {
            text.AppendLine($"{i + 1}. {c.Steps[i]}");
        }

        text.AppendLine();
        text.AppendLine("期待結果:");
        text.AppendLine(Expected.Text);
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text.ToString());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        PrepareStatus.Text = "テストケースの内容をコピーしました。";
    }

    // ---- 選び方の保存 ----

    private static string FilterChoicePath => Path.Combine(RunStore.Folder, "filter-choice.txt");

    private static string HideEquipmentPath => Path.Combine(RunStore.Folder, "hide-equipment.txt");

    private static int LoadChoice(string path, int fallback, int count)
    {
        try
        {
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out int index) && index >= 0 && index < count)
            {
                return index;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return fallback;
    }

    private static void SaveChoice(string path, int value)
    {
        try
        {
            Directory.CreateDirectory(RunStore.Folder);
            File.WriteAllText(path, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_restored && ReferenceEquals(sender, FilterChoice) && FilterChoice.SelectedIndex >= 0)
        {
            SaveChoice(FilterChoicePath, FilterChoice.SelectedIndex);
        }

        Refresh();
    }

    private void HideEquipment_Changed(object sender, RoutedEventArgs e)
    {
        if (_restored)
        {
            SaveChoice(HideEquipmentPath, HidesEquipment ? 1 : 0);
        }

        Refresh();
    }

    private void FeatureFilter_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void CaseList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CaseList.SelectedItem is CaseItem item)
        {
            Show(item.Case);
        }
    }

    // ---- 準備 ----

    private void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } c)
        {
            return;
        }

        PrepareStatus.Text = "準備しています...";
        PrepareOutcome outcome = Preparer.Prepare(c, _book.For(c.Id));
        _hexEditor = outcome.HexEditor ?? _hexEditor;
        BuildPrepPanel(c, outcome);
        PrepareStatus.Text = outcome.Problems.Count == 0
            ? "準備が終わりました。上の「3. あなたが行うこと」を行ってから、手順に進んでください。"
            : "準備の一部ができませんでした。上の「✖」の行を見てください。";
    }

    private async void MakeVhd_Click(object sender, RoutedEventArgs e) => await RunVirtualDisksAsync(dismount: false);

    private async void RemoveVhd_Click(object sender, RoutedEventArgs e) => await RunVirtualDisksAsync(dismount: true);

    private async Task RunVirtualDisksAsync(bool dismount)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        MakeVhd.IsEnabled = RemoveVhd.IsEnabled = false;
        ToolStatus.Text = dismount
            ? "仮想ディスクを外しています。管理者の確認が出たら「はい」を押してください..."
            : "仮想ディスクを作っています。管理者の確認が出たら「はい」を押してください (1 分ほどかかります)...";
        try
        {
            string? log = await Preparer.RunVirtualDiskScriptAsync(dismount);
            ToolStatus.Text = log is null
                ? "管理者の確認で「いいえ」が押されたため、何もしませんでした。"
                : Preparer.DescribeVirtualDiskLog(log) + Environment.NewLine + $"(ログ: {Preparer.VhdLog})";
        }
        finally
        {
            _busy = false;
            MakeVhd.IsEnabled = RemoveVhd.IsEnabled = true;
            Refresh();
        }
    }

    private async void BuildPortable_Click(object sender, RoutedEventArgs e) => await BuildAsync("Portable");

    private async void BuildInstaller_Click(object sender, RoutedEventArgs e) => await BuildAsync("Installer");

    private async Task BuildAsync(string distro)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        BuildPortable.IsEnabled = BuildInstaller.IsEnabled = false;
        string name = distro == "Portable" ? "ポータブル版" : "インストーラ版";
        ToolStatus.Text = $"{name}を作っています (数分かかります。このツールは閉じないでください)...";
        var progress = new Progress<string>(line => ToolStatus.Text = $"{name}を作っています (数分かかります)...{Environment.NewLine}{line}");
        try
        {
            string? error = await Preparer.BuildDistributionAsync(distro, progress);
            ToolStatus.Text = error is null
                ? (distro == "Portable"
                    ? $"ポータブル版を作りました: {Preparer.PortableExe}{Environment.NewLine}zip: {Preparer.ArtifactsFolder(distro)}{Environment.NewLine}もう一度「準備」を押してください。"
                    : $"インストーラ版をインストールしました: {Preparer.InstallerExe}{Environment.NewLine}Setup.exe: {Preparer.ArtifactsFolder(distro)}{Environment.NewLine}もう一度「準備」を押してください。")
                : error;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or InvalidDataException)
        {
            ToolStatus.Text = $"{name}を作れませんでした: {ex.Message}";
        }
        finally
        {
            _busy = false;
            BuildPortable.IsEnabled = BuildInstaller.IsEnabled = true;
        }
    }

    private void Pass_Click(object sender, RoutedEventArgs e) => Record(Outcome.Pass);

    private void Fail_Click(object sender, RoutedEventArgs e) => Record(Outcome.Fail);

    private void Hold_Click(object sender, RoutedEventArgs e) => Record(Outcome.Hold);

    private void NotApplicable_Click(object sender, RoutedEventArgs e) => Record(Outcome.NotApplicable);

    // ---- 報告 ----

    private async void Report_Click(object sender, RoutedEventArgs e)
    {
        List<(TestCase, string)> skipped = HiddenByEquipment().Select(c => (c, string.Join("、", _book.EquipmentOf(c)))).ToList();
        string path = _store.ExportReport(Selected().ToList(), skipped);
        var text = new StringBuilder();
        text.AppendLine(path);
        if (skipped.Count > 0)
        {
            text.AppendLine().AppendLine($"機材が必要なため一覧から除いた {skipped.Count} 件は、報告の「機材が必要なため今回は実施しない」に書きました (件数には数えていません)。");
        }

        // 終わったときの後片付け (テストのために変えたものを戻す)。
        var reminders = new List<string>();
        if (Preparer.VirtualDisksExist)
        {
            reminders.Add("仮想ディスクが接続されたままです。手動テストが終わったなら「仮想ディスクを外す」を押してください (管理者の確認が出ます)。");
        }

        if (File.Exists(Path.Combine(Preparer.PortableFolder, "HexEditor", "HexEditor.Elevated.exe.bak")))
        {
            reminders.Add("ポータブル版の HexEditor.Elevated.exe.bak の名前が戻っていません (TC-ENG-28-10)。HexEditor.Elevated.exe に戻してください: " + Path.Combine(Preparer.PortableFolder, "HexEditor"));
        }

        if (File.Exists(Preparer.InstallerExe))
        {
            reminders.Add("インストーラ版がインストールされています。要らなければ「設定 > アプリ > インストールされているアプリ」で HexEditor をアンインストールしてください。");
        }

        if (reminders.Count > 0)
        {
            text.AppendLine().AppendLine("後片付け:");
            foreach (string r in reminders)
            {
                text.AppendLine("・" + r);
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "報告を書き出しました",
            Content = new ScrollViewer { Content = new TextBlock { Text = text.ToString(), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
            CloseButtonText = "閉じる",
            PrimaryButtonText = Preparer.VirtualDisksExist ? "仮想ディスクを外す" : string.Empty,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            if (_current is not null && Detail.Visibility == Visibility.Visible)
            {
                await RunVirtualDisksAsync(dismount: true);
            }
            else
            {
                string? log = await Preparer.RunVirtualDiskScriptAsync(dismount: true);
                Summary.Text = log is null ? "仮想ディスクは外しませんでした。" : Preparer.DescribeVirtualDiskLog(log);
            }
        }
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
            presenter.IsAlwaysOnTop = TopMost.IsChecked == true && !DevOptions.NoActivate;
        }
    }
}
