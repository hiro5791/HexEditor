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
    private bool _keepingSelection;
    private TestCase? _current;
    private Process? _hexEditor;
    private bool _busy;

    // 「いま行うこと」の状態。手で行った段階 ([済んだ] を押したもの) と準備の結果は、テストケースごとに覚える (このツールを閉じるまで)。
    private readonly Dictionary<string, HashSet<string>> _handDone = [];
    private readonly Dictionary<string, PrepareOutcome> _outcomes = [];
    private readonly HashSet<string> _prepared = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _guideTimer;
    private readonly HashSet<string> _autoStarted = [];
    private readonly Dictionary<string, string> _failed = [];
    private string? _runningCase;
    private string? _runningKey;
    private string? _restoreNote;
    private GuidePlan? _plan;
    private string _guideSignature = string.Empty;

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

        // 前回、隠したまま終わった補助プロセス (HexEditor.Elevated.exe.bak など) を元に戻す。
        _restoreNote = Preparer.RestoreHidden(_book, keepCase: null);

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

        // 開発中の確認 (dev-no-activate) では前面に出さない。それ以外は前回の選択 (既定はオン) を使う。
        TopMost.IsChecked = !DevOptions.NoActivate && LoadChoice(TopMostPath, 1, 2) == 1;
        Refresh();

        // 仮想ディスク・ポータブル版・インストーラ版ができたことは、ファイルとログで見つけて自動で「済み」にする (2 秒ごと)。
        _guideTimer = DispatcherQueue.CreateTimer();
        _guideTimer.Interval = TimeSpan.FromSeconds(2);
        _guideTimer.Tick += (_, _) => UpdateGuide(force: false);
        _guideTimer.Start();
        Closed += (_, _) =>
        {
            _guideTimer.Stop();
            Preparer.RestoreHidden(_book, keepCase: null);
        };
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
        var items = cases
            .Select(c => new CaseItem(c, $"{Mark(_store.OutcomeOf(c.Id))} {c.Id} ({c.Minutes} 分)", c.Title))
            .ToList();

        // 一覧を作り直しても、表示中のテストケースの選択を残す (準備の後などに、どれを選んでいるか分からなくならないように)。
        // 選び直しで Show が動くと手順のチェックなどが消えるため、そのあいだは選択の変更を無視する。
        _keepingSelection = true;
        try
        {
            CaseList.ItemsSource = items;
            int keep = _current is { } shown ? items.FindIndex(i => i.Case.Id == shown.Id) : -1;
            if (keep >= 0)
            {
                CaseList.SelectedIndex = keep;
                CaseList.ScrollIntoView(items[keep]);
            }
        }
        finally
        {
            _keepingSelection = false;
        }

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
        // 別のテストケースを選んだら、前のテストケースで隠したファイルを戻す (このテストケースの分は戻さない)。
        string? restoreProblem = Preparer.RestoreHidden(_book, keepCase: c.Id) ?? _restoreNote;
        _restoreNote = null;
        _current = c;
        Detail.Visibility = ResultPanel.Visibility = Visibility.Visible;
        CaseTitle.Text = $"{c.Id} {c.Title}";
        CaseFields.Text = string.Join("  /  ", c.Fields.Select(f => $"{f.Key}: {f.Value}"));
        BuildPrepPanel(c, _outcomes.GetValueOrDefault(c.Id));
        Steps.Children.Clear();
        for (int i = 0; i < c.Steps.Count; i++)
        {
            // チェックボックスの中の文字は選べないので、チェックボックスの右に選べる文字を置く (手順をコピーできるように)。
            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string stepText = $"{i + 1}. {Preparer.WithDiskNumbers(c.Steps[i])}";
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
        CurrentResult.Text = r is null ? "まだ結果を付けていません" : $"記録: {RunStore.Label(r.Outcome)} ({r.At:MM/dd HH:mm})";
        ShowRecordedOutcome(r?.Outcome ?? Outcome.NotRun);
        Warning.Text = "注意: このツールが管理者として動いているため、起動する HexEditor も管理者になります。一般ユーザーの権限のテストでは、ツールを普通に起動し直してください。";
        Warning.Visibility = Preparer.ToolIsElevated ? Visibility.Visible : Visibility.Collapsed;
        _plan = PrepGuide.Plan(_book, c);
        Status = _book.NeedsVirtualDisks(c) && Preparer.VirtualDisksAttached
            ? "前回の仮想ディスクの結果:" + Environment.NewLine + Preparer.DescribeVirtualDiskLog(File.ReadAllText(Preparer.VhdLog))
            : string.Empty;
        if (restoreProblem is not null)
        {
            Status = "隠したファイルを元に戻せませんでした:" + Environment.NewLine + restoreProblem;
        }

        UpdateGuide(force: true);
        DetailScroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    // ---- いま行うこと ----

    private bool IsDone(TestCase c, GuideStep step) => step.Action switch
    {
        GuideAction.Hand => _handDone.TryGetValue(c.Id, out HashSet<string>? done) && done.Contains(step.Key),
        GuideAction.MakeVirtualDisks => Preparer.VirtualDisksAttached,
        GuideAction.BuildPortable => Preparer.IsUpToDate("Portable"),
        GuideAction.BuildInstaller => Preparer.IsUpToDate("Installer"),
        GuideAction.HideFiles => Preparer.AreHidden(_book.For(c.Id)),
        GuideAction.Prepare => _prepared.Contains(c.Id),
        _ => false,
    };

    /// <summary>準備ボタン 1 つで自動で行う段階 (仮想ディスク・ポータブル版・インストーラ版・準備)。</summary>
    private static bool IsAutomatic(GuideAction action) =>
        action is GuideAction.MakeVirtualDisks or GuideAction.BuildPortable or GuideAction.BuildInstaller or GuideAction.HideFiles or GuideAction.Prepare;

    /// <summary>まだ済んでいない最初の段階 (いま行うこと)。</summary>
    private int CurrentStepIndex(TestCase c, GuidePlan plan)
    {
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            if (!IsDone(c, plan.Steps[i]))
            {
                return i;
            }
        }

        return plan.Steps.Count - 1;
    }

    /// <summary>「いま行うこと」の下の結果・進み具合の文。空なら欄を畳む (ボタンの下に空きを作らない)。</summary>
    private string Status
    {
        set
        {
            GuideStatus.Text = value;
            GuideStatus.Visibility = value.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private GuideStep? CurrentStep => _current is { } c && _plan is { } plan ? plan.Steps[CurrentStepIndex(c, plan)] : null;

    /// <summary>自動の段階の、行う前に知っておくこと (確認の画面が出る・時間がかかる)。</summary>
    private static string AutomaticHint(GuideAction action) => action switch
    {
        GuideAction.MakeVirtualDisks => "Windows の確認 (このアプリがデバイスに変更を加えることを許可しますか?) が出たら「はい」を押してください。",
        GuideAction.BuildPortable or GuideAction.BuildInstaller => "数分かかります。このツールは閉じないでください。",
        _ => string.Empty,
    };

    /// <summary>
    /// 「いま行うこと」と準備の流れを作り直す。<paramref name="force"/> が false なら、段階の済み・未済が変わったときだけ作り直す
    /// (2 秒ごとの確認で、選んでいる文字やフォーカスを消さないように)。
    /// </summary>
    private void UpdateGuide(bool force)
    {
        if (_current is not { } c || _plan is not { } plan)
        {
            return;
        }

        bool[] done = plan.Steps.Select(s => IsDone(c, s)).ToArray();
        string? failure = _failed.GetValueOrDefault(c.Id);
        bool running = _runningCase == c.Id;
        string signature = $"{c.Id}|{string.Join(",", done)}|{_busy}|{_runningKey}|{failure}|{Preparer.VirtualDisksExist}";
        if (!force && signature == _guideSignature)
        {
            return;
        }

        _guideSignature = signature;
        int current = CurrentStepIndex(c, plan);
        GuideStep step = plan.Steps[current];
        bool final = step.Action == GuideAction.RunSteps;
        GuideLines.Children.Clear();
        string label;
        if (running)
        {
            // 準備ボタンで自動で進めているところ。
            GuideTitle.Text = $"準備しています ({current + 1}/{plan.Steps.Count} {step.Title})";
            if (AutomaticHint(step.Action) is { Length: > 0 } hint)
            {
                GuideLines.Children.Add(Line(hint, strong: true));
            }

            label = string.Empty;
        }
        else if (IsAutomatic(step.Action))
        {
            // 自動で行える段階: 準備ボタン 1 つで、次の手で行う段階までをまとめて行う。
            List<GuideStep> auto = plan.Steps.Skip(current).TakeWhile(s => IsAutomatic(s.Action)).Where(s => !IsDone(c, s)).ToList();
            if (failure is not null)
            {
                GuideTitle.Text = $"{current + 1}/{plan.Steps.Count}  準備が途中で止まりました";
                GuideLines.Children.Add(Line($"止まったところ: {step.Title}", strong: true));
                GuideLines.Children.Add(Line(failure));
                GuideLines.Children.Add(Line("直せることがあれば直してから、「もう一度」を押してください。続きから行います。"));
                label = "もう一度";
            }
            else
            {
                GuideTitle.Text = $"{current + 1}/{plan.Steps.Count}  「準備」を押してください";
                GuideLines.Children.Add(Line("次のことを、順に自動で行います:"));
                foreach (GuideStep s in auto)
                {
                    GuideLines.Children.Add(Line("・" + s.Title.Replace("「準備」を押して、", string.Empty, StringComparison.Ordinal)));
                }

                foreach (string hint in auto.Select(s => AutomaticHint(s.Action)).Where(h => h.Length > 0).Distinct())
                {
                    GuideLines.Children.Add(Line(hint, strong: true));
                }

                label = "準備";
            }
        }
        else
        {
            GuideTitle.Text = $"{current + 1}/{plan.Steps.Count}  {step.Title}";

            // 「ないとき:」の行は、下の飛ばすボタンの説明に置き換える。
            foreach (string line in step.Lines.Where(l => step.SkipOutcome is null || !l.StartsWith("ないとき", StringComparison.Ordinal)))
            {
                GuideLines.Children.Add(Line(line));
            }

            if (step.SkipOutcome is { } skip)
            {
                string comment = step.SkipComment is { } text ? $"コメントに「{text}」と書いて" : "コメントに理由を書いてから押すと";
                GuideLines.Children.Add(Line(skip == "保留"
                    ? $"まだできないときは「まだできない (保留にする)」を押します ({comment}、結果を「保留」にして次のテストケースに進みます)。"
                    : $"ないときは「ない (対象外にする)」を押します ({comment}、結果を「対象外」にして次のテストケースに進みます)。"));
            }

            if (final)
            {
                AddFinalLines(c, plan);
            }

            label = final ? string.Empty : "済んだ";
        }

        GuideButton.Content = label;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(GuideButton, label);
        GuideButton.Visibility = label.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        GuideButton.IsEnabled = !_busy;
        bool skippable = step.SkipOutcome is not null && !running && !IsAutomatic(step.Action);
        SkipButton.Visibility = skippable ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Content = step.SkipOutcome == "保留" ? "まだできない (保留にする)" : "ない (対象外にする)";
        SkipButton.IsEnabled = !_busy;

        // 準備の流れ (済み ✓・いま ▶・このあと ・)。手順の段階になったら 1 行にまとめ、手順を「いま行うこと」のすぐ下に出す。
        Checklist.Children.Clear();
        if (final)
        {
            if (plan.Steps.Count > 1)
            {
                Checklist.Children.Add(Line($"✓ 準備はすべて済みました ({plan.Steps.Count - 1} 件)", secondary: true));
            }
        }
        else
        {
            Checklist.Children.Add(Line("準備の流れ", strong: true));
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                string mark = i == current ? (failure is not null && !running ? "×" : "▶") : done[i] ? "✓" : "・";
                string state = i != current ? (done[i] ? "  (済み)" : string.Empty)
                    : running ? "  ← 実行中"
                    : failure is not null ? "  ← 止まりました"
                    : "  ← いまここ";
                string title = plan.Steps[i].Action == GuideAction.RunSteps ? "手順を行い、結果を押す" : plan.Steps[i].Title;
                Checklist.Children.Add(Line($"{mark} {i + 1}. {title}{state}", strong: i == current, secondary: i != current));
            }
        }

        RedoPrepare.Visibility = _prepared.Contains(c.Id) ? Visibility.Visible : Visibility.Collapsed;
        RemoveVhd.Visibility = plan.Steps.Any(s => s.Action == GuideAction.MakeVirtualDisks && IsDone(c, s)) ? Visibility.Visible : Visibility.Collapsed;
        GuideStep? built = plan.Steps.FirstOrDefault(s => s.Action is GuideAction.BuildPortable or GuideAction.BuildInstaller && IsDone(c, s));
        Rebuild.Visibility = built is null ? Visibility.Collapsed : Visibility.Visible;
        Rebuild.Content = built?.Action == GuideAction.BuildInstaller ? "インストーラ版を作り直す" : "ポータブル版を作り直す";
        Rebuild.Tag = built?.Action;
        RedoPrepare.IsEnabled = Rebuild.IsEnabled = RemoveVhd.IsEnabled = !_busy;

        StepsHeading.Text = final ? "手順" : "手順 (準備が終わってから行います。いまはまだ行いません)";
        foreach (CheckBox check in Steps.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<CheckBox>()))
        {
            check.IsEnabled = final;
        }
    }

    /// <summary>最後の段階 (手順を行う) に、手順の間に気をつけること・準備で作ったテストデータの場所・参照するテストケースを書く。</summary>
    private void AddFinalLines(TestCase c, GuidePlan plan)
    {
        foreach (PrepNeed note in plan.Notes)
        {
            GuideLines.Children.Add(Line("■ " + note.Name, strong: true));
            foreach (string line in note.Lines)
            {
                GuideLines.Children.Add(Line(line));
            }
        }

        if (_outcomes.GetValueOrDefault(c.Id)?.Setup is { Files.Count: > 0 } setup)
        {
            GuideLines.Children.Add(Line("■ 準備で作ったテストデータの場所", strong: true));
            foreach ((string id, string path) in setup.Files)
            {
                GuideLines.Children.Add(Line($"{id}: {path}"));
            }
        }

        foreach (string id in PrepBook.ReferencedCases(c))
        {
            if (!_everyCase.TryGetValue(id, out TestCase? other))
            {
                continue;
            }

            GuideLines.Children.Add(Line($"■ 参照するテストケース {other.Id} {other.Title}", strong: true));
            if (other.Precondition.Length > 0)
            {
                GuideLines.Children.Add(Line("前提: " + other.Precondition));
            }

            for (int i = 0; i < other.Steps.Count; i++)
            {
                GuideLines.Children.Add(Line($"手順 {i + 1}. {other.Steps[i]}"));
            }
        }
    }

    private static TextBlock Line(string text, bool strong = false, bool secondary = false)
    {
        var block = new TextBlock
        {
            Text = text,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        };
        if (secondary)
        {
            block.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }

        return block;
    }

    private async void GuideButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } c || CurrentStep is not { } step)
        {
            return;
        }

        if (IsAutomatic(step.Action))
        {
            await RunAutomaticAsync(c);
            return;
        }

        if (step.Action == GuideAction.Hand)
        {
            if (!_handDone.TryGetValue(c.Id, out HashSet<string>? done))
            {
                _handDone[c.Id] = done = [];
            }

            done.Add(step.Key);
            Status = string.Empty;
            UpdateGuide(force: true);

            // 準備ボタンをもう押してあれば、続きの自動の段階をそのまま行う。
            if (_autoStarted.Contains(c.Id) && CurrentStep is { } next && IsAutomatic(next.Action))
            {
                await RunAutomaticAsync(c);
            }
        }
    }

    /// <summary>
    /// 準備ボタン: いまの段階から、手で行う段階 (または手順) に来るまで、自動の段階 (仮想ディスク・ポータブル版・インストーラ版・準備) を順に行う。
    /// 済んでいる段階は飛ばす。失敗したらそこで止め、理由と「もう一度」を出す。
    /// </summary>
    private async Task RunAutomaticAsync(TestCase c)
    {
        if (_busy)
        {
            return;
        }

        GuidePlan plan = PrepGuide.Plan(_book, c);
        _busy = true;
        _runningCase = c.Id;
        _autoStarted.Add(c.Id);
        _failed.Remove(c.Id);
        Status = string.Empty;
        try
        {
            while (true)
            {
                GuideStep step = plan.Steps[CurrentStepIndex(c, plan)];
                if (!IsAutomatic(step.Action))
                {
                    break;
                }

                _runningKey = step.Key;
                UpdateGuide(force: true);
                string? error = step.Action switch
                {
                    GuideAction.MakeVirtualDisks => await VirtualDisksCoreAsync(dismount: false),
                    GuideAction.BuildPortable => await BuildCoreAsync("Portable"),
                    GuideAction.BuildInstaller => await BuildCoreAsync("Installer"),
                    GuideAction.HideFiles => Preparer.HideFiles(_book.For(c.Id)),
                    _ => Prepare(c),
                };
                if (error is null && !IsDone(c, step))
                {
                    error = $"「{step.Title}」が済んだことを確かめられませんでした。";
                }

                if (error is not null)
                {
                    // 理由は「いま行うこと」に出すので、進み具合の文は消す。
                    _failed[c.Id] = error;
                    Status = string.Empty;
                    break;
                }
            }
        }
        finally
        {
            _busy = false;
            _runningCase = null;
            _runningKey = null;
            Refresh();
            UpdateGuide(force: true);
        }
    }

    /// <summary>機材などがないとき: 結果を「対象外」(または「保留」) にして次のテストケースへ。</summary>
    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentStep is not { SkipOutcome: { } outcome } step)
        {
            return;
        }

        if (Comment.Text.Trim().Length == 0)
        {
            if (step.SkipComment is null)
            {
                Status = $"下の「コメント」に理由 (テストケースの前提にある条件など) を書いてから、もう一度「{SkipButton.Content}」を押してください。";
                Comment.Focus(FocusState.Programmatic);
                return;
            }

            Comment.Text = step.SkipComment;
        }

        Record(outcome == "保留" ? Outcome.Hold : Outcome.NotApplicable);
    }

    private async void RedoPrepare_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } c || _busy)
        {
            return;
        }

        _prepared.Remove(c.Id);
        await RunAutomaticAsync(c);
    }

    private async void Rebuild_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateGuide(force: true);
        try
        {
            string? error = await BuildCoreAsync(Rebuild.Tag is GuideAction.BuildInstaller ? "Installer" : "Portable");
            if (error is not null)
            {
                Status = "✖ " + error;
            }
        }
        finally
        {
            _busy = false;
            UpdateGuide(force: true);
        }
    }

    // ---- 準備の手順 ----

    /// <summary>
    /// 準備の手順の欄を作る。<paramref name="outcome"/> が null なら準備ボタンで行うことを「・」で、準備の後は行ったことを「✓」で示す。
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
                + (publish ? "公開・署名がまだなら「保留」を押してください。" : "なければ「対象外」を押してください (下の「用意のしかた」に書き方があります)。"), strong: true);
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
                AddText("✓ " + line);
            }

            foreach (string line in outcome.Problems)
            {
                AddText("✖ " + line, strong: true);
            }
        }

        AddHeading("2. テストケースの前提");
        AddText(c.Precondition.Length > 0 ? c.Precondition : "(なし)");

        AddHeading("3. 用意のしかた (上の「いま行うこと」が 1 つずつ案内します)");
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
            AddText("「準備」を押すだけで、前提の状態になります。");
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
                : $"{id} は作れない (「用意のしかた」を見る)";
        }

        if (prep.Settings is { } settings)
        {
            yield return "テスト用の設定を書く: " + settings;
        }

        if (prep.CopyTo == "デスクトップ")
        {
            yield return "テストデータをデスクトップに置く";
        }

        if (prep.HiddenFiles.Count > 0)
        {
            yield return $"補助プロセス ({string.Join("、", prep.HiddenFiles)}) を一時的に隠す (名前を .bak に変える。結果を押す・別のテストケースを選ぶ・ツールを閉じると自動で戻す)";
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

        // 結果を付けたら、隠したファイルを戻す。
        _restoreNote = Preparer.RestoreHidden(_book, keepCase: null);
        // 次のテストケースへ進む (「未実施のもの」で選んでいると、結果を付けたものは一覧から消える)。
        var before = (CaseList.ItemsSource as List<CaseItem>) ?? [];
        string? next = before.SkipWhile(i => i.Case.Id != c.Id).Skip(1).FirstOrDefault()?.Case.Id;
        Refresh();
        var after = (CaseList.ItemsSource as List<CaseItem>) ?? [];
        int target = next is null ? -1 : after.FindIndex(i => i.Case.Id == next);
        CaseList.SelectedIndex = target >= 0 ? target : after.FindIndex(i => i.Case.Id == c.Id);
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
        text.AppendLine("いま行うこと: " + GuideTitle.Text);
        foreach (TextBlock line in GuideLines.Children.OfType<TextBlock>())
        {
            text.AppendLine(line.Text);
        }

        foreach (TextBlock line in Checklist.Children.OfType<TextBlock>())
        {
            text.AppendLine(line.Text);
        }

        text.AppendLine();
        text.AppendLine("詳しい説明:");
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
        Status = "テストケースの内容をコピーしました。";
    }

    // ---- 選び方の保存 ----

    private static string FilterChoicePath => Path.Combine(RunStore.Folder, "filter-choice.txt");

    private static string HideEquipmentPath => Path.Combine(RunStore.Folder, "hide-equipment.txt");

    private static string TopMostPath => Path.Combine(RunStore.Folder, "top-most.txt");

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
        if (!_keepingSelection && CaseList.SelectedItem is CaseItem item)
        {
            Show(item.Case);
        }
    }

    // ---- 準備 ----

    /// <summary>
    /// 前のテストケースで起動した HexEditor を閉じる (テストケースごとにウィンドウが増えないように)。ふつうに閉じるだけで強制終了はしないので、
    /// 保存の確認が出たときなどは開いたままになる。
    /// </summary>
    private void CloseLastHexEditor()
    {
        try
        {
            if (_hexEditor is { HasExited: false } last && last.CloseMainWindow())
            {
                last.WaitForExit(2000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // すでに終わっている・権限がなくて触れない (管理者として動いているなど): そのままにする。
        }
    }

    /// <summary>
    /// テストデータと設定フォルダを作り、HexEditor を起動する。起動できたら (起動しないテストケースはテストデータを作れたら) 準備の段階を「済み」にし、
    /// null を返す。できなければ理由を返す。
    /// </summary>
    private string? Prepare(TestCase c)
    {
        CloseLastHexEditor();
        CasePrep prep = _book.For(c.Id);
        PrepareOutcome outcome = Preparer.Prepare(c, prep);
        _hexEditor = outcome.HexEditor ?? _hexEditor;
        _outcomes[c.Id] = outcome;
        BuildPrepPanel(c, outcome);
        bool ready = outcome.Setup is not null && (prep.Launch == LaunchKind.None || outcome.Launched);
        if (!ready)
        {
            return outcome.Problems.Count > 0 ? string.Join(Environment.NewLine, outcome.Problems) : "HexEditor を起動できませんでした。";
        }

        _prepared.Add(c.Id);
        var text = new StringBuilder("✓ 準備が終わりました。");
        foreach (string problem in outcome.Problems)
        {
            text.AppendLine().Append("注意: ").Append(problem);
        }

        Status = text.ToString();
        return null;
    }

    private async void RemoveVhd_Click(object sender, RoutedEventArgs e) => await RunVirtualDisksAsync(dismount: true);

    /// <summary>仮想ディスクを外す (報告のダイアログと「仮想ディスクを外す」から)。</summary>
    private async Task RunVirtualDisksAsync(bool dismount)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateGuide(force: true);
        try
        {
            string? error = await VirtualDisksCoreAsync(dismount);
            if (error is not null)
            {
                Status = "✖ " + error;
            }
        }
        finally
        {
            _busy = false;
            Refresh();
            UpdateGuide(force: true);
        }
    }

    /// <summary>仮想ディスクを作る・外す (管理者の確認が出る)。成功したら null、できなければ理由。</summary>
    private async Task<string?> VirtualDisksCoreAsync(bool dismount)
    {
        Status = dismount
            ? "仮想ディスクを外しています。Windows の確認が出たら「はい」を押してください..."
            : "仮想ディスクを作っています (1 分ほどかかります)...";
        string? log = await Preparer.RunVirtualDiskScriptAsync(dismount);
        if (log is null)
        {
            return "Windows の確認で「いいえ」が押されたため、仮想ディスクを作れませんでした。";
        }

        Status = Preparer.DescribeVirtualDiskLog(log) + Environment.NewLine + $"(ログ: {Preparer.VhdLog})";
        return log.Contains("ERROR:", StringComparison.Ordinal) ? Preparer.DescribeVirtualDiskLog(log) : null;
    }

    /// <summary>ポータブル版・インストーラ版を作る (数分かかる)。進み具合は「いま行うこと」の下に出す。成功したら null、できなければ理由。</summary>
    private async Task<string?> BuildCoreAsync(string distro)
    {
        CloseLastHexEditor();
        string name = distro == "Portable" ? "ポータブル版" : "インストーラ版";
        Status = $"{name}を作っています (数分かかります。このツールは閉じないでください)...";
        var progress = new Progress<string>(line => Status = $"{name}を作っています (数分かかります)...{Environment.NewLine}{line}");
        try
        {
            DateTime started = DateTime.UtcNow;
            string? error = await Preparer.BuildDistributionAsync(distro, progress);
            if (error is not null)
            {
                return error;
            }

            Preparer.MarkBuilt(distro, started);

            Status = distro == "Portable"
                ? $"✓ ポータブル版を作りました: {Preparer.PortableExe}{Environment.NewLine}zip: {Preparer.ArtifactsFolder(distro)}"
                : $"✓ インストーラ版をインストールしました: {Preparer.InstallerExe}{Environment.NewLine}Setup.exe: {Preparer.ArtifactsFolder(distro)}";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or InvalidDataException)
        {
            return $"{name}を作れませんでした: {ex.Message}";
        }
    }

    /// <summary>記録した結果のボタンだけを強調する (何も記録していなければ、どれも強調しない。押す前から選ばれているように見せない)。</summary>
    private void ShowRecordedOutcome(Outcome outcome)
    {
        var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
        foreach ((Button button, Outcome value) in new[] { (PassButton, Outcome.Pass), (FailButton, Outcome.Fail), (HoldButton, Outcome.Hold), (NotApplicableButton, Outcome.NotApplicable) })
        {
            button.Style = value == outcome ? accent : null;
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

        // 隠したファイルは結果を付けたときに自動で戻る。戻っていなければ、もう一度戻してみて、それでも戻らなければ知らせる (念のため)。
        Preparer.RestoreHidden(_book, keepCase: null);
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

        if (_restored && !DevOptions.NoActivate)
        {
            SaveChoice(TopMostPath, TopMost.IsChecked == true ? 1 : 0);
        }
    }
}
