using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.Core.Search;
using HexEditor.Core.Search.Yara;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HexEditor.App.Controls;

/// <summary>フォルダのスキャンの結果 1 件 (ファイル、ルール、文字列、位置)。</summary>
public sealed record YaraFileMatch(string Path, string Rule, string? Identifier, long Offset, int Length)
{
    public override string ToString() =>
        $"{System.IO.Path.GetFileName(Path)}  {Rule}{(Identifier is null ? string.Empty : "  " + Identifier + "  " + StatusFormat.Hex(Offset) + " (" + Length + ")")}";
}

/// <summary>
/// 「YARA」パネル (FIND-33 の画面。下のパネル)。ルールセットの選択・保存、ルールファイルの追加・削除、外部変数、範囲、「スキャン」、結果一覧。
/// ルールは利用者が用意する (アプリに同梱しない)。ルールのコンパイルの誤りはファイル名・行番号・メッセージで一覧にし、スキャンを始めない。
/// ドキュメントのスキャンの結果は共通の結果一覧に、フォルダのスキャンの結果はファイルごとの一覧に出す。
/// </summary>
public sealed partial class YaraPanel : UserControl
{
    /// <summary>名前を付けたルールセット (state.json。{ 名前: { files: [...], externals: "a=1" } })。</summary>
    public const string RuleSetsStateKey = "yara.ruleSets";

    private readonly ComboBox _ruleSet = new() { MinWidth = 160 };
    private readonly ListView _files = new() { Height = 96, SelectionMode = ListViewSelectionMode.Extended };
    private readonly TextBox _externals = new() { AcceptsReturn = true, Height = 64, FontFamily = DialogParts.Mono, TextWrapping = TextWrapping.NoWrap };
    private readonly ComboBox _scope = new();
    private readonly TextBox _folder = new() { Width = 220 };
    private readonly TextBox _mask = new() { Width = 100, Text = "*" };
    private readonly Button _scan = new();
    private readonly Button _cancel = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _limitation = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly ListView _errors = new() { MaxHeight = 120, Visibility = Visibility.Collapsed };
    private readonly ListView _folderResults = new() { Visibility = Visibility.Collapsed };
    private readonly List<string> _paths = [];
    private CancellationTokenSource? _running;
    private YaraSearch? _lastSearch;

