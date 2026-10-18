using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using HexEditor.Sdk;

namespace HexEditor.Core.Scripting;

/// <summary>
/// 1 本の専用スレッドで順に実行する受け渡し (ヘッドレス実行とテストの「UI スレッド」)。スレッドの中の <c>await</c> も同じスレッドに戻る。
/// </summary>
public sealed class SerialDispatcher : SynchronizationContext, IScriptDispatcher, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly Thread _thread;

    public SerialDispatcher(string name = "Script UI")
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public bool CheckAccess() => Thread.CurrentThread == _thread;

    public Task<T> InvokeAsync<T>(Func<Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try
            {
                tcs.TrySetResult(await work().ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, null);
        return tcs.Task;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        // 呼び出し側の実行の文脈 (メッセージの言語など) を受け継ぐ。
        ExecutionContext? context = ExecutionContext.Capture();
        SendOrPostCallback callback = context is null ? d : s => ExecutionContext.Run(context, x => d(x), s);
        try
        {
            _queue.Add((callback, state));
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(SerialDispatcher));
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (CheckAccess())
        {
            d(state);
            return;
        }

        this.Invoke(() => d(state));
    }

    private void Loop()
    {
        SetSynchronizationContext(this);
        foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
        {
            callback(state);
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>アプリ内の代わりのクリップボード (ヘッドレス実行・テスト。システムのクリップボードには触れない)。</summary>
public sealed class MemoryScriptClipboard : IScriptClipboard
{
    public string? Text { get; private set; }

    public byte[]? Bytes { get; private set; }

    public void SetText(string text)
    {
        Text = text;
        Bytes = null;
    }

    public void SetBytes(byte[] data, string hexText)
    {
        Bytes = data;
        Text = hexText;
    }

    public string? GetText() => Text;

    public byte[]? GetBytes() => Bytes;
}

/// <summary>
/// ヘッドレス実行 (AUTO-41) とテストのドキュメント。Core のドキュメントとビューの状態を包み、保存は通常の保存 (01) と同じ
/// <see cref="DocumentSaver"/> で行う。
/// </summary>
public sealed class CoreDocumentHost : IScriptDocumentHost
{
    private readonly CoreScriptEnvironment _env;

    internal CoreDocumentHost(CoreScriptEnvironment env, Document document, string? path, string name, string kind)
    {
        _env = env;
        Document = document;
        Editor = new EditorState(document);
        Path = path;
        Name = name;
        Kind = kind;
        Editor.Changed += (_, _) => env.Events.RaiseSelectionChanged(this);
    }

    public Document Document { get; }

    public EditorState Editor { get; }

    public string? Path { get; private set; }

    public string Name { get; private set; }

    public string Kind { get; }

    public bool IsClosed { get; private set; }

    /// <summary>スクリプトが <c>save()</c> を呼んだ (ヘッドレス実行の既定の保存の扱い。AUTO-41 の仕様 4)。</summary>
    public bool SaveRequested { get; private set; }

    /// <summary>書き込み中の表示 (テストで確かめる)。</summary>
    public ScriptRun? WritingRun { get; private set; }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        SaveRequested = true;
        if (Path is null)
        {
            throw new IoError(Msg.T("新規ドキュメントは saveAs で保存してください。", "Save a new document with saveAs."));
        }

        string? reason = await _env.Events.RaiseBeforeSaveAsync(this, Api.ScriptEvents.BeforeSaveTimeout).ConfigureAwait(true);
        if (reason is not null)
        {
            _env.Notices.Add(reason);
            throw new IoError(Msg.T("保存が中止されました: ", "Save was aborted: ") + reason);
        }

        if (_env.DryRun)
        {
            return;
        }

        if (_env.SaveOverride?.Invoke(this) is bool handled)
        {
            if (!handled)
            {
                throw new IoError(Msg.T($"保存できませんでした: {Path}", $"Could not save: {Path}"));
            }

            _env.Events.RaiseAfterSave(this);
            return;
        }

        DocumentSnapshot snapshot = Document.DataSnapshot;
        string target = Path;
        FileByteSource saved = await Task.Run(() => DocumentSaver.Save(snapshot, target), cancellationToken).ConfigureAwait(true);
        Document.CompleteSave(saved);
        _env.Events.RaiseAfterSave(this);
    }

    public async Task SaveAsAsync(string path, CancellationToken cancellationToken)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        await SaveAsync(cancellationToken).ConfigureAwait(true);
    }

    public Task<bool> CloseAsync(bool discard)
    {
        if (Document.IsModified && !discard)
        {
            // ヘッドレス実行では安全側 (閉じない) を選ぶ (AUTO-06 の仕様 3)。
            return Task.FromResult(false);
        }

        _env.Close(this);
        return Task.FromResult(true);
    }

    internal void MarkClosed()
    {
        IsClosed = true;
        Editor.Detach();
    }

    public Task<IScriptDocumentHost> OpenRangeAsync(long start, long length)
    {
        Document view = Document.CreateLinkedView(start, length);
        view.SetReadOnly(ReadOnlyReason.User);
        return Task.FromResult<IScriptDocumentHost>(_env.Add(view, null, $"{Name} [0x{start:X}+0x{length:X}]", Kind, activate: true));
    }

    public void ShowScriptWriting(ScriptRun? run) => WritingRun = run;
}

/// <summary>
/// ヘッドレス実行とテストの環境 (<see cref="IScriptEnvironment"/>)。UI のない操作 (ダイアログ) は <see cref="Ui"/> の実装で決める
/// (既定は、メッセージを <see cref="Output"/> に書き、確認は安全側を選び、入力はできない)。
/// </summary>
public sealed class CoreScriptEnvironment : IScriptEnvironment, IDisposable
{
    private readonly List<CoreDocumentHost> _documents = [];
    private readonly bool _ownsDispatcher;
    private CoreDocumentHost? _active;

    public CoreScriptEnvironment(IScriptDispatcher? dispatcher = null, string language = "en", TimeProvider? time = null)
    {
        _ownsDispatcher = dispatcher is null;
        Dispatcher = dispatcher ?? new SerialDispatcher();
        Language = language;
        Operations = new OperationCenter(time);
        Ui = new HeadlessScriptUi(this);
    }

    public IScriptDispatcher Dispatcher { get; }

    public string AppVersion { get; init; } = "0.0.0";

    public string Language { get; set; }

    public bool IsElevated { get; set; }

    /// <summary>保存しない (<c>--dry-run</c>。AUTO-41 の仕様 4)。</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// 保存の差し替え (コマンドラインの保存の扱い。AUTO-41 の仕様 4)。true は保存した、false は保存できなかった、null は通常の保存を行う。
    /// UI スレッドで呼ぶ。
    /// </summary>
    public Func<CoreDocumentHost, bool?>? SaveOverride { get; set; }

    public OperationCenter Operations { get; }

    public ScriptEventSource Events { get; } = new();

    IScriptEventSource IScriptEnvironment.Events => Events;

    /// <summary>保存の中止などの通知 (App の InfoBar に当たる)。</summary>
    public List<string> Notices { get; } = [];

    public DocumentOptions DocumentOptions { get; init; } = new();

    public IReadOnlyList<IScriptDocumentHost> Documents => [.. _documents];

    public IReadOnlyList<CoreDocumentHost> Hosts => [.. _documents];

    public IScriptDocumentHost? ActiveDocument
    {
        get => _active;
        set
        {
            _active = value as CoreDocumentHost;
            Events.RaiseActiveDocumentChanged();
        }
    }

    /// <summary>ファイルを開いて加える (UI スレッドで呼ぶ)。</summary>
    public CoreDocumentHost OpenFile(string path, bool readOnly = false, bool activate = true)
    {
        var source = FileByteSource.Open(path);
        var doc = new Document(source, DocumentOptions);
        if (readOnly)
        {
            doc.SetReadOnly(ReadOnlyReason.OpenedReadOnly);
        }

        return Add(doc, path, System.IO.Path.GetFileName(path), "file", activate);
    }

    /// <summary>データソースのドキュメントを加える (テスト: 仮想の巨大ソース・ディスクの偽物)。</summary>
    public CoreDocumentHost Add(Document document, string? path, string name, string kind = "file", bool activate = true)
    {
        var host = new CoreDocumentHost(this, document, path, name, kind);
        _documents.Add(host);
        Events.RaiseDocumentOpened(host);
        if (activate || _active is null)
        {
            ActiveDocument = host;
        }

        return host;
    }

    public CoreDocumentHost Add(IByteSource source, string name, string kind = "file", bool activate = true) =>
        Add(new Document(source, DocumentOptions), null, name, kind, activate);

    internal void Close(CoreDocumentHost host)
    {
        _documents.Remove(host);
        host.MarkClosed();
        if (_active == host)
        {
            _active = _documents.LastOrDefault();
            Events.RaiseActiveDocumentChanged();
        }

        Events.RaiseDocumentClosed(host);
        host.Document.Dispose();
    }

    public Task<IScriptDocumentHost> OpenFileAsync(string path, bool readOnly, bool background) =>
        Task.FromResult<IScriptDocumentHost>(OpenFile(path, readOnly, !background));

    public Func<string, IByteSource>? DiskOpener { get; set; }

    public Task<IScriptDocumentHost> OpenDiskAsync(string id, bool readOnly, bool background)
    {
        IByteSource source = DiskOpener?.Invoke(id) ?? throw new IoError(Msg.T($"ディスクを開けません: {id}", $"Cannot open the disk: {id}"));
        var doc = new Document(source, DocumentOptions);
        doc.SetReadOnly(readOnly ? ReadOnlyReason.OpenedReadOnly : ReadOnlyReason.None);
        return Task.FromResult<IScriptDocumentHost>(Add(doc, null, id, "disk", !background));
    }

    public Task<IScriptDocumentHost> OpenProcessAsync(int pid, bool readOnly, bool background) =>
        throw new IoError(Msg.T("ヘッドレス実行ではプロセスのメモリを開けません。", "Process memory cannot be opened in headless runs."));

    public IScriptDocumentHost NewDocument(long length, byte[] fill)
    {
        var doc = new Document(MemoryByteSource.CreateEmpty("new"), DocumentOptions);
        if (length > 0)
        {
            doc.InsertPattern(0, length, fill);
            doc.MarkSaved();
        }

        return Add(doc, null, "new", "memory");
    }

    public IScriptCommandService Commands { get; set; } = new NoCommands();

    public IScriptUiService Ui { get; set; }

    public IScriptClipboard Clipboard { get; set; } = new MemoryScriptClipboard();

    public IScriptSettings Settings { get; set; } = new MemorySettings();

    public IScriptInspectorPanel Inspector { get; set; } = new DefaultInspectorPanel();

    public IScriptTemplateEngine? Templates { get; set; }

    public IScriptHttp Http { get; set; } = new NoHttp();

    public IScriptProcessLauncher Processes { get; set; } = new ProcessLauncher();

    public ScriptOutputLog Output { get; set; } = new();

    public OperationCenter OperationsFor(IScriptDocumentHost? document) => Operations;

    public Clipboard.CopyOptions CopyOptions { get; set; } = new();

    public int RunningScripts { get; private set; }

    public void OnRunningScriptsChanged(int count) => RunningScripts = count;

    public string Localize(string key, params object[] args) => ScriptTexts.Default(key, Language, args);

    public void Dispose()
    {
        if (_ownsDispatcher && Dispatcher is SerialDispatcher d)
        {
            d.Dispose();
        }
    }

    /// <summary>ヘッドレス実行で使えるコマンド: カーソル移動 (<c>cursor.*</c>。マクロを再生できるように。AUTO-28)。</summary>
    private sealed class NoCommands : IScriptCommandService
    {
        public IReadOnlyList<CommandInfo> List() =>
            [.. Macros.CursorCommands.All.Select(c => new CommandInfo(c.Id, Msg.T(c.Ja, c.En), "cursor", ["count"]))];

        public bool Exists(string id) => Macros.CursorCommands.Exists(id);

        public Task<object?> ExecuteAsync(string id, IReadOnlyDictionary<string, object?> args, IScriptDocumentHost? target, bool headless)
        {
            if (!Macros.CursorCommands.Exists(id))
            {
                throw new Sdk.ArgumentError(Msg.T($"コマンドが見つかりません: {id}", $"Command not found: {id}"));
            }

            if (target is null)
            {
                throw new Sdk.ArgumentError(Msg.T("ドキュメントが開かれていません。", "No document is open."));
            }

            int count = args.TryGetValue("count", out object? c) && c is not null ? Convert.ToInt32(c, System.Globalization.CultureInfo.InvariantCulture) : 1;
            Macros.CursorCommands.Execute(target.Editor, id, count);
            return Task.FromResult<object?>(true);
        }
    }

    private sealed class NoHttp : IScriptHttp
    {
        public Task<(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)> SendAsync(string method, Uri url,
            IReadOnlyDictionary<string, string> headers, byte[]? body, long maxResponseBytes, CancellationToken cancellationToken) =>
            throw new IoError(Msg.T("この環境では通信できません。", "Network access is not available in this environment."));
    }

    /// <summary>設定 (メモリ上。ヘッドレス実行は App の設定ファイルを読む実装に差し替える)。</summary>
    public sealed class MemorySettings : IScriptSettings
    {
        public Dictionary<string, JsonNode?> Values { get; } = new(StringComparer.Ordinal);

        public bool Exists(string key) => Values.ContainsKey(key) || HexEditor.Core.Settings.BuiltInSettings.All.Any(s => s.Key == key);

        public JsonNode? Get(string key) =>
            Values.TryGetValue(key, out JsonNode? v) ? v?.DeepClone() : HexEditor.Core.Settings.BuiltInSettings.DefaultOf(key);

        public void Set(string key, JsonNode? value) => Values[key] = value?.DeepClone();
    }

    /// <summary>インスペクタパネル (既定の設定)。</summary>
    public sealed class DefaultInspectorPanel : IScriptInspectorPanel
    {
        public bool BigEndian { get; set; }

        public Inspector.InspectorOptions Options { get; set; } = new();

        public HashSet<string> Visible { get; } = new(StringComparer.Ordinal);

        public void SetVisible(string typeId, bool visible)
        {
            if (visible)
            {
                Visible.Add(typeId);
            }
            else
            {
                Visible.Remove(typeId);
            }
        }
    }
}

/// <summary>
/// ヘッドレス実行の UI (AUTO-16 の仕様 4): メッセージは出力に書き、確認は出さない (信頼の確認・許可は拒否、時間の上限は続けない)。
/// 入力の API は <see cref="Api.ScriptUi"/> が <c>--arg</c> の値で答える。
/// </summary>
public class HeadlessScriptUi(CoreScriptEnvironment env) : IScriptUiService
{
    public virtual Task MessageAsync(string scriptName, string text, string kind, bool modal)
    {
        env.Output.Write(ScriptRun.ChannelPrefix + scriptName, kind switch
        {
            "error" => ScriptOutputKind.Error,
            "warning" => ScriptOutputKind.Warning,
            _ => ScriptOutputKind.Info,
        }, text);
        return Task.CompletedTask;
    }

    public virtual Task<int> ConfirmAsync(string scriptName, string text, IReadOnlyList<string> buttons) => Task.FromResult(-1);

    public virtual Task<IReadOnlyDictionary<string, string>?> FormAsync(string scriptName, string? title, IReadOnlyList<ScriptFormField> fields,
        Func<IReadOnlyDictionary<string, string>, (string Field, string Message)?> validate) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

    public virtual Task<IReadOnlyList<string>?> PickAsync(string scriptName, string kind, PickOptions options) =>
        Task.FromResult<IReadOnlyList<string>?>(null);

    public virtual void Status(string text) => env.Output.Write("status", ScriptOutputKind.Info, text);

    public virtual void ShowResults(string scriptName, string title, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyList<string> columns,
        IScriptDocumentHost? document)
    {
    }

    public virtual Task<TrustAnswer> ConfirmTrustAsync(TrustPrompt prompt) => Task.FromResult(TrustAnswer.Deny);

    public virtual Task<bool> ConfirmPermissionAsync(string scriptName, string permission, string detail) => Task.FromResult(false);

    public virtual Task<bool> ConfirmContinueAfterTimeoutAsync(ScriptRun run, TimeSpan limit) => Task.FromResult(false);

    public virtual Task<bool> OfferCSharpComponentAsync() => Task.FromResult(false);
}

/// <summary>
/// 外部プログラムの実行 (AUTO-18 の仕様 3)。シェルを経由せず (<see cref="ProcessStartInfo.UseShellExecute"/> を false)、引数は
/// <see cref="ProcessStartInfo.ArgumentList"/> で 1 つずつ渡す (空白や <c>&amp;</c> を含んでもそのまま 1 つの引数になる)。
/// </summary>
public sealed class ProcessLauncher : IScriptProcessLauncher
{
    public const int MaxOutputChars = 16 * 1024 * 1024;

    public async Task<ProcessResult> RunAsync(string path, IReadOnlyList<string> args, string? workingDirectory, string? input, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IoError(Msg.T($"プログラムを実行できません: {path} ({ex.Message})", $"Cannot run the program: {path} ({ex.Message})"), ex);
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw new CanceledError(Msg.T("キャンセルされました。", "Canceled."));
            }

            throw new LimitError(Msg.T($"外部プログラムが制限時間 {timeout.TotalSeconds:0.#} 秒を超えたため終了させました。", $"The external program was terminated because it exceeded the time limit of {timeout.TotalSeconds:0.#} seconds.")) { Limit = "time" };
        }

        string o = await stdout.ConfigureAwait(false);
        string e = await stderr.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, o.Length > MaxOutputChars ? o[..MaxOutputChars] : o, e.Length > MaxOutputChars ? e[..MaxOutputChars] : e);
    }
}
