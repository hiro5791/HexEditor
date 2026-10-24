using System.Globalization;
using HexEditor.App.Commands;
using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.App.ViewModels;
using HexEditor.Core.Editing.Codecs;
using HexEditor.Core.FileMap;
using HexEditor.Core.Operations;
using HexEditor.Core.Panels;
using HexEditor.Core.Selection;
using HexEditor.Core.Statistics;
using HexEditor.Core.Templates;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// ファイルマップ (ANA-33・ANA-34) の組み込み: パネル (既定は下)、コマンド、作成 (処理センターの長時間処理)、領域の操作、エクスポート、
/// 埋め込まれたファイルへのテンプレートの提案 (InfoBar)、ミニマップの印。結果はドキュメントごとに <see cref="FileMapStore"/> が持つ
/// (自動では作らない: 作るのはファイルマップを作成する操作だけ。利用者の決定 2026-10-21)。並べ替え・絞り込み・選んだ領域はウィンドウごと。
/// </summary>
public sealed partial class MainWindow
{
    public const string FileMapPanelId = "fileMap";

    /// <summary>設定「埋め込まれたファイルのテンプレート」(提案する / しない)。</summary>
    public const string FileMapTemplatesKey = "fileMap.applyTemplatesToEmbedded";

    /// <summary>テンプレートの推定 (07 の TPL-38) のコマンド (引数 start・length・recordLength)。</summary>
    public const string TemplateInferCommand = "templates.infer";

    private FileMapPanel? _fileMapPanel;
    private int[] _fileMapRows = [];
    private FileMapResult? _fileMapRowsFor;
    private FileMapKind? _fileMapFilter;
    private (int Column, bool Descending)? _fileMapSort;
    private int _fileMapSelected = -1;
    private bool _fileMapRefreshQueued;
    private Core.Engine.Document? _fileMapDocument;

    /// <summary>パネルの一覧に登録する (アプリの起動時、ウィンドウを作る前に 1 度)。</summary>
    public static void RegisterFileMapPanel()
    {
        if (PanelRegistry.Find(FileMapPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(FileMapPanelId, "Panel_FileMap_Title", PanelDock.Bottom,
                ctx => ((MainWindow)ctx.Window).CreateFileMapPanel()));
        }

        // ミニマップの「ファイルマップ」の印 (ANA-34 の仕様 8) が読む。
        MinimapView.FileMapOf = FileMapStore.ResultOf;
    }

    private FileMapPanel CreateFileMapPanel()
    {
        _fileMapPanel = new FileMapPanel(this);
        return _fileMapPanel;
    }

    internal EditorState? FileMapEditor => Vm.Selected?.Editor;

    internal FileMapState? CurrentFileMapState => Vm.Selected is { } doc ? FileMapStore.Get(doc.Document) : null;

    internal FileMapResult? CurrentFileMap => CurrentFileMapState?.Result;

    internal FileMapKind? FileMapFilter => _fileMapFilter;

    internal int FileMapSelected => CurrentFileMap is { } map && _fileMapSelected < map.Regions.Count ? _fileMapSelected : -1;

    private void RegisterFileMapCommands()
    {
        Commands.Register("analysis.fileMap", () => BuildFileMapAsync(rebuild: false), NeedsDocument);
        Commands.Register("analysis.fileMap.rebuild", () => BuildFileMapAsync(rebuild: true), NeedsDocument);
        Commands.Register("analysis.fileMap.show", () => ShowPanel(FileMapPanelId));
        Commands.Register("analysis.fileMap.export", () => ExportFileMapAsync(FileMapExportFormat.Json),
            () => NeedsDocument(d => FileMapStore.ResultOf(d.Document) is null || FileMapStore.Get(d.Document)!.IsBuilding ? Loc.Get("FileMap_Reason_NoMap") : null));
        Commands.Register("analysis.fileMap.applyTemplates", ApplyFileMapTemplates,
            () => NeedsDocument(d => FileMapStore.ResultOf(d.Document) is { } m && m.EmbeddedWithTemplate.Any() ? null : Loc.Get("FileMap_Reason_NoTemplates")));

        FileMapStore.Changed += FileMapStore_Changed;
        Vm.PropertyChanged += FileMapVm_PropertyChanged;
        Closed += (_, _) =>
        {
            FileMapStore.Changed -= FileMapStore_Changed;
            Vm.PropertyChanged -= FileMapVm_PropertyChanged;
        };
    }

