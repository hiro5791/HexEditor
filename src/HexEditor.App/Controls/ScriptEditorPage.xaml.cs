using System.Text;
using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.App.Services.Scripting;
using HexEditor.App.ViewModels;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Debugging;
using HexEditor.Core.Scripting.Editing;
using HexEditor.Core.Scripting.Repl;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>スクリプトエディタの窓の機能 (ドキュメントの一覧、デバッガのアタッチの案内など)。MainWindow が実装する。</summary>
internal interface IScriptEditorHost : IScriptPanelHost
{
    /// <summary>実行の対象にできるドキュメントの名前と、そのタブ。</summary>
    IReadOnlyList<(string Name, object Document)> OpenDocuments { get; }

    /// <summary>今アクティブなドキュメントのタブ (なければ null)。</summary>
    object? ActiveDocument { get; }

    void ActivateDocument(object document);

    /// <summary>C# スクリプトのデバッグ実行: 「デバッガのアタッチを待っています」のダイアログ。実行してよければ true (AUTO-22 の仕様 2)。</summary>
    Task<bool> ConfirmAttachDebuggerAsync(int processId);

    void ShowNotice(string text, bool error);

    /// <summary>コマンドの状態 (F5 など) を更新する。</summary>
    void RefreshCommandStates();

    /// <summary>デバッグ中のコンソールの評価を使えるようにする (止まっている位置のスコープ。AUTO-21 の仕様 5)。</summary>
    void SetDebugSession(JsDebugSession? session);
}

/// <summary>
/// スクリプトエディタ (AUTO-20) の画面と操作。実行・デバッグは <see cref="ScriptEditorSession"/>、構文のチェックは
/// <see cref="ScriptSyntaxChecker"/>。コードのエディタ部品は <see cref="IScriptCodeEditor"/> (テンプレートエディタと共通。<see cref="CodeEditorFactory"/>)。
/// </summary>
public sealed partial class ScriptEditorPage : UserControl, Panels.IPanelContent, ICodeEditorHost
{
    /// <summary>スクリプトファイルの上限 (AUTO-20 の「巨大ファイル」: 10 MB。超える場合は外部のエディタで編集するよう案内する)。</summary>
    public const long MaxFileBytes = 10 * 1024 * 1024;

    /// <summary>入力が止まってから構文を調べるまでの待ち (AUTO-20 の仕様 4)。</summary>
    public static readonly TimeSpan CheckDelay = TimeSpan.FromMilliseconds(500);

    private readonly IScriptEditorHost _host;
    private readonly IScriptCodeEditor? _editor;
    private readonly ScriptEditorSession _session;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _checkTimer;
    private readonly Dictionary<Microsoft.UI.Xaml.Controls.TreeViewNode, ReplValue> _variableValues = [];
    private readonly List<ScriptDiagnostic> _runDiagnostics = [];
    private byte[] _preamble = [];
    private string _newline = "\r\n";
    private string _savedText = string.Empty;
    private int _lineCount = 1;
    private bool _loading;

