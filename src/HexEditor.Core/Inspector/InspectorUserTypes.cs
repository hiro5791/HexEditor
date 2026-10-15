using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;

namespace HexEditor.Core.Inspector;

/// <summary>ユーザー定義型の出どころ (INSP-22 の仕様 1・2、07 の TPL-29 の仕様 1)。</summary>
public enum InspectorUserTypeSource
{
    /// <summary>設定フォルダの <c>inspector/</c> のテンプレート (INSP-22 の仕様 2)。</summary>
    Folder,

    /// <summary>設定「インスペクタ用テンプレート」に登録したテンプレート (TPL-29 の仕様 1)。</summary>
    Registered,

    /// <summary>プラグインのインスペクタの型 (08 の AUTO-31 の <c>IInspectorType</c>。INSP-22 の仕様 1・6)。</summary>
    Plugin,
}

/// <summary>
/// ユーザー定義型 1 つ (INSP-22)。テンプレートの型に <c>[[inspector("表示名")]]</c> を付けたもの、またはプラグインの型。
/// テンプレートの型は <see cref="File"/> がファイル (フォルダの型はファイル名、登録した型はフルパス)、プラグインの型は <see cref="File"/> が
/// プラグインの名前、<see cref="TypeName"/> が拡張の ID。
/// </summary>
public sealed record InspectorUserType(string File, string TypeName, string DisplayName, TemplateProgram? Program)
{
    public InspectorUserTypeSource Source { get; init; } = InspectorUserTypeSource.Folder;

    /// <summary>プラグインの型 (<see cref="InspectorUserTypeSource.Plugin"/> のとき)。</summary>
    public Plugins.PluginIntegration.InspectorTypeEntry? Plugin { get; init; }

    /// <summary>登録時の警告: 可変長の型、または 4 KB を超える型 (TPL-29 の仕様 4)。</summary>
    public bool NotFixedSize { get; init; }

    /// <summary>行を区別する鍵 (ファイルと型の名前、またはプラグインの型の ID)。</summary>
    public string Key => Source == InspectorUserTypeSource.Plugin ? "plugin:" + TypeName : File + "|" + TypeName;
}

/// <summary>定義ファイルの読み込みのエラー (InfoBar にファイル名・行番号・理由を出す。INSP-22 の「エラー」)。</summary>
public sealed record InspectorUserTypeError(string File, int Line, int Column, TemplateDiagnostic Diagnostic);

/// <summary>
/// ユーザー定義型の解釈の結果。<see cref="TimedOut"/> は時間の上限 (既定 50 ms) を超えた (仕様 3)。<see cref="NotEnoughData"/> は起点から
/// 読めるデータが型の大きさに足りない、<see cref="Stopped"/> はプラグインを止めている (08 の AUTO-30 の仕様 7)。
/// </summary>
public sealed record InspectorUserValue(string Display, IReadOnlyList<(string Name, string Value)> Fields, long Size, string? Error, bool TimedOut)
{
    public bool NotEnoughData { get; init; }

    public bool Stopped { get; init; }

    public static InspectorUserValue Failed(string error) => new(string.Empty, [], 0, error, false);
}

/// <summary>
/// ユーザー定義型 (INSP-22) の一覧と解釈 (インスペクタの「ユーザー定義」グループの唯一の読み込み・評価の経路)。型の出どころは 3 つ:
/// 設定フォルダの <c>inspector/</c> の <c>.hxt</c> (起動時とファイルの変更時に読み込む。仕様 2)、設定「インスペクタ用テンプレート」に登録した
/// テンプレート (07 の TPL-29)、有効なプラグインのインスペクタの型 (仕様 1・6)。型の解釈は別のスレッドで、起点から最大 4 KB のデータに
/// 1 つの型を適用する。1 つの型の失敗はその行だけのエラーにし (仕様 4)、時間の上限を超えた型は「時間切れ」にする (仕様 3)。
/// スレッドに依存しない (変更の通知はファイルの監視のスレッドから来る)。
/// </summary>
public sealed class InspectorUserTypes : IDisposable
{
    public const string FolderName = "inspector";

    /// <summary>1 つの型の解釈の時間の上限 (既定 50 ms。仕様 3)。</summary>
    public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromMilliseconds(50);

    /// <summary>変更の通知をまとめる時間 (保存の途中の書き込みを待つ)。</summary>
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(300);

    private readonly object _gate = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _pluginCalls = new();
    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;
    private IReadOnlyList<string> _registered = [];
    private string _registeredStamp = string.Empty;
    private IReadOnlyList<InspectorUserType> _templateTypes = [];

    public InspectorUserTypes(string settingsFolder)
    {
        Folder = Path.Combine(settingsFolder, FolderName);
    }

