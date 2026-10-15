using System.Text.Json;
using System.Text.Json.Serialization;
using HexEditor.Core.Templates.Loading;

namespace HexEditor.Core.Templates;

/// <summary>
/// 適用の記録 (TPL-20 の仕様 7。ドキュメントに付随するデータに保存する)。追従 (TPL-26) では <see cref="TypeName"/> の型を
/// カーソル位置で評価する。
/// </summary>
public sealed record TemplateApplicationInfo
{
    public required string TemplatePath { get; init; }

    public long StartOffset { get; init; }

    public TemplateApplyKind Kind { get; init; } = TemplateApplyKind.Fixed;

    /// <summary>既定のエンディアン (null はドキュメントの設定)。</summary>
    public bool? BigEndian { get; init; }

    /// <summary>追従で評価する型の名前 (例: <c>std::pe::SectionHeader</c>)。</summary>
    public string? TypeName { get; init; }
}

/// <summary>1 つのドキュメントの適用の一覧 (上限 32 個。TPL-20 の仕様 3)。</summary>
public sealed class TemplateApplicationSet
{
    public const int MaxApplications = 32;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly List<TemplateApplicationInfo> _items = [];

    public IReadOnlyList<TemplateApplicationInfo> Items => _items;

    /// <summary>追加する。上限に達していれば false。</summary>
    public bool TryAdd(TemplateApplicationInfo info)
    {
        if (_items.Count >= MaxApplications)
        {
            return false;
        }

        _items.Add(info);
        return true;
    }

    public bool Remove(TemplateApplicationInfo info) => _items.Remove(info);

