using System.Text.Json.Nodes;
using HexEditor.App.ViewModels;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Commands;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Scripting;
using HexEditor.Core.View;
using HexEditor.Sdk;
using Microsoft.UI.Dispatching;

namespace HexEditor.App.Services.Scripting;

/// <summary>UI スレッド (DispatcherQueue) への受け渡し。</summary>
internal sealed class DispatcherQueueScriptDispatcher(DispatcherQueue queue) : IScriptDispatcher
{
    public bool CheckAccess() => queue.HasThreadAccess;

    public Task<T> InvokeAsync<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(async () =>
        {
            try
            {
                tcs.TrySetResult(await work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("The UI thread is shutting down."));
        }

        return tcs.Task;
    }
}

/// <summary>
/// アプリのドキュメントのタブをスクリプトに見せる (08 の AUTO-07)。保存・閉じる・別のタブで開くはウィンドウの処理 (MainWindow) を使う。
/// </summary>
internal sealed class AppDocumentHost(DocumentViewModel vm) : IScriptDocumentHost
{
    private Core.Notifications.Notification? _writing;

    public DocumentViewModel ViewModel { get; } = vm;

    public MainWindow? Window => WindowManager.Windows.FirstOrDefault(w => w.Vm.Documents.Contains(ViewModel));

    public Document Document => ViewModel.Document;

    public EditorState Editor => ViewModel.PrimaryEditor;

    public string? Path => ViewModel.FilePath;

    public string Name => ViewModel.DisplayName;

    public string Kind => ViewModel.ScriptKind;

    public bool IsClosed => Window is null || Document.IsDisposed;

    private MainWindow RequireWindow() => Window ?? throw new Sdk.ArgumentError(Msg.T($"ドキュメント「{Name}」は閉じられています。", $"The document \"{Name}\" has been closed."));

    public Task SaveAsync(CancellationToken cancellationToken) => RequireWindow().SaveForScriptAsync(ViewModel, null);

    public Task SaveAsAsync(string path, CancellationToken cancellationToken) => RequireWindow().SaveForScriptAsync(ViewModel, path);

    public Task<bool> CloseAsync(bool discard) => RequireWindow().CloseForScriptAsync(ViewModel, discard);

    public Task<IScriptDocumentHost> OpenRangeAsync(long start, long length) => RequireWindow().OpenRangeForScriptAsync(ViewModel, start, length);

    public void ShowScriptWriting(ScriptRun? run)
    {
        MainWindow? window = Window;
        if (_writing is not null)
        {
            window?.Vm.Notifications.Dismiss(_writing);
            _writing = null;
        }

        if (run is not null && window is not null)
        {
            _writing = window.ShowScriptWritingNotice(ViewModel, run);
        }
    }
}

/// <summary>
/// アプリの環境 (<see cref="IScriptEnvironment"/>)。ドキュメントはすべてのウィンドウのタブ、UI はアクティブなウィンドウ。
/// </summary>
internal sealed class AppScriptEnvironment : IScriptEnvironment
{
    private readonly Dictionary<DocumentViewModel, AppDocumentHost> _hosts = [];

    public AppScriptEnvironment(DispatcherQueue queue, string appVersion, bool elevated)
    {
        Dispatcher = new DispatcherQueueScriptDispatcher(queue);
        AppVersion = appVersion;
        IsElevated = elevated;
        Ui = new AppScriptUi(this);
    }

    public IScriptDispatcher Dispatcher { get; }

    public string AppVersion { get; }

    public string Language => System.Globalization.CultureInfo.CurrentUICulture.Name;

    public bool IsElevated { get; }

    public ScriptEventSource EventSource { get; } = new();

    public IScriptEventSource Events => EventSource;

    internal AppDocumentHost HostFor(DocumentViewModel vm)
    {
        if (!_hosts.TryGetValue(vm, out AppDocumentHost? host))
        {
            host = new AppDocumentHost(vm);
            _hosts[vm] = host;
        }

        return host;
    }

