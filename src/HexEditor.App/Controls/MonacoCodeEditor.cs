using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.App.Commands;
using HexEditor.App.Services;
using HexEditor.Core.Commands;
using HexEditor.Core.Scripting.Debugging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace HexEditor.App.Controls;

/// <summary>
/// Monaco Editor (同梱。WebView2 の中) でできたコードエディタ (08 の AUTO-20 の仕様 1、07 の TPL-31 の仕様 2)。スクリプトエディタと
/// テンプレートエディタの両方が <see cref="IScriptCodeEditor"/> を通して使う。
/// <para>
/// ページ (<c>Assets/Monaco/host/index.html</c>) はアプリ内の仮想ホスト <see cref="HostName"/> から読み込み、ほかの場所への移動・
/// 通信はすべて止める (インターネットから読み込まない。AUTO-20 の仕様 1)。本文の写しはこちらで持ち、ページから届く変更の差分で保つ
/// (取得は同期で返せる)。こちらからの変更はページに送り、ページはその変更を送り返さない。
/// </para>
/// <para>
/// WebView2 ランタイムがない・初期化に失敗した・ページの処理が落ちた場合は、標準のリッチテキストの入力欄の代替 (<see cref="CodeEditor"/>) に
/// 切り替え、上に InfoBar で理由とランタイムの入れ方を出す (AUTO-20 の仕様 9 と「エラー」)。切り替えた後も本文・カーソル・印は引き継ぐ。
/// </para>
/// メッセージ (アプリ → ページ): init / setText / replace / readOnly / caret / goto / focus / breakpoints / currentLine / markers / theme /
/// keys / chord / action / aria / getText / reply。(ページ → アプリ): hello / ready / error / changed / sel / focus / gutter / key /
/// emarkers / complete / format / text。
/// </summary>
public sealed partial class MonacoCodeEditor : Grid, IScriptCodeEditor, IDisposable
{
    /// <summary>同梱のファイルを見せる仮想ホストの名前 (.invalid は実在しない名前。外の場所と取り違えない)。</summary>
    public const string HostName = "monaco.hexeditor.invalid";

    private const string Origin = "https://" + HostName + "/";

    /// <summary>ページの準備ができるまでの待ちの上限 (超えたら代替に切り替える)。</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(60);

    private static readonly List<WeakReference<MonacoCodeEditor>> Live = [];
    private static Task<CoreWebView2Environment>? s_environment;
    private static Windows.UI.ViewManagement.UISettings? s_uiSettings;

    private readonly string _automationId;
    private readonly string _accessibleName;
    private readonly CodeLanguage _language;
    private readonly string _fileName;
    private readonly InfoBar _info = new() { IsOpen = false, IsClosable = true, Severity = InfoBarSeverity.Warning, Margin = new Thickness(0, 0, 0, 4) };
    private readonly List<string> _requests = [];
    private WebView2? _web;
    private CodeEditor? _plain;
    private DispatcherTimer? _readyTimer;
    private string _text = string.Empty;
    private int _selStart;
    private int _selEnd;
    private int _caret;
    private Rect _caretRect;
    private bool _readOnly;
    private bool _initSent;
    private bool _focusOnReady;
    private bool _jsFocused;
    private bool _disposed;
    private IReadOnlyList<Breakpoint> _breakpoints = [];
    private IReadOnlyList<CodeMarker> _markers = [];
    private IReadOnlyList<CodeSpan> _spans = [];
    private int? _currentLine;
    private Func<VirtualKey, bool>? _previewKey;
    private Func<string, string, Color?>? _classColor;

