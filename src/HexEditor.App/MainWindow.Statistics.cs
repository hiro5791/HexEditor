using System.Collections.Specialized;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.FileTypes;
using HexEditor.Core.Hashing;
using HexEditor.Core.Panels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace HexEditor.App;

/// <summary>
/// 統計パネル (ANA-10〜ANA-16、既定は下) と「ファイル形式」パネル (ANA-17、既定は右) の組み込み。状態 (<see cref="StatisticsViewModel"/>、
/// <see cref="FileTypeViewModel"/>) はウィンドウごとに 1 つで、パネルの中身 (浮動パネルとの間を移るたびに作り直す) はそれを共有する。
/// </summary>
public sealed partial class MainWindow
{
    public const string StatisticsPanelId = "statistics";
    public const string FileTypePanelId = "fileType";

    private StatisticsViewModel? _statsVm;
    private StatisticsPanel? _statsPanel;
    private FileTypeViewModel? _fileTypeVm;
    private FileTypePanel? _fileTypePanel;

    /// <summary>「ここから展開...」(ANA-16 の仕様 5) の行き先 (03 の展開機能 F4-04、フェーズ 4)。null なら無効。</summary>
    public static Func<MainWindow, long, string, Task>? DecompressHook { get; set; }

    public bool IsStatisticsPanelOpen => IsPanelShown(StatisticsPanelId);

    public bool IsFileTypePanelOpen => IsPanelShown(FileTypePanelId);

    /// <summary>パネルの一覧に登録し、シグネチャデータベース (内蔵 + 設定フォルダの magic/) を読む (アプリの起動時、ウィンドウを作る前に 1 度)。</summary>
    public static void RegisterStatisticsPanels(string settingsFolder)
    {
        if (PanelRegistry.Find(StatisticsPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(StatisticsPanelId, "Panel_Statistics_Title", PanelDock.Bottom,
                ctx => ((MainWindow)ctx.Window).CreateStatisticsPanel()));
            PanelRegistry.Register(new PanelRegistration(FileTypePanelId, "Panel_FileType_Title", PanelDock.Right,
                ctx => ((MainWindow)ctx.Window).CreateFileTypePanel()));
        }

        // 利用者のデータベースの構文エラーは読み飛ばし、起動時の InfoBar で知らせる (ANA-17 の「エラー」)。
        FileTypeDatabase database = FileTypeDatabase.WithUserFolder(Path.Combine(settingsFolder, FileTypeDatabase.UserFolderName));
        FileTypeViewModel.Detector = new FileTypeDetector(database);
        foreach (MagicLoadError error in database.Errors)
        {
            StartupNotices.Add(new StartupNotice("FileType_MagicError", StartupNoticeSeverity.Warning, [error.FileName, error.Line]));
        }
    }

    private StatisticsViewModel StatsVm => _statsVm ??= CreateStatisticsViewModel();

    private FileTypeViewModel FileTypeVm => _fileTypeVm ??= new FileTypeViewModel(Vm.Operations)
    {
        GoTo = GoToFromAnalysis,
        Annotations = AnalysisAnnotations,
    };

    private StatisticsViewModel CreateStatisticsViewModel() => new(Vm.Operations, App.Settings)
    {
        SetClipboardText = text =>
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            SystemClipboard.SetContent(package);
        },
        PickSavePath = PickStatisticsSavePathAsync,
        GoTo = GoToFromAnalysis,
        Select = (start, length) =>
        {
            Editor?.Select(start, length);
            FocusEditor();
        },
        SearchNext = SearchBytesAsync,
        FindAll = FindAllBytesAsync,
        ShowRecordView = ShowRecordViewOfLength,
        OpenDecompress = DecompressHook is { } decompress ? (offset, format) => decompress(this, offset, format) : null,

