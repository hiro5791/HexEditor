using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Operations;
using HexEditor.Core.Search;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>開いているドキュメント (複数ファイル検索で、ファイルの代わりに未保存の状態を検索・置換する)。</summary>
public sealed record OpenFileDocument(string Path, EditorState Editor);

/// <summary>
/// 「複数ファイル検索」パネル (FIND-30、FIND-31)。フォルダのファイルを並列に検索し、結果をファイルごとにまとめて出す。置換モードでは
/// チェックした一致だけを、確認のあとファイルごとの安全な保存で置換する (開いているドキュメントはエディタ上で置換し、Undo で戻せる)。
/// </summary>
public sealed partial class MultiFileSearchPanel : UserControl
{
    /// <summary>名前を付けて保存した対象の指定 (アプリの状態。FIND-30 の仕様 9)。</summary>
    public const string TargetSetsKey = "search.multiFile.targets";

    private CancellationTokenSource? _running;
    private MultiFileSearchResults? _results;
    private SearchPattern? _pattern;
    private ReplacementTemplate? _template;
    private bool _ready;

    public MultiFileSearchPanel()
    {
        InitializeComponent();
        foreach (EncodingEntry entry in TermRow.Encodings)
        {
            EncodingChoice.Items.Add(new ComboBoxItem { Content = MainWindow.EncodingDisplayText(entry), Tag = entry.Id });
        }

        EncodingChoice.SelectedIndex = Math.Max(0, TermRow.Encodings.ToList().FindIndex(e => e.Id == "ascii"));
        AutomationProperties.SetName(ModeChoice, Loc.Get("MultiFile_Mode_Name"));
        AutomationProperties.SetName(KindChoice, Loc.Get("Find_Kind_Name"));
        AutomationProperties.SetName(EncodingChoice, Loc.Get("Find_Encoding_Name"));
        AutomationProperties.SetName(LengthPolicyChoice, Loc.Get("Find_LengthPolicy_Name"));
        AutomationProperties.SetName(FolderList, Loc.Get("MultiFile_Folders_Name"));
        AutomationProperties.SetName(ResultList, Loc.Get("MultiFile_Results_Name"));
        AutomationProperties.SetName(TargetSetChoice, Loc.Get("MultiFile_TargetSet_Name"));
        FillTargetSets();
        _ready = true;
        Validate();
    }

    /// <summary>検索するフォルダ。</summary>
    public ObservableCollection<string> Folders { get; } = [];

    /// <summary>結果の行 (ファイルの行と、その下の一致の行)。</summary>
    public ObservableCollection<MultiFileRow> Rows { get; } = [];

    public OperationCenter? Operations { get; set; }

    public Microsoft.UI.WindowId WindowId { get; set; }

    /// <summary>確認ダイアログ (置換の前。FIND-31 の仕様 3)。</summary>
    public Func<ConfirmRequest, Task<ConfirmChoice>>? Confirm { get; set; }

    /// <summary>開いているドキュメント (UI のスレッドで呼ぶ)。</summary>
    public Func<IReadOnlyList<OpenFileDocument>>? OpenDocuments { get; set; }

    /// <summary>既定のフォルダ (現在のドキュメントのフォルダ。FIND-30 の仕様 2)。</summary>
    public Func<string?>? DefaultFolder { get; set; }

    /// <summary>結果を開く: そのファイルをタブで開き (開いていれば切り替え)、一致を選択する (FIND-30 の仕様 6)。</summary>
    public event EventHandler<(string Path, long Offset, long Length)>? OpenRequested;

    /// <summary>通知を出す (失敗など)。</summary>
    public event EventHandler<(string Message, InfoBarSeverity Severity)>? NoticeRequested;

    public bool IsRunning => _running is not null;

    /// <summary>置換モードか。</summary>
    public bool IsReplaceMode => ModeChoice.SelectedIndex == 1;

    /// <summary>テスト用: 結果。</summary>
    internal MultiFileSearchResults? Results => _results;

