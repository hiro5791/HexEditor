using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HexEditor.Sdk;
using HexEditor.Sdk.Hosting;

namespace HexEditor.Core.Scripting.CSharp;

/// <summary>
/// C# スクリプトを実行する (AUTO-03 の仕様 1・2・5〜8)。コンパイルした結果をキャッシュし、実行ごとに回収可能な
/// <see cref="AssemblyLoadContext"/> に読み込んで、実行後に解放する。キャンセルは <c>Ct</c> による協調的なものだけ。
/// </summary>
internal static class CSharpScriptRunner
{
    /// <summary>
    /// 参照できるアセンブリ (仕様 5)。既定で参照し、<c>#r</c> でもこれ以外は参照できない。トリミングの対象から外す (PKG-10)。
    /// <c>System.Private.CoreLib</c>・<c>System.Console</c>・<c>System.Runtime.Numerics</c> は基本ライブラリの実体 (System.Runtime の型の実体と
    /// <c>Console.WriteLine</c>・<c>BigInteger</c>) のため含める。
    /// </summary>
    public static IReadOnlyList<string> AllowedReferences { get; } =
    [
        "HexEditor.Sdk",
        "System.Runtime",
        "System.Private.CoreLib",
        "System.Collections",
        "System.Linq",
        "System.Text.RegularExpressions",
        "System.Text.Json",
        "System.Memory",
        "System.Runtime.Numerics",
        "System.Security.Cryptography",
        "System.IO",
        "System.Console",
        "netstandard",
    ];