    /// <summary>定義ファイルのフォルダ。</summary>
    public string Folder { get; }

    public TimeSpan TimeLimit { get; init; } = DefaultTimeLimit;

    /// <summary>
    /// すべての型: フォルダの型 (ファイルの名前の順、ファイルの中の順)、登録したテンプレートの型 (登録の順)、プラグインの型 (有効なものだけ)。
    /// </summary>
    public IReadOnlyList<InspectorUserType> Types
    {
        get
        {
            IReadOnlyList<InspectorUserType> templates;
            lock (_gate)
            {
                templates = _templateTypes;
            }

            IReadOnlyList<Plugins.PluginIntegration.InspectorTypeEntry> plugins = PluginTypes?.Invoke() ?? [];
            return plugins.Count == 0
                ? templates
                : [.. templates, .. plugins.Select(p => new InspectorUserType(p.PluginName, p.Id, p.Name, null) { Source = InspectorUserTypeSource.Plugin, Plugin = p })];
        }
    }

    /// <summary>プラグインのインスペクタの型 (アプリが有効なプラグインの一覧をつなぐ。セーフモードでは読み込まれないので空)。</summary>
    public Func<IReadOnlyList<Plugins.PluginIntegration.InspectorTypeEntry>>? PluginTypes { get; set; }

    /// <summary>設定「インスペクタ用テンプレート」のパス (TPL-29)。</summary>
    public IReadOnlyList<string> RegisteredTemplates => _registered;

    /// <summary>最後の読み込みのエラー。</summary>
    public IReadOnlyList<InspectorUserTypeError> Errors { get; private set; } = [];

    /// <summary>読み込み直した (型またはエラーが変わったかもしれない)。ファイルの監視のスレッドから呼ばれることがある。</summary>
    public event EventHandler? Reloaded;

