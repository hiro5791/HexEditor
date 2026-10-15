using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;

namespace HexEditor.Core.Inspector;

/// <summary>ユーザー定義型 1 つ (INSP-22)。テンプレートの型に <c>[[inspector("表示名")]]</c> を付けたもの。</summary>
public sealed record InspectorUserType(string File, string TypeName, string DisplayName, TemplateProgram Program)
{
    /// <summary>行を区別する鍵 (ファイルと型の名前)。</summary>
    public string Key => File + "|" + TypeName;
}

/// <summary>定義ファイルの読み込みのエラー (InfoBar にファイル名・行番号・理由を出す。INSP-22 の「エラー」)。</summary>
public sealed record InspectorUserTypeError(string File, int Line, int Column, TemplateDiagnostic Diagnostic);

/// <summary>ユーザー定義型の解釈の結果。<see cref="TimedOut"/> は時間の上限 (既定 50 ms) を超えた (仕様 3)。</summary>
public sealed record InspectorUserValue(string Display, IReadOnlyList<(string Name, string Value)> Fields, long Size, string? Error, bool TimedOut)
{
    public static InspectorUserValue Failed(string error) => new(string.Empty, [], 0, error, false);
}

/// <summary>
/// ユーザー定義型 (INSP-22) の定義ファイル: 設定フォルダの <c>inspector/</c> の <c>.hxt</c> を、起動時とファイルの変更時に読み込む (仕様 2)。
/// 型の解釈はテンプレートの追従 (TPL-26) と同じ仕組みで、起点から最大 4 KB のデータに 1 つの型を適用する。1 つの型の失敗はその行だけのエラーにし
/// (仕様 4)、時間の上限を超えた型は「時間切れ」にする (仕様 3)。スレッドに依存しない (変更の通知はファイルの監視のスレッドから来る)。
/// </summary>
public sealed class InspectorUserTypes : IDisposable
{
    public const string FolderName = "inspector";

    /// <summary>1 つの型の解釈の時間の上限 (既定 50 ms。仕様 3)。</summary>
    public static readonly TimeSpan DefaultTimeLimit = TimeSpan.FromMilliseconds(50);

    /// <summary>変更の通知をまとめる時間 (保存の途中の書き込みを待つ)。</summary>
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(300);

    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;

    public InspectorUserTypes(string settingsFolder)
    {
        Folder = Path.Combine(settingsFolder, FolderName);
    }

    /// <summary>定義ファイルのフォルダ。</summary>
    public string Folder { get; }

    public TimeSpan TimeLimit { get; init; } = DefaultTimeLimit;

    /// <summary>読み込んだ型 (ファイルの名前の順、ファイルの中の順)。</summary>
    public IReadOnlyList<InspectorUserType> Types { get; private set; } = [];

    /// <summary>最後の読み込みのエラー。</summary>
    public IReadOnlyList<InspectorUserTypeError> Errors { get; private set; } = [];

    /// <summary>読み込み直した (型またはエラーが変わったかもしれない)。ファイルの監視のスレッドから呼ばれることがある。</summary>
    public event EventHandler? Reloaded;

    /// <summary>定義ファイルを読み込む。フォルダがなければ型なし。</summary>
    public void Load()
    {
        var types = new List<InspectorUserType>();
        var errors = new List<InspectorUserTypeError>();
        if (Directory.Exists(Folder))
        {
            foreach (string file in Directory.EnumerateFiles(Folder, "*.hxt").Order(StringComparer.OrdinalIgnoreCase))
            {
                TemplateProgram program;
                try
                {
                    program = TemplateLoader.LoadFile(file, new TemplateLoadOptions { SearchPaths = [Folder] });
                }
                catch (IOException)
                {
                    // 保存の途中で読めない: 次の変更の通知で読み直す。
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (TemplateDiagnostic d in program.Diagnostics.Where(d => d.Severity == TemplateSeverity.Error))
                {
                    errors.Add(new InspectorUserTypeError(Path.GetFileName(file), d.Location?.Line ?? 0, d.Location?.Column ?? 0, d));
                }

                if (program.HasErrors)
                {
                    continue;
                }

                foreach ((string type, string display) in program.InspectorTypes)
                {
                    types.Add(new InspectorUserType(Path.GetFileName(file), type, display, program));
                }
            }
        }

        lock (_gate)
        {
            Types = types;
            Errors = errors;
        }

        Reloaded?.Invoke(this, EventArgs.Empty);
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
        TemplateLimits limits = TemplateLimits.Default with { TimeSeconds = TimeLimit.TotalSeconds, Patterns = 100_000 };
        var apply = new TemplateApplyOptions
        {
            TypeName = type.TypeName,
            BigEndian = endian == Endianness.Big,
            LimitOverride = limits,
        };
        using TemplateResult result = TemplateEngine.Apply(type.Program, TemplateDataSource.FromBytes(data), apply);
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
