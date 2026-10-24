using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.App.Templates;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Debugging;
using HexEditor.Core.Templates.Editing;
using HexEditor.Core.Templates.Loading;
using HexEditor.Core.Templates.Syntax;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;

namespace HexEditor.App.Controls;

/// <summary>テンプレートエディタが頼む操作 (ウィンドウが実装する)。</summary>
public interface ITemplateEditorHost
{
    /// <summary>「試しに実行」・デバッグの対象に選べるドキュメント。</summary>
    IReadOnlyList<ViewModels.DocumentViewModel> TemplateTargets { get; }

    /// <summary>直前にフォーカスのあったドキュメント (対象の既定)。</summary>
    ViewModels.DocumentViewModel? LastTemplateTarget { get; }

    /// <summary>保存していない内容で適用する (「試しに実行」。TPL-31 の仕様 4)。</summary>
    void TryRunTemplate(ViewModels.DocumentViewModel target, string templatePath, TemplateProgram program);

    /// <summary>デバッグの適用を始める (TPL-32)。</summary>
    TemplateApplicationEntry? StartTemplateDebug(ViewModels.DocumentViewModel target, string templatePath, TemplateProgram program, TemplateDebugger debugger);

    /// <summary>デバッグを終えた (ドキュメントの読み取り専用を戻す)。</summary>
    void EndTemplateDebug(TemplateApplicationEntry entry);

    /// <summary>テンプレートを保存した (使っている適用を評価し直す。TPL-20 の仕様 6)。</summary>
    void TemplateSaved(string path);

    /// <summary>テンプレートのファイルを開く (定義へ移動。別のファイルなら新しいタブ)。</summary>
    Task OpenTemplateEditorAtAsync(string file, int line, int column);

    /// <summary>複製を作って開く (「複製して編集」。TPL-34 の仕様 5)。</summary>
    Task DuplicateTemplateAsync(TemplateEditorPage page);

    void ShowEditorNotice(string message, InfoBarSeverity severity);
}

/// <summary>変数・呼び出し履歴などの一覧の 1 行。</summary>
public sealed record TemplateDebugRow(string Name, string Value, string Detail)
{
    public override string ToString() => Detail.Length > 0 ? $"{Name} = {Value} ({Detail})" : $"{Name} = {Value}";
}

/// <summary>
/// テンプレートエディタ (TPL-31) とテンプレートデバッガ (TPL-32) のタブ。テキストは常に左から右 (00 の 5.4)。構文の色分け、
/// 入力中の構文チェック (300 ms ごと。波線・行の印・ツールチップ)、補完、定義へ移動、書式の整形、コメントの切り替え、
/// 「試しに実行」、ブレークポイント・ステップ実行・変数・ウォッチ・呼び出し履歴の表示。
/// エディタの部品はスクリプトエディタ (08 の AUTO-20) と共通の <see cref="IScriptCodeEditor"/> (<see cref="CodeEditorFactory"/> が作る。TPL-31 の仕様 2)。
/// </summary>
public sealed partial class TemplateEditorPage : UserControl, ICodeEditorHost, ICodeEditorKeys
{
    private readonly ITemplateEditorHost _host;
    private readonly IScriptCodeEditor _editor;
    private readonly ComboBox _target;
    private readonly ListView _problems;
    private readonly ListView _variables;
    private readonly ListView _watches;
    private readonly ListView _callStack;
    private readonly ListView _breakpoints;
    private readonly TextBox _watchInput;
    private readonly Grid _debugPane;
    private readonly InfoBar _info;
    private readonly Popup _completionPopup;
    private readonly ListView _completionList;
    private readonly CheckBox _eager;
    private readonly DispatcherTimer _checkTimer;
    private readonly ObservableCollection<string> _problemRows = [];
    private string _text = string.Empty;
    private string _savedText = string.Empty;
    private bool _crlf;
    private bool _suppressChange;
    private TemplateCompletionResult? _completion;
    private TemplateApplicationEntry? _debugEntry;

