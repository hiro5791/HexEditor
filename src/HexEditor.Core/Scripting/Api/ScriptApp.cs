using HexEditor.Sdk;

namespace HexEditor.Core.Scripting.Api;

/// <summary>
/// <c>app</c> (AUTO-06)。アプリの環境 (<see cref="IScriptEnvironment"/>) を通してドキュメント・コマンド・イベントを扱う。
/// </summary>
public sealed class ScriptApp : IApp
{
    private readonly ScriptContext _ctx;

    internal ScriptApp(ScriptContext context)
    {
        _ctx = context;
        Commands = new ScriptCommands(context);
        Inspector = new ScriptInspector(context);
        Text = new ScriptText();
        Templates = new ScriptTemplates(context);
        Hash = new ScriptHash(context);
        Clipboard = new ScriptClipboard(context);
        Ui = new ScriptUi(context);
        Settings = new ScriptSettings(context);
        Script = new ScriptInfo(context);
        Fs = new ScriptFileSystem(context);
        Net = new ScriptNet(context);
        Process = new ScriptProcessRunner(context);
        Events = new ScriptEvents(context);
        Plugins = new ScriptPlugins();
    }

    private IScriptEnvironment Env => _ctx.Environment;

    public string Version => Env.AppVersion;

    public IPlugins Plugins { get; }

    public string ApiVersion => ApiInfo.Version;

    public string Language => Env.Language;

    public IReadOnlyList<IDocument> Documents => _ctx.Dispatcher.Invoke(() => Env.Documents.Select(d => (IDocument)_ctx.Wrap(d)).ToList());

    public IDocument? ActiveDocument
    {
        get => _ctx.Dispatcher.Invoke(() => _ctx.WrapOrNull(Env.ActiveDocument));
        set
        {
            if (value is not ScriptDocument doc)
            {
                throw new Sdk.ArgumentError(Msg.T("activeDocument にはドキュメントを指定してください。", "Specify a document for activeDocument."));
            }

            doc.ThrowIfClosed();
            _ctx.Dispatcher.Invoke(() => Env.ActiveDocument = doc.Host);
        }
    }

    public Task<IDocument> OpenAsync(string path, OpenOptions? options = null) =>
        OpenCoreAsync(() => Env.OpenFileAsync(Path.GetFullPath(path), options?.ReadOnly ?? false, options?.Background ?? false), options);

    public Task<IDocument> OpenDiskAsync(string id, OpenOptions? options = null)
    {
        _ctx.RequirePermission(ScriptPermissions.Disk, id);
        return OpenCoreAsync(() => Env.OpenDiskAsync(id, options?.ReadOnly ?? false, options?.Background ?? false), options);
    }

    public Task<IDocument> OpenProcessAsync(int pid, OpenOptions? options = null)
    {
        _ctx.RequirePermission(ScriptPermissions.Disk, "pid " + pid);
        return OpenCoreAsync(() => Env.OpenProcessAsync(pid, options?.ReadOnly ?? false, options?.Background ?? false), options);
    }

    private Task<IDocument> OpenCoreAsync(Func<Task<IScriptDocumentHost>> open, OpenOptions? options) => ScriptErrorMap.GuardAsync(async () =>
    {
        IScriptDocumentHost host;
        using (_ctx.Clock.Pause())
        {
            try
            {
                host = await _ctx.Dispatcher.InvokeAsync(open).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                throw new IoError(Msg.T($"開けません: {ex.Message}", $"Cannot open: {ex.Message}"), ex);
            }
        }

        ScriptDocument doc = _ctx.Wrap(host);
        if (options?.Encoding is EncodingArg enc)
        {
            doc.SetEncoding(enc);
        }

        if (options?.Select is RangeArg sel)
        {
            doc.Selection.Set(sel);
        }
        else if (options?.Offset is OffsetArg off)
        {
            doc.GoTo(off);
        }

        return (IDocument)doc;
    });

    public IDocument NewDocument(NewDocumentOptions? options = null) => ScriptErrorMap.Guard(() =>
    {
        long length = options?.Length ?? 0;
        if (length < 0)
        {
            throw new Sdk.ArgumentError(Msg.T("length は 0 以上にしてください。", "length must be 0 or greater."));
        }

        byte[] fill = options?.Fill is BytePattern p ? ScriptDocument.PatternBytes(p, allowRandom: false) : [0];
        IScriptDocumentHost host = _ctx.Dispatcher.Invoke(() => Env.NewDocument(length, fill));
        return (IDocument)_ctx.Wrap(host);
    });

    public ICommands Commands { get; }

    public IInspector Inspector { get; }

    public IText Text { get; }

    public ITemplates Templates { get; }

    public IHash Hash { get; }

    public IClipboard Clipboard { get; }

    public IUi Ui { get; }

    public ISettings Settings { get; }

    public IScript Script { get; }

    public IFileSystem Fs { get; }

    public INet Net { get; }

    public IProcessRunner Process { get; }

    internal ScriptEvents Events { get; }

    public IEventSubscription On(string eventName, Action<ScriptEventArgs> handler) => Events.Subscribe(eventName, handler);

    public void Stop()
    {
        _ctx.StopCalled = true;
        throw new ScriptTerminatedException(normal: true);
    }

    /// <summary>
    /// スクリプトを終え、ヘッドレス実行の終了コードを決める (AUTO-41 の仕様 8。0〜255。2〜10 は共通の意味と重なるため 64 以上を勧める)。
    /// </summary>
    public void Exit(int code)
    {
        if (code is < 0 or > 255)
        {
            throw new Sdk.RangeError(Msg.T($"終了コードは 0〜255 です: {code}", $"The exit code must be 0 to 255: {code}"));
        }

        _ctx.ExitCode = code;
        Stop();
    }
}