    /// <summary>テスト用: 置換の結果 (ファイルごと)。</summary>
    internal IReadOnlyList<FileReplaceOutcome> Outcomes { get; private set; } = [];

    internal string SummaryText => Summary.Text;

    internal string ErrorText => Error.Text;

    /// <summary>パネルを表示したとき: フォルダがなければ現在のドキュメントのフォルダを入れる。</summary>
    public void PrepareToShow(bool replace)
    {
        if (Folders.Count == 0 && DefaultFolder?.Invoke() is { Length: > 0 } folder)
        {
            Folders.Add(folder);
        }

        if (replace)
        {
            ModeChoice.SelectedIndex = 1;
        }

        Query.Focus(FocusState.Programmatic);
    }

    /// <summary>テスト用: 条件と対象を入れる。</summary>
    internal void Configure(JsonObject request)
    {
        if (request["mode"]?.GetValue<string>() is { } mode)
        {
            ModeChoice.SelectedIndex = mode == "replace" ? 1 : 0;
        }

        if (request["kind"]?.GetValue<string>() is { } kind)
        {
            KindChoice.SelectedIndex = (int)Enum.Parse<SearchKind>(kind, ignoreCase: true);
        }

        if (request["query"]?.GetValue<string>() is { } query)
        {
            Query.Text = query;
        }

        if (request["replace"]?.GetValue<string>() is { } replace)
        {
            ReplaceQuery.Text = replace;
        }

        if (request["folders"] is JsonArray folders)
        {
            Folders.Clear();
            foreach (JsonNode? f in folders)
            {
                Folders.Add(f!.GetValue<string>());
            }
        }

        if (request["exclude"]?.GetValue<string>() is { } exclude)
        {
            ExcludeInput.Text = exclude;
        }

        if (request["include"]?.GetValue<string>() is { } include)
        {
            IncludeInput.Text = include;
        }

        if (request["backup"] is { } backup)
        {
            BackupChoice.IsChecked = backup.GetValue<bool>();
        }

        Validate();
    }