    public YaraPanel()
    {
        Results = new SearchResultsPanel();
        AutomationProperties.SetAutomationId(Results, "Yara_Results");

        var bar = new WrapPanel();
        AddLabeled(bar, "Yara_RuleSet", _ruleSet);
        _ruleSet.SelectionChanged += (_, _) => LoadSelectedSet();
        bar.Children.Add(MakeButton("Yara_SaveSet", async () => await SaveSetAsync()));
        bar.Children.Add(MakeButton("Yara_DeleteSet", DeleteSet));
        bar.Children.Add(MakeButton("Yara_AddFiles", async () => await AddFilesAsync()));
        bar.Children.Add(MakeButton("Yara_AddFolder", async () => await AddFolderAsync()));
        bar.Children.Add(MakeButton("Yara_RemoveFiles", RemoveSelectedFiles));
        _scope.Items.Add(new ComboBoxItem { Content = Loc.Get("Yara_Scope_Document") });
        _scope.Items.Add(new ComboBoxItem { Content = Loc.Get("Yara_Scope_Selection") });
        _scope.Items.Add(new ComboBoxItem { Content = Loc.Get("Yara_Scope_Folder") });
        _scope.SelectedIndex = 0;
        _scope.SelectionChanged += (_, _) => UpdateScope();
        AddLabeled(bar, "Yara_Scope", _scope);
        AddLabeled(bar, "Yara_Folder", _folder);
        AddLabeled(bar, "Yara_Mask", _mask);
        _scan.Content = Loc.Get("Yara_Scan");
        _scan.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        AutomationProperties.SetAutomationId(_scan, "Yara_Scan");
        _scan.Click += async (_, _) => await ScanAsync();
        bar.Children.Add(_scan);
        _cancel.Content = Loc.Get("Common_Cancel");
        AutomationProperties.SetAutomationId(_cancel, "Yara_Cancel");
        _cancel.Click += (_, _) => Cancel();
        _cancel.IsEnabled = false;
        bar.Children.Add(_cancel);

        AutomationProperties.SetAutomationId(_files, "Yara_Files");
        AutomationProperties.SetName(_files, Loc.Get("Yara_Files"));
        AutomationProperties.SetAutomationId(_externals, "Yara_Externals");
        AutomationProperties.SetName(_externals, Loc.Get("Yara_Externals"));
        _externals.Header = Loc.Get("Yara_Externals");
        _externals.PlaceholderText = "name=value";
        AutomationProperties.SetAutomationId(_status, "Yara_Status");
        AutomationProperties.SetAutomationId(_limitation, "Yara_Limitation");
        AutomationProperties.SetAutomationId(_details, "Yara_Details");
        AutomationProperties.SetAutomationId(_errors, "Yara_Errors");
        AutomationProperties.SetName(_errors, Loc.Get("Yara_ErrorsName"));
        AutomationProperties.SetAutomationId(_folderResults, "Yara_FolderResults");
        AutomationProperties.SetName(_folderResults, Loc.Get("Yara_FolderResultsName"));
        _folderResults.IsItemClickEnabled = true;
        _folderResults.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is YaraFileMatch m)
            {
                OpenFileRequested?.Invoke(this, m);
            }
        };
        _limitation.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _limitation.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _status.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];

        // 左: ルールファイルと外部変数。右: 制限の説明・状態・誤りの一覧・行の詳細。下: 結果一覧。
        var left = new StackPanel { Spacing = 4, Width = 320 };
        left.Children.Add(new TextBlock { Text = Loc.Get("Yara_Files"), Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        left.Children.Add(_files);
        left.Children.Add(_externals);
        var right = new StackPanel { Spacing = 4 };
        right.Children.Add(_limitation);
        right.Children.Add(_status);
        right.Children.Add(_errors);
        right.Children.Add(_details);
        var middle = new Grid { ColumnSpacing = 12 };
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        middle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        middle.Children.Add(left);
        Grid.SetColumn(right, 1);
        middle.Children.Add(right);

        var root = new Grid { RowSpacing = 4, Padding = new Thickness(0, 6, 0, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(new Border { Padding = new Thickness(12, 0, 12, 0), Child = bar });
        var middleHost = new Border { Padding = new Thickness(12, 0, 12, 0), Child = middle };
        Grid.SetRow(middleHost, 1);
        root.Children.Add(middleHost);
        Grid.SetRow(Results, 2);
        root.Children.Add(Results);
        Grid.SetRow(_folderResults, 2);
        root.Children.Add(_folderResults);
        Content = root;
        Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"];
        UpdateLimitation();
        LoadSets(null);
        UpdateScope();
    }

    /// <summary>結果一覧 (共通の結果一覧。00-overview 9 章)。</summary>
    public SearchResultsPanel Results { get; }

    /// <summary>スキャンの対象のビュー (選択中のタブ)。</summary>
    public Func<EditorState?>? EditorSource { get; set; }

    /// <summary>ルールファイルを選ぶ (複数)。</summary>
    public Func<Task<IReadOnlyList<string>>>? PickFiles { get; set; }

    /// <summary>フォルダを選ぶ。</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    /// <summary>名前を入力させる (ルールセットの保存)。</summary>
    public Func<string, Task<string?>>? AskName { get; set; }

    /// <summary>ドキュメントのスキャンが終わった (注釈を付ける。FIND-33 の仕様 9)。</summary>
    public event EventHandler<(EditorState Editor, YaraSearch Search, long Offset)>? Scanned;

    /// <summary>フォルダのスキャンの結果を開く。</summary>
    public event EventHandler<YaraFileMatch>? OpenFileRequested;

    /// <summary>スキャン中か。</summary>
    public bool IsRunning => _running is not null;

    /// <summary>テスト用: 状態。</summary>
    internal JsonObject TestState() => new()
    {
        ["files"] = new JsonArray([.. _paths.Select(p => (JsonNode?)p)]),
        ["errors"] = new JsonArray([.. _errors.Items.OfType<YaraCompileError>().Select(e => (JsonNode?)new JsonObject
        {
            ["file"] = e.File is null ? null : Path.GetFileName(e.File),
            ["line"] = e.Line,
            ["message"] = e.Message,
        })]),
        ["status"] = _status.Text,
        ["limitation"] = _limitation.Text,
        ["details"] = _details.Text,
        ["running"] = IsRunning,
        ["scanned"] = _lastSearch?.Result is not null,
        ["timedOut"] = _lastSearch?.Result?.TimedOut ?? false,
        ["rules"] = new JsonArray([.. (_lastSearch?.Result?.Rules ?? []).Select(r => (JsonNode?)new JsonObject
        {
            ["rule"] = r.Rule.Name,
            ["namespace"] = r.Rule.Namespace,
            ["strings"] = new JsonArray([.. r.Strings.Select(x => (JsonNode?)new JsonObject { ["id"] = x.Identifier, ["offset"] = x.Offset + _lastSearch!.ScanOffset, ["length"] = x.Length })]),
        })]),
        ["sets"] = new JsonArray([.. _ruleSet.Items.OfType<ComboBoxItem>().Select(i => (JsonNode?)(i.Content as string))]),
        ["folderResults"] = new JsonArray([.. _folderResults.Items.OfType<YaraFileMatch>().Select(m => (JsonNode?)new JsonObject
        {
            ["path"] = m.Path,
            ["rule"] = m.Rule,
            ["id"] = m.Identifier,
            ["offset"] = m.Offset,
            ["length"] = m.Length,
        })]),
    };

    /// <summary>テスト用: ルールファイル・外部変数・範囲を設定する。</summary>
    internal void TestSet(IReadOnlyList<string>? files, string? externals, int? scope, string? folder)
    {
        if (files is not null)
        {
            _paths.Clear();
            _paths.AddRange(files);
            RefreshFiles();
        }

        if (externals is not null)
        {
            _externals.Text = externals;
        }

        if (scope is int s)
        {
            _scope.SelectedIndex = s;
        }

        if (folder is not null)
        {
            _folder.Text = folder;
        }
    }

    private static void AddLabeled(Panel panel, string key, FrameworkElement control)
    {
        string name = Loc.Get(key);
        AutomationProperties.SetAutomationId(control, key);
        AutomationProperties.SetName(control, name);
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(control);
        panel.Children.Add(stack);
    }

    private static Button MakeButton(string key, Action click)
    {
        var button = new Button { Content = Loc.Get(key) };
        AutomationProperties.SetAutomationId(button, key);
        button.Click += (_, _) => click();
        return button;
    }

    private void UpdateScope()
    {
        bool folder = _scope.SelectedIndex == 2;
        foreach (FrameworkElement e in new FrameworkElement[] { _folder, _mask })
        {
            if (e.Parent is FrameworkElement host)
            {
                host.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>ブロックをまたぐ一致の制限の説明 (常に表示する。仕様 6)。</summary>
    private void UpdateLimitation() =>
        _limitation.Text = Loc.Format("Yara_LimitationText", BlockSizeMiB(), YaraScanOptions.DefaultOverlap / 1024);

    private static int BlockSizeMiB() => Math.Clamp(App.Settings?.GetInt(YaraSettings.BlockSizeKey, YaraSettings.DefaultBlockSizeMiB) ?? YaraSettings.DefaultBlockSizeMiB,
        YaraSettings.MinBlockSizeMiB, YaraSettings.MaxBlockSizeMiB);

    private static int TimeLimitSeconds() => Math.Clamp(App.Settings?.GetInt(YaraSettings.TimeLimitKey, YaraSettings.DefaultTimeLimitSeconds)
        ?? YaraSettings.DefaultTimeLimitSeconds, 1, YaraSettings.MaxTimeLimitSeconds);

    // ---- ルールファイルとルールセット ----

    private void RefreshFiles()
    {
        _files.Items.Clear();
        foreach (string p in _paths)
        {
            _files.Items.Add(p);
        }
    }

    private async Task AddFilesAsync()
    {
        if (PickFiles is null)
        {
            return;
        }

        foreach (string path in await PickFiles())
        {
            if (!_paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                _paths.Add(path);
            }
        }

        RefreshFiles();
    }

    /// <summary>フォルダの中の .yar / .yara をすべて加える (仕様 1)。</summary>
    private async Task AddFolderAsync()
    {
        if (PickFolder is null || await PickFolder() is not { } folder)
        {
            return;
        }

        try
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".yar", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".yara", StringComparison.OrdinalIgnoreCase)).Order())
            {
                if (!_paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    _paths.Add(path);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _status.Text = Loc.Format("Yara_FolderError", e.Message);
        }

        RefreshFiles();
    }

    private void RemoveSelectedFiles()
    {
        foreach (string selected in _files.SelectedItems.OfType<string>().ToList())
        {
            _paths.Remove(selected);
        }

        RefreshFiles();
    }

    private static JsonObject LoadSetsState()
    {
        try
        {
            return JsonNode.Parse(AppState.GetString(RuleSetsStateKey, "{}")) as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private bool _loadingSets;

    private void LoadSets(string? select)
    {
        _loadingSets = true;
        try
        {
            _ruleSet.Items.Clear();
            _ruleSet.Items.Add(new ComboBoxItem { Content = Loc.Get("Yara_NoRuleSet") });
            foreach ((string name, JsonNode? _) in LoadSetsState().OrderBy(p => p.Key, StringComparer.CurrentCulture))
            {
                _ruleSet.Items.Add(new ComboBoxItem { Content = name });
            }

            _ruleSet.SelectedIndex = select is null ? 0 : Math.Max(0, _ruleSet.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => (string)i.Content == select));
        }
        finally
        {
            _loadingSets = false;
        }
    }

    private void LoadSelectedSet()
    {
        if (_loadingSets || _ruleSet.SelectedIndex <= 0 || _ruleSet.SelectedItem is not ComboBoxItem { Content: string name }
            || LoadSetsState()[name] is not JsonObject set)
        {
            return;
        }

        _paths.Clear();
        _paths.AddRange(set["files"]?.AsArray().Select(n => n?.GetValue<string>()).OfType<string>() ?? []);
        _externals.Text = set["externals"]?.GetValue<string>() ?? string.Empty;
        RefreshFiles();
    }

    /// <summary>今のルールファイルと外部変数を名前を付けて保存する (仕様 2)。</summary>
    internal async Task SaveSetAsync(string? name = null)
    {
        name ??= AskName is null ? null : await AskName(_ruleSet.SelectedIndex > 0 && _ruleSet.SelectedItem is ComboBoxItem { Content: string current } ? current : string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        JsonObject sets = LoadSetsState();
        sets[name.Trim()] = new JsonObject
        {
            ["files"] = new JsonArray([.. _paths.Select(p => (JsonNode?)p)]),
            ["externals"] = _externals.Text,
        };
        AppState.SetString(RuleSetsStateKey, sets.ToJsonString());
        LoadSets(name.Trim());
    }

    private void DeleteSet()
    {
        if (_ruleSet.SelectedIndex <= 0 || _ruleSet.SelectedItem is not ComboBoxItem { Content: string name })
        {
            return;
        }

        JsonObject sets = LoadSetsState();
        sets.Remove(name);
        AppState.SetString(RuleSetsStateKey, sets.ToJsonString());
        LoadSets(null);
    }

    // ---- スキャン ----

    /// <summary>外部変数 (1 行に 1 つ、name=value)。</summary>
    private IEnumerable<(string Name, string Value)> Externals() =>
        _externals.Text.Split('\n', '\r').Select(l => l.Trim()).Where(l => l.Length > 0 && l.Contains('='))
            .Select(l => (l[..l.IndexOf('=')].Trim(), l[(l.IndexOf('=') + 1)..].Trim()));

    /// <summary>ルールをコンパイルする。誤りがあれば一覧に出して null (スキャンを始めない)。</summary>
    private YaraRuleSet? Compile()
    {
        _errors.Items.Clear();
        _errors.Visibility = Visibility.Collapsed;
        if (_paths.Count == 0)
        {
            _status.Text = Loc.Get("Yara_NoRules");
            return null;
        }

        var compiler = new YaraCompiler();
        foreach ((string name, string value) in Externals())
        {
            compiler.DefineExternalText(name, value);
        }

        foreach (string path in _paths)
        {
            compiler.AddFile(path);
        }

        if (compiler.Errors.Count > 0)
        {
            foreach (YaraCompileError e in compiler.Errors)
            {
                _errors.Items.Add(e);
            }

            _errors.Visibility = Visibility.Visible;
            _status.Text = Loc.Format("Yara_CompileErrors", compiler.Errors.Count);
            return null;
        }

        return compiler.Build();
    }

    /// <summary>「スキャン」。</summary>
    public async Task ScanAsync()
    {
        UpdateLimitation();
        _details.Text = string.Empty;
        if (Compile() is not { } rules)
        {
            return;
        }

        var options = new YaraScanOptions
        {
            BlockSize = BlockSizeMiB() * 1024 * 1024,
            TimeLimit = TimeSpan.FromSeconds(TimeLimitSeconds()),
            Time = TimeProvider.System,
        };
        if (_scope.SelectedIndex == 2)
        {
            await ScanFolderAsync(rules, options);
            return;
        }

        if (EditorSource?.Invoke() is not { } editor)
        {
            _status.Text = Loc.Get("Command_NoDocument");
            return;
        }

        long offset = 0, length = editor.Document.Length;
        if (_scope.SelectedIndex == 1)
        {
            if (!editor.HasSelection)
            {
                _status.Text = Loc.Get("Strings_NoSelection");
                return;
            }

            (offset, length) = (editor.SelectionStart, editor.SelectionLength);
        }

        _running?.Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;
        _scan.IsEnabled = false;
        _cancel.IsEnabled = true;
        _folderResults.Visibility = Visibility.Collapsed;
        _status.Text = Loc.Get("Yara_Scanning");
        int limit = Math.Clamp(App.Settings?.GetInt(FindBar.FindAllLimitKey, 1_000_000) ?? 1_000_000, 1_000, 100_000_000);
        YaraSearch? search = null;
        (search, SearchResults results) = YaraSearch.Create(editor.Document.Current, offset, length, rules, options,
            new SearchOptions { ChunkSize = FindBar.ChunkSizeSetting, MaxMatches = limit }, m => ShowDetails(search!.RowOf(m)));
        AppLog.Info($"YARA: scan ({rules.Rules.Count} rules)");
        _lastSearch = search;
        try
        {
            await Results.RunAsync([new SearchTarget(editor, string.Empty, results)], Loc.Get("Yara_KindName"),
                string.Join(", ", _paths.Select(Path.GetFileName)), Encoding.ASCII, cts);
            ShowScanStatus(search.Result, search.Rows.Select(r => r.Rule).Distinct().Count());
            Scanned?.Invoke(this, (editor, search, offset));
        }
        catch (Exception e) when (e is IOException or OutOfMemoryException or InvalidOperationException)
        {
            // YARA の内部エラー (読み込みの失敗・メモリ不足など)。エラーの種類と説明を示す (「エラー」)。
            _status.Text = Loc.Format("Yara_InternalError", e.GetType().Name, e.Message);
        }
        finally
        {
            if (_running == cts)
            {
                _running = null;
            }

            _scan.IsEnabled = true;
            _cancel.IsEnabled = false;
        }
    }

    private void ShowScanStatus(YaraScanResult? result, int rules)
    {
        if (result is null)
        {
            _status.Text = Loc.Get("Yara_Cancelled");
            return;
        }

        var parts = new List<string> { Loc.Format("Yara_Matched", rules) };
        if (result.TimedOut)
        {
            parts.Add(Loc.Get("Yara_TimedOut"));
        }

        if (result.Cancelled)
        {
            parts.Add(Loc.Get("Yara_Cancelled"));
        }

        if (result.TooManyMatches.Count > 0)
        {
            parts.Add(Loc.Format("Yara_TooManyMatches", string.Join(", ", result.TooManyMatches)));
        }

        if (result.ConsoleOutput.Count > 0)
        {
            parts.Add(Loc.Get("Yara_Console") + " " + string.Join(" | ", result.ConsoleOutput.Take(20)));
        }

        _status.Text = string.Join(" ", parts);
    }

    /// <summary>一覧の行を開いた: ルールのメタ情報をすべて表示する (仕様 8)。</summary>
    private void ShowDetails(YaraRow row) => DispatcherQueue.TryEnqueue(() =>
        _details.Text = Loc.Format("Yara_DetailsText", row.Rule.Name, row.Rule.Namespace, row.Tags,
            string.Join(Environment.NewLine, row.Rule.Meta.Select(m => "  " + m.Key + " = " + m.Value))));

    /// <summary>フォルダの中のファイルをスキャンする (FIND-30 と同じ対象指定)。結果はファイルごとの一覧に出す。</summary>
    private async Task ScanFolderAsync(YaraRuleSet rules, YaraScanOptions options)
    {
        string folder = _folder.Text.Trim();
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            _status.Text = Loc.Get("Yara_FolderMissing");
            return;
        }

        _running?.Cancel();
        var cts = new CancellationTokenSource();
        _running = cts;
        _scan.IsEnabled = false;
        _cancel.IsEnabled = true;
        _folderResults.Items.Clear();
        _folderResults.Visibility = Visibility.Visible;
        Results.Visibility = Visibility.Collapsed;
        _status.Text = Loc.Get("Yara_Scanning");
        var targets = new MultiFileTargets { Folders = [folder], IncludeMasks = _mask.Text.Length == 0 ? "*" : _mask.Text };
        var found = new List<YaraFileMatch>();
        int files = 0, timedOut = 0;
        try
        {
            await Task.Run(() =>
            {
                foreach (string path in MultiFileSearch.Enumerate(targets, null, cts.Token))
                {
                    cts.Token.ThrowIfCancellationRequested();
                    try
                    {
                        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        YaraScanResult r = YaraScanner.Scan(rules, new YaraFileData(handle, RandomAccess.GetLength(handle)), options, cts.Token);
                        files++;
                        timedOut += r.TimedOut ? 1 : 0;
                        foreach (YaraRuleResult rule in r.Rules)
                        {
                            if (rule.Strings.Count == 0)
                            {
                                found.Add(new YaraFileMatch(path, rule.Rule.Name, null, 0, 0));
                            }

                            found.AddRange(rule.Strings.Select(s => new YaraFileMatch(path, rule.Rule.Name, s.Identifier, s.Offset, s.Length)));
                        }

                        if (r.Cancelled)
                        {
                            break;
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        AppLog.Info($"YARA: skipped {Path.GetFileName(path)}: {e.GetType().Name}");
                    }
                }
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_running == cts)
            {
                _running = null;
            }

            _scan.IsEnabled = true;
            _cancel.IsEnabled = false;
        }

        foreach (YaraFileMatch m in found)
        {
            _folderResults.Items.Add(m);
        }

        _status.Text = Loc.Format("Yara_FolderDone", files.ToString("N0", CultureInfo.CurrentCulture), found.Select(f => f.Path).Distinct().Count())
            + (timedOut > 0 ? " " + Loc.Get("Yara_TimedOut") : string.Empty);
    }

    /// <summary>スキャンを取り消す (それまでの結果を残す)。</summary>
    public void Cancel()
    {
        _running?.Cancel();
        Results.CancelRunning();
    }
}