        // マルチ選択 (EDIT-07) の要素、ドキュメントのエンディアン (VIEW-11)、共通の注釈レイヤー (INSP-32)。
        MultiSelectionOf = editor => editor.HasMultipleRanges
            ? [.. editor.SelectedRanges.Where(r => r.Length > 0).Select(r => new HashRange(r.Start, r.Length))]
            : [],
        DocumentBigEndian = doc => doc.Editor.View.BigEndian,
        Annotations = AnalysisAnnotations,
    };

    /// <summary>解析の注釈 (分類・埋め込まれた形式) を共通の注釈レイヤー (INSP-32) に載せる口。</summary>
    private static readonly Core.Statistics.IAnnotationSink AnalysisAnnotations = new Core.Statistics.AnnotationLayerSink();

    /// <summary>「この長さでレコード表示」(ANA-15 の仕様 4): レコード表示 (VIEW-18) を、見つかった周期のレコード長でオンにする。</summary>
    private void ShowRecordViewOfLength(long length)
    {
        if (Editor is not { } editor || length < 1 || length > int.MaxValue)
        {
            return;
        }

        editor.ApplyView(editor.View with
        {
            RecordView = true,
            RecordLength = (int)length,
            RecordPerRow = length <= Core.View.ViewSettings.MaxBytesPerRow,
        });
        UpdateViewMenu();
        QueueStatusBarLayout();
        FocusEditor();
    }

    private StatisticsPanel CreateStatisticsPanel()
    {
        var panel = new StatisticsPanel(StatsVm);
        AutomationProperties.SetAutomationId(panel, "StatisticsPanel");
        _statsPanel = panel;
        return panel;
    }

    private FileTypePanel CreateFileTypePanel()
    {
        var panel = new FileTypePanel(FileTypeVm) { EmbeddedRanges = EmbeddedRanges };
        AutomationProperties.SetAutomationId(panel, "FileTypePanel");
        _fileTypePanel = panel;
        return panel;
    }

    private IReadOnlyList<HashRange> EmbeddedRanges() =>
        Editor is { HasSelection: true } e ? [new HashRange(e.SelectionStart, e.SelectionLength)] : [];

    private void RegisterStatisticsCommands()
    {
        Commands.Register("analysis.statistics", () => ShowStatistics(StatsTab.Histogram), NeedsDocument);
        Commands.Register("analysis.descriptive", () => ShowStatistics(StatsTab.Descriptive), NeedsDocument);
        Commands.Register("analysis.entropy", () => ShowStatistics(StatsTab.Entropy), NeedsDocument);
        Commands.Register("analysis.entropyGraph", () => ShowStatistics(StatsTab.Entropy, focusGraph: true), NeedsDocument);
        Commands.Register("analysis.digram", () => ShowStatistics(StatsTab.Digram, digramMode: 0), NeedsDocument);
        Commands.Register("analysis.byteDistribution", () => ShowStatistics(StatsTab.Digram, digramMode: 1), NeedsDocument);
        Commands.Register("analysis.patterns", () => ShowStatistics(StatsTab.Pattern), NeedsDocument);
        Commands.Register("analysis.classify", async () =>
        {
            ShowStatistics(StatsTab.Classify);
            await StatsVm.ClassifyAsync();
        }, NeedsDocument);
        Commands.Register("analysis.fileType", async () =>
        {
            ShowFileTypePanel();
            await FileTypeVm.DetectAsync();
        }, NeedsDocument);
        Commands.Register("analysis.fileType.here", async () =>
        {
            ShowFileTypePanel();
            await FileTypeVm.DetectHereAsync();
        }, NeedsDocument);
        Commands.Register("analysis.fileType.embedded", async () =>
        {
            ShowFileTypePanel();
            await FileTypeVm.FindEmbeddedAsync(EmbeddedRanges());
        }, NeedsDocument);

        // 作業中の文書が変わったら、パネルの対象とステータスバーの形式表示を切り替える。
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Selected))
            {
                SyncStatisticsTargets();
                UpdateFileTypeStatus();
            }
        };

        // 開いたドキュメントの形式をバックグラウンドで判定する (ANA-17 の仕様 4)。
        Vm.Documents.CollectionChanged += Documents_CollectionChangedForFileType;
        FileTypeViewModel.Detected += FileType_Detected;
        Closed += (_, _) =>
        {
            FileTypeViewModel.Detected -= FileType_Detected;
            Vm.Documents.CollectionChanged -= Documents_CollectionChangedForFileType;
        };
        foreach (DocumentViewModel doc in Vm.Documents)
        {
            StartFileTypeDetection(doc);
        }
    }

    private void Documents_CollectionChangedForFileType(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (DocumentViewModel doc in e.NewItems?.OfType<DocumentViewModel>() ?? [])
        {
            StartFileTypeDetection(doc);
        }
    }

    private void StartFileTypeDetection(DocumentViewModel doc)
    {
        if (App.Settings.GetBool(FileTypeViewModel.AutoDetectKey, true) && FileTypeViewModel.ReportOf(doc.Document) is null)
        {
            FileTypeViewModel.DetectInBackground(doc.Document, doc.FilePath);
        }
    }

    private void FileType_Detected(object? sender, Core.Engine.Document document) => DispatcherQueue.TryEnqueue(() =>
    {
        if (Vm.Selected?.Document == document)
        {
            UpdateFileTypeStatus();
            _fileTypeVm?.Refresh();
        }
    });

    /// <summary>ステータスバーの形式表示 (例: PNG image)。クリックで「ファイル形式」パネルを開く (ANA-17 の「画面」)。</summary>
    private void UpdateFileTypeStatus()
    {
        string text = Vm.Selected is { } doc ? FileTypeViewModel.StatusText(FileTypeViewModel.ReportOf(doc.Document)) : string.Empty;
        if (!Equals(StatusFileType.Content, text))
        {
            StatusFileType.Content = text;
            AutomationProperties.SetName(StatusFileType, text.Length == 0 ? string.Empty : Loc.Format("Status_FileTypeName", text));
            UpdateStatusBarLayout();
        }
    }

    private void StatusFileType_Click(object sender, RoutedEventArgs e) => ShowFileTypePanel();

    /// <summary>統計パネルを表示してタブを選ぶ。</summary>
    public void ShowStatistics(StatsTab tab, bool focusGraph = false, int? digramMode = null)
    {
        ShowPanel(StatisticsPanelId);
        SyncStatisticsTargets();
        StatsVm.Tab = tab;
        if (digramMode is int mode)
        {
            StatsVm.DigramMode = mode;
        }

        if (StatsVm.Result is null && StatsVm.IsIdle)
        {
            _ = StatsVm.ComputeAsync();
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            _statsPanel?.ShowTab(tab);
            if (focusGraph)
            {
                _statsPanel?.EntropyChartControl.Focus(FocusState.Programmatic);
            }
            else
            {
                _statsPanel?.FocusContent();
            }
        });
    }

    public void ShowFileTypePanel()
    {
        ShowPanel(FileTypePanelId);
        SyncStatisticsTargets();
    }

    /// <summary>パネルの対象を作業中の文書にする (パネルの配置・作業中の文書が変わるたびに呼ぶ)。閉じている間は追わない。</summary>
    private void SyncStatisticsTargets()
    {
        if (_statsVm is not null)
        {
            _statsVm.Target = IsStatisticsPanelOpen ? Vm.Selected : null;
        }

        if (_fileTypeVm is not null)
        {
            _fileTypeVm.Target = IsFileTypePanelOpen ? Vm.Selected : null;
        }
    }

    /// <summary>解析の結果から位置へ移動する (ジャンプとして記録し、Hex ビューにフォーカスを戻す)。</summary>
    private void GoToFromAnalysis(long offset)
    {
        if (Editor is { } editor)
        {
            editor.GoTo(Math.Clamp(offset, 0, Math.Max(0, editor.Document.Length - 1)));
            FocusEditor();
        }
    }

    /// <summary>Hex のバイト列を検索バーに入れて次を検索する (ANA-14 の仕様 4)。</summary>
    private async Task SearchBytesAsync(byte[] bytes)
    {
        OpenFindBar(replace: false);
        FindBar.SetHexQuery(bytes);
        await FindBar.FindAsync(forward: true);
    }

    /// <summary>Hex のバイト列で 04 のすべて検索を実行する (ANA-10 の仕様 8、ANA-15 の仕様 2)。</summary>
    private async Task FindAllBytesAsync(byte[] bytes)
    {
        OpenFindBar(replace: false);
        FindBar.SetHexQuery(bytes);
        await FindBar.FindAllAsync();
    }

    private async Task<string?> PickStatisticsSavePathAsync(string suggestedName, string extension)
    {
        if (TestHooks.TrySavePicker(suggestedName, out string? chosen))
        {
            return chosen;
        }

        var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggestedName, SettingsIdentifier = "HexEditor.StatisticsSave" };
        picker.FileTypeChoices.Add(Loc.Get("FileType_All"), [extension]);
        return (await picker.PickSaveFileAsync())?.Path;
    }

    // ---- Hex ビューの右クリックメニュー (ANA-10「統計」、ANA-17「ここからの形式を判定」の「呼び出し」) ----

    private void ExtendHexViewStatisticsMenu(MenuFlyout menu)
    {
        const string StatsId = "HexViewMenu_Statistics";
        const string FormatId = "HexViewMenu_FileTypeHere";
        if (!menu.Items.Any(i => AutomationProperties.GetAutomationId(i) == StatsId))
        {
            var stats = new MenuFlyoutItem { Text = Loc.Get("HexView_Menu_Statistics"), Tag = "Statistics" };
            AutomationProperties.SetAutomationId(stats, StatsId);
            stats.Click += (_, _) => _ = Commands.ExecuteAsync("analysis.statistics");
            var format = new MenuFlyoutItem { Text = Loc.Get("HexView_Menu_FileTypeHere"), Tag = "FileTypeHere" };
            AutomationProperties.SetAutomationId(format, FormatId);
            format.Click += (_, _) => _ = Commands.ExecuteAsync("analysis.fileType.here");
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(stats);
            menu.Items.Add(format);
        }

        foreach (MenuFlyoutItemBase item in menu.Items)
        {
            if (AutomationProperties.GetAutomationId(item) is StatsId)
            {
                ((MenuFlyoutItem)item).IsEnabled = Editor?.HasSelection == true;
            }
        }
    }
}