    internal void Forget(DocumentViewModel vm) => _hosts.Remove(vm);

    public IReadOnlyList<IScriptDocumentHost> Documents =>
        [.. WindowManager.Windows.SelectMany(w => w.Vm.Documents).Where(d => !d.IsPending).Select(HostFor)];

    public IScriptDocumentHost? ActiveDocument
    {
        get => WindowManager.Current.Vm.Selected is { IsPending: false } d ? HostFor(d) : null;
        set
        {
            if (value is AppDocumentHost { Window: { } window } host)
            {
                window.Vm.Selected = host.ViewModel;
                WindowManager.MarkActive(window);
            }
        }
    }

    public async Task<IScriptDocumentHost> OpenFileAsync(string path, bool readOnly, bool background) =>
        HostFor(await WindowManager.Current.OpenForScriptAsync(path, readOnly, background));

    public async Task<IScriptDocumentHost> OpenDiskAsync(string id, bool readOnly, bool background) =>
        HostFor(await WindowManager.Current.OpenDiskForScriptAsync(id, readOnly));

    public async Task<IScriptDocumentHost> OpenProcessAsync(int pid, bool readOnly, bool background) =>
        HostFor(await WindowManager.Current.OpenProcessForScriptAsync(pid, readOnly));

    public IScriptDocumentHost NewDocument(long length, byte[] fill) => HostFor(WindowManager.Current.NewDocumentForScript(length, fill));

    public IScriptCommandService Commands { get; } = new AppScriptCommands();

    public IScriptUiService Ui { get; }

    public IScriptClipboard Clipboard { get; } = new AppScriptClipboard();

    public IScriptSettings Settings { get; } = new AppScriptSettings();

    public IScriptInspectorPanel Inspector { get; } = new AppInspectorPanel();

    /// <summary>テンプレートエンジン (テンプレートの領域が設定する。08 の AUTO-13)。</summary>
    public IScriptTemplateEngine? Templates { get; set; }

    public IScriptHttp Http { get; } = new AppScriptHttp();

    public IScriptProcessLauncher Processes { get; } = new ProcessLauncher();

    public ScriptOutputLog Output => ScriptingService.Output;

    public OperationCenter OperationsFor(IScriptDocumentHost? document) =>
        (document as AppDocumentHost)?.Window?.Vm.Operations ?? WindowManager.Current.Vm.Operations;

    public CopyOptions CopyOptions => WindowManager.Current.CopyOptionsForScript();

    public void OnRunningScriptsChanged(int count)
    {
        foreach (MainWindow w in WindowManager.Windows)
        {
            w.UpdateScriptStatus(count);
        }
    }

    /// <summary>リソースの文字列。キーがないもの (インスペクタの型の名前など) は既定の文字列にする。</summary>
    public string Localize(string key, params object[] args) =>
        Loc.TryGet(key) is { } format
            ? Core.Text.MessageFormat.Format(format, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CultureInfo.CurrentUICulture.Name, args)
            : key.StartsWith("Inspector_Row_", StringComparison.Ordinal) ? key["Inspector_Row_".Length..] : ScriptTexts.Default(key, Language, args);
}

/// <summary>コマンドの実行 (AUTO-06)。<c>args.argument</c> は引数を尋ねるコマンドの入力として渡す。</summary>
internal sealed class AppScriptCommands : IScriptCommandService
{
    public IReadOnlyList<CommandInfo> List() =>
        [.. Commands.CommandService.Catalog.All.Select(c => new CommandInfo(c.Id, Commands.CommandService.DisplayName(c), c.Category, ArgumentsOf(c)))];

    private static IReadOnlyList<string> ArgumentsOf(CommandDefinition c) => c.Argument is null ? [] : ["argument"];

    public bool Exists(string id) => Commands.CommandService.Catalog.Contains(id);