    public static async Task<ScriptExecution> RunAsync(ScriptContext ctx, string code, ScriptRunRequest request)
    {
        CSharpComponentManager? manager = ctx.Host.CSharp;
        if (manager is null || !manager.IsInstalled)
        {
            bool installed = false;
            if (manager is not null && !ctx.Headless)
            {
                // コンポーネントの追加を案内する (仕様 8)。App はダイアログで確認し、追加まで行う。
                installed = await ctx.Dispatcher.InvokeAsync(() => ctx.Environment.Ui.OfferCSharpComponentAsync()).ConfigureAwait(false) && manager.IsInstalled;
            }

            if (!installed)
            {
                string message = Msg.T("C# スクリプト コンポーネントがありません。メニュー「ツール > スクリプト > C# スクリプト コンポーネントを追加...」で追加してください。", "The C# script component is not installed. Add it from the menu \"Tools > Script > Add C# Script Component...\".");
                return ScriptExecution.Error(ScriptFailure.Io, message, [new ScriptDiagnostic(message, ctx.Path, 0, 0)]);
            }
        }

        ICSharpScriptBackend backend;
        try
        {
            backend = manager!.LoadBackend();
        }
        catch (CSharpComponentException ex)
        {
            return ScriptExecution.Error(ScriptFailure.Io, ex.Message, [new ScriptDiagnostic(ex.Message, ctx.Path, 0, 0)]);
        }

        CompiledScript compiled;
        try
        {
            compiled = await Task.Run(() => CSharpCompileCache.For(ctx.Host.Folders.CSharpCache).GetOrCompile(backend, ctx, code, request.Debug), ctx.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ScriptExecution.Stopped();
        }

        if (compiled.Diagnostics.Count > 0)
        {
            return ScriptExecution.Error(ScriptFailure.ScriptError, Msg.T("コンパイルエラー", "Compilation error"), compiled.Diagnostics);
        }

        var thread = new CSharpScriptThread();
        ctx.Thread = thread;
        var alc = new AssemblyLoadContext("csx: " + ctx.Name, isCollectible: true);
        try
        {
            Assembly assembly;
            using (var stream = new MemoryStream(compiled.Assembly!))
            {
                assembly = alc.LoadFromStream(stream);
            }

            MethodInfo entry = assembly.GetType(compiled.EntryType!, throwOnError: true)!
                .GetMethod(compiled.EntryMethod!, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            var globals = new ScriptGlobals(ctx.App, ctx.Token);
            using (ScriptConsole.Redirect(ctx))
            {
                // Roslyn のスクリプトの入口: Task<object> <Factory>(object[] submissionArray)。[0] がグローバル変数。
                var task = (Task)entry.Invoke(null, [new object?[] { globals, null }])!;
                await task.ConfigureAwait(false);

                // イベントを購読していれば、停止か購読の終わりまで常駐する (AUTO-06 の仕様 4)。
                if (ctx.Subscriptions > 0 && !ctx.StopRequested)
                {
                    var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    ctx.SubscriptionsEnded += (_, _) => ended.TrySetResult();
                    using CancellationTokenRegistration reg = ctx.Token.Register(() => ended.TrySetResult());
                    if (ctx.Subscriptions > 0)
                    {
                        await ended.Task.ConfigureAwait(false);
                    }
                }
            }

            return ctx.StopRequested && !ctx.StopCalled ? ScriptExecution.Stopped() : ScriptExecution.Success;
        }
        catch (ScriptTerminatedException)
        {
            return ScriptExecution.Success;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is { } inner)
        {
            return FromException(ctx, inner);
        }
        catch (Exception ex)
        {
            return FromException(ctx, ex);
        }
        finally
        {
            alc.Unload();
        }
    }

    private static ScriptExecution FromException(ScriptContext ctx, Exception ex)
    {
        if (ex is ScriptTerminatedException)
        {
            return ScriptExecution.Success;
        }

        if (ex is OperationCanceledException or CanceledError && ctx.StopRequested)
        {
            return ScriptExecution.Stopped();
        }

        // 例外のスタックトレースにスクリプトの行番号を含める (AUTO-22 の仕様 3。PDB を埋め込んでいる)。
        string stack = ex.ToString();
        (int line, int column) = FindLine(stack, ctx.Path);
        ScriptFailure failure = ex switch
        {
            LimitError => ScriptFailure.Limit,
            PermissionError => ScriptFailure.Permission,
            _ => ScriptFailure.ScriptError,
        };
        string message = $"{ex.GetType().Name}: {ex.Message}";
        return ScriptExecution.Error(failure, message, [new ScriptDiagnostic(message, ctx.Path, line, column, ex.GetType().Name)], stack);
    }

    private static (int Line, int Column) FindLine(string stack, string? path)
    {
        // ".csx:line 12" の形 (英語) と、日本語の ".csx:行 12" の形。
        var m = System.Text.RegularExpressions.Regex.Match(stack, @"\.csx:(?:line|行) (\d+)");
        return m.Success ? (int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 0) : (0, 0);
    }
}

/// <summary>C# スクリプトの「スクリプトのスレッド」(スレッドプール。ハンドラはスレッドプールで呼ぶ)。</summary>
internal sealed class CSharpScriptThread : IScriptThread
{
    public bool IsCurrent => true;

    public Task PostAsync(Action action) => Task.Run(action);

    public T Invoke<T>(Func<T> func) => func();
}

/// <summary>コンパイルの結果。</summary>
internal sealed record CompiledScript(byte[]? Assembly, string? EntryType, string? EntryMethod, IReadOnlyList<ScriptDiagnostic> Diagnostics);

/// <summary>
/// コンパイル結果のキャッシュ (AUTO-03 の仕様 6)。キーは「本文の SHA-256 + <c>#load</c> したファイルのハッシュ + SDK のバージョン +
/// コンポーネントのバージョン」。キャッシュ先は <c>&lt;データフォルダ&gt;/cache/csx/</c>。上限 200 MB を超えたら古いものから消す。
/// <c>#load</c> したファイルはコンパイルするまで分からないので、本文などのキーで探し、記録した <c>#load</c> のファイルのハッシュが今も同じなら使う。
/// </summary>
internal sealed class CSharpCompileCache(string folder)
{
    public const long LimitBytes = 200L * 1024 * 1024;

    private static readonly Dictionary<string, CSharpCompileCache> Caches = [];
    private static readonly Lock CachesLock = new();
    private readonly Lock _lock = new();

    public static CSharpCompileCache For(string folder)
    {
        lock (CachesLock)
        {
            if (!Caches.TryGetValue(folder, out CSharpCompileCache? cache))
            {
                cache = new CSharpCompileCache(folder);
                Caches[folder] = cache;
            }

            return cache;
        }
    }

    public string Folder { get; } = folder;

    private sealed record Entry(string EntryType, string EntryMethod, Dictionary<string, string> Loaded);

    public CompiledScript GetOrCompile(ICSharpScriptBackend backend, ScriptContext ctx, string code, bool debug)
    {
        string sdkVersion = typeof(IApp).Assembly.GetName().Version?.ToString() ?? "0";
        string baseKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{code}\n{sdkVersion}\n{backend.Version}\n{debug}\n{ctx.Path}")))[..40];
        lock (_lock)
        {
            if (TryLoad(baseKey) is { } hit)
            {
                return hit;
            }
        }

        var request = new CSharpCompileRequest
        {
            Code = code,
            Path = ctx.Path is null ? null : Path.GetFullPath(ctx.Path),
            LoadFolders = [.. new[] { ctx.ScriptFolder, ctx.Host.Folders.Library }.OfType<string>()],
            AllowedReferences = CSharpScriptRunner.AllowedReferences,
            ReferencePaths = ReferencePaths(),
            Debug = debug,
        };
        CSharpCompileResult result = backend.Compile(request, ctx.Token);
        List<ScriptDiagnostic> errors = [.. result.Diagnostics.Where(d => d.IsError)
            .Select(d => new ScriptDiagnostic(d.Message, d.Path ?? ctx.Path, d.Line, d.Column, d.Id))];
        if (result.Assembly is null || errors.Count > 0)
        {
            return new CompiledScript(null, null, null, errors.Count > 0 ? errors : [new ScriptDiagnostic(Msg.T("コンパイルに失敗しました。", "Compilation failed."), ctx.Path, 0, 0)]);
        }

        lock (_lock)
        {
            Save(baseKey, result);
        }

        return new CompiledScript(result.Assembly, result.EntryType, result.EntryMethod, []);
    }

    /// <summary>参照するアセンブリのファイル (アプリの基本ライブラリと SDK)。</summary>
    internal static IReadOnlyDictionary<string, string> ReferencePaths()
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        foreach (string file in (tpa ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (CSharpScriptRunner.AllowedReferences.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                paths[name] = file;
            }
        }

        string sdk = typeof(IApp).Assembly.Location;
        if (!string.IsNullOrEmpty(sdk))
        {
            paths["HexEditor.Sdk"] = sdk;
        }

        return paths;
    }

    private CompiledScript? TryLoad(string baseKey)
    {
        string meta = Path.Combine(Folder, baseKey + ".json");
        string dll = Path.Combine(Folder, baseKey + ".dll");
        if (!File.Exists(meta) || !File.Exists(dll))
        {
            return null;
        }

        try
        {
            Entry? entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(meta));
            if (entry is null)
            {
                return null;
            }

            foreach ((string path, string hash) in entry.Loaded)
            {
                if (!File.Exists(path) || CSharpComponentManager.Sha256Of(path) != hash)
                {
                    return null;
                }
            }

            File.SetLastWriteTimeUtc(dll, DateTime.UtcNow);
            return new CompiledScript(File.ReadAllBytes(dll), entry.EntryType, entry.EntryMethod, []);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Save(string baseKey, CSharpCompileResult result)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(Path.Combine(Folder, baseKey + ".dll"), result.Assembly!);
            var entry = new Entry(result.EntryType!, result.EntryMethod!, new Dictionary<string, string>(result.LoadedFiles));
            File.WriteAllText(Path.Combine(Folder, baseKey + ".json"), JsonSerializer.Serialize(entry));
            Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // キャッシュに書けなくても実行は続ける。
        }
    }

    /// <summary>上限を超えたら古い (最後に使った時刻が古い) ものから消す。</summary>
    private void Trim()
    {
        var files = new DirectoryInfo(Folder).GetFiles("*.dll").OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length);
        foreach (FileInfo f in files)
        {
            if (total <= LimitBytes)
            {
                break;
            }

            total -= f.Length;
            f.Delete();
            File.Delete(Path.ChangeExtension(f.FullName, ".json"));
        }
    }