    public MonacoCodeEditor(string automationId, string accessibleName, CodeLanguage language, string fileName)
    {
        _automationId = automationId;
        _accessibleName = accessibleName;
        _language = language;
        _fileName = string.IsNullOrEmpty(fileName) ? "untitled" : fileName;
        FlowDirection = FlowDirection.LeftToRight;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        AutomationProperties.SetAutomationId(_info, automationId + "_Fallback");
        Children.Add(_info);
        lock (Live)
        {
            Live.Add(new WeakReference<MonacoCodeEditor>(this));
        }

        EnsureSystemColorWatch();
        ActualThemeChanged += (_, _) => PostTheme();
        CommandService.Keys.Changed += Keys_Changed;

        string? version = null;
        try
        {
            if (!SimulateFailure)
            {
                version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warning("Code editor: WebView2 runtime not found: " + ex.Message);
        }

        if (string.IsNullOrEmpty(version))
        {
            FallBack(Loc.Get("CodeEditor_WebView2Missing"));
            return;
        }

        _web = new WebView2
        {
            FlowDirection = FlowDirection.LeftToRight,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        AutomationProperties.SetAutomationId(_web, automationId);
        AutomationProperties.SetName(_web, accessibleName);
        SetRow(_web, 1);
        Children.Add(_web);
        _ = InitializeAsync();
    }

    /// <summary>テスト用: 次に作るエディタの WebView2 の初期化を失敗させる (代替の表示の確認)。</summary>
    internal static bool SimulateFailure { get; set; }

    public FrameworkElement View => this;

    /// <summary>Monaco が使える状態か (代替に切り替えたら false)。</summary>
    public bool IsMonaco => _plain is null;

    /// <summary>ページの準備ができたか (テスト用)。</summary>
    internal bool IsReady { get; private set; }

    /// <summary>代替に切り替えた理由 (テスト用。切り替えていなければ null)。</summary>
    internal string? FallbackReason { get; private set; }

    /// <summary>ページが読み込んだ (読み込もうとした) 場所 (テスト用。AUTO-20 の受け入れ基準 5)。</summary>
    internal IReadOnlyList<string> RequestedUris
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>中の WebView2 (テスト用の命令がページの状態を読む)。</summary>
    internal WebView2? WebView => _web;

    public string Text
    {
        get => _plain?.Text ?? _text;
        set
        {
            string normalized = Normalize(value);
            if (_plain is { } plain)
            {
                plain.Text = normalized;
                return;
            }

            bool changed = normalized != _text;
            _text = normalized;
            _selStart = Math.Min(_selStart, _text.Length);
            _selEnd = Math.Min(_selEnd, _text.Length);
            _caret = Math.Min(_caret, _text.Length);
            if (changed && _initSent)
            {
                Post(new JsonObject { ["t"] = "setText", ["text"] = normalized });
            }
        }
    }

    public bool IsReadOnly
    {
        get => _plain?.IsReadOnly ?? _readOnly;
        set
        {
            _readOnly = value;
            if (_plain is { } plain)
            {
                plain.IsReadOnly = value;
            }
            else if (_initSent)
            {
                Post(new JsonObject { ["t"] = "readOnly", ["value"] = value });
            }
        }
    }

    public (int Line, int Column) CaretPosition
    {
        get
        {
            if (_plain is { } plain)
            {
                return plain.CaretPosition;
            }

            int index = Math.Clamp(_caret, 0, _text.Length);
            int lineStart = index == 0 ? 0 : _text.LastIndexOf('\n', index - 1) + 1;
            int line = 1;
            for (int i = 0; i < lineStart; i++)
            {
                if (_text[i] == '\n')
                {
                    line++;
                }
            }

            return (line, index - lineStart + 1);
        }
    }

    public int CaretIndex
    {
        get => _plain?.CaretIndex ?? Math.Clamp(_caret, 0, _text.Length);
        set
        {
            if (_plain is { } plain)
            {
                plain.CaretIndex = value;
                return;
            }

            int p = Math.Clamp(value, 0, _text.Length);
            _caret = _selStart = _selEnd = p;
            if (_initSent)
            {
                Post(new JsonObject { ["t"] = "caret", ["start"] = p });
            }
        }
    }

    public (int Start, int End) Selection => _plain?.Selection ?? (Math.Clamp(_selStart, 0, _text.Length), Math.Clamp(_selEnd, 0, _text.Length));

    public bool HasFocus => _plain?.HasFocus ?? (_web is { } w && (w.FocusState != FocusState.Unfocused || _jsFocused));

    public event EventHandler? TextChanged;

    public event EventHandler? CaretMoved;

    public event EventHandler<int>? GutterClicked;

    public event EventHandler<char>? CharacterTyped;

    public event EventHandler<IReadOnlyList<CodeMarker>>? EditorMarkersChanged;

    public Func<VirtualKey, bool>? PreviewKey
    {
        get => _previewKey;
        set
        {
            _previewKey = value;
            if (_plain is { } plain)
            {
                plain.PreviewKey = value;
            }
        }
    }

    public Func<string, string, Color?>? ClassColor
    {
        get => _classColor;
        set
        {
            _classColor = value;
            if (_plain is { } plain)
            {
                plain.ClassColor = value;
            }
        }
    }

    public Func<int, string, Task<CodeCompletionList?>>? CompletionProvider { get; set; }

    public Func<string, string?>? Formatter { get; set; }

    public bool FocusEditor()
    {
        if (_plain is { } plain)
        {
            return plain.FocusEditor();
        }

        if (_web is null)
        {
            return false;
        }

        bool focused = _web.Focus(FocusState.Programmatic);
        if (IsReady)
        {
            Post(new JsonObject { ["t"] = "focus" });
        }
        else
        {
            _focusOnReady = true;
        }

        return focused;
    }

    public void GoTo(int line, int column)
    {
        if (_plain is { } plain)
        {
            plain.GoTo(line, column);
            return;
        }

        _caret = _selStart = _selEnd = OffsetOf(line, column);
        if (_initSent)
        {
            Post(new JsonObject { ["t"] = "goto", ["line"] = Math.Max(1, line), ["column"] = Math.Max(1, column), ["focus"] = true });
        }

        _web?.Focus(FocusState.Programmatic);
    }

    public void Replace(int start, int length, string text)
    {
        if (_plain is { } plain)
        {
            plain.Replace(start, length, text);
            return;
        }

        string t = Normalize(text);
        int s = Math.Clamp(start, 0, _text.Length);
        int e = Math.Clamp(start + length, s, _text.Length);
        _text = string.Concat(_text.AsSpan(0, s), t, _text.AsSpan(e));
        _caret = _selStart = _selEnd = s + t.Length;
        if (_initSent)
        {
            Post(new JsonObject { ["t"] = "replace", ["start"] = s, ["length"] = e - s, ["text"] = t });
        }

        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    public Rect CaretBounds(UIElement relativeTo)
    {
        if (_plain is { } plain)
        {
            return plain.CaretBounds(relativeTo);
        }

        if (_web is null)
        {
            return default;
        }

        Point origin = _web.TransformToVisual(relativeTo).TransformPoint(new Point(_caretRect.X, _caretRect.Y));
        return new Rect(origin.X, origin.Y, 1, Math.Max(1, _caretRect.Height));
    }

    public void SetBreakpoints(IReadOnlyList<Breakpoint> breakpoints)
    {
        bool same = _breakpoints.Count == breakpoints.Count
            && _breakpoints.Zip(breakpoints).All(p => p.First.Line == p.Second.Line && p.First.Enabled == p.Second.Enabled && p.First.HasCondition == p.Second.HasCondition);
        _breakpoints = breakpoints;
        if (_plain is { } plain)
        {
            plain.SetBreakpoints(breakpoints);
        }
        else if (_initSent && !same)
        {
            // 同じなら送らない (入力のたびに呼ばれる)。行の数が変わったときは ApplyChanges が送り直す
            PostBreakpoints();
        }
    }

    private void PostBreakpoints() => Post(new JsonObject { ["t"] = "breakpoints", ["items"] = BreakpointsJson() });

    public void SetCurrentLine(int? line)
    {
        bool same = _currentLine == line;
        _currentLine = line;
        if (_plain is { } plain)
        {
            plain.SetCurrentLine(line);
        }
        else if (_initSent && !same)
        {
            Post(new JsonObject { ["t"] = "currentLine", ["line"] = line });
        }
    }

    public void SetMarkers(IReadOnlyList<CodeMarker> markers)
    {
        bool same = _markers.SequenceEqual(markers);
        _markers = markers;
        if (_plain is { } plain)
        {
            plain.SetMarkers(markers);
        }
        else if (_initSent && !same)
        {
            Post(new JsonObject { ["t"] = "markers", ["items"] = MarkersJson() });
        }
    }

    /// <summary>色分けは Monaco の字句解析 (Monarch) がするので、代替のときだけ使う。</summary>
    public void SetClassification(IReadOnlyList<CodeSpan> spans)
    {
        _spans = spans;
        _plain?.SetClassification(spans);
    }

    public bool TryExecute(CodeEditorAction action)
    {
        if (_plain is not null || !IsReady)
        {
            return false;
        }

        string? id = action switch
        {
            CodeEditorAction.Format => "format",
            CodeEditorAction.ToggleComment => "comment",
            CodeEditorAction.GoToLine => "goToLine",
            CodeEditorAction.Complete => "complete",
            CodeEditorAction.Find => "find",
            CodeEditorAction.Replace => "replace",
            CodeEditorAction.GoToDefinition => "editor.action.revealDefinition",
            CodeEditorAction.FindReferences => "editor.action.goToReferences",
            _ => null,
        };
        if (id is null)
        {
            return false;
        }

        _web?.Focus(FocusState.Programmatic);
        Post(new JsonObject { ["t"] = "action", ["id"] = id });
        return true;
    }

    /// <summary>
    /// ページで JavaScript の式を評価する (テスト用の命令がエディタの状態を読む)。式が Promise なら待つ。結果は JSON の文字列
    /// (式の値が文字列ならその文字列を JSON にしたもの)。
    /// </summary>
    internal async Task<string?> EvaluateAsync(string expression)
    {
        if (_web?.CoreWebView2 is not { } core || !IsReady)
        {
            return null;
        }

        int id = Interlocked.Increment(ref _nextEval);
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _evals[id] = done;
        string script = $$"""
            (async () => {
              let value;
              try { value = await ({{expression}}); } catch (e) { value = { error: String(e && e.stack || e) }; }
              window.chrome.webview.postMessage({ t: 'evalResult', id: {{id}}, value: JSON.stringify(value === undefined ? null : value) });
            })(); 0
            """;
        await core.ExecuteScriptAsync(script);
        Task finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        _evals.Remove(id);
        return finished == done.Task ? await done.Task : null;
    }

    private readonly Dictionary<int, TaskCompletionSource<string?>> _evals = [];
    private int _nextEval;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CommandService.Keys.Changed -= Keys_Changed;
        _readyTimer?.Stop();
        lock (Live)
        {
            Live.RemoveAll(w => !w.TryGetTarget(out MonacoCodeEditor? e) || ReferenceEquals(e, this));
        }

        if (_web is { } web)
        {
            _web = null;
            try
            {
                web.Close();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
            }
        }
    }

    // ───────────── WebView2 の準備 ─────────────

    private static Task<CoreWebView2Environment> SharedEnvironment()
    {
        // 1 つのプロセスで 1 つ (同じユーザーデータのフォルダ・同じ設定)。昇格中は別のフォルダ (整合性レベルの違うプロセスで共有できない)
        return s_environment ??= Create();

        static async Task<CoreWebView2Environment> Create()
        {
            Platform.IAppEnvironment env = Hosting.Program.Environment;
            string root = env.IsDataDirectoryWritable ? env.Locations.Root : env.Locations.Temp;
            string folder = Path.Combine(root, env.IsElevated ? "WebView2-Admin" : "WebView2");
            var options = new CoreWebView2EnvironmentOptions
            {
                // 外への通信をしない (更新の確認・テレメトリなどの裏の通信も止める)
                AdditionalBrowserArguments = "--disable-background-networking --disable-component-update --disable-sync --no-pings --disable-domain-reliability",
                Language = Hosting.Localization.CurrentLanguage,
            };
            return await CoreWebView2Environment.CreateWithOptionsAsync(null, folder, options);
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (SimulateFailure)
            {
                throw new InvalidOperationException("Simulated WebView2 failure (test).");
            }

            CoreWebView2Environment environment = await SharedEnvironment();
            if (_disposed || _web is null)
            {
                return;
            }

            _web.DefaultBackgroundColor = MonacoTheme.Background(this);
            await _web.EnsureCoreWebView2Async(environment);
            if (_disposed || _web?.CoreWebView2 is not { } core)
            {
                return;
            }

            CoreWebView2Settings settings = core.Settings;
#if DEBUG
            settings.AreDevToolsEnabled = true;
#else
            settings.AreDevToolsEnabled = false;
#endif
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.AreHostObjectsAllowed = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsPinchZoomEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsBuiltInErrorPageEnabled = false;
            settings.IsReputationCheckingRequired = false;
            settings.IsWebMessageEnabled = true;
            settings.IsScriptEnabled = true;

            core.SetVirtualHostNameToFolderMapping(HostName, AssetsFolder, CoreWebView2HostResourceAccessKind.Deny);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += Core_WebResourceRequested;
            core.NavigationStarting += (_, e) =>
            {
                if (!IsLocal(e.Uri))
                {
                    e.Cancel = true;
                }
            };
            core.FrameNavigationStarting += (_, e) =>
            {
                if (!IsLocal(e.Uri))
                {
                    e.Cancel = true;
                }
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.PermissionRequested += (_, e) =>
                e.State = e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead && IsLocal(e.Uri)
                    ? CoreWebView2PermissionState.Allow
                    : CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += Core_WebMessageReceived;
            core.ProcessFailed += (_, e) => DispatcherQueue.TryEnqueue(() =>
            {
                AppLog.Warning($"Code editor: WebView2 process failed ({e.ProcessFailedKind}).");
                if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                    or CoreWebView2ProcessFailedKind.BrowserProcessExited)
                {
                    FallBack(Loc.Get("CodeEditor_WebView2Failed"));
                }
            });

            _readyTimer = new DispatcherTimer { Interval = ReadyTimeout };
            _readyTimer.Tick += (_, _) =>
            {
                _readyTimer.Stop();
                if (!IsReady)
                {
                    FallBack(Loc.Get("CodeEditor_WebView2Failed"));
                }
            };
            _readyTimer.Start();
            core.Navigate(Origin + "host/index.html");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warning("Code editor: WebView2 initialization failed: " + ex.Message);
            FallBack(Loc.Get(ex is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x80070002) } ? "CodeEditor_WebView2Missing" : "CodeEditor_WebView2Failed"));
        }
    }