    internal ScriptEditorPage(string path, IScriptEditorHost host)
    {
        Path = System.IO.Path.GetFullPath(path);
        _host = host;
        InitializeComponent();
        _session = new ScriptEditorSession(Path, DispatcherQueue);
        Language = _session.Language;
        _checkTimer = DispatcherQueue.CreateTimer();
        _checkTimer.IsRepeating = false;
        _checkTimer.Interval = CheckDelay;
        _checkTimer.Tick += (_, _) => RunCheck();
        LocalizeToolbar();

        long length = 0;
        try
        {
            length = new FileInfo(Path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (length > MaxFileBytes)
        {
            // 10 MB を超えるスクリプトは編集しない (外部のエディタで編集するよう案内する)。
            EditorHost.Content = new TextBlock
            {
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap,
                Text = Loc.Format("ScriptEditor_TooLarge", System.IO.Path.GetFileName(Path)),
            };
            DisableToolbar();
            return;
        }

        // コードエディタの部品はテンプレートエディタ (07 の TPL-31) と共通 (CodeEditorFactory)。
        _editor = CodeEditorFactory.Create("ScriptEditor_Text", Loc.Format("ScriptEditor_Name", System.IO.Path.GetFileName(Path)));
        EditorHost.Content = _editor.View;
        AutomationProperties.SetName(_editor.View, Loc.Format("ScriptEditor_Name", System.IO.Path.GetFileName(Path)));
        Load();
        _editor.TextChanged += Editor_TextChanged;
        _editor.CaretMoved += (_, _) => UpdateStatus();
        _editor.GutterClicked += (_, line) => ToggleBreakpoint(line);
        ScriptWorkspace.Breakpoints.Changed += Breakpoints_Changed;
        _session.StateChanged += Session_StateChanged;
        _session.Paused += Session_Paused;
        _session.Ended += Session_Ended;
        PopulateTargets();
        RefreshBreakpoints();
        RefreshToolbar();
        RunCheck();
    }

    /// <summary>ファイルのパス (フルパス)。</summary>
    public string Path { get; }

    public ScriptLanguage Language { get; }

    /// <summary>未保存の変更があるか。</summary>
    public bool IsDirty { get; private set; }

    /// <summary>エディタの本文 (テスト・実行に使う)。</summary>
    public string Text
    {
        get => _editor?.Text ?? string.Empty;
        set
        {
            if (_editor is not null)
            {
                _editor.Text = value;
                MarkDirty();
            }
        }
    }

    internal IScriptCodeEditor? Editor => _editor;

    internal ScriptEditorSession Session => _session;

    /// <summary>問題の一覧の行数 (テスト用)。</summary>
    public int ProblemCount => ProblemsList.Items.Count;

    bool Panels.IPanelContent.FocusContent() => _editor?.FocusEditor() ?? false;

    private void LocalizeToolbar()
    {
        foreach ((AppBarButton button, string key) in new[]
        {
            (RunButton, "ScriptEditor_Run"), (DebugButton, "ScriptEditor_Debug"), (StopButton, "ScriptEditor_Stop"), (PauseButton, "ScriptEditor_Pause"),
            (StepOverButton, "ScriptEditor_StepOver"), (StepInButton, "ScriptEditor_StepIn"), (StepOutButton, "ScriptEditor_StepOut"),
        })
        {
            button.Label = Loc.Get(key);
            AutomationProperties.SetName(button, Loc.Get(key));
            ToolTipService.SetToolTip(button, Loc.Get(key));
        }

        AutomationProperties.SetName(TargetBox, Loc.Get("ScriptEditor_Target"));
        ProblemsTab.Content = Loc.Get("ScriptEditor_Problems");
        DebugTab.Content = Loc.Get("ScriptEditor_DebugTab");
        CallStackLabel.Text = Loc.Get("ScriptEditor_CallStack");
        VariablesLabel.Text = Loc.Get("ScriptEditor_Variables");
        WatchLabel.Text = Loc.Get("ScriptEditor_Watches");
        WatchBox.PlaceholderText = Loc.Get("ScriptEditor_WatchAdd");
        AutomationProperties.SetName(WatchBox, Loc.Get("ScriptEditor_WatchAdd"));
        AutomationProperties.SetName(ProblemsList, Loc.Get("ScriptEditor_Problems"));
        AutomationProperties.SetName(CallStackList, Loc.Get("ScriptEditor_CallStack"));
        AutomationProperties.SetName(VariablesTree, Loc.Get("ScriptEditor_Variables"));
        AutomationProperties.SetName(WatchList, Loc.Get("ScriptEditor_Watches"));
    }

    private void DisableToolbar()
    {
        foreach (AppBarButton b in new[] { RunButton, DebugButton, StopButton, PauseButton, StepOverButton, StepInButton, StepOutButton })
        {
            b.IsEnabled = false;
        }
    }

    // ---- 読み込みと保存 ----

    private void Load()
    {
        _loading = true;
        try
        {
            byte[] bytes = [];
            try
            {
                bytes = File.ReadAllBytes(Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _host.ShowNotice(Loc.Format("ScriptEditor_ReadFailed", ex.Message), error: true);
            }

            _preamble = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? [0xEF, 0xBB, 0xBF] : [];
            string text = new UTF8Encoding(false).GetString(bytes, _preamble.Length, bytes.Length - _preamble.Length);
            _newline = text.Contains("\r\n", StringComparison.Ordinal) || !text.Contains('\n') ? "\r\n" : "\n";
            _editor!.Text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
            _savedText = _editor.Text;
            IsDirty = false;
            _lineCount = _editor.Text.Count(c => c == '\n') + 1;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>保存する (Ctrl+S)。アプリ内のエディタで保存したスクリプトは利用者が書いたものとして信頼済みにする (AUTO-20 の仕様 6)。</summary>
    public bool Save()
    {
        if (_editor is null)
        {
            return false;
        }

        try
        {
            string text = _editor.Text.Replace("\n", _newline, StringComparison.Ordinal);
            string temp = Path + ".tmp";
            File.WriteAllBytes(temp, [.. _preamble, .. new UTF8Encoding(false).GetBytes(text)]);
            File.Move(temp, Path, overwrite: true);
            ScriptingService.Trust.TrustSavedScript(Path, text, TrustSource.SavedInApp);
            _savedText = _editor.Text;
            IsDirty = false;
            UpdateStatus();
            _host.ShowStatus(Loc.Format("ScriptEditor_Saved", System.IO.Path.GetFileName(Path)));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _host.ShowNotice(Loc.Format("ScriptEditor_SaveFailed", ex.Message), error: true);
            return false;
        }
    }

    private void MarkDirty()
    {
        IsDirty = _editor is not null && _editor.Text != _savedText;
        UpdateStatus();
    }

    /// <summary>ファイルを開いたまま外で書き換えられたとき、未保存の変更がなければ読み直す (一覧の監視から呼ぶ)。</summary>
    public void ReloadIfUnchanged()
    {
        if (_editor is not null && !IsDirty)
        {
            string current = _editor.Text;
            Load();
            if (_editor.Text != current)
            {
                RunCheck();
            }
        }
    }

    // ---- 編集 ----

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        if (_loading)
        {
            return;
        }

        // 行の挿入・削除でブレークポイントの行をずらす (カーソルの行から後ろ)。
        int lines = _editor!.Text.Count(c => c == '\n') + 1;
        if (lines != _lineCount)
        {
            ScriptWorkspace.Breakpoints.Shift(Path, _editor.CaretPosition.Line, lines - _lineCount);
            _lineCount = lines;
        }

        MarkDirty();
        _runDiagnostics.Clear();
        _checkTimer.Stop();
        _checkTimer.Start();
    }

    /// <summary>入力が止まったら構文を調べ、波線と問題の一覧に出す (AUTO-20 の仕様 4)。</summary>
    private void RunCheck()
    {
        if (_editor is null)
        {
            return;
        }

        string text = _editor.Text;
        if (text.Length > 1_000_000)
        {
            return;
        }

        _ = Task.Run(() => ScriptSyntaxChecker.Check(Language, text, Path)).ContinueWith(task => DispatcherQueue.TryEnqueue(() =>
        {
            if (task.IsCompletedSuccessfully && _editor.Text == text)
            {
                ShowProblems([.. task.Result, .. _runDiagnostics]);
            }
        }), TaskScheduler.Default);
    }

    private void ShowProblems(IReadOnlyList<ScriptDiagnostic> diagnostics)
    {
        ProblemsList.ItemsSource = diagnostics.Select(d => new ProblemRow(d)).ToList();
        _editor?.SetMarkers([.. diagnostics.Where(d => d.Line > 0).Select(d => new CodeMarker(d.Line, Math.Max(1, d.Column), 1, d.IsError, d.Message))]);
        ProblemsTab.Content = diagnostics.Count > 0
            ? Loc.Format("ScriptEditor_ProblemsCount", diagnostics.Count)
            : Loc.Get("ScriptEditor_Problems");
    }

    private void Problems_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProblemsList.SelectedItem is ProblemRow { Diagnostic.Line: > 0 } row)
        {
            _editor?.GoTo(row.Diagnostic.Line, Math.Max(1, row.Diagnostic.Column));
        }
    }

    /// <summary>行の位置へ移動する (出力パネルのリンク。AUTO-24 の仕様 3)。</summary>
    public void GoToLine(int line, int column) => _editor?.GoTo(line, column);

    /// <summary>カーソルの行 (1 から。テスト・F9 用)。</summary>
    public int CaretLine => _editor?.CaretPosition.Line ?? 1;

    private void UpdateStatus()
    {
        if (_editor is null)
        {
            return;
        }

        (int line, int column) = _editor.CaretPosition;
        string state = _session.State switch
        {
            EditorRunState.Running => Loc.Get("ScriptEditor_StateRunning"),
            EditorRunState.Paused => Loc.Format("ScriptEditor_StatePaused", _session.CurrentPause?.Line ?? 0),
            _ => string.Empty,
        };
        StatusText.Text = string.Join("   ", new[]
        {
            Loc.Format("ScriptEditor_Position", line, column),
            IsDirty ? Loc.Get("ScriptEditor_Modified") : string.Empty,
            state,
        }.Where(s => s.Length > 0));
    }

    // ---- ブレークポイント (AUTO-21 の仕様 1・8) ----

    /// <summary>行のブレークポイントを設定・解除する (余白のクリック・F9)。C# では何もしない。</summary>
    public void ToggleBreakpoint(int? line = null)
    {
        if (_editor is null)
        {
            return;
        }

        if (Language == ScriptLanguage.CSharp)
        {
            _host.ShowStatus(Loc.Get("ScriptEditor_CSharpNoStep"));
            return;
        }

        ScriptWorkspace.Breakpoints.Toggle(Path, line ?? _editor.CaretPosition.Line);
    }

    private void Breakpoints_Changed(object? sender, string path)
    {
        if (string.Equals(System.IO.Path.GetFullPath(path), Path, StringComparison.OrdinalIgnoreCase))
        {
            DispatcherQueue.TryEnqueue(RefreshBreakpoints);
        }
    }

    private void RefreshBreakpoints() => _editor?.SetBreakpoints(ScriptWorkspace.Breakpoints.For(Path));

    /// <summary>設定されているブレークポイントの行 (テスト用)。</summary>
    public IReadOnlyList<int> BreakpointLines => [.. ScriptWorkspace.Breakpoints.For(Path).Select(b => b.Line)];

    // ---- 実行とデバッグ ----

    private void Session_StateChanged(object? sender, EventArgs e)
    {
        RefreshToolbar();
        if (_session.State == EditorRunState.Running)
        {
            _editor?.SetCurrentLine(null);
        }

        UpdateStatus();
        _host.RefreshCommandStates();
    }

    private void RefreshToolbar()
    {
        bool idle = _session.State == EditorRunState.Idle;
        bool paused = _session.State == EditorRunState.Paused;
        bool js = Language == ScriptLanguage.JavaScript;
        RunButton.IsEnabled = idle || paused;
        DebugButton.IsEnabled = idle;
        StopButton.IsEnabled = !idle;
        PauseButton.IsEnabled = _session.State == EditorRunState.Running && _session.Debug is not null;
        StepOverButton.IsEnabled = js && (paused || idle);
        StepInButton.IsEnabled = js && (paused || idle);
        StepOutButton.IsEnabled = js && paused;
    }

    /// <summary>実行 / 続行 (F5)。一時停止中なら続行する。</summary>
    public void RunOrContinue()
    {
        if (_editor is null)
        {
            return;
        }

        if (_session.State == EditorRunState.Paused)
        {
            _session.Continue();
        }
        else if (_session.State == EditorRunState.Idle)
        {
            Start(debug: false);
        }
    }

    /// <summary>デバッグ実行。JavaScript はブレークポイントで止まる。C# は外部のデバッガのアタッチを待つ (AUTO-22)。</summary>
    public async void DebugRun()
    {
        if (_editor is null || _session.State != EditorRunState.Idle)
        {
            return;
        }

        if (Language == ScriptLanguage.CSharp)
        {
            if (!await _host.ConfirmAttachDebuggerAsync(Environment.ProcessId))
            {
                return;
            }
        }

        Start(debug: true);
    }

    private void Start(bool debug, DebugStart start = DebugStart.Run)
    {
        ApplyTarget();
        _runDiagnostics.Clear();
        _host.SetDebugSession(null);
        ScriptRun? run = _session.Start(_editor!.Text, debug, start);
        if (run is not null && _session.Debug is { } session)
        {
            _host.SetDebugSession(session);
        }
    }

    public void StopRun() => _session.Stop();

    public void StepOver()
    {
        if (Language == ScriptLanguage.CSharp)
        {
            _host.ShowStatus(Loc.Get("ScriptEditor_CSharpNoStep"));
        }
        else if (_session.State == EditorRunState.Idle)
        {
            Start(debug: true, DebugStart.StepIn);
        }
        else
        {
            _session.StepOver();
        }
    }

    public void StepIn()
    {
        if (Language == ScriptLanguage.CSharp)
        {
            _host.ShowStatus(Loc.Get("ScriptEditor_CSharpNoStep"));
        }
        else if (_session.State == EditorRunState.Idle)
        {
            Start(debug: true, DebugStart.StepIn);
        }
        else
        {
            _session.StepIn();
        }
    }

    public void StepOut()
    {
        if (Language == ScriptLanguage.CSharp)
        {
            _host.ShowStatus(Loc.Get("ScriptEditor_CSharpNoStep"));
        }
        else
        {
            _session.StepOut();
        }
    }

    public void PauseRun() => _session.PauseNow();

    // ---- コードエディタのコマンド (codeEditor.*。MainWindow.CodeEditor.cs が振り分ける。00-overview 8.5) ----

    public CommandState GetCodeEditorCommandState(string commandId) => commandId switch
    {
        CodeEditorCommands.Run or CodeEditorCommands.Stop or CodeEditorCommands.ToggleBreakpoint or CodeEditorCommands.StepOver
            or CodeEditorCommands.StepInto or CodeEditorCommands.StepOut or CodeEditorCommands.AddWatch => CommandState.Available,
        _ => CodeEditorCommands.NotSupported, // 定義へ移動・補完などは Monaco を同梱するときに加える (AUTO-20 の実装の注記)
    };

    public Task ExecuteCodeEditorCommandAsync(string commandId)
    {
        switch (commandId)
        {
            case CodeEditorCommands.Run: RunOrContinue(); break;
            case CodeEditorCommands.Stop: StopRun(); break;
            case CodeEditorCommands.ToggleBreakpoint: ToggleBreakpoint(); break;
            case CodeEditorCommands.StepOver: StepOver(); break;
            case CodeEditorCommands.StepInto: StepIn(); break;
            case CodeEditorCommands.StepOut: StepOut(); break;
        }

        return Task.CompletedTask;
    }

    private void Run_Click(object sender, RoutedEventArgs e) => RunOrContinue();

    private void Debug_Click(object sender, RoutedEventArgs e) => DebugRun();

    private void Stop_Click(object sender, RoutedEventArgs e) => StopRun();

    private void Pause_Click(object sender, RoutedEventArgs e) => PauseRun();

    private void StepOver_Click(object sender, RoutedEventArgs e) => StepOver();

    private void StepIn_Click(object sender, RoutedEventArgs e) => StepIn();

    private void StepOut_Click(object sender, RoutedEventArgs e) => StepOut();

    // ---- 対象のドキュメント ----

    private void PopulateTargets()
    {
        TargetBox.Items.Clear();
        TargetBox.Items.Add(new TargetItem(Loc.Get("ScriptEditor_TargetActive"), null));
        foreach ((string name, object document) in _host.OpenDocuments)
        {
            TargetBox.Items.Add(new TargetItem(name, document));
        }

        TargetBox.SelectedIndex = 0;
    }

    private sealed record TargetItem(string Name, object? Document);

    private void Target_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    /// <summary>実行の前に、選んだ対象のドキュメントをアクティブにする (スクリプトの <c>doc</c> と <c>app.activeDocument</c> がそれを指す)。</summary>
    private void ApplyTarget()
    {
        if (TargetBox.SelectedItem is TargetItem { Document: { } document })
        {
            _host.ActivateDocument(document);
        }
    }

    /// <summary>対象のドキュメントの一覧を作り直す (ドキュメントを開いた・閉じたとき)。</summary>
    internal void RefreshTargets()
    {
        object? selected = (TargetBox.SelectedItem as TargetItem)?.Document;
        PopulateTargets();
        for (int i = 0; i < TargetBox.Items.Count; i++)
        {
            if (TargetBox.Items[i] is TargetItem item && ReferenceEquals(item.Document, selected))
            {
                TargetBox.SelectedIndex = i;
            }
        }
    }

    // ---- 実行の結果とデバッグの表示 ----

    private void Session_Ended(object? sender, ScriptRunResult result)
    {
        _editor?.SetCurrentLine(null);
        _host.SetDebugSession(null);
        VariablesTree.RootNodes.Clear();
        _variableValues.Clear();
        CallStackList.ItemsSource = null;
        _runDiagnostics.Clear();
        foreach (ScriptDiagnostic d in result.Diagnostics.Where(d => d.Line > 0 && (d.Path is null || string.Equals(System.IO.Path.GetFullPath(d.Path), Path, StringComparison.OrdinalIgnoreCase))))
        {
            _runDiagnostics.Add(d);
        }

        if (_runDiagnostics.Count > 0)
        {
            RunCheck();
            ProblemsTab.IsChecked = true;
            ShowTab(debug: false);
        }

        StatusText.Text = result.Status switch
        {
            ScriptRunStatus.Succeeded => Loc.Format("ScriptEditor_Succeeded", result.Elapsed.TotalSeconds.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture)),
            ScriptRunStatus.Stopped => Loc.Get("ScriptEditor_Stopped"),
            ScriptRunStatus.NotRun => Loc.Get("ScriptEditor_NotRun"),
            _ => Loc.Get("ScriptEditor_Failed"),
        };
    }

    private async void Session_Paused(object? sender, DebugPause pause)
    {
        if (pause.Path is not null && string.Equals(System.IO.Path.GetFullPath(pause.Path), Path, StringComparison.OrdinalIgnoreCase))
        {
            _editor?.SetCurrentLine(pause.Line);
            _editor?.GoTo(pause.Line, pause.Column);
        }

        UpdateStatus();
        ShowTab(debug: true);
        CallStackList.ItemsSource = pause.Frames.Select(f => $"{f.Function}  :{f.Line}").ToList();
        FillVariables(pause);
        await RefreshWatchesAsync();
    }

    private void FillVariables(DebugPause pause)
    {
        VariablesTree.RootNodes.Clear();
        _variableValues.Clear();
        if (!string.IsNullOrEmpty(pause.Exception))
        {
            VariablesTree.RootNodes.Add(new Microsoft.UI.Xaml.Controls.TreeViewNode { Content = Loc.Format("ScriptEditor_Exception", pause.Exception) });
        }

        foreach (DebugScopeInfo scope in pause.Scopes)
        {
            var scopeNode = new Microsoft.UI.Xaml.Controls.TreeViewNode { Content = ScopeTitle(scope.Kind), IsExpanded = scope.Kind == "local" };
            foreach (DebugVariable variable in scope.Variables)
            {
                scopeNode.Children.Add(VariableNode(variable.Name, variable.Value));
            }

            VariablesTree.RootNodes.Add(scopeNode);
        }
    }

    private static string ScopeTitle(string kind) => kind switch
    {
        "local" => Loc.Get("ScriptEditor_ScopeLocal"),
        "closure" => Loc.Get("ScriptEditor_ScopeClosure"),
        "global" => Loc.Get("ScriptEditor_ScopeGlobal"),
        "script" => Loc.Get("ScriptEditor_ScopeScript"),
        _ => kind,
    };

    private Microsoft.UI.Xaml.Controls.TreeViewNode VariableNode(string name, ReplValue value)
    {
        string summary = value.Text.Contains('\n', StringComparison.Ordinal) ? value.Type : value.Display;
        var node = new Microsoft.UI.Xaml.Controls.TreeViewNode
        {
            Content = $"{name} = {summary}",
            HasUnrealizedChildren = value.Expandable,
        };
        _variableValues[node] = value;
        return node;
    }

    private void Variables_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        Microsoft.UI.Xaml.Controls.TreeViewNode node = args.Node;
        if (node.HasUnrealizedChildren && _variableValues.TryGetValue(node, out ReplValue? value))
        {
            node.Children.Clear();
            foreach (ReplProperty child in value.Children)
            {
                node.Children.Add(VariableNode(child.Name, child.Value));
            }

            node.HasUnrealizedChildren = false;
        }
    }