    public async Task<object?> ExecuteAsync(string id, IReadOnlyDictionary<string, object?> args, IScriptDocumentHost? target, bool headless)
    {
        MainWindow window = (target as AppDocumentHost)?.Window ?? WindowManager.Current;
        string? argument = args.TryGetValue("argument", out object? a) ? Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture) : null;
        bool done = await window.Commands.ExecuteAsync(id, argument);
        return done;
    }
}

/// <summary>クリップボード (AUTO-15)。テスト用のビルドを --test-hooks で起動したときはアプリの中の代わりを使う (SystemClipboard)。</summary>
internal sealed class AppScriptClipboard : IScriptClipboard
{
    public void SetText(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        SystemClipboard.SetContent(package);
    }

    public void SetBytes(byte[] data, string hexText) => WindowManager.Current.SetClipboardBytesForScript(data, hexText);

    public string? GetText()
    {
        var view = SystemClipboard.GetContent();
        return view.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)
            ? view.GetTextAsync().AsTask().GetAwaiter().GetResult()
            : null;
    }

    public byte[]? GetBytes() => WindowManager.Current.GetClipboardBytesForScript();
}

/// <summary>設定 (AUTO-17)。設定画面と同じ定義で検査し、その場で反映する。</summary>
internal sealed class AppScriptSettings : IScriptSettings
{
    public bool Exists(string key) => Core.Settings.BuiltInSettings.All.Any(s => s.Key == key) || App.Settings.Contains(key);

    public JsonNode? Get(string key) => App.Settings.GetNode(key)?.DeepClone() ?? Core.Settings.BuiltInSettings.DefaultOf(key);

    public void Set(string key, JsonNode? value)
    {
        Core.Settings.SettingDefinition? def = Core.Settings.BuiltInSettings.All.FirstOrDefault(s => s.Key == key);
        if (def is not null && !def.Validate(value))
        {
            throw new Sdk.ArgumentError(Msg.T($"設定 {key} の値が正しくありません。", $"The value for the setting {key} is invalid."));
        }

        App.Settings.SetNode(key, value, def?.Default);
    }
}

/// <summary>インスペクタパネル (AUTO-12 の仕様 4)。</summary>
internal sealed class AppInspectorPanel : IScriptInspectorPanel
{
    public bool BigEndian => WindowManager.Current.Vm.Selected?.Editor.View.BigEndian == true;

    public Core.Inspector.InspectorOptions Options => WindowManager.Current.InspectorOptionsForScript();

    public void SetVisible(string typeId, bool visible) => WindowManager.Current.SetInspectorRowForScript(typeId, visible);
}

/// <summary>
/// スクリプトの通信 (AUTO-18 の <c>net.fetch</c>)。許可 <c>network</c> を与えたスクリプトだけが呼ぶ。アプリが自分から行う通信ではないため
/// <c>network.offline</c> の対象外 (00 の 11.4)。
/// </summary>
internal sealed class AppScriptHttp : IScriptHttp
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(5) };

    public async Task<(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)> SendAsync(string method, Uri url,
        IReadOnlyDictionary<string, string> headers, byte[]? body, long maxResponseBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        foreach ((string name, string value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
        }

        using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.Content.Headers.ContentLength > maxResponseBytes)
        {
            throw new LimitError(Msg.T("応答が 100 MB を超えます。", "The response exceeds 100 MB.")) { Limit = "size" };
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + n > maxResponseBytes)
            {
                throw new LimitError(Msg.T("応答が 100 MB を超えます。", "The response exceeds 100 MB.")) { Limit = "size" };
            }

            buffer.Write(chunk, 0, n);
        }

        var responseHeaders = response.Headers.Concat(response.Content.Headers)
            .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(h => h.Value)), StringComparer.OrdinalIgnoreCase);
        return ((int)response.StatusCode, responseHeaders, buffer.ToArray());
    }
}