    private void FileMapVm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Selected))
        {
            QueueFileMapRefresh();
        }
    }

    private void FileMapStore_Changed(object? sender, Core.Engine.Document document)
    {
        if (Vm.Documents.Any(d => d.Document == document))
        {
            QueueFileMapRefresh();
        }
    }

    /// <summary>パネルとミニマップを描き直す (結果が 0.5 秒ごとに変わっても、UI スレッドには 1 回にまとめて出す)。</summary>
    private void QueueFileMapRefresh()
    {
        if (_fileMapRefreshQueued)
        {
            return;
        }

        _fileMapRefreshQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _fileMapRefreshQueued = false;
            RefreshFileMapUi();
        });
    }

    private void RefreshFileMapUi()
    {
        Core.Engine.Document? document = Vm.Selected?.Document;
        if (!ReferenceEquals(document, _fileMapDocument))
        {
            _fileMapDocument = document;
            _fileMapSelected = -1;
            _fileMapRowsFor = null;
        }

        RebuildFileMapRows();
        _fileMapPanel?.Refresh();
        foreach (HexView view in _views)
        {
            view.Minimap?.Redraw();
        }

        RefreshCommandUi();
    }

    // ---- 作成 (ANA-33) ----

    /// <summary>ファイルマップを作る (作っていれば、パネルを開くだけ。<paramref name="rebuild"/> ならすべての段階をやり直す)。</summary>
    private async Task BuildFileMapAsync(bool rebuild)
    {
        if (Vm.Selected is not { } doc)
        {
            return;
        }

        ShowPanel(FileMapPanelId, focus: false);
        FileMapState state = FileMapStore.GetOrCreate(doc.Document);
        if (state.IsBuilding || (!rebuild && state.Result is { } existing && ReferenceEquals(existing.Snapshot, doc.Document.Current)))
        {
            QueueFileMapRefresh();
            return;
        }

        Core.Engine.DocumentSnapshot snapshot = doc.Document.Current;
        var request = new FileMapRequest
        {
            Templates = TemplateService.Catalog,
            FileName = doc.FilePath ?? doc.DisplayName,
            Database = FileTypeViewModel.Detector.Database,
            Thresholds = ClassifyThresholds(),
            TransformDescription = doc.Document.ViewTransform is { } t ? HexView.TransformViewLabel(t) : null,
            Existing = DataClassifier.LatestFor(doc.Document) is { Completed: true } latest && ReferenceEquals(latest.Snapshot, snapshot) ? latest : null,
        };
        state.Error = null;
        state.OverviewElapsed = null;
        state.StartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        state.TransformLabel = doc.Document.ViewTransform is { } transform ? HexView.TransformViewLabel(transform) : null;
        state.RunningStage = FileMapStage.Sample;
        AppLog.Info($"File map: start ({snapshot.Length} bytes)");
        try
        {
            FileMapResult result = await Vm.Operations.RunAsync(Loc.Get("Operation_FileMap"), OperationKind.ReadOnly, doc.Document, snapshot.Length, op =>
            {
                state.Operation = op;
                op.ReportDetail(Loc.Get("FileMap_StageName_Sample"));
                FileMapStore.Raise(doc.Document);
                var callbacks = new FileMapCallbacks
                {
                    StageStarted = stage =>
                    {
                        state.RunningStage = stage;
                        op.ReportDetail(Loc.Get("FileMap_StageName_" + stage));
                        FileMapStore.Raise(doc.Document);
                    },
                    Partial = partial =>
                    {
                        if (state.OverviewElapsed is null && partial.Stages.Sample == FileMapStageState.Done)
                        {
                            state.OverviewElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(state.StartedAt);
                        }

                        state.Result = partial;
                        FileMapStore.Raise(doc.Document);
                    },
                };

                // Hex ビューの表示の読み込みを優先する (作っている間もスクロールを滑らかに保つ)。
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                try
                {
                    FileMapResult r = FileMapBuilder.Build(snapshot, request, op, callbacks);
                    state.Result = r;
                    op.CancellationToken.ThrowIfCancellationRequested();
                    return Task.FromResult(r);
                }
                finally
                {
                    Thread.CurrentThread.Priority = ThreadPriority.Normal;
                }
            });
            AppLog.Info($"File map: done ({result.Regions.Count} regions)");
            OnFileMapCompleted(doc, result);
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("File map: cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException)
        {
            state.Error = Loc.Format("FileMap_Failed", ex.Message);
            AppLog.Warning($"File map: {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            state.Operation = null;
            state.RunningStage = null;
            FileMapStore.Raise(doc.Document);
        }
    }

    /// <summary>詳細設定のしきい値 (ANA-16 の仕様 6。統計パネルと同じ)。</summary>
    private static ClassThresholds ClassifyThresholds()
    {
        var t = new ClassThresholds();
        const string Prefix = "analysis.classify.";
        return new ClassThresholds
        {
            ConstantShare = App.Settings.GetDouble(Prefix + "constantShare", t.ConstantShare),
            TextShare = App.Settings.GetDouble(Prefix + "textShare", t.TextShare),
            EncryptedEntropy = App.Settings.GetDouble(Prefix + "encryptedEntropy", t.EncryptedEntropy),
            PValueLow = App.Settings.GetDouble(Prefix + "pValueLow", t.PValueLow),
            PValueHigh = App.Settings.GetDouble(Prefix + "pValueHigh", t.PValueHigh),
            CompressedEntropy = App.Settings.GetDouble(Prefix + "compressedEntropy", t.CompressedEntropy),
        };
    }

    private void OnFileMapCompleted(DocumentViewModel doc, FileMapResult result)
    {
        // 仕様 5: 全体の走査の分類を ANA-16 の結果として登録する (統計パネルの「分類」タブとミニマップは読み直さずに使う)。
        if (result.Classification is { } classes && result.Start == 0 && result.Length == doc.Document.Length)
        {
            DataClassifier.Remember(doc.Document, classes);
        }

        // ANA-34 の仕様 7: テンプレートの決まった埋め込まれたファイルがあれば提案する (自動では適用しない)。
        int count = result.EmbeddedWithTemplate.Count();
        if (count > 0 && App.Settings.GetString(FileMapTemplatesKey, "suggest") == "suggest")
        {
            Core.Notifications.Notification? notice = null;
            notice = ShowNotice(Loc.Format("FileMap_SuggestTemplates", count), InfoBarSeverity.Informational, doc, actions:
            [
                new Core.Notifications.NotificationAction(Loc.Get("FileMap_SuggestApply"), () =>
                {
                    DismissNotice(notice);
                    ApplyFileMapTemplates();
                }),
                new Core.Notifications.NotificationAction(Loc.Get("FileMap_SuggestList"), () =>
                {
                    DismissNotice(notice);
                    ShowPanel(FileMapPanelId);
                    FilterFileMapRows(FileMapKind.Embedded);
                }),
            ]);
            LastFileMapSuggestion = notice;
        }
    }

    /// <summary>最後に出したテンプレートの提案 (テスト用)。</summary>
    internal Core.Notifications.Notification? LastFileMapSuggestion { get; private set; }

    internal void CancelFileMap() => CurrentFileMapState?.Operation?.Cancel();

    // ---- 一覧 (並べ替え・絞り込み) ----

    internal long FileMapRowCount => _fileMapRows.Length;

    internal int FileMapRowRegion(long row) => row >= 0 && row < _fileMapRows.Length ? _fileMapRows[row] : -1;

    internal long FileMapRowOf(int region) => region < 0 ? -1 : Array.IndexOf(_fileMapRows, region);

    private void RebuildFileMapRows(bool force = false)
    {
        FileMapResult? map = CurrentFileMap;
        if (!force && ReferenceEquals(map, _fileMapRowsFor))
        {
            return;
        }

        _fileMapRowsFor = map;
        if (map is null)
        {
            _fileMapRows = [];
            return;
        }

        IEnumerable<int> rows = Enumerable.Range(0, map.Regions.Count);
        if (_fileMapFilter is { } kind)
        {
            rows = rows.Where(i => map.Regions[i].Kind == kind);
        }

        int[] list = [.. rows];
        if (_fileMapSort is (int column, bool descending))
        {
            Comparison<int> compare = column switch
            {
                0 => (a, b) => map.Regions[a].Offset.CompareTo(map.Regions[b].Offset),
                1 => (a, b) => map.Regions[a].End.CompareTo(map.Regions[b].End),
                2 => (a, b) => map.Regions[a].Length.CompareTo(map.Regions[b].Length),
                3 => (a, b) => string.Compare(FileMapVisuals.KindName(map.Regions[a].Kind), FileMapVisuals.KindName(map.Regions[b].Kind), StringComparison.CurrentCulture),
                4 => (a, b) => string.Compare(FileMapVisuals.Label(map, a), FileMapVisuals.Label(map, b), StringComparison.CurrentCulture),
                5 => (a, b) => map.Regions[a].Confidence.CompareTo(map.Regions[b].Confidence),
                _ => (a, b) => map.Regions[a].Stage.CompareTo(map.Regions[b].Stage),
            };
            Comparison<int> stable = (a, b) => compare(a, b) is var c && c != 0 ? c : a.CompareTo(b);
            Array.Sort(list, descending ? (a, b) => stable(b, a) : stable);
        }

        _fileMapRows = list;
    }

    internal void SortFileMapRows(int column)
    {
        _fileMapSort = _fileMapSort is (int c, bool d) && c == column ? (column, !d) : (column, false);
        RebuildFileMapRows(force: true);
        _fileMapPanel?.Refresh();
    }

    internal void FilterFileMapRows(FileMapKind? kind)
    {
        _fileMapFilter = kind;
        RebuildFileMapRows(force: true);
        _fileMapPanel?.Refresh();
    }

    /// <summary>領域を選ぶ (帯と一覧の選択を合わせる)。</summary>
    internal void FileMapSelect(int region, bool fromBand)
    {
        if (region == _fileMapSelected)
        {
            return;
        }

        _fileMapSelected = region;
        _fileMapPanel?.Refresh();
    }

    // ---- 帯の操作 (ANA-34 の仕様 4) ----

    /// <summary>クリック: その位置へカーソルを移す (VIEW-34。ジャンプ履歴に記録する)。</summary>
    internal void FileMapNavigate(long offset)
    {
        if (FileMapEditor is { } editor)
        {
            editor.GoTo(Math.Clamp(offset, 0, Math.Max(0, editor.Document.Length - 1)));
        }
    }

    /// <summary>ダブルクリック・Enter: 領域の先頭へ移動してその範囲を選ぶ。</summary>
    internal void FileMapSelectRange(int region)
    {
        if (FileMapEditor is not { } editor || CurrentFileMap is not { } map || region < 0 || region >= map.Regions.Count)
        {
            return;
        }

        FileMapRegion r = map.Regions[region];
        editor.GoTo(r.Offset);
        editor.Select(r.Offset, Math.Min(r.Length, editor.Document.Length - r.Offset));
        FileMapSelect(region, fromBand: true);
    }

    /// <summary>ミニマップのファイルマップの印のクリック: パネルを開き、その位置の領域を選ぶ (仕様 8)。</summary>
    internal void FileMapFromMinimap(long offset)
    {
        if (CurrentFileMap is not { } map)
        {
            return;
        }

        ShowPanel(FileMapPanelId, focus: false);
        if (_fileMapFilter is not null)
        {
            FilterFileMapRows(null);
        }

        FileMapSelect(map.ContentAt(offset), fromBand: false);
    }

    // ---- 領域の操作 (ANA-34 の仕様 6) ----

    /// <summary>その種類の領域で使える操作だけを返す (使いやすさの原則 3)。</summary>
    internal IReadOnlyList<FileMapAction> FileMapActions(int region)
    {
        if (CurrentFileMap is not { } map || region < 0 || region >= map.Regions.Count || Vm.Selected is not { } doc)
        {
            return [];
        }

        FileMapRegion r = map.Regions[region];
        FileMapDetail? detail = r.Detail >= 0 ? map.Details[r.Detail] : null;
        string label = FileMapVisuals.Label(map, region);
        var actions = new List<FileMapAction>
        {
            new("Select", Loc.Get("FileMap_Act_Select"), () =>
            {
                FileMapSelectRange(region);
                return Task.CompletedTask;
            }),
            new("Bookmark", Loc.Get("FileMap_Act_Bookmark"), () =>
            {
                AddFileMapBookmarks(doc, [region]);
                return Task.CompletedTask;
            }),
        };
        if (r.Kind is FileMapKind.Structure or FileMapKind.Embedded && detail?.Template is { } template)
        {
            actions.Add(new("ApplyTemplate", Loc.Format("FileMap_Act_ApplyTemplate", template.DisplayName(CultureInfo.CurrentUICulture)), () =>
            {
                // 領域の開始オフセットで、固定の適用 (TPL-20) を、適用の設定画面を出さずに作る。
                long start = r.Kind == FileMapKind.Structure ? map.Start : r.Offset;
                if (r.Kind == FileMapKind.Structure && r.Parent >= 0)
                {
                    start = map.Regions[r.Parent].Offset;
                }

                AddTemplateApplication(doc, new TemplateApplicationInfo { TemplatePath = template.Path, StartOffset = start });
                return Task.CompletedTask;
            }));
        }

        if (r.Kind is FileMapKind.Embedded or FileMapKind.Compressed or FileMapKind.Text or FileMapKind.Records)
        {
            actions.Add(new("OpenTab", Loc.Get("FileMap_Act_OpenTab"), () =>
            {
                OpenRangeInNewTab(doc, r.Offset, Math.Min(r.Length, doc.Document.Length - r.Offset), label, copy: false);
                return Task.CompletedTask;
            }));
        }

        if (r.Kind == FileMapKind.Compressed && detail?.Format is { } format && CompressionFormatOf(format) is { } compression)
        {
            actions.Add(new("Decompress", Loc.Get("FileMap_Act_Decompress"), () => DecompressFromAsync(r.Offset, r.Length, compression)));
        }

        if (r.Kind == FileMapKind.Records)
        {
            bool available = Commands.HasHandler(TemplateInferCommand);
            actions.Add(new("InferTemplate", Loc.Get("FileMap_Act_InferTemplate"), async () =>
            {
                FileMapLastInferRequest = (r.Offset, r.Length, r.Extra);
                using (CommandArgs.Begin(new Dictionary<string, object?> { ["start"] = r.Offset, ["length"] = r.Length, ["recordLength"] = r.Extra }))
                {
                    await Commands.ExecuteAsync(TemplateInferCommand);
                }
            }, available, available ? null : Loc.Get("FileMap_Reason_NoInference")));
        }

        if (r.Kind == FileMapKind.Text)
        {
            actions.Add(new("Strings", Loc.Get("FileMap_Act_Strings"), () =>
            {
                StringsExtraction.SetRange(r.Offset, r.Last);
                ShowPanel(StringsPanelId);
                return Task.CompletedTask;
            }));
        }

        if (r.Kind == FileMapKind.Embedded)
        {
            string extension = "." + (detail?.Extension ?? "bin");
            actions.Add(new("SaveAs", Loc.Get("FileMap_Act_SaveAs"), () => SaveSelectionAsync((r.Offset, Math.Min(r.Length, doc.Document.Length - r.Offset)), extension)));
        }

        return actions;
    }

    /// <summary>テンプレートの推定に渡した値 (テスト用)。</summary>
    internal (long Start, long Length, int RecordLength)? FileMapLastInferRequest { get; private set; }

    private static CompressionFormat? CompressionFormatOf(string format) => format.ToLowerInvariant() switch
    {
        "zlib" => CompressionFormat.Zlib,
        "gzip" => CompressionFormat.Gzip,
        "zstd" => CompressionFormat.Zstd,
        "xz" => CompressionFormat.Xz,
        "lz4" => CompressionFormat.Lz4Frame,
        _ => null,
    };

    /// <summary>ブックマークに追加: ラベルを名前、種類と根拠をコメントにする。</summary>
    private void AddFileMapBookmarks(DocumentViewModel doc, IReadOnlyList<int> regions)
    {
        if (CurrentFileMap is not { } map)
        {
            return;
        }

        Core.Bookmarks.BookmarkCollection bookmarks = AnnotationsFor(doc).Bookmarks;
        int added = 0;
        try
        {
            foreach (int i in regions)
            {
                FileMapRegion r = map.Regions[i];
                long length = Math.Min(r.Length, doc.Document.Length - r.Offset);
                if (length <= 0)
                {
                    continue;
                }

                Core.Bookmarks.Bookmark b = bookmarks.Add(r.Offset, length, FileMapVisuals.Label(map, i));
                bookmarks.SetComment(b, FileMapVisuals.KindName(r.Kind) + ": " + FileMapVisuals.Evidence(map, i));
                added++;
            }
        }
        catch (Core.Bookmarks.BookmarkLimitException)
        {
            ShowNotice(Loc.Format("Bookmarks_Limit", Core.Bookmarks.BookmarkCollection.MaxCount.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Error, doc);
        }

        ShowNotice(Loc.Format("SearchResults_BookmarksAdded", added.ToString("N0", CultureInfo.CurrentCulture)), InfoBarSeverity.Success, doc);
    }

    /// <summary>一覧の行 (絞り込みの結果) をブックマークにする (共通の結果一覧の機能)。</summary>
    internal void FileMapRowsToBookmarks()
    {
        if (Vm.Selected is { } doc && _fileMapRows.Length > 0)
        {
            AddFileMapBookmarks(doc, _fileMapRows);
        }
    }

    /// <summary>一覧の行 (絞り込みの結果) をマルチ選択にする (共通の結果一覧の機能)。</summary>
    internal void FileMapRowsToMultiSelection()
    {
        if (FileMapEditor is not { } editor || CurrentFileMap is not { } map || _fileMapRows.Length == 0)
        {
            return;
        }

        editor.SetSelections(_fileMapRows.Select(i => map.Regions[i]).Where(r => r.Offset < editor.Document.Length)
            .Select(r => new ByteRange(r.Offset, Math.Min(r.Length, editor.Document.Length - r.Offset))));
    }

    /// <summary>
    /// 「見つかったファイルにテンプレートを適用」(ANA-34 の仕様 7): テンプレートの決まった埋め込まれたファイルに、一覧の順で固定の適用を作る。
    /// 適用の数の上限 (32 個) に達したら止め、適用しなかった件数を InfoBar で知らせる。
    /// </summary>
    private void ApplyFileMapTemplates()
    {
        if (Vm.Selected is not { } doc || CurrentFileMap is not { } map)
        {
            return;
        }

        DocumentTemplates templates = DocumentTemplates.For(doc);
        templates.MarkLoaded();
        int[] targets = [.. map.EmbeddedWithTemplate];
        int applied = 0;
        int skipped = 0;
        foreach (int i in targets)
        {
            FileMapRegion r = map.Regions[i];
            if (templates.Entries.Count >= TemplateApplicationSet.MaxApplications
                || templates.Add(new TemplateApplicationInfo { TemplatePath = map.Details[r.Detail].Template!.Path, StartOffset = r.Offset }, null, false, null) is null)
            {
                skipped++;
                continue;
            }

            applied++;
        }

        if (applied > 0)
        {
            ShowPanel(TemplatesPanelId, focus: false);
        }

        ShowNotice(skipped > 0 ? Loc.Format("FileMap_TemplatesLimit", applied, skipped, TemplateApplicationSet.MaxApplications)
            : Loc.Format("FileMap_TemplatesApplied", applied), skipped > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success, doc);
    }

    // ---- エクスポート (ANA-34 の仕様 9) ----

    internal async Task ExportFileMapAsync(FileMapExportFormat format)
    {
        if (Vm.Selected is not { } doc || CurrentFileMap is not { } map)
        {
            return;
        }

        string extension = format switch { FileMapExportFormat.Json => ".json", FileMapExportFormat.Csv => ".csv", _ => ".txt" };
        string suggested = Path.GetFileNameWithoutExtension(doc.DisplayName) + "-filemap" + extension;
        if (!TestHooks.TrySavePicker(suggested, out string? path))
        {
            var picker = new FileSavePicker(WindowId) { SuggestedFileName = suggested, SettingsIdentifier = "HexEditor.FileMapExport" };
            picker.FileTypeChoices.Add(Loc.Get("FileMap_Export_" + format), [extension]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }

        if (path is null)
        {
            return;
        }

        var info = new FileMapDocumentInfo(doc.DisplayName, doc.FilePath, doc.Document.Length, map.TransformDescription);
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo ui = CultureInfo.CurrentUICulture;
        try
        {
            await Vm.Operations.RunAsync(Loc.Get("Operation_FileMapExport"), OperationKind.ReadOnly, doc.Document, null, op =>
            {
                FileMapExport.ToFile(path, format, map, info, Loc.Get, culture, ui, op.CancellationToken);
                return Task.CompletedTask;
            });
            ShowNotice(Loc.Format("FileMap_Exported", Path.GetFileName(path)), InfoBarSeverity.Success, doc);
        }
        catch (OperationCanceledException)
        {
            // 書きかけのファイルは消した。
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // エクスポート先に書き込めない: InfoBar に理由を出す (書きかけのファイルは消した)。
            ShowNotice(Loc.Format("FileMap_ExportFailed", ex.Message), InfoBarSeverity.Error, doc);
        }
    }
}