    /// <summary>同梱のファイルの場所 (vs/ が Monaco、host/ がこのページ)。</summary>
    internal static string AssetsFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "Monaco");

    private static bool IsLocal(string? uri) => uri is not null && uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase);

    private void Core_WebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        string uri = e.Request.Uri;
        lock (_requests)
        {
            if (_requests.Count < 1000)
            {
                _requests.Add(uri);
            }
        }

        // 仮想ホスト以外への要求はすべて断る (インターネットから読み込まない。AUTO-20 の仕様 1)
        if (!IsLocal(uri) && !uri.StartsWith("blob:", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            e.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Forbidden", string.Empty);
        }
    }

    // ───────────── 代替への切り替え ─────────────

    private void FallBack(string reason)
    {
        if (_plain is not null || _disposed)
        {
            return;
        }

        FallbackReason = reason;
        _readyTimer?.Stop();
        bool hadFocus = HasFocus;
        if (_web is { } web)
        {
            _web = null;
            Children.Remove(web);
            try
            {
                web.Close();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
            }
        }

        IsReady = false;
        var plain = new CodeEditor(_automationId, _accessibleName);
        plain.ClassColor = _classColor;
        plain.PreviewKey = _previewKey;
        plain.Text = _text;
        plain.IsReadOnly = _readOnly;
        plain.CaretIndex = Math.Clamp(_caret, 0, _text.Length);
        plain.SetBreakpoints(_breakpoints);
        plain.SetMarkers(_markers);
        plain.SetClassification(_spans);
        plain.SetCurrentLine(_currentLine);
        plain.TextChanged += (_, e) => TextChanged?.Invoke(this, e);
        plain.CaretMoved += (_, e) => CaretMoved?.Invoke(this, e);
        plain.GutterClicked += (_, line) => GutterClicked?.Invoke(this, line);
        plain.CharacterTyped += (_, c) => CharacterTyped?.Invoke(this, c);
        SetRow(plain, 1);
        Children.Add(plain);
        _plain = plain;

        _info.Title = Loc.Get("CodeEditor_FallbackTitle");
        _info.Message = reason;
        var link = new HyperlinkButton { Content = Loc.Get("CodeEditor_GetWebView2"), NavigateUri = new Uri("https://go.microsoft.com/fwlink/p/?LinkId=2124703") };
        AutomationProperties.SetAutomationId(link, _automationId + "_GetWebView2");
        _info.ActionButton = link;
        _info.IsOpen = true;
        if (hadFocus)
        {
            plain.FocusEditor();
        }

        EditorMarkersChanged?.Invoke(this, []);
    }

    // ───────────── ページとのやり取り ─────────────

    private void Post(JsonObject message)
    {
        if (_web?.CoreWebView2 is not { } core || _disposed)
        {
            return;
        }

        try
        {
            core.PostWebMessageAsJson(message.ToJsonString());
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ObjectDisposedException)
        {
            AppLog.Warning("Code editor: post failed: " + ex.Message);
        }
    }

    private void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsLocal(e.Source) || _plain is not null)
        {
            return;
        }

        JsonObject? m;
        try
        {
            m = JsonNode.Parse(e.WebMessageAsJson) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        if (m?["t"]?.GetValue<string>() is not { } type)
        {
            return;
        }

        switch (type)
        {
            case "hello":
                SendInit();
                break;
            case "ready":
                IsReady = true;
                _readyTimer?.Stop();
                if (m["len"]?.GetValue<int>() is int len && len != _text.Length)
                {
                    // 準備中にこちらで変えた本文を送り直す
                    Post(new JsonObject { ["t"] = "setText", ["text"] = _text, ["reset"] = true });
                }

                if (_focusOnReady)
                {
                    _focusOnReady = false;
                    Post(new JsonObject { ["t"] = "focus" });
                }

                break;
            case "error":
                AppLog.Warning("Code editor: page error: " + m["message"]?.GetValue<string>());
                FallBack(Loc.Get("CodeEditor_WebView2Failed"));
                break;
            case "changed":
                ApplyChanges(m);
                break;
            case "text":
                _text = Normalize(m["text"]?.GetValue<string>() ?? string.Empty);
                TextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "sel":
                _selStart = Read(m, "a");
                _selEnd = Read(m, "b");
                _caret = Read(m, "c");
                _caretRect = new Rect(ReadDouble(m, "x"), ReadDouble(m, "y"), 1, ReadDouble(m, "h"));
                CaretMoved?.Invoke(this, EventArgs.Empty);
                break;
            case "focus":
                _jsFocused = m["has"]?.GetValue<bool>() == true;
                break;
            case "gutter":
                GutterClicked?.Invoke(this, Read(m, "line"));
                break;
            case "key":
                OnForwardedKey(Read(m, "vk"), (KeyModifiers)Read(m, "m"));
                break;
            case "emarkers":
                EditorMarkersChanged?.Invoke(this, ReadMarkers(m["items"] as JsonArray));
                break;
            case "complete":
                _ = CompleteAsync(m);
                break;
            case "format":
                Reply(m, new JsonObject { ["text"] = Formatter?.Invoke(_text) });
                break;
            case "evalResult":
                if (_evals.TryGetValue(Read(m, "id"), out TaskCompletionSource<string?>? eval))
                {
                    eval.TrySetResult(m["value"]?.GetValue<string>());
                }

                break;
        }
    }

    private void Reply(JsonObject request, JsonObject reply)
    {
        reply["t"] = "reply";
        reply["id"] = request["id"]?.GetValue<int>() ?? 0;
        Post(reply);
    }

    private async Task CompleteAsync(JsonObject request)
    {
        int offset = Math.Clamp(Read(request, "o"), 0, _text.Length);
        string trigger = request["trigger"]?.GetValue<string>() ?? string.Empty;
        CodeCompletionList? list = null;
        try
        {
            if (CompletionProvider is { } provider)
            {
                list = await provider(offset, trigger);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Warning("Code editor: completion failed: " + ex.Message);
        }

        Reply(request, CompletionJson(list));
    }

    private JsonObject CompletionJson(CodeCompletionList? list)
    {
        if (list is null)
        {
            return new JsonObject { ["items"] = null };
        }

        LastCompletion = list;
        return new JsonObject
        {
            ["start"] = list.ReplaceStart,
            ["length"] = list.ReplaceLength,
            ["items"] = new JsonArray([.. list.Items.Take(500).Select(i => (JsonNode?)new JsonObject
            {
                ["label"] = i.Label,
                ["kind"] = i.Kind,
                ["detail"] = i.Detail,
                ["doc"] = i.Documentation,
                ["insert"] = i.InsertText,
            })]),
        };
    }

    /// <summary>最後にページへ返した補完の候補 (テスト用)。</summary>
    internal CodeCompletionList? LastCompletion { get; private set; }

    private void ApplyChanges(JsonObject m)
    {
        bool linesChanged = false;
        if (m["c"] is JsonArray changes)
        {
            foreach (JsonNode? change in changes)
            {
                if (change is JsonArray { Count: 3 } c)
                {
                    int offset = Math.Clamp(c[0]!.GetValue<int>(), 0, _text.Length);
                    int length = Math.Clamp(c[1]!.GetValue<int>(), 0, _text.Length - offset);
                    string text = c[2]!.GetValue<string>();
                    linesChanged |= _text.AsSpan(offset, length).Contains('\n') || text.Contains('\n', StringComparison.Ordinal);
                    _text = string.Concat(_text.AsSpan(0, offset), text, _text.AsSpan(offset + length));
                }
            }
        }

        if (linesChanged && (_breakpoints.Count > 0 || _currentLine is not null))
        {
            // Monaco の印は編集に合わせて動くので、ブレークポイント・現在の行を本当の行に置き直す (画面の側が行をずらしたら SetBreakpoints が来る)
            PostBreakpoints();
            Post(new JsonObject { ["t"] = "currentLine", ["line"] = _currentLine });
        }

        if (m["len"]?.GetValue<int>() is int len && len != _text.Length)
        {
            // 写しがずれた (こちらからの変更と入力が行き違った): 本文をまとめて取り直す
            Post(new JsonObject { ["t"] = "getText" });
        }

        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SendInit()
    {
        _initSent = true;
        JsonObject init = new()
        {
            ["t"] = "init",
            ["language"] = LanguageId,
            ["fileName"] = _fileName,
            ["text"] = _text,
            ["readOnly"] = _readOnly,
            ["caret"] = _caret,
            ["ariaLabel"] = _accessibleName,
            ["fontFamily"] = "'Cascadia Mono', Consolas, monospace",
            ["fontSize"] = 14,
            ["nls"] = MonacoLanguage(Hosting.Localization.CurrentLanguage),
            ["theme"] = MonacoTheme.Build(this),
            ["keys"] = ForwardedKeysJson(),
            ["breakpoints"] = BreakpointsJson(),
            ["markers"] = MarkersJson(),
            ["currentLine"] = _currentLine,
            ["hostCompletion"] = CompletionProvider is not null,
            ["completionTriggers"] = new JsonArray([.. CompletionTriggers.Select(c => (JsonNode?)c)]),
            ["hostFormat"] = Formatter is not null,
        };
        if (_language == CodeLanguage.Template)
        {
            Core.Templates.Editing.TemplateSyntaxLists lists = Core.Templates.Editing.TemplateLanguageService.SyntaxLists();
            init["hxt"] = new JsonObject
            {
                ["keywords"] = new JsonArray([.. lists.Keywords.Select(k => (JsonNode?)k)]),
                ["types"] = new JsonArray([.. lists.Types.Select(k => (JsonNode?)k)]),
                ["attributes"] = new JsonArray([.. lists.Attributes.Select(k => (JsonNode?)k)]),
            };
        }

        if (_language == CodeLanguage.JavaScript)
        {
            init["extraLibs"] = ScriptLibraries();
        }

        Post(init);
    }

    /// <summary>補完を出すきっかけの文字 (テンプレート言語: メンバー・名前空間・配列)。</summary>
    private IReadOnlyList<string> CompletionTriggers => _language == CodeLanguage.Template ? [".", ":", "["] : ["."];

    private string LanguageId => _language switch
    {
        CodeLanguage.JavaScript => "javascript",
        CodeLanguage.CSharp => "csharp",
        _ => "hxt",
    };

    /// <summary>JavaScript の補完に使う型定義 (AUTO-04 の hexeditor.d.ts。表示言語が日本語なら説明が日本語のもの) とスクリプトの環境の定義。</summary>
    private static JsonArray ScriptLibraries()
    {
        var libs = new JsonArray();
        string host = Path.Combine(AssetsFolder, "host");
        string api = Hosting.Localization.CurrentLanguage == "ja" && File.Exists(Path.Combine(host, "hexeditor.ja.d.ts")) ? "hexeditor.ja.d.ts" : "hexeditor.d.ts";
        foreach (string name in new[] { api, "script-env.d.ts" })
        {
            try
            {
                libs.Add(new JsonObject { ["path"] = "file:///" + name, ["content"] = File.ReadAllText(Path.Combine(host, name)) });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Warning("Code editor: cannot read " + name + ": " + ex.Message);
            }
        }

        return libs;
    }

    /// <summary>Monaco の表示言語 (同梱の vs/nls/lang にあるものだけ。ほかは英語)。</summary>
    internal static string MonacoLanguage(string tag) => tag switch
    {
        "ja" or "ko" or "de" or "fr" or "es" or "it" or "ru" or "pl" or "cs" or "tr" => tag,
        "zh-Hans" => "zh-cn",
        "zh-Hant" => "zh-tw",
        "pt" => "pt-br",
        _ => "en",
    };

    private JsonArray BreakpointsJson() => new([.. _breakpoints.Select(b => (JsonNode?)new JsonArray(
        b.Line, b.Enabled, b.HasCondition,
        Loc.Get(b.Enabled ? (b.HasCondition ? "ScriptEditor_BreakpointConditional" : "ScriptEditor_Breakpoint") : "ScriptEditor_BreakpointDisabled")))]);

    private JsonArray MarkersJson() => new([.. _markers.Where(m => m.Line > 0).Select(m => (JsonNode?)new JsonArray(
        m.Line, Math.Max(1, m.Column), Math.Max(1, m.Length), m.IsError, m.Message))]);

    private static List<CodeMarker> ReadMarkers(JsonArray? items)
    {
        var list = new List<CodeMarker>();
        foreach (JsonNode? node in items ?? [])
        {
            if (node is JsonArray { Count: >= 5 } a)
            {
                list.Add(new CodeMarker(a[0]!.GetValue<int>(), a[1]!.GetValue<int>(), a[2]!.GetValue<int>(), a[3]!.GetValue<bool>(), a[4]!.GetValue<string>()));
            }
        }

        return list;
    }

    private static int Read(JsonObject m, string key) => m[key] is JsonValue v && v.TryGetValue(out double d) ? (int)d : 0;

    private static double ReadDouble(JsonObject m, string key) => m[key] is JsonValue v && v.TryGetValue(out double d) ? d : 0;

    private static string Normalize(string? text) => (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private int OffsetOf(int line, int column)
    {
        int p = 0;
        for (int l = 1; l < line; l++)
        {
            int nl = _text.IndexOf('\n', p);
            if (nl < 0)
            {
                return _text.Length;
            }

            p = nl + 1;
        }

        int end = _text.IndexOf('\n', p);
        end = end < 0 ? _text.Length : end;
        return Math.Min(end, p + Math.Max(0, column - 1));
    }

    // ───────────── テーマ ─────────────

    private void PostTheme()
    {
        if (_web is { } web)
        {
            web.DefaultBackgroundColor = MonacoTheme.Background(this);
        }

        if (_initSent && _plain is null)
        {
            Post(new JsonObject { ["t"] = "theme", ["theme"] = MonacoTheme.Build(this) });
        }
    }

    /// <summary>コントラストテーマの切り替え (色の設定の変更で知る。UI スレッド以外から呼ばれる)。</summary>
    private static void EnsureSystemColorWatch()
    {
        if (s_uiSettings is not null)
        {
            return;
        }

        s_uiSettings = new Windows.UI.ViewManagement.UISettings();
        s_uiSettings.ColorValuesChanged += (_, _) =>
        {
            List<MonacoCodeEditor> editors;
            lock (Live)
            {
                editors = [.. Live.Select(w => w.TryGetTarget(out MonacoCodeEditor? e) ? e : null).OfType<MonacoCodeEditor>()];
            }

            foreach (MonacoCodeEditor editor in editors)
            {
                editor.DispatcherQueue?.TryEnqueue(editor.PostTheme);
            }
        };
    }

    // ───────────── キー (AUTO-20 の仕様 5: アプリが処理するキーはページから受け取る) ─────────────

    /// <summary>ページから受け取ったキー (コードエディタの有効範囲のキーと、Monaco が使わないグローバルのキー)。</summary>
    public static event EventHandler<CodeEditorKeyEventArgs>? KeyForwarded;

    private void OnForwardedKey(int key, KeyModifiers modifiers)
    {
        var args = new CodeEditorKeyEventArgs(this, key, modifiers);
        KeyForwarded?.Invoke(this, args);
        if (args.ChordPending)
        {
            Post(new JsonObject { ["t"] = "chord", ["on"] = true });
        }
    }

    private void Keys_Changed(object? sender, EventArgs e) => DispatcherQueue?.TryEnqueue(() =>
    {
        if (_initSent && _plain is null)
        {
            Post(new JsonObject { ["t"] = "keys", ["items"] = ForwardedKeysJson() });
        }
    });

    private static JsonArray ForwardedKeysJson() => new([.. ForwardedStrokes().Select(s => (JsonNode?)$"{s.Key}:{(int)s.Modifiers}")]);

    /// <summary>
    /// Monaco の編集のキー (検索・置換、行へ移動、元に戻す、切り取り・コピー・貼り付け、補完、コメント、整形など)。グローバルの同じキーより
    /// 優先する (AUTO-20 の仕様 5 の 3)。コードエディタの有効範囲の割り当て (F5 など) はこれより優先する (仕様 5 の 1)。
    /// </summary>
    internal static readonly IReadOnlyList<string> MonacoKeys =
    [
        "Ctrl+F", "Ctrl+H", "Ctrl+G", "F3", "Shift+F3", "Ctrl+F3", "Ctrl+Shift+F3", "Alt+Enter", "Ctrl+D", "Ctrl+K", "Ctrl+U", "Ctrl+L",
        "Ctrl+Shift+L", "Ctrl+F2", "Ctrl+Z", "Ctrl+Y", "Ctrl+Shift+Z", "Ctrl+X", "Ctrl+C", "Ctrl+V", "Ctrl+Insert", "Shift+Insert", "Shift+Delete",
        "Ctrl+A", "Ctrl+Oem2", "Ctrl+Divide", "Shift+Alt+A", "Ctrl+Oem4", "Ctrl+Oem6", "Ctrl+Shift+Oem4", "Ctrl+Shift+Oem6", "Ctrl+Shift+Oem5",
        "Ctrl+Space", "Ctrl+I", "Ctrl+Shift+Space", "Ctrl+Shift+K", "Ctrl+Enter", "Ctrl+Shift+Enter", "Alt+Up", "Alt+Down", "Shift+Alt+Up",
        "Shift+Alt+Down", "Shift+Alt+Left", "Shift+Alt+Right", "Ctrl+Alt+Up", "Ctrl+Alt+Down", "Ctrl+Up", "Ctrl+Down", "Alt+PageUp", "Alt+PageDown",
        "Ctrl+Home", "Ctrl+End", "Ctrl+Shift+Home", "Ctrl+Shift+End", "Ctrl+Left", "Ctrl+Right", "Ctrl+Shift+Left", "Ctrl+Shift+Right",
        "Ctrl+Backspace", "Ctrl+Delete", "Shift+Alt+F", "Ctrl+OemPeriod", "F8", "Shift+F8", "Alt+F8", "Escape", "Shift+Escape", "Shift+F10",
        "Alt+F1", "Ctrl+M", "F2", "Ctrl+F12", "Alt+F12", "Shift+Alt+I", "Tab", "Shift+Tab", "Ctrl+Shift+Up", "Ctrl+Shift+Down",
    ];

    private static readonly HashSet<KeyStroke> MonacoStrokes = [.. MonacoKeys.Select(k => KeyStroke.TryParse(k, out KeyStroke s) ? s : default)];

    /// <summary>ページからアプリに渡すキー (打鍵の 1 つ目)。</summary>
    internal static IReadOnlyList<KeyStroke> ForwardedStrokes()
    {
        var result = new HashSet<KeyStroke>();
        foreach (EffectiveBinding b in CommandService.Keys.All)
        {
            KeyStroke first = b.Binding.Chord.First;
            if (first.Modifiers.HasFlag(KeyModifiers.Win) || VirtualKeys.IsModifier(first.Key))
            {
                continue;
            }

            if (b.Binding.Scope == KeyScope.CodeEditor)
            {
                result.Add(first);
            }
            else if (b.Binding.Scope == KeyScope.Global && !MonacoStrokes.Contains(first) && !IsTyping(first))
            {
                result.Add(first);
            }
        }

        return [.. result];
    }

    /// <summary>文字の入力・カーソルの移動のキー (修飾キーなし・Shift だけの、ファンクションキー以外のキー)。</summary>
    private static bool IsTyping(KeyStroke s) =>
        (s.Modifiers & ~KeyModifiers.Shift) == KeyModifiers.None && !VirtualKeys.IsFunctionKey(s.Key);
}

/// <summary>ページから受け取ったキー。受け取った側は連続キーの 1 打鍵目なら <see cref="ChordPending"/> を true にする。</summary>
public sealed class CodeEditorKeyEventArgs(MonacoCodeEditor source, int key, KeyModifiers modifiers) : EventArgs
{
    public MonacoCodeEditor Source { get; } = source;

    public int Key { get; } = key;

    public KeyModifiers Modifiers { get; } = modifiers;

    public bool Handled { get; set; }

    public bool ChordPending { get; set; }
}