    /// <summary>テスト用: 行のチェックを切り替える (ファイルのパスの末尾と、一致の番号。番号がなければファイルの行)。</summary>
    internal bool SetChecked(string fileSuffix, int? match, bool value)
    {
        MultiFileRow? file = Rows.FirstOrDefault(r => r.IsFile && r.File.Path.EndsWith(fileSuffix, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            return false;
        }

        MultiFileRow row = match is int m ? file.Children[m] : file;
        row.Checked = value;
        return true;
    }

    /// <summary>テスト用: 行を開く (ダブルクリック・Enter と同じ)。</summary>
    internal bool OpenRow(string fileSuffix, int match)
    {
        MultiFileRow? file = Rows.FirstOrDefault(r => r.IsFile && r.File.Path.EndsWith(fileSuffix, StringComparison.OrdinalIgnoreCase));
        if (file is null || match >= file.Children.Count)
        {
            return false;
        }

        ResultList.SelectedItem = file.Children[match];
        Open(file.Children[match]);
        return true;
    }

    // ---- 条件 ----

    private SearchKind Kind => (SearchKind)Math.Max(0, KindChoice.SelectedIndex);

    private Encoding SelectedEncoding => EncodingChoice.SelectedItem is ComboBoxItem { Tag: string id } && TextEncodings.FromCatalogId(id) is { } e
        ? e
        : Encoding.ASCII;

    /// <summary>検索バーと同じ種類の検索語 (FIND-30 の仕様 1)。</summary>
    private SearchPattern BuildPattern() => Kind switch
    {
        SearchKind.Text => SearchPattern.FromText(Query.Text, SelectedEncoding, new TextSearchOptions { CaseSensitive = CaseChoice.IsChecked == true }),
        SearchKind.Integer => NumericRange.IsRange(Query.Text)
            ? NumericRange.Integer(Query.Text, new IntegerSearchOptions())
            : NumericSearch.Integer(Query.Text, new IntegerSearchOptions()),
        SearchKind.Float => NumericSearch.Float(Query.Text, new FloatSearchOptions()),
        SearchKind.RegexText => RegexSearch.Text(Query.Text, SelectedEncoding, new RegexSearchOptions { IgnoreCase = CaseChoice.IsChecked != true }),
        SearchKind.RegexBytes => RegexSearch.Bytes(Query.Text, new RegexSearchOptions { Singleline = true }),
        _ => SearchPattern.FromHex(Query.Text),
    };

    private ReplacementTemplate BuildTemplate(SearchPattern pattern) => Kind switch
    {
        SearchKind.Text => ReplacementTemplate.FromText(ReplaceQuery.Text, SelectedEncoding, false),
        SearchKind.Integer or SearchKind.Float when pattern.Numeric is { } numeric => ReplacementTemplate.FromNumeric(ReplaceQuery.Text, numeric),
        SearchKind.RegexText or SearchKind.RegexBytes => ReplacementTemplate.FromRegex(pattern, ReplaceQuery.Text),
        _ => ReplacementTemplate.FromHex(ReplaceQuery.Text),
    };

    /// <summary>対象の指定 (FIND-30 の仕様 2)。サイズ・深さの入力が不正なら null。</summary>
    private MultiFileTargets? BuildTargets(out string? error)
    {
        string? problem = null;
        long? Size(TextBox box)
        {
            if (box.Text.Trim().Length == 0)
            {
                return null;
            }

            if (ExpressionEvaluator.TryEvaluate(box.Text, new NoContext(), out long v, out _) && v >= 0)
            {
                return v;
            }

            problem = Loc.Get("MultiFile_SizeInvalid");
            return null;
        }

        int? depth = null;
        if (DepthInput.Text.Trim().Length > 0)
        {
            if (int.TryParse(DepthInput.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int d))
            {
                depth = d;
            }
            else
            {
                problem = Loc.Get("MultiFile_DepthInvalid");
            }
        }

        var targets = new MultiFileTargets
        {
            Folders = [.. Folders],
            IncludeSubfolders = SubfoldersChoice.IsChecked == true,
            MaxDepth = depth,
            IncludeMasks = IncludeInput.Text,
            ExcludeMasks = ExcludeInput.Text,
            MinSize = Size(MinSizeInput),
            MaxSize = Size(MaxSizeInput),
            IncludeHidden = HiddenChoice.IsChecked == true,
            FollowLinks = LinksChoice.IsChecked == true,
            IncludeOpenDocuments = OpenDocsChoice.IsChecked == true,
        };
        error = problem;
        return problem is null ? targets : null;
    }

    private void Validate()
    {
        if (!_ready)
        {
            return;
        }

        bool replace = IsReplaceMode;
        ReplaceRow.Visibility = ReplaceButton.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;
        EncodingChoice.Visibility = Kind is SearchKind.Text or SearchKind.RegexText ? Visibility.Visible : Visibility.Collapsed;
        CaseChoice.Visibility = Kind is SearchKind.Text or SearchKind.RegexText ? Visibility.Visible : Visibility.Collapsed;
        string? error = null;
        try
        {
            _pattern = Query.Text.Length == 0 ? null : BuildPattern();
        }
        catch (PatternException ex)
        {
            _pattern = null;
            error = Loc.Format("MultiFile_QueryInvalid", ex.Error.ToString());
        }

        _template = null;
        if (replace && _pattern is not null)
        {
            try
            {
                _template = BuildTemplate(_pattern);
            }
            catch (PatternException ex)
            {
                error = Loc.Format("MultiFile_ReplaceInvalid", ex.Error.ToString());
            }
        }

        Error.Text = error ?? string.Empty;
        SearchButton.IsEnabled = _pattern is not null && _running is null && Folders.Count > 0;
        ReplaceButton.IsEnabled = replace && _template is not null && _results is { State: not MultiFileSearchState.Running } && _running is null
            && Rows.Any(r => r.IsFile && r.Checked != false);
        foreach (MultiFileRow row in Rows)
        {
            row.CheckVisibility = replace ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---- 検索 (FIND-30) ----

    /// <summary>「検索」。対象のフォルダがなければ検索の前にエラーを出す (「エラー」)。</summary>
    public async Task SearchAsync()
    {
        if (_pattern is not { } pattern || _running is not null)
        {
            return;
        }

        if (BuildTargets(out string? targetError) is not { } targets)
        {
            Error.Text = targetError ?? string.Empty;
            return;
        }

        if (MultiFileSearch.MissingFolders(targets) is { Count: > 0 } missing)
        {
            Error.Text = Loc.Format("MultiFile_FolderMissing", string.Join(", ", missing));
            return;
        }

        Error.Text = string.Empty;
        Rows.Clear();
        Outcomes = [];
        var cts = new CancellationTokenSource();
        _running = cts;
        CancelButton.IsEnabled = true;
        Validate();
        var results = new MultiFileSearchResults(pattern, targets);
        _results = results;

        // 開いているドキュメントは、検索を始めたときの (未保存の編集を含む) 状態を検索する。
        var open = new Dictionary<string, OpenDocumentSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (OpenFileDocument doc in OpenDocuments?.Invoke() ?? [])
        {
            open[doc.Path] = new OpenDocumentSnapshot(doc.Editor.Document.Current, doc.Editor.Document.Length, File.GetLastWriteTimeUtc(doc.Path));
        }

        int parallelism = Math.Clamp(App.Settings?.GetInt(MultiFileSearch.ParallelismKey, MultiFileSearch.DefaultParallelism) ?? MultiFileSearch.DefaultParallelism,
            1, MultiFileSearch.MaxParallelism);
        int added = 0;
        var flushLock = new object();
        void Flush()
        {
            // 検索のスレッドは並列に動くため、行を作るのは 1 つずつ (同じファイルの行を 2 回作らない)。
            var rows = new List<MultiFileRow>();
            lock (flushLock)
            {
                IReadOnlyList<FileSearchResult> files = results.Files;
                for (; added < files.Count; added++)
                {
                    rows.AddRange(BuildRows(files[added]));
                }
            }

            if (rows.Count == 0)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_results == results)
                {
                    foreach (MultiFileRow row in rows)
                    {
                        row.CheckVisibility = IsReplaceMode ? Visibility.Visible : Visibility.Collapsed;
                        Rows.Add(row);
                    }

                    UpdateSummary();
                }
            });
        }

        AppLog.Info($"Multi-file search: start ({targets.Folders.Count} folder(s))");
        try
        {
            async Task Work(LongRunningOperation? op)
            {
                CancellationToken token = op?.CancellationToken ?? cts.Token;
                if (op is not null)
                {
                    cts.Token.Register(op.Cancel);
                }

                results.Changed += (_, _) =>
                {
                    op?.ReportMatches(results.MatchCount);
                    op?.ReportDetail(Loc.Format("MultiFile_Progress", results.ProcessedFiles.ToString("N0", CultureInfo.CurrentCulture),
                        results.FoundFiles.ToString("N0", CultureInfo.CurrentCulture), results.CurrentFile ?? string.Empty));
                    if (results.Files.Count > added)
                    {
                        Flush();
                    }
                };
                await MultiFileSearch.RunAsync(results, new SearchOptions { ChunkSize = FindBar.ChunkSizeSetting }, path => open.TryGetValue(path, out var s) ? s : null,
                    parallelism, MultiFileSearch.DefaultPerFileLimit, MultiFileSearch.DefaultTotalLimit, op, token);
                op?.ReportDetail(Loc.Format("MultiFile_Progress", results.ProcessedFiles.ToString("N0", CultureInfo.CurrentCulture),
                    results.FoundFiles.ToString("N0", CultureInfo.CurrentCulture), string.Empty));
            }

            if (Operations is null)
            {
                await Task.Run(() => Work(null));
            }
            else
            {
                await Operations.RunAsync(Loc.Get("Operation_MultiFileSearch"), OperationKind.ReadOnly, null, null, Work);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Flush();
            if (_running == cts)
            {
                _running = null;
            }

            CancelButton.IsEnabled = false;
            AppLog.Info($"Multi-file search: end ({results.State}, {results.Files.Count} file(s), {results.MatchCount} match(es))");
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateSummary();
                Validate();
            });
        }
    }

    /// <summary>ファイルの行と一致の行を作る (一致のデータを読む。検索のスレッドで呼ぶ)。</summary>
    private static List<MultiFileRow> BuildRows(FileSearchResult file)
    {
        string date = file.LastWriteUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var fileRow = MultiFileRow.ForFile(file, Loc.Format("MultiFile_FileRow", file.Path, file.Matches.Count.ToString("N0", CultureInfo.CurrentCulture),
            file.Size.ToString("N0", CultureInfo.CurrentCulture), date));
        var rows = new List<MultiFileRow> { fileRow };
        Microsoft.Win32.SafeHandles.SafeFileHandle? handle = null;
        try
        {
            if (file.Snapshot is null)
            {
                handle = File.OpenHandle(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        using (handle)
        {
            byte[] buffer = new byte[16];
            foreach (SearchMatch m in file.Matches)
            {
                int n = (int)Math.Min(buffer.Length, m.Length);
                int got = 0;
                try
                {
                    got = file.Snapshot is { } snapshot ? snapshot.Read(m.Offset, buffer.AsSpan(0, n)).BytesReturned
                        : handle is not null ? RandomAccess.Read(handle, buffer.AsSpan(0, n), m.Offset) : 0;
                }
                catch (IOException)
                {
                }

                string hex = string.Join(' ', buffer.Take(got).Select(b => b.ToString("X2", CultureInfo.InvariantCulture))) + (m.Length > n ? " …" : string.Empty);
                string text = new([.. buffer.Take(got).Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.')]);
                rows.Add(MultiFileRow.ForMatch(fileRow, m, Loc.Format("MultiFile_MatchRow", StatusFormat.Hex(m.Offset), m.Length.ToString("N0", CultureInfo.CurrentCulture), hex, text)));
            }
        }

        return rows;
    }

    /// <summary>見出し: 一致のあったファイル数・件数・状態、一致のなかったファイル数、スキップしたファイル (FIND-30 の仕様 7)。</summary>
    private void UpdateSummary()
    {
        if (_results is not { } r)
        {
            Summary.Text = string.Empty;
            return;
        }

        string state = Loc.Get(r.State switch
        {
            MultiFileSearchState.Running => "SearchResults_State_Running",
            MultiFileSearchState.Cancelled => "SearchResults_State_Cancelled",
            MultiFileSearchState.LimitReached => "MultiFile_State_Limit",
            _ => "SearchResults_State_Completed",
        });
        Summary.Text = Loc.Format("MultiFile_Summary", r.Files.Count, r.MatchCount, r.FilesWithoutMatches, state);
        IReadOnlyList<SkippedFile> skipped = r.Skipped;
        SkippedButton.Visibility = skipped.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        string skippedText = Loc.Format("MultiFile_Skipped", skipped.Count);
        SkippedButton.Content = skippedText;
        AutomationProperties.SetName(SkippedButton, skippedText);
        SkippedMenu.Items.Clear();
        foreach (SkippedFile s in skipped.Take(200))
        {
            SkippedMenu.Items.Add(new MenuFlyoutItem { Text = Loc.Format("MultiFile_SkippedItem", s.Path, ReasonText(s.Reason, s.Message)), IsEnabled = false });
        }
    }

    internal static string ReasonText(FileSkipReason reason, string message) =>
        Loc.Get("MultiFile_Reason_" + reason) + (message.Length > 0 && reason is FileSkipReason.IoError ? $" ({message})" : string.Empty);

    private void Open(MultiFileRow row)
    {
        if (row.Match is { } m)
        {
            OpenRequested?.Invoke(this, (row.File.Path, m.Offset, m.Length));
        }
        else if (row.Children.FirstOrDefault()?.Match is { } first)
        {
            OpenRequested?.Invoke(this, (row.File.Path, first.Offset, first.Length));
        }
    }

    // ---- 置換 (FIND-31) ----

    /// <summary>「置換を実行」: 確認ダイアログのあと、チェックした一致を置換して保存する。</summary>
    public async Task ReplaceAsync()
    {
        if (_results is not { } results || _template is not { } template || _pattern is not { } pattern || _running is not null)
        {
            return;
        }

        List<(MultiFileRow Row, IReadOnlyList<SearchMatch> Matches)> work = [.. Rows.Where(r => r.IsFile).Select(r => (r, r.CheckedMatches)).Where(w => w.Item2.Count > 0)];
        if (work.Count == 0)
        {
            return;
        }

        long count = work.Sum(w => (long)w.Matches.Count);
        bool backup = BackupChoice.IsChecked == true;
        ConfirmChoice choice = Confirm is null ? ConfirmChoice.Cancel : await Confirm(new ConfirmRequest(
            "MultiFileReplaceConfirm",
            Loc.Get("MultiFile_Confirm_Title"),
            Loc.Format("MultiFile_Confirm_Body", work.Count, count, Loc.Get(backup ? "MultiFile_Confirm_Backup" : "MultiFile_Confirm_NoBackup")),
            Loc.Get("MultiFile_Confirm_Replace"),
            null,
            Loc.Get("MultiFile_Confirm_Cancel")));
        if (choice != ConfirmChoice.Primary)
        {
            return;
        }

        var options = new MultiFileReplaceOptions
        {
            Template = template,
            ReplaceOptions = new ReplaceOptions { Policy = (LengthPolicy)Math.Max(0, LengthPolicyChoice.SelectedIndex) },
            Backup = backup,
            ClearReadOnly = ClearReadOnlyChoice.IsChecked == true,
            JournalDirectory = Path.Combine(Path.GetTempPath(), "HexEditor", "multifile-replace"),
        };
        Dictionary<string, EditorState> open = (OpenDocuments?.Invoke() ?? []).GroupBy(d => d.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Editor, StringComparer.OrdinalIgnoreCase);
        var cts = new CancellationTokenSource();
        _running = cts;
        CancelButton.IsEnabled = true;
        Validate();
        var outcomes = new List<FileReplaceOutcome>();
        AppLog.Info($"Multi-file replace: start ({work.Count} file(s), {count} match(es))");
        try
        {
            async Task Work(LongRunningOperation? op)
            {
                CancellationToken token = op?.CancellationToken ?? cts.Token;
                if (op is not null)
                {
                    cts.Token.Register(op.Cancel);
                }

                int done = 0;
                foreach ((MultiFileRow row, IReadOnlyList<SearchMatch> matches) in work)
                {
                    token.ThrowIfCancellationRequested();
                    op?.ReportDetail(Loc.Format("MultiFile_ReplaceProgress", done, work.Count, row.File.Path));
                    FileReplaceOutcome outcome;
                    if (open.TryGetValue(row.File.Path, out EditorState? editor))
                    {
                        // 開いているドキュメントはエディタ上で置換する (Undo で戻せる。保存は利用者が行う。仕様 6)。
                        outcome = await OnUiAsync(() => ReplaceInEditor(editor, row.File.Path, matches, pattern, options));
                    }
                    else
                    {
                        outcome = await Task.Run(() => MultiFileSearch.ReplaceInFile(row.File, matches, pattern, options, null, token), token);
                    }

                    outcomes.Add(outcome);
                    done++;
                    DispatcherQueue.TryEnqueue(() => row.Title += "  — " + OutcomeText(outcome));
                }
            }

            if (Operations is null)
            {
                await Work(null);
            }
            else
            {
                await Operations.RunAsync(Loc.Get("Operation_MultiFileReplace"), OperationKind.WritesExternal, null, null, Work);
            }
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("Multi-file replace: cancelled");
        }
        finally
        {
            if (_running == cts)
            {
                _running = null;
            }

            CancelButton.IsEnabled = false;
            Outcomes = outcomes;
            int replacedFiles = outcomes.Count(o => o.Status is FileReplaceStatus.Replaced or FileReplaceStatus.ReplacedInEditor && o.Count > 0);
            long replaced = outcomes.Sum(o => o.Status is FileReplaceStatus.Replaced or FileReplaceStatus.ReplacedInEditor ? o.Count : 0);
            int skippedFiles = outcomes.Count(o => o.Status is FileReplaceStatus.Skipped or FileReplaceStatus.Failed);
            int notProcessed = work.Count - outcomes.Count;
            Summary.Text = Loc.Format("MultiFile_ReplaceSummary", replacedFiles, replaced, skippedFiles, notProcessed);
            AppLog.Info($"Multi-file replace: end ({replacedFiles} file(s), {replaced} replacement(s), {skippedFiles} skipped, {notProcessed} not processed)");
            Validate();
        }
    }

    /// <summary>開いているドキュメントでの置換 (UI のスレッド。1 ファイルで 1 回の Undo)。</summary>
    private static FileReplaceOutcome ReplaceInEditor(EditorState editor, string path, IReadOnlyList<SearchMatch> matches, SearchPattern pattern, MultiFileReplaceOptions options)
    {
        Document doc = editor.Document;
        if (doc.IsReadOnly || editor.ReadOnly)
        {
            return new FileReplaceOutcome(path, FileReplaceStatus.Skipped, 0, FileSkipReason.ReadOnly, string.Empty);
        }

        DocumentSnapshot current = doc.Current;
        var edits = new List<ReplacementEdit>();
        long previousEnd = 0;
        foreach (SearchMatch m in matches.OrderBy(m => m.Offset))
        {
            if (m.Offset < previousEnd || !Replacer.TryVerify(current, pattern, m.Offset, out int length, out int variant))
            {
                continue;
            }

            if (Replacer.Plan(options.Template, m.Offset, length, variant, options.ReplaceOptions, current.Length, doc.CanResize, out ReplacementEdit? edit, current)
                == ReplaceIssue.None)
            {
                previousEnd = edit!.Offset + edit.RemoveLength;
                edits.Add(edit);
            }
        }

        if (edits.Count > 0)
        {
            doc.ApplyReplacements(edits, Loc.Get("History_ReplaceAll"));
        }

        return new FileReplaceOutcome(path, FileReplaceStatus.ReplacedInEditor, edits.Count, null, string.Empty);
    }

    internal static string OutcomeText(FileReplaceOutcome o) => o.Status switch
    {
        FileReplaceStatus.Replaced => Loc.Format("MultiFile_Outcome_Replaced", o.Count),
        FileReplaceStatus.ReplacedInEditor => Loc.Format("MultiFile_Outcome_InEditor", o.Count),
        FileReplaceStatus.Skipped => Loc.Format("MultiFile_Outcome_Skipped", ReasonText(o.Reason ?? FileSkipReason.IoError, o.Message)),
        FileReplaceStatus.Failed => Loc.Format("MultiFile_Outcome_Failed", ReasonText(o.Reason ?? FileSkipReason.IoError, o.Message)),
        _ => Loc.Get("MultiFile_Outcome_NotProcessed"),
    };

    private Task<T> OnUiAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }))
        {
            tcs.SetCanceled();
        }

        return tcs.Task;
    }

    /// <summary>実行中の検索・置換を取り消す。</summary>
    public void Cancel() => _running?.Cancel();

    // ---- 対象の指定の保存 (FIND-30 の仕様 9) ----

    private static List<(string Name, JsonObject Targets)> LoadTargetSets()
    {
        var sets = new List<(string, JsonObject)>();
        if (Commands.CommandService.State?.Get(TargetSetsKey) is JsonArray array)
        {
            foreach (JsonObject o in array.OfType<JsonObject>())
            {
                if (o["name"]?.GetValue<string>() is { } name && o["targets"] is JsonObject t)
                {
                    sets.Add((name, (JsonObject)t.DeepClone()));
                }
            }
        }

        return sets;
    }

    private void FillTargetSets()
    {
        TargetSetChoice.SelectionChanged -= TargetSet_Changed;
        TargetSetChoice.Items.Clear();
        foreach ((string name, JsonObject targets) in LoadTargetSets())
        {
            TargetSetChoice.Items.Add(new ComboBoxItem { Content = name, Tag = targets });
        }

        TargetSetChoice.PlaceholderText = Loc.Get("MultiFile_TargetSet_Placeholder");
        TargetSetChoice.SelectionChanged += TargetSet_Changed;
    }

    private void TargetSet_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TargetSetChoice.SelectedItem is ComboBoxItem { Tag: JsonObject json })
        {
            MultiFileTargets t = MultiFileTargets.FromJson(json);
            Folders.Clear();
            foreach (string f in t.Folders)
            {
                Folders.Add(f);
            }

            SubfoldersChoice.IsChecked = t.IncludeSubfolders;
            DepthInput.Text = t.MaxDepth?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            IncludeInput.Text = t.IncludeMasks;
            ExcludeInput.Text = t.ExcludeMasks;
            MinSizeInput.Text = t.MinSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            MaxSizeInput.Text = t.MaxSize?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            HiddenChoice.IsChecked = t.IncludeHidden;
            LinksChoice.IsChecked = t.FollowLinks;
            OpenDocsChoice.IsChecked = t.IncludeOpenDocuments;
            Validate();
        }
    }

    private void TargetSetSave_Click(object sender, RoutedEventArgs e)
    {
        string name = TargetSetName.Text.Trim();
        if (name.Length == 0 || BuildTargets(out _) is not { } targets)
        {
            return;
        }

        List<(string Name, JsonObject Targets)> sets = LoadTargetSets();
        sets.RemoveAll(s => s.Name == name);
        sets.Add((name, targets.ToJson()));
        Commands.CommandService.State?.Set(TargetSetsKey, new JsonArray([.. sets.Select(s => (JsonNode?)new JsonObject { ["name"] = s.Name, ["targets"] = s.Targets })]));
        TargetSetName.Text = string.Empty;
        FillTargetSets();
    }

    // ---- イベント ----

    private void Option_Changed(object sender, SelectionChangedEventArgs e) => Validate();

    private void Check_Changed(object sender, RoutedEventArgs e) => Validate();

    private void Text_Changed(object sender, TextChangedEventArgs e) => Validate();

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void Replace_Click(object sender, RoutedEventArgs e) => await ReplaceAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        string path = FolderInput.Text.Trim().Trim('"');
        if (path.Length > 0 && !Folders.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            Folders.Add(path);
            FolderInput.Text = string.Empty;
            Validate();
        }
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(WindowId) { SettingsIdentifier = "HexEditor.MultiFileFolder" };
        if ((await picker.PickSingleFolderAsync())?.Path is { } path && !Folders.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            Folders.Add(path);
            Validate();
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is string path)
        {
            Folders.Remove(path);
            Validate();
        }
    }

    private void ResultList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ResultList.SelectedItem is MultiFileRow row)
        {
            Open(row);
        }
    }

    private void ResultList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ResultList.SelectedItem is MultiFileRow row)
        {
            e.Handled = true;
            Open(row);
        }
        else if (e.Key == VirtualKey.Space && IsReplaceMode && ResultList.SelectedItem is MultiFileRow toggle)
        {
            e.Handled = true;
            toggle.Checked = toggle.Checked != true;
            Validate();
        }
    }

    /// <summary>サイズの入力式の文脈 (名前と読み取りは使わない)。</summary>
    private sealed class NoContext : IExpressionContext
    {
        public long Cursor => 0;

        public long Length => 0;

        public long SelectionStart => 0;

        public long SelectionLength => 0;

        public int SectorSize => 512;

        public long? ClusterSize => null;

        public long? RecordLength => null;

        public long? Bookmark(string name) => null;

        public bool TryRead(long offset, Span<byte> destination) => false;
    }
}