    public void Replace(TemplateApplicationInfo old, TemplateApplicationInfo replacement)
    {
        int i = _items.IndexOf(old);
        if (i >= 0)
        {
            _items[i] = replacement;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(_items, Json);

    public static TemplateApplicationSet FromJson(string? json)
    {
        var set = new TemplateApplicationSet();
        if (string.IsNullOrWhiteSpace(json))
        {
            return set;
        }

        try
        {
            foreach (TemplateApplicationInfo info in JsonSerializer.Deserialize<List<TemplateApplicationInfo>>(json, Json) ?? [])
            {
                set.TryAdd(info);
            }
        }
        catch (JsonException)
        {
            // 壊れた記録は捨てる
        }

        return set;
    }
}

/// <summary>
/// 1 つの適用の実行 (TPL-20、TPL-26)。テンプレートの読み込み、評価、ドキュメントの編集の後の再評価 (最後の編集から 500 ms 後。
/// 仕様 5)、テンプレートのファイルの保存による再評価 (仕様 6)、追従のカーソルの移動による再評価 (100 ms 後、200 ms・10 万パターンの
/// 上限。TPL-26 の仕様 2・4) を受け持つ。結果は <see cref="ResultChanged"/> で知らせる (UI スレッドではないスレッドから呼ぶ)。
/// </summary>
public sealed class TemplateApplication : IDisposable
{
    /// <summary>編集の後の再評価までの待ち時間 (TPL-20 の仕様 5)。</summary>
    public static readonly TimeSpan EditDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>追従のカーソルの移動の後の待ち時間 (TPL-26 の仕様 2)。</summary>
    public static readonly TimeSpan FollowDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>追従の評価の上限 (TPL-26 の仕様 4)。</summary>
    public static TemplateLimits FollowLimits { get; } = TemplateLimits.Default with { TimeSeconds = 0.2, Patterns = 100_000 };

    private readonly Func<TemplateDataSource> _data;
    private readonly TemplateLoadOptions _loadOptions;
    private readonly TemplateApplyOptions _applyOptions;
    private readonly TimeProvider _time;
    private readonly bool _watchFiles;
    private readonly object _lock = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private ITimer? _editTimer;
    private ITimer? _fileTimer;
    private ITimer? _followTimer;
    private long _followOffset;
    private bool _disposed;

    public TemplateApplication(TemplateApplicationInfo info, Func<TemplateDataSource> data, TemplateLoadOptions? loadOptions = null,
        TemplateApplyOptions? applyOptions = null, TimeProvider? timeProvider = null, bool watchFiles = true,
        Func<string, TemplateProgram>? loader = null)
    {
        Info = info;
        _data = data;
        _loadOptions = loadOptions ?? TemplateLoadOptions.Default;
        _applyOptions = applyOptions ?? new TemplateApplyOptions();
        _time = timeProvider ?? TimeProvider.System;
        _watchFiles = watchFiles;
        _loader = loader ?? (path => TemplateLoader.LoadFile(path, _loadOptions));
        _followOffset = info.StartOffset;
    }

    private readonly Func<string, TemplateProgram> _loader;
    private TemplateProgram? _unsaved;
    private TemplateLimits? _limitOverride;

    /// <summary>
    /// 「試しに実行」(TPL-31 の仕様 4): 保存していないテンプレートの内容で評価する (null で解除し、ファイルを読み直す)。結果ツリーに
    /// 「(未保存)」と表示する。
    /// </summary>
    public void SetUnsavedProgram(TemplateProgram? program)
    {
        _unsaved = program;
        if (program is null)
        {
            Reevaluate();
        }
        else
        {
            Program = program;
            Evaluate();
        }
    }

    /// <summary>保存していない内容で評価している (「(未保存)」)。</summary>
    public bool IsUnsaved => _unsaved is not null;

    /// <summary>「上限を上げて再実行」(TPL-28 の仕様 3): 上限を上書きして評価し直す。</summary>
    public void ReevaluateWithLimits(TemplateLimits limits)
    {
        _limitOverride = limits;
        Evaluate();
    }

    /// <summary>今の評価の上限の上書き (「上限を上げて再実行」の後)。</summary>
    public TemplateLimits? LimitOverride => _limitOverride;

    /// <summary>今のカーソル位置 (追従)。</summary>
    public long FollowOffset => Interlocked.Read(ref _followOffset);

    /// <summary>適用の設定 (開始オフセット・エンディアン) を変えて評価し直す。</summary>
    public void Update(TemplateApplicationInfo info)
    {
        Info = info;
        if (info.Kind != TemplateApplyKind.Follow)
        {
            Interlocked.Exchange(ref _followOffset, info.StartOffset);
        }

        Evaluate();
    }

    /// <summary>評価を始めた (新しい結果)。評価の終わりは <see cref="ResultChanged"/>。</summary>
    public event EventHandler<TemplateResult>? Started;

    public TemplateApplicationInfo Info { get; private set; }

    public TemplateProgram? Program { get; private set; }

    public TemplateResult? Result { get; private set; }

    /// <summary>テンプレートのファイルが見つからない (TPL-20 の仕様 7)。</summary>
    public bool TemplateMissing { get; private set; }

    /// <summary>設定「編集後に自動で再評価」(既定でオン)。</summary>
    public bool AutoReevaluate { get; set; } = true;

    /// <summary>評価の回数 (テスト用)。</summary>
    public int EvaluationCount { get; private set; }

    public event EventHandler? ResultChanged;

    /// <summary>テンプレートを読み込み、評価を始める。</summary>
    public void Start()
    {
        Load();
        Evaluate();
    }

    /// <summary>テンプレートを読み直して評価し直す (「再実行」)。</summary>
    public void Reevaluate()
    {
        Load();
        Evaluate();
    }

    /// <summary>ドキュメントが編集された。最後の編集から 500 ms 後に再評価する (TPL-20 の仕様 5)。</summary>
    public void DocumentChanged()
    {
        if (!AutoReevaluate)
        {
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _editTimer?.Dispose();
            _editTimer = _time.CreateTimer(_ => Evaluate(), null, EditDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>追従: カーソルが動いた。100 ms 後にその位置で評価し直す (TPL-26 の仕様 2)。</summary>
    public void MoveCursor(long offset)
    {
        if (Info.Kind != TemplateApplyKind.Follow)
        {
            return; // 固定はカーソルを動かしても変わらない (仕様 1)
        }

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _followOffset = offset;
            Result?.Cancel();
            _followTimer?.Dispose();
            _followTimer = _time.CreateTimer(_ => Evaluate(), null, FollowDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>「固定に変換」: そのときのカーソル位置で固定の適用の記録を作る (TPL-26 の仕様 6)。</summary>
    public TemplateApplicationInfo ToFixed() => Info with
    {
        Kind = TemplateApplyKind.Fixed,
        StartOffset = Interlocked.Read(ref _followOffset),
    };

    /// <summary>「固定 / 追従を切り替え」。</summary>
    public void SetKind(TemplateApplyKind kind, string? typeName = null)
    {
        Info = Info with { Kind = kind, TypeName = typeName ?? Info.TypeName };
        Evaluate();
    }

    private void Load()
    {
        if (_unsaved is not null)
        {
            Program = _unsaved;
            return;
        }

        TemplateProgram program = _loader(Info.TemplatePath);
        TemplateMissing = !Info.TemplatePath.StartsWith(BundledTemplates.Root, StringComparison.Ordinal) && !File.Exists(Info.TemplatePath)
            && program.HasErrors;
        Program = program;
        if (_watchFiles)
        {
            Watch(program);
        }
    }

    private void Evaluate()
    {
        TemplateProgram? program = Program;
        if (program is null)
        {
            return;
        }

        TemplateApplyOptions options = _applyOptions with
        {
            StartOffset = Info.Kind == TemplateApplyKind.Follow ? Interlocked.Read(ref _followOffset) : Info.StartOffset,
            BigEndian = Info.BigEndian ?? _applyOptions.BigEndian,
            TypeName = Info.Kind == TemplateApplyKind.Follow ? Info.TypeName : null,
            LimitOverride = Info.Kind == TemplateApplyKind.Follow ? FollowLimits : _limitOverride ?? _applyOptions.LimitOverride,
        };
        TemplateResult? old;
        TemplateResult result;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            old = Result;
            old?.Cancel();
            result = TemplateEngine.Apply(program, _data(), options);
            Result = result;
            EvaluationCount++;
        }

        old?.Dispose();
        Started?.Invoke(this, result);
        result.Completion.ContinueWith(_ => ResultChanged?.Invoke(this, EventArgs.Empty), TaskScheduler.Default);
    }

    /// <summary>テンプレートのファイル (とインポートしたファイル) の保存を監視する (TPL-20 の仕様 6)。</summary>
    private void Watch(TemplateProgram program)
    {
        lock (_lock)
        {
            foreach (FileSystemWatcher w in _watchers)
            {
                w.Dispose();
            }

            _watchers.Clear();
            foreach (string file in program.Files.Prepend(Info.TemplatePath).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (file.StartsWith(BundledTemplates.Root, StringComparison.Ordinal) || Path.GetDirectoryName(file) is not { } dir
                    || !Directory.Exists(dir))
                {
                    continue;
                }

                var watcher = new FileSystemWatcher(dir, Path.GetFileName(file))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                };
                watcher.Changed += (_, _) => FileChanged();
                watcher.Created += (_, _) => FileChanged();
                watcher.Renamed += (_, _) => FileChanged();
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
        }
    }

    private void FileChanged()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            // 一時ファイルに書いてから置き換える保存では、通知が続けて来るのでまとめる
            _fileTimer?.Dispose();
            _fileTimer = _time.CreateTimer(_ => ReloadAfterSave(), null, TimeSpan.FromMilliseconds(200), Timeout.InfiniteTimeSpan);
        }
    }

    private void ReloadAfterSave()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            Load();
            bool readFailed = Program!.Diagnostics.Any(d => d.Code == TemplateMessages.ImportNotFound
                && string.Equals(d.Location?.File, Program.Path, StringComparison.OrdinalIgnoreCase));
            if (!readFailed)
            {
                break;
            }

            Thread.Sleep(50); // 保存中で開けない
        }

        Evaluate();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _editTimer?.Dispose();
            _fileTimer?.Dispose();
            _followTimer?.Dispose();
            foreach (FileSystemWatcher w in _watchers)
            {
                w.Dispose();
            }

            _watchers.Clear();
        }

        Result?.Dispose();
    }
}