    /// <summary>キャッシュを消す (設定画面「スクリプト > C#」のキャッシュの削除)。</summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }
        }
    }
}

/// <summary>
/// <c>Console.WriteLine</c> を出力パネルに出す (AUTO-22 の仕様 3)。プロセス全体の <see cref="Console.Out"/> を差し替え、実行中のスクリプト
/// (非同期の流れで受け継ぐ) の出力に振り分ける。
/// </summary>
internal static class ScriptConsole
{
    private static readonly AsyncLocal<ScriptContext?> Current = new();
    private static readonly Lock InstallLock = new();
    private static TextWriter? _original;

    public static IDisposable Redirect(ScriptContext ctx)
    {
        lock (InstallLock)
        {
            if (_original is null)
            {
                _original = Console.Out;
                Console.SetOut(new Router(_original));
            }
        }

        ScriptContext? previous = Current.Value;
        Current.Value = ctx;
        return new Scope(previous);
    }

    private sealed class Scope(ScriptContext? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }

    private sealed class Router(TextWriter fallback) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (Current.Value is not ScriptContext ctx)
            {
                fallback.Write(value);
                return;
            }

            lock (_line)
            {
                if (value == '\n')
                {
                    ctx.Output(ScriptOutputKind.Info, _line.ToString().TrimEnd('\r'));
                    _line.Clear();
                }
                else
                {
                    _line.Append(value);
                }
            }
        }

        public override void WriteLine(string? value)
        {
            if (Current.Value is ScriptContext ctx)
            {
                lock (_line)
                {
                    string text = _line.ToString() + value;
                    _line.Clear();
                    ctx.Output(ScriptOutputKind.Info, text);
                }

                return;
            }

            fallback.WriteLine(value);
        }
    }
}