    public TemplateEditorPage(ITemplateEditorHost host, string? path, string text)
    {
        _host = host;
        Path = path ?? System.IO.Path.Combine(TemplateService.DefaultUserFolder, "untitled.hxt");
        IsNew = path is null;
        IsStandard = path is not null && TemplateCatalog.IsStandardPath(path);
        FlowDirection = FlowDirection.LeftToRight;
        AutomationProperties.SetAutomationId(this, "TemplateEditorPage");

        // ---- ツールバー ----
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(8, 6, 8, 6) };
        toolbar.Children.Add(ToolButton("TplEd_Save", "TplEd_Save", "", async () => await SaveAsync()));
        toolbar.Children.Add(ToolButton("TplEd_TryRun", "TplEd_TryRun", "", TryRun));
        _target = new ComboBox { MinWidth = 180, PlaceholderText = Loc.Get("TplEd_TargetPlaceholder") };
        AutomationProperties.SetAutomationId(_target, "TplEd_Target");
        AutomationProperties.SetName(_target, Loc.Get("TplEd_Target"));
        _target.DropDownOpened += (_, _) => RefreshTargets();
        toolbar.Children.Add(_target);
        toolbar.Children.Add(new AppBarSeparator());
        toolbar.Children.Add(ToolButton("TplEd_Debug", "TplEd_Debug", "", () => Run()));
        toolbar.Children.Add(ToolButton("TplEd_StepOver", "TplEd_StepOver", "", () => Step(TemplateDebugCommand.StepOver)));
        toolbar.Children.Add(ToolButton("TplEd_StepInto", "TplEd_StepInto", "", () => Step(TemplateDebugCommand.StepInto)));
        toolbar.Children.Add(ToolButton("TplEd_StepOut", "TplEd_StepOut", "", () => Step(TemplateDebugCommand.StepOut)));
        toolbar.Children.Add(ToolButton("TplEd_Stop", "TplEd_Stop", "", StopDebug));
        _eager = new CheckBox { Content = Loc.Get("TplEd_Eager"), IsChecked = true, MinWidth = 0 };
        AutomationProperties.SetAutomationId(_eager, "TplEd_Eager");
        toolbar.Children.Add(_eager);
        toolbar.Children.Add(new AppBarSeparator());
        toolbar.Children.Add(ToolButton("TplEd_Format", "TplEd_Format", "", FormatDocument));
        toolbar.Children.Add(ToolButton("TplEd_Comment", "TplEd_ToggleComment", "", ToggleComment));

        _info = new InfoBar { IsOpen = false, IsClosable = true, Margin = new Thickness(8, 0, 8, 4) };
        AutomationProperties.SetAutomationId(_info, "TplEd_Message");

        // ---- エディタ本体 ----
        _editor = CodeEditorFactory.Create("TplEd_Text", Loc.Get("TplEd_TextName"), CodeLanguage.Template, System.IO.Path.GetFileName(Path));
        // Monaco の補完の一覧と整形の操作から呼ばれる (テンプレート言語の部品。TPL-31 の仕様 3)
        _editor.CompletionProvider = (offset, _) => Task.FromResult<CodeCompletionList?>(MonacoCompletion(offset));
        _editor.Formatter = TemplateLanguageService.Format;
        _editor.TextChanged += (_, _) => Editor_TextChanged();
        _editor.CaretMoved += (_, _) => UpdateDecorations();
        _editor.PreviewKey = Editor_PreviewKey;
        _editor.CharacterTyped += Editor_CharacterTyped;
        _editor.GutterClicked += (_, line) => ToggleBreakpoint(line);
        _editor.ClassColor = (cls, theme) => TemplateBrushes.ThemeColor("TplSyntax" + cls + "Color", theme);

        _completionList = new ListView { MaxHeight = 220, MinWidth = 260, SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetAutomationId(_completionList, "TplEd_Completion");
        AutomationProperties.SetName(_completionList, Loc.Get("TplEd_CompletionName"));
        _completionList.DoubleTapped += (_, _) => AcceptCompletion();
        _completionPopup = new Popup
        {
            Child = new Border
            {
                Child = _completionList,
                Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
                BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
            },
            IsLightDismissEnabled = true,
        };

        // ---- デバッグの表示 (変数・ウォッチ・呼び出し履歴・ブレークポイント) ----
        _variables = DebugList("TplEd_Variables");
        _watches = DebugList("TplEd_Watches");
        _callStack = DebugList("TplEd_CallStack");
        _breakpoints = DebugList("TplEd_Breakpoints");
        _callStack.DoubleTapped += (_, _) =>
        {
            if (_callStack.SelectedItem is TemplateDebugRow { Detail.Length: > 0 } r && int.TryParse(r.Detail.Split(':')[^1], out int line))
            {
                MoveCaret(line, 1);
            }
        };
        _watchInput = new TextBox { PlaceholderText = Loc.Get("TplEd_AddWatchPlaceholder"), FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetAutomationId(_watchInput, "TplEd_WatchInput");
        AutomationProperties.SetName(_watchInput, Loc.Get("TplEd_AddWatch"));
        _watchInput.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                AddWatch(_watchInput.Text);
                _watchInput.Text = string.Empty;
            }
        };
        var debugStack = new StackPanel { Spacing = 4, Padding = new Thickness(8) };
        debugStack.Children.Add(Header("TplEd_VariablesHeader"));
        debugStack.Children.Add(_variables);
        debugStack.Children.Add(Header("TplEd_WatchesHeader"));
        debugStack.Children.Add(_watchInput);
        debugStack.Children.Add(_watches);
        debugStack.Children.Add(Header("TplEd_CallStackHeader"));
        debugStack.Children.Add(_callStack);
        debugStack.Children.Add(Header("TplEd_BreakpointsHeader"));
        debugStack.Children.Add(_breakpoints);
        _debugPane = new Grid { Width = 280, Visibility = Visibility.Collapsed };
        _debugPane.Children.Add(new ScrollViewer { Content = debugStack });