    private void CallStack_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = CallStackList.SelectedIndex;
        if (index >= 0 && _session.CurrentPause is { } pause && index < pause.Frames.Count)
        {
            DebugFrame frame = pause.Frames[index];
            if (frame.Path is not null && string.Equals(System.IO.Path.GetFullPath(frame.Path), Path, StringComparison.OrdinalIgnoreCase))
            {
                _editor?.GoTo(frame.Line, frame.Column);
            }
        }
    }

    // ---- ウォッチ (AUTO-21 の仕様 4) ----

    private void Watch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && WatchBox.Text.Trim() is { Length: > 0 } expression)
        {
            AddWatch(expression);
            WatchBox.Text = string.Empty;
            e.Handled = true;
        }
    }

    private void WatchList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete && WatchList.SelectedItem is WatchRow row)
        {
            _watches.Remove(row.Expression);
            _ = RefreshWatchesAsync();
            e.Handled = true;
        }
    }

    private readonly List<string> _watches = [];

    /// <summary>ウォッチ式を足す。</summary>
    public void AddWatch(string expression)
    {
        _watches.Add(expression);
        _ = RefreshWatchesAsync();
    }

    private async Task RefreshWatchesAsync()
    {
        var rows = new List<WatchRow>();
        if (_session.Debug is { State: DebugState.Paused } debug)
        {
            foreach (string w in debug.Watches.ToList())
            {
                debug.RemoveWatch(w);
            }

            foreach (string w in _watches)
            {
                debug.AddWatch(w);
            }

            try
            {
                rows.AddRange((await debug.EvaluateWatchesAsync()).Select(r => new WatchRow(r)));
            }
            catch (InvalidOperationException)
            {
                // 評価の途中で再開した。
            }
        }
        else
        {
            rows.AddRange(_watches.Select(w => new WatchRow(new WatchResult(w, null, null))));
        }

        WatchList.ItemsSource = rows;
    }

    /// <summary>ウォッチ式の一覧 (テスト用)。</summary>
    public IReadOnlyList<string> WatchExpressions => _watches;

    /// <summary>ウォッチの現在の表示 (テスト用: 式と値の文字列)。</summary>
    public IReadOnlyList<(string Expression, string Value, bool IsError)> WatchRows =>
        [.. (WatchList.ItemsSource as IEnumerable<WatchRow> ?? []).Select(r => (r.Expression, r.ValueText, r.IsError))];

    /// <summary>変数のツリーの文字列 (テスト用: スコープごとの変数の「名前 = 値」)。</summary>
    public IReadOnlyList<(string Scope, IReadOnlyList<string> Variables)> VariableSummary =>
        [.. VariablesTree.RootNodes.Select(scope => (scope.Content?.ToString() ?? string.Empty,
            (IReadOnlyList<string>)[.. scope.Children.Select(c => c.Content?.ToString() ?? string.Empty)]))];

    /// <summary>呼び出し履歴の文字列 (テスト用)。</summary>
    public IReadOnlyList<string> CallStackItems => [.. (CallStackList.ItemsSource as IEnumerable<string> ?? [])];

    // ---- 下部の切り替え ----

    private void ProblemsTab_Click(object sender, RoutedEventArgs e) => ShowTab(debug: false);

    private void DebugTab_Click(object sender, RoutedEventArgs e) => ShowTab(debug: true);

    private void ShowTab(bool debug)
    {
        ProblemsTab.IsChecked = !debug;
        DebugTab.IsChecked = debug;
        ProblemsList.Visibility = debug ? Visibility.Collapsed : Visibility.Visible;
        DebugPanel.Visibility = debug ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- 後始末 ----

    private void Detach()
    {
        _checkTimer.Stop();
        ScriptWorkspace.Breakpoints.Changed -= Breakpoints_Changed;
        _session.Dispose();
    }

    /// <summary>閉じる前の後始末 (実行中なら止める)。</summary>
    public void Close() => Detach();
}