    /// <summary>登録したテンプレートを変える (設定の変更時)。パスと更新日時が同じなら読み込み直さない。</summary>
    public void SetRegisteredTemplates(IEnumerable<string> paths)
    {
        IReadOnlyList<string> list = [.. paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (list.SequenceEqual(_registered, StringComparer.OrdinalIgnoreCase) && Stamp(list) == _registeredStamp)
        {
            return;
        }

        _registered = list;
        Load();
    }

    /// <summary>登録したテンプレートのファイルが変わっていれば読み込み直す (テンプレートを保存した後など)。</summary>
    public void ReloadIfChanged()
    {
        if (Stamp(_registered) != _registeredStamp)
        {
            Load();
        }
    }

    private static string Stamp(IReadOnlyList<string> paths) => string.Join("|", paths.Select(p =>
    {
        try
        {
            return p + "@" + System.IO.File.GetLastWriteTimeUtc(p).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return p + "@0";
        }
    }));

    /// <summary>定義ファイル (フォルダと登録したテンプレート) を読み込む。フォルダがなければフォルダの型はなし。</summary>
    public void Load()
    {
        var types = new List<InspectorUserType>();
        var errors = new List<InspectorUserTypeError>();
        if (Directory.Exists(Folder))
        {
            foreach (string file in Directory.EnumerateFiles(Folder, "*.hxt").Order(StringComparer.OrdinalIgnoreCase))
            {
                LoadFile(file, Path.GetFileName(file), InspectorUserTypeSource.Folder, [Folder], types, errors);
            }
        }

        IReadOnlyList<string> registered = _registered;
        string stamp = Stamp(registered);
        foreach (string path in registered)
        {
            LoadFile(path, path, InspectorUserTypeSource.Registered, [Path.GetDirectoryName(Path.GetFullPath(path))!], types, errors);
        }

        lock (_gate)
        {
            _templateTypes = types;
            _registeredStamp = stamp;
            Errors = errors;
        }

        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    private static void LoadFile(string path, string name, InspectorUserTypeSource source, string[] searchPaths,
        List<InspectorUserType> types, List<InspectorUserTypeError> errors)
    {
        TemplateProgram program;
        try
        {
            program = TemplateLoader.LoadFile(path, new TemplateLoadOptions { SearchPaths = searchPaths });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存の途中で読めない・消えた: 次の変更の通知で読み直す。
            return;
        }

        foreach (TemplateDiagnostic d in program.Diagnostics.Where(d => d.Severity == TemplateSeverity.Error))
        {
            errors.Add(new InspectorUserTypeError(Path.GetFileName(path), d.Location?.Line ?? 0, d.Location?.Column ?? 0, d));
        }

        if (program.HasErrors)
        {
            return;
        }

        foreach ((string type, string display) in program.InspectorTypes)
        {
            types.Add(new InspectorUserType(name, type, display, program)
            {
                Source = source,
                NotFixedSize = source == InspectorUserTypeSource.Registered && !Templates.Presentation.TemplateInspectorTypes.IsSmallFixed(program, type),
            });
        }
    }

    /// <summary>フォルダの変更を監視する (保存し直すと再起動せずに行が更新される。受け入れ基準 4)。フォルダがなければ作る。</summary>
    public void Watch()
    {
        if (_watcher is not null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Folder);
            _watcher = new FileSystemWatcher(Folder, "*.hxt")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += (_, _) => QueueReload();
            _watcher.Created += (_, _) => QueueReload();
            _watcher.Deleted += (_, _) => QueueReload();
            _watcher.Renamed += (_, _) => QueueReload();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void QueueReload()
    {
        lock (_gate)
        {
            _reloadTimer ??= new Timer(_ => Load());
            _reloadTimer.Change(ReloadDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        lock (_gate)
        {
            _reloadTimer?.Dispose();
            _reloadTimer = null;
        }
    }

    /// <summary>
    /// 型を起点のデータ (最大 4 KB) で解釈する (別のスレッドで評価する)。値は <c>format</c> があればその文字列、構造体なら子の一覧 (最大 64 個)。
    /// 時間の上限を超えたら <see cref="InspectorUserValue.TimedOut"/>、例外・構文エラー・データ不足は <see cref="InspectorUserValue.Error"/>。
    /// </summary>
    /// <remarks>
    /// <paramref name="options"/> があれば、書式の指定のない整数のフィールドをインスペクタの表示形式 (基数・桁区切り。INSP-02) で表示する。
    /// </remarks>
    public async Task<InspectorUserValue> EvaluateAsync(InspectorUserType type, byte[] data, Endianness endian, InspectorOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (type.Plugin is { } plugin)
        {
            return await EvaluatePluginAsync(type, plugin, data, endian, cancellationToken).ConfigureAwait(false);
        }

        TemplateLimits limits = TemplateLimits.Default with { TimeSeconds = TimeLimit.TotalSeconds, Patterns = 100_000 };
        var apply = new TemplateApplyOptions
        {
            TypeName = type.TypeName,
            BigEndian = endian == Endianness.Big,
            LimitOverride = limits,
        };
        using TemplateResult result = TemplateEngine.Apply(type.Program!, TemplateDataSource.FromBytes(data), apply);
        using (cancellationToken.Register(result.Cancel))
        {
            // 評価器の時間の上限に加えて、上限を少し過ぎても終わらなければ止める (UI を待たせない)。
            Task finished = await Task.WhenAny(result.Completion, Task.Delay(TimeLimit * 4, CancellationToken.None)).ConfigureAwait(false);
            if (finished != result.Completion)
            {
                result.Cancel();
                return new InspectorUserValue(string.Empty, [], 0, null, TimedOut: true);
            }
        }

        if (result.State == TemplateRunState.LimitReached && result.LimitReached is TemplateLimitKind.Time or TemplateLimitKind.Steps)
        {
            return new InspectorUserValue(string.Empty, [], 0, null, TimedOut: true);
        }

        return await result.InvokeAsync(() => Describe(result, options)).ConfigureAwait(false);
    }

    /// <summary>
    /// プラグインの型を別のスレッドで解釈する。無限ループなどで上限を超えた呼び出しは「時間切れ」にし、それが終わるまでは同じ型を呼ばない
    /// (止められないスレッドを増やさない)。例外はその行だけのエラー (「エラー: メッセージ」)。呼び出しの時間は 08 の AUTO-31 の仕様 5 と同じく
    /// 見張り (<see cref="Plugins.PluginGuard"/>) が測り、遅い呼び出しが続くプラグインを止める。
    /// </summary>
    private async Task<InspectorUserValue> EvaluatePluginAsync(InspectorUserType type, Plugins.PluginIntegration.InspectorTypeEntry plugin, byte[] data,
        Endianness endian, CancellationToken cancellationToken)
    {
        if (plugin.Registration.Guard.IsStopped)
        {
            return new InspectorUserValue(string.Empty, [], 0, null, false) { Stopped = true };
        }

        if (_pluginCalls.TryGetValue(type.Key, out Task? previous) && !previous.IsCompleted)
        {
            return new InspectorUserValue(string.Empty, [], 0, null, TimedOut: true);
        }

        TimeSpan elapsed = TimeSpan.Zero;
        var call = new Task<(string? Value, int Size)>(() =>
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                int size = plugin.Size;
                if (size <= 0 || data.Length < size)
                {
                    return (null, size);
                }

                return (plugin.DecodeOrThrow(data.AsSpan(0, size), endian == Endianness.Big), size);
            }
            finally
            {
                elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start);
            }
        }, TaskCreationOptions.LongRunning);
        _pluginCalls[type.Key] = call;
        call.Start();

        // 上限を少し過ぎても終わらなければ待たない (UI を待たせない)。終わった呼び出しは、かかった時間で判定する。
        Task finished = await Task.WhenAny(call, Task.Delay(TimeLimit * 4, cancellationToken)).ConfigureAwait(false);
        if (finished != call)
        {
            return new InspectorUserValue(string.Empty, [], 0, null, TimedOut: true);
        }

        try
        {
            (string? value, int size) = await call.ConfigureAwait(false);
            if (elapsed > TimeLimit)
            {
                return new InspectorUserValue(string.Empty, [], 0, null, TimedOut: true);
            }

            return value is not null ? new InspectorUserValue(value, [], size, null, false)
                : plugin.Registration.Guard.IsStopped ? new InspectorUserValue(string.Empty, [], 0, null, false) { Stopped = true }
                : new InspectorUserValue(string.Empty, [], Math.Max(0, size), null, false) { NotEnoughData = true };
        }
        catch (Plugins.PluginCallException ex)
        {
            Exception shown = ex.InnerException is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex.InnerException ?? ex;
            return InspectorUserValue.Failed(shown.Message);
        }
    }

    /// <summary>
    /// 書き換えのためにテンプレートの型を評価し直す (INSP-22 の仕様 5、TPL-29 の仕様 3)。結果は呼び出し側が破棄する。最初の子のノードを
    /// <see cref="Templates.Presentation.TemplateInspectorTypes.PlanEdit"/> に渡すと、書き込む内容 (オフセットは起点から) が分かる。
    /// </summary>
    public TemplateResult? ApplyForEdit(InspectorUserType type, byte[] data, Endianness endian)
    {
        if (type.Program is not { } program)
        {
            return null;
        }

        var apply = new TemplateApplyOptions
        {
            TypeName = type.TypeName,
            BigEndian = endian == Endianness.Big,
            LimitOverride = TemplateLimits.Default with { TimeSeconds = TimeLimit.TotalSeconds, Patterns = 100_000 },
        };
        return TemplateEngine.ApplyAndWait(program, TemplateDataSource.FromBytes(data), apply);
    }

    /// <summary>プラグインの型の書き換え: 値の文字列からバイト列を作る (INSP-22 の仕様 5)。書き換えられない型・不正な値は null。</summary>
    public static byte[]? EncodePlugin(InspectorUserType type, string text, Endianness endian) =>
        type.Plugin?.Encode(text, endian == Endianness.Big);

    private static InspectorUserValue Describe(TemplateResult result, InspectorOptions? options)
    {
        TemplateDiagnostic? error = result.Diagnostics.FirstOrDefault(d => d.Severity == TemplateSeverity.Error);
        if (result.Root.Children.Count == 0)
        {
            return InspectorUserValue.Failed(error?.Message ?? "no value");
        }

        TemplateNode node = result.Root.Children[0];
        if (node.HasError)
        {
            return InspectorUserValue.Failed(node.Diagnostics.FirstOrDefault(d => d.Severity == TemplateSeverity.Error)?.Message ?? error?.Message ?? "error");
        }

        var fields = new List<(string, string)>();
        if (node.Kind is TemplateNodeKind.Struct or TemplateNodeKind.Union)
        {
            foreach (TemplateNode child in node.Children.Take(64))
            {
                fields.Add((child.DisplayName, Display(child, options)));
            }
        }

        return new InspectorUserValue(Display(node, options), fields, node.Size, null, false);
    }

    /// <summary>値の表示。書式の指定のない整数 (既定の 10 進の表示) は、インスペクタの表示形式にする。</summary>
    private static string Display(TemplateNode node, InspectorOptions? options)
    {
        string display = node.GetDisplay(TemplateDisplayContext.Current);
        if (options is null || node.Kind != TemplateNodeKind.Integer || node.Size is < 1 or > 8)
        {
            return display;
        }

        object? value = node.Value;
        (System.Numerics.BigInteger number, string plain) = value switch
        {
            long l => ((System.Numerics.BigInteger)l, l.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ulong u => ((System.Numerics.BigInteger)u, u.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            _ => (System.Numerics.BigInteger.Zero, string.Empty),
        };
        if (plain.Length == 0 || display != plain)
        {
            return display;
        }

        int size = (int)node.Size;
        ulong bits = (ulong)(number & ((System.Numerics.BigInteger.One << (size * 8)) - 1));
        return NumberFormat.Integer(number, bits, size, options.IntegerBase, options.DigitGrouping, options.Culture);
    }
}