        _problems = new ListView { Height = 96, ItemsSource = _problemRows, SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetAutomationId(_problems, "TplEd_Problems");
        AutomationProperties.SetName(_problems, Loc.Get("TplEd_Problems"));
        _problems.DoubleTapped += (_, _) => GoToProblem();
        _problems.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                GoToProblem();
            }
        };

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(_editor.View);
        Grid.SetColumn(_debugPane, 1);
        body.Children.Add(_debugPane);

        var root = new Grid { Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"] };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(toolbar);
        Grid.SetRow(_info, 1);
        root.Children.Add(_info);
        Grid.SetRow(body, 2);
        root.Children.Add(body);
        Grid.SetRow(_problems, 3);
        root.Children.Add(_problems);
        root.Children.Add(_completionPopup);
        Content = root;

        _checkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _checkTimer.Tick += (_, _) =>
        {
            _checkTimer.Stop();
            Check();
        };

        Debugger.Paused += Debugger_Paused;
        Debugger.Resumed += Debugger_Resumed;
        SetText(text, markSaved: true);
        RefreshTargets();
    }

    // ───────────── 状態 ─────────────

    /// <summary>テンプレートのパス (新規作成は保存先の予定のパス)。</summary>
    public string Path { get; private set; }

    public bool IsNew { get; private set; }

    /// <summary>標準テンプレート (読み取り専用。上書き保存できない。TPL-34 の仕様 5)。</summary>
    public bool IsStandard { get; }

    public bool IsDirty => _text != _savedText;

    public string Text => _text;

    /// <summary>このエディタのブレークポイント・ウォッチ (デバッグの適用に渡す)。</summary>
    public TemplateDebugger Debugger { get; } = new();

    public bool IsDebugging => _debugEntry is { Result.Completion.IsCompleted: false };

    public TemplateApplicationEntry? DebugEntry => _debugEntry;

    /// <summary>今のエディタの装飾 (波線と行の印。テスト用の命令)。</summary>
    public IReadOnlyList<TemplateEditorDiagnostic> Decorations { get; private set; } = [];

    /// <summary>補完の候補 (開いていれば。Monaco では最後に一覧に渡した候補。テスト用の命令)。</summary>
    public TemplateCompletionResult? Completion => _completionPopup.IsOpen || _editor.IsMonaco ? _completion : null;

    /// <summary>コードエディタの部品 (テスト用の命令が Monaco の状態を読む)。</summary>
    internal IScriptCodeEditor CodeEditor => _editor;

    /// <summary>タブの見出し。</summary>
    public string Title => System.IO.Path.GetFileName(TemplateCatalog.IsStandardPath(Path) ? Path[(Path.IndexOf(':') + 1)..] : Path) + (IsDirty ? " ●" : string.Empty);

    public event EventHandler? TitleChanged;

    // ───────────── テキスト ─────────────

    /// <summary>テキストを置き換える (<paramref name="markSaved"/> なら保存した内容とみなす)。</summary>
    public void SetText(string text, bool markSaved = false)
    {
        _crlf = text.Contains("\r\n", StringComparison.Ordinal);
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        _suppressChange = true;
        try
        {
            _editor.Text = normalized;
        }
        finally
        {
            _suppressChange = false;
        }

        _text = _editor.Text;
        if (markSaved)
        {
            _savedText = _text;
        }

        TitleChanged?.Invoke(this, EventArgs.Empty);
        Check();
    }

    private void Editor_TextChanged()
    {
        if (_suppressChange)
        {
            return;
        }

        _text = _editor.Text;
        TitleChanged?.Invoke(this, EventArgs.Empty);
        _checkTimer.Stop();
        _checkTimer.Start();
        UpdateDecorations();
    }

    /// <summary>キャレットの位置 (文字の位置)。</summary>
    public int Caret
    {
        get => Math.Clamp(_editor.CaretIndex, 0, _text.Length);
        set => _editor.CaretIndex = Math.Clamp(value, 0, _text.Length);
    }

    /// <summary>キャレットの行 (1 から)。</summary>
    public int CaretLine => LineOf(Caret);

    private int LineOf(int position)
    {
        int line = 1;
        for (int i = 0; i < Math.Min(position, _text.Length); i++)
        {
            if (_text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private int PositionOf(int line, int column)
    {
        int p = 0;
        for (int l = 1; l < line && p < _text.Length; l++)
        {
            int nl = _text.IndexOf('\n', p);
            if (nl < 0)
            {
                return _text.Length;
            }

            p = nl + 1;
        }

        return Math.Min(_text.Length, p + Math.Max(0, column - 1));
    }

    /// <summary>行・列へキャレットを移す (定義へ移動、エラーの行)。</summary>
    public void MoveCaret(int line, int column)
    {
        _editor.GoTo(line, column);
        FocusEditor();
        UpdateDecorations();
    }

    public void FocusEditor() => _editor.FocusEditor();

    // ───────────── 構文チェックと色分け (TPL-31 の仕様 3) ─────────────

    private void Check()
    {
        string text = _text;
        string path = Path;
        Decorations = TemplateLanguageService.Diagnose(text, path, TemplateService.LoadOptions);
        _problemRows.Clear();
        foreach (TemplateEditorDiagnostic d in Decorations)
        {
            _problemRows.Add($"{d.Line}:{d.Column} {TemplateDiagnosticText.Line(d.Diagnostic)}");
        }

        ApplyFormatting();
        UpdateDecorations();
    }

    /// <summary>色分けと波線・現在の行を付け直す (テーマの色。ハイコントラストではシステム色だけにする。描くのはエディタの部品)。</summary>
    private void ApplyFormatting()
    {
        // 色分けは Monaco の字句解析がする。代替の入力欄だけ色の範囲を渡す (大きなテンプレートは色分けしない。TPL-31 の「巨大ファイル」)
        if (!_editor.IsMonaco)
        {
            _editor.SetClassification(_text.Length > 1024 * 1024 ? []
                : [.. TemplateLanguageService.Classify(_text).Select(s => new CodeSpan(s.Start, s.Length, s.Class.ToString()))]);
        }

        _editor.SetMarkers([.. Decorations.Select(d => new CodeMarker(d.Line, d.Column, Math.Max(1, d.Length),
            d.Diagnostic.Severity == TemplateSeverity.Error, TemplateDiagnosticText.Line(d.Diagnostic)))]);
        _editor.SetCurrentLine(_debugLine);
    }

    /// <summary>位置のエラー・警告の理由 (波線の上のツールチップ)。</summary>
    public string? ToolTipAt(int line, int column) =>
        Decorations.FirstOrDefault(d => d.Line == line && column >= d.Column && column < d.Column + Math.Max(1, d.Length))?.Diagnostic is { } diag
            ? TemplateDiagnosticText.Line(diag)
            : Decorations.FirstOrDefault(d => d.Line == line)?.Diagnostic is { } any ? TemplateDiagnosticText.Line(any) : null;

    // ───────────── 行の印 (ブレークポイント・現在の行。エラーの印は ApplyFormatting) ─────────────

    private void UpdateDecorations()
    {
        _editor.SetBreakpoints([.. Debugger.Breakpoints.Select(b => new HexEditor.Core.Scripting.Debugging.Breakpoint(b.Line) { Condition = b.Condition })]);
        _editor.SetCurrentLine(_debugLine);
    }

    // ───────────── 補完 (TPL-31 の仕様 3) ─────────────

    private void Editor_CharacterTyped(object? sender, char character)
    {
        if (character is '.' or ':' or '[')
        {
            DispatcherQueue.TryEnqueue(() => ShowCompletion(automatic: true));
        }
        else if (_completionPopup.IsOpen)
        {
            DispatcherQueue.TryEnqueue(() => ShowCompletion(automatic: true));
        }
    }

    /// <summary>Monaco の補完の一覧に渡す候補 (<c>.</c>・<c>::</c>・<c>[</c> の入力と Ctrl+Space)。</summary>
    private CodeCompletionList MonacoCompletion(int offset)
    {
        _text = _editor.Text;
        TemplateCompletionResult result = TemplateLanguageService.Complete(_text, Math.Clamp(offset, 0, _text.Length), Path, TemplateService.LoadOptions);
        _completion = result;
        return new CodeCompletionList(result.ReplaceStart, result.ReplaceLength,
            [.. result.Items.Take(500).Select(i => new CodeCompletionItem(i.Label, CompletionKind(i.Kind), i.Detail))]);
    }

    private static string CompletionKind(TemplateCompletionKind kind) => kind switch
    {
        TemplateCompletionKind.Keyword => "keyword",
        TemplateCompletionKind.BuiltinType => "builtinType",
        TemplateCompletionKind.Type => "type",
        TemplateCompletionKind.Function => "function",
        TemplateCompletionKind.Member => "member",
        TemplateCompletionKind.Pattern => "pattern",
        TemplateCompletionKind.Attribute => "attribute",
        TemplateCompletionKind.Pragma => "pragma",
        _ => "constant",
    };

    /// <summary>補完の候補を出す (Ctrl+Space、または <c>.</c> の入力の後)。</summary>
    public void ShowCompletion(bool automatic = false)
    {
        if (!automatic && _editor.TryExecute(CodeEditorAction.Complete))
        {
            return; // Monaco の補完の一覧 (候補は MonacoCompletion)
        }

        _text = _editor.Text;
        TemplateCompletionResult result = TemplateLanguageService.Complete(_text, Caret, Path, TemplateService.LoadOptions);
        _completion = result;
        if (result.Items.Count == 0 || (automatic && result.ReplaceLength == 0 && Caret > 0 && _text[Caret - 1] is not ('.' or ':' or '[')))
        {
            _completionPopup.IsOpen = false;
            return;
        }

        _completionList.Items.Clear();
        foreach (TemplateCompletion item in result.Items.Take(200))
        {
            var row = new ListViewItem { Content = item.Detail.Length > 0 ? $"{item.Label}  —  {item.Detail}" : item.Label, Tag = item };
            AutomationProperties.SetName(row, item.Detail.Length > 0 ? $"{item.Label}, {item.Detail}" : item.Label);
            _completionList.Items.Add(row);
        }

        _completionList.SelectedIndex = 0;
        Windows.Foundation.Rect caret = _editor.CaretBounds(this);
        _completionPopup.HorizontalOffset = caret.Left;
        _completionPopup.VerticalOffset = caret.Bottom + 2;
        _completionPopup.IsOpen = true;
    }

    private void AcceptCompletion()
    {
        if (_completion is not { } c || (_completionList.SelectedItem as ListViewItem)?.Tag is not TemplateCompletion item)
        {
            _completionPopup.IsOpen = false;
            return;
        }

        _editor.Replace(c.ReplaceStart, c.ReplaceLength, item.Label);
        _completionPopup.IsOpen = false;
    }

    /// <summary>補完の一覧が開いているときのキー (↑ ↓ で選び、Enter・Tab で確定、Esc で閉じる)。処理したら true。</summary>
    private bool Editor_PreviewKey(VirtualKey key)
    {
        if (!_completionPopup.IsOpen)
        {
            return false;
        }

        switch (key)
        {
            case VirtualKey.Down:
                _completionList.SelectedIndex = Math.Min(_completionList.Items.Count - 1, _completionList.SelectedIndex + 1);
                _completionList.ScrollIntoView(_completionList.SelectedItem);
                return true;
            case VirtualKey.Up:
                _completionList.SelectedIndex = Math.Max(0, _completionList.SelectedIndex - 1);
                _completionList.ScrollIntoView(_completionList.SelectedItem);
                return true;
            case VirtualKey.Enter or VirtualKey.Tab:
                AcceptCompletion();
                return true;
            case VirtualKey.Escape:
                _completionPopup.IsOpen = false;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// エディタ部品の標準の編集キー (00-overview 8.1: グローバルの同じキーより優先する)。Ctrl+Space で補完、Ctrl+/ でコメントの切り替え、
    /// Ctrl+G で行へ移動、Shift+Alt+F で整形。処理したら true。
    /// </summary>
    public bool HandleEditorKey(VirtualKey key, bool ctrl, bool shift, bool alt)
    {
        if (_editor.IsMonaco || !_editor.HasFocus)
        {
            return false; // Monaco は自分で処理する。ウォッチ式の入力欄など、エディタ以外の部品
        }

        switch (key)
        {
            case VirtualKey.Space when ctrl && !shift && !alt:
                ShowCompletion();
                return true;
            case (VirtualKey)191 when ctrl && !alt: // OEM_2 (US 配列の / ?)
            case VirtualKey.Divide when ctrl && !alt:
                ToggleComment();
                return true;
            case VirtualKey.G when ctrl && !shift && !alt:
                ShowGoToLine();
                return true;
            case VirtualKey.F when shift && alt && !ctrl:
                FormatDocument();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Ctrl+G: 行番号を入力して移動する (エディタの上に小さな入力欄を出す)。</summary>
    internal void ShowGoToLine()
    {
        if (_editor.TryExecute(CodeEditorAction.GoToLine))
        {
            return;
        }

        var box = new TextBox { PlaceholderText = Loc.Format("TplEd_GoToLineRange", LineCount), MinWidth = 200, FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetAutomationId(box, "TplEd_GoToLineInput");
        AutomationProperties.SetName(box, Loc.Get("TplEd_GoToLine"));
        var flyout = new Flyout { Content = box, Placement = FlyoutPlacementMode.Top };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                if (int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int line) && line >= 1)
                {
                    flyout.Hide();
                    MoveCaret(Math.Min(line, LineCount), 1);
                    _editor.FocusEditor();
                }
            }
        };
        flyout.ShowAt(_editor.View);
        box.Focus(FocusState.Programmatic);
    }

    private int LineCount => _text.Count(c => c == '\n') + 1;

    // ───────────── 定義へ移動・整形・コメント ─────────────

    /// <summary>F12: キャレットの名前の定義へ (インポート先のファイルは別のタブで開く。TPL-31 の仕様 3)。</summary>
    public async Task GoToDefinitionAsync()
    {
        _text = _editor.Text;
        if (TemplateLanguageService.FindDefinition(_text, Caret, Path, TemplateService.LoadOptions) is { } at)
        {
            await _host.OpenTemplateEditorAtAsync(at.File, at.Line, at.Column);
        }
    }

    public void FormatDocument()
    {
        if (_editor.TryExecute(CodeEditorAction.Format))
        {
            return; // Monaco の整形の操作 (元に戻せる。結果は Formatter)
        }

        int line = CaretLine;
        SetText(TemplateLanguageService.Format(_text));
        MoveCaret(line, 1);
    }

    public void ToggleComment()
    {
        if (_editor.TryExecute(CodeEditorAction.ToggleComment))
        {
            return;
        }

        int first = LineOf(_editor.Selection.Start);
        int last = LineOf(_editor.Selection.End);
        SetText(TemplateLanguageService.ToggleComment(_text, first, last));
        MoveCaret(first, 1);
    }

    /// <summary>参照の検索: このファイルの中の同じ名前の位置を問題の一覧に出す。</summary>
    public void FindReferences()
    {
        IReadOnlyList<(int Line, int Column)> refs = TemplateLanguageService.FindReferences(_text, Caret);
        _problemRows.Clear();
        foreach ((int line, int column) in refs)
        {
            _problemRows.Add(Loc.Format("TplEd_Reference", line, column));
        }
    }

    private void GoToProblem()
    {
        if (_problems.SelectedItem is string row && row.Split(' ')[0].Split(':') is [var l, var c, ..]
            && int.TryParse(l, out int line) && int.TryParse(c, out int column))
        {
            MoveCaret(line, column);
        }
    }

    // ───────────── 保存 (TPL-31 の仕様 6、TPL-34 の仕様 5) ─────────────

    public async Task<bool> SaveAsync()
    {
        _text = _editor.Text;
        if (IsStandard)
        {
            // 標準テンプレートは直接保存できない。「複製して編集」を案内する
            ShowInfo(Loc.Get("TplEd_StandardReadOnly"), InfoBarSeverity.Warning, Loc.Get("TplEd_Duplicate"), async () => await _host.DuplicateTemplateAsync(this));
            return false;
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            string content = _crlf ? _text.Replace("\n", "\r\n", StringComparison.Ordinal) : _text;
            string temp = Path + ".tmp";
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false));
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存に失敗: 理由を表示し、編集内容は残す (TPL-31 の「エラー」)
            ShowInfo(Loc.Format("TplEd_SaveError", ex.Message), InfoBarSeverity.Error);
            return false;
        }

        _savedText = _text;
        IsNew = false;
        TitleChanged?.Invoke(this, EventArgs.Empty);
        _host.TemplateSaved(Path);
        ShowInfo(null, InfoBarSeverity.Informational);
        return true;
    }

    /// <summary>保存先の既定 (未保存の新しいタブのまま。テンプレートの推定の「エディタで開く」。TPL-40 の仕様 6)。</summary>
    public void SetSuggestedPath(string path)
    {
        Path = path;
        TitleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>内容を未保存にする (保存していない新しい内容。テンプレートの推定の下書き)。</summary>
    public void MarkUnsaved()
    {
        _savedText = string.Empty;
        TitleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>名前を付けて保存したのと同じにする (新規作成・複製の保存先)。</summary>
    public void SetPath(string path)
    {
        Path = path;
        IsNew = false;
        TitleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowInfo(string? message, InfoBarSeverity severity, string? action = null, Action? onAction = null)
    {
        _info.IsOpen = message is not null;
        _info.Message = message ?? string.Empty;
        _info.Severity = severity;
        if (action is not null && onAction is not null)
        {
            var button = new Button { Content = action };
            AutomationProperties.SetAutomationId(button, "TplEd_DuplicateButton");
            button.Click += (_, _) => onAction();
            _info.ActionButton = button;
        }
        else
        {
            _info.ActionButton = null;
        }

        LastMessage = message;
    }

    /// <summary>最後に出した案内 (テスト用の命令)。</summary>
    public string? LastMessage { get; private set; }

    // ───────────── 試しに実行 (TPL-31 の仕様 4) ─────────────

    private void RefreshTargets()
    {
        object? keep = _target.SelectedItem;
        _target.Items.Clear();
        foreach (ViewModels.DocumentViewModel d in _host.TemplateTargets)
        {
            _target.Items.Add(new ComboBoxItem { Content = d.DisplayName, Tag = d });
        }

        ComboBoxItem? match = _target.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, (keep as ComboBoxItem)?.Tag))
            ?? _target.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, _host.LastTemplateTarget))
            ?? _target.Items.OfType<ComboBoxItem>().FirstOrDefault();
        _target.SelectedItem = match;
    }

    /// <summary>対象のドキュメントを選ぶ (テスト用の命令)。</summary>
    /// <summary>対象のドキュメントの名前 (テスト用の命令)。</summary>
    internal string? TargetName => Target?.DisplayName;

    public void SelectTarget(ViewModels.DocumentViewModel doc)
    {
        RefreshTargets();
        _target.SelectedItem = _target.Items.OfType<ComboBoxItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, doc));
    }

    private ViewModels.DocumentViewModel? Target
    {
        get
        {
            if (_target.SelectedItem is null)
            {
                RefreshTargets();
            }

            return (_target.SelectedItem as ComboBoxItem)?.Tag as ViewModels.DocumentViewModel;
        }
    }

    private TemplateProgram Compile()
    {
        _text = _editor.Text;
        string? dir = TemplateCatalog.IsStandardPath(Path) ? null : System.IO.Path.GetDirectoryName(Path);
        return TemplateLoader.LoadText(_text, System.IO.Path.GetFileName(Path), dir, TemplateService.LoadOptions);
    }

    public void TryRun()
    {
        if (Target is not { } target)
        {
            ShowInfo(Loc.Get("TplEd_NoTarget"), InfoBarSeverity.Warning);
            return;
        }

        _host.TryRunTemplate(target, Path, Compile());
    }

    // ───────────── デバッグ (TPL-32) ─────────────

    private int? _debugLine;

    /// <summary>F5: デバッグ中でなければ始め (ブレークポイントがなければ最後まで評価する)、止まっていれば続行する。</summary>
    public void Run()
    {
        if (Debugger.IsPaused)
        {
            Debugger.Resume(TemplateDebugCommand.Continue);
            return;
        }

        StartDebug(stopOnEntry: false);
    }

    /// <summary>ステップ実行 (デバッグ中でなければ最初の文で止まって始める)。</summary>
    public void Step(TemplateDebugCommand command)
    {
        if (!IsDebugging)
        {
            StartDebug(stopOnEntry: true);
            return;
        }

        if (Debugger.IsPaused)
        {
            Debugger.Resume(command);
        }
    }

    public void RunToCursor()
    {
        if (!IsDebugging)
        {
            Debugger.SetBreakpoint(new TemplateBreakpoint(Path, CaretLine) { HitCount = 1 });
            StartDebug(stopOnEntry: false);
            return;
        }

        Debugger.Resume(TemplateDebugCommand.RunToCursor, Path, CaretLine);
    }

    public void StopDebug()
    {
        if (_debugEntry is { } entry)
        {
            Debugger.Resume(TemplateDebugCommand.Stop);
            entry.Result?.Cancel();
        }
    }

    private void StartDebug(bool stopOnEntry)
    {
        if (Target is not { } target)
        {
            ShowInfo(Loc.Get("TplEd_NoTarget"), InfoBarSeverity.Warning);
            return;
        }

        Debugger.ResetHits();
        Debugger.Eager = _eager.IsChecked != false;
        Debugger.StopOnEntry = stopOnEntry;
        TemplateProgram program = Compile();
        if (program.HasErrors)
        {
            Check();
            ShowInfo(Loc.Get("TplEd_SyntaxErrors"), InfoBarSeverity.Error);
            return;
        }

        if (Debugger.Eager)
        {
            ShowInfo(Loc.Get("TplEd_EagerNote"), InfoBarSeverity.Informational);
        }

        _debugEntry = _host.StartTemplateDebug(target, Path, program, Debugger);
        _debugPane.Visibility = Visibility.Visible;
        if (_debugEntry?.Result is { } result)
        {
            TemplateApplicationEntry entry = _debugEntry;
            _ = result.Completion.ContinueWith(_ => DispatcherQueue.TryEnqueue(() => DebugFinished(entry)), TaskScheduler.Default);
        }
    }

    private void DebugFinished(TemplateApplicationEntry entry)
    {
        if (!ReferenceEquals(entry, _debugEntry))
        {
            return;
        }

        _debugLine = null;
        _host.EndTemplateDebug(entry);
        ApplyFormatting();
        UpdateDecorations();
        DebugEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>デバッグが終わった (読み取り専用を戻した後)。</summary>
    public event EventHandler? DebugEnded;

    /// <summary>止まった (評価器のスレッドから)。</summary>
    private void Debugger_Paused(object? sender, TemplateDebugState state) => DispatcherQueue.TryEnqueue(() => ShowPaused(state));

    private void Debugger_Resumed(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!Debugger.IsPaused)
        {
            _debugLine = null;
            ApplyFormatting();
            UpdateDecorations();
        }
    });

    /// <summary>止まった位置を表示する: 現在の行を強調し、変数・ウォッチ・呼び出し履歴を出す (TPL-32 の仕様 3)。</summary>
    private void ShowPaused(TemplateDebugState state)
    {
        if (!Debugger.IsPaused || !ReferenceEquals(Debugger.State, state))
        {
            return;
        }

        bool sameFile = string.Equals(System.IO.Path.GetFileName(state.Location.File), System.IO.Path.GetFileName(Path), StringComparison.OrdinalIgnoreCase);
        _debugLine = sameFile ? state.Location.Line : null;
        _variables.Items.Clear();
        foreach (TemplateDebugVariable v in state.Locals)
        {
            _variables.Items.Add(new TemplateDebugRow(v.Name, v.Value, string.Empty));
        }

        foreach (TemplateDebugVariable v in state.Patterns)
        {
            _variables.Items.Add(new TemplateDebugRow(v.Name, v.Value, v.Offset is { } o ? "0x" + o.ToString("X", CultureInfo.InvariantCulture) : string.Empty));
        }

        _variables.Items.Add(new TemplateDebugRow("$", "0x" + state.Position.ToString("X", CultureInfo.InvariantCulture), string.Empty));
        _watches.Items.Clear();
        foreach ((string expr, string value) in state.Watches)
        {
            _watches.Items.Add(new TemplateDebugRow(expr, value, string.Empty));
        }

        _callStack.Items.Clear();
        foreach (TemplateDebugFrame f in state.CallStack)
        {
            _callStack.Items.Add(new TemplateDebugRow(f.Name, string.Empty, f.Location is { } at ? System.IO.Path.GetFileName(at.File) + ":" + at.Line : string.Empty));
        }

        RefreshBreakpointList();
        if (state.Error is { } error)
        {
            ShowInfo(TemplateDiagnosticText.Line(error), InfoBarSeverity.Error);
        }

        if (_debugLine is { } line)
        {
            MoveCaret(line, 1);
        }

        ApplyFormatting();
        UpdateDecorations();
        Paused?.Invoke(this, state);
    }

    /// <summary>止まった位置を表示した (Hex ビューの <c>$</c> の印を描き直す)。</summary>
    public event EventHandler<TemplateDebugState>? Paused;

    /// <summary>F9: キャレットの行 (または指定の行) のブレークポイントを設定・解除する。</summary>
    public void ToggleBreakpoint(int? line = null)
    {
        Debugger.ToggleBreakpoint(Path, line ?? CaretLine);
        RefreshBreakpointList();
        UpdateDecorations();
    }

    private void RefreshBreakpointList()
    {
        _breakpoints.Items.Clear();
        foreach (TemplateBreakpoint b in Debugger.Breakpoints)
        {
            _breakpoints.Items.Add(new TemplateDebugRow(System.IO.Path.GetFileName(b.File) + ":" + b.Line, b.Condition ?? string.Empty, string.Empty));
        }

        foreach (long offset in Debugger.DataBreakpoints)
        {
            _breakpoints.Items.Add(new TemplateDebugRow(Loc.Get("TplEd_DataBreakpoint"), "0x" + offset.ToString("X", CultureInfo.InvariantCulture), string.Empty));
        }
    }

    /// <summary>ウォッチ式を加える (止まっていればすぐ評価する)。</summary>
    public void AddWatch(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return;
        }

        Debugger.AddWatch(expression);
        string value = Debugger.IsPaused && _debugEntry?.Result is { } r ? r.EvaluateInPausedFrame(expression) : string.Empty;
        _watches.Items.Add(new TemplateDebugRow(expression.Trim(), value, string.Empty));
    }

    /// <summary>「ここを読むときに止める」(データのブレークポイント)。</summary>
    public void AddDataBreakpoint(long offset)
    {
        Debugger.AddDataBreakpoint(offset);
        RefreshBreakpointList();
    }

    /// <summary>テスト用の命令: 表示している一覧。</summary>
    internal IReadOnlyList<string> VariableRows => [.. _variables.Items.Select(i => i!.ToString()!)];

    internal IReadOnlyList<string> WatchRows => [.. _watches.Items.Select(i => i!.ToString()!)];

    internal IReadOnlyList<string> CallStackRows => [.. _callStack.Items.Select(i => i!.ToString()!)];

    internal int? DebugLine => _debugLine;

    // ───────────── コードエディタのコマンド (codeEditor.*。MainWindow.CodeEditor.cs が振り分ける) ─────────────

    public CommandState GetCodeEditorCommandState(string commandId) => commandId switch
    {
        CodeEditorCommands.Stop => IsDebugging ? CommandState.Available : CommandState.Unavailable(Loc.Get("TplEd_NotDebugging")),
        _ => CommandState.Available,
    };

    public async Task ExecuteCodeEditorCommandAsync(string commandId)
    {
        switch (commandId)
        {
            case CodeEditorCommands.Run: Run(); break;
            case CodeEditorCommands.Stop: StopDebug(); break;
            case CodeEditorCommands.ToggleBreakpoint: ToggleBreakpoint(); break;
            case CodeEditorCommands.StepOver: Step(TemplateDebugCommand.StepOver); break;
            case CodeEditorCommands.StepInto: Step(TemplateDebugCommand.StepInto); break;
            case CodeEditorCommands.StepOut: Step(TemplateDebugCommand.StepOut); break;
            case CodeEditorCommands.RunToCursor: RunToCursor(); break;
            case CodeEditorCommands.GoToDefinition: await GoToDefinitionAsync(); break;
            case CodeEditorCommands.FindReferences: FindReferences(); break;
            case CodeEditorCommands.Format: FormatDocument(); break;
            case CodeEditorCommands.ToggleComment: ToggleComment(); break;
            case CodeEditorCommands.Complete: ShowCompletion(); break;
        }
    }

    /// <summary>タブを閉じた後の後始末 (エディタの部品の WebView2 を閉じる)。</summary>
    public void Close()
    {
        Debugger.Paused -= Debugger_Paused;
        Debugger.Resumed -= Debugger_Resumed;
        _checkTimer.Stop();
        _editor.Dispose();
    }

    // ───────────── 部品 ─────────────

    private static Button ToolButton(string id, string labelKey, string glyph, Action click)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 } };
        AutomationProperties.SetAutomationId(button, id);
        AutomationProperties.SetName(button, Loc.Get(labelKey));
        ToolTipService.SetToolTip(button, Loc.Get(labelKey));
        button.Click += (_, _) => click();
        return button;
    }

    private static ListView DebugList(string id)
    {
        var list = new ListView { MaxHeight = 200, SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetAutomationId(list, id);
        AutomationProperties.SetName(list, Loc.Get(id));
        return list;
    }

    private static TextBlock Header(string key) => new()
    {
        Text = Loc.Get(key),
        Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
    };
}
