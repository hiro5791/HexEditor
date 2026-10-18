using System.Diagnostics;
using System.Globalization;
using HexEditor.Core.Scripting.CSharp;
using HexEditor.Core.Scripting.Js;
using HexEditor.Sdk;

namespace HexEditor.Core.Scripting;

/// <summary>
/// 実行の上限 (AUTO-02 の仕様 4)。設定の値が既定で、スクリプトはメタデータ (<c>@timeout</c> / <c>@memory</c>) で引き上げを要求できる。
/// 要求は設定できる範囲の上限 (実行時間は無制限、メモリは 4 GB) に収める。
/// </summary>
public sealed record ScriptLimits
{
    public const long MinMemory = 16L * 1024 * 1024;
    public const long MaxMemory = 4L * 1024 * 1024 * 1024;
    public const int MinRecursion = 100;
    public const int MaxRecursion = 10_000;

    /// <summary>実行時間。null は無制限。</summary>
    public TimeSpan? Time { get; init; } = TimeSpan.FromSeconds(60);

    public long MemoryBytes { get; init; } = 512L * 1024 * 1024;

    public int RecursionDepth { get; init; } = 1000;

    /// <summary>同時に実行できるスクリプトの数 (AUTO-01 の仕様 6。1〜16)。</summary>
    public int MaxConcurrent { get; init; } = 4;

    /// <summary>メタデータの要求を反映した上限。</summary>
    public ScriptLimits WithRequest(ScriptMetadata metadata)
    {
        ScriptLimits l = this;
        if (metadata.TimeoutSeconds is double s && (Time is null || s > Time.Value.TotalSeconds))
        {
            l = l with { Time = TimeSpan.FromSeconds(s) };
        }

        if (metadata.MemoryMegabytes is long mb)
        {
            long requested = Math.Min(MaxMemory, mb * 1024 * 1024);
            if (requested > MemoryBytes)
            {
                l = l with { MemoryBytes = requested };
            }
        }

        return l;
    }

    /// <summary>設定の値から作る (キーは <see cref="ScriptSettingKeys"/>)。</summary>
    public static ScriptLimits FromSettings(Func<string, double?> read)
    {
        double timeout = read(ScriptSettingKeys.Timeout) ?? 60;
        double memory = read(ScriptSettingKeys.Memory) ?? 512;
        double recursion = read(ScriptSettingKeys.Recursion) ?? 1000;
        double concurrent = read(ScriptSettingKeys.MaxConcurrent) ?? 4;
        return new ScriptLimits
        {
            Time = timeout <= 0 ? null : TimeSpan.FromSeconds(Math.Max(1, timeout)),
            MemoryBytes = Math.Clamp((long)memory * 1024 * 1024, MinMemory, MaxMemory),
            RecursionDepth = Math.Clamp((int)recursion, MinRecursion, MaxRecursion),
            MaxConcurrent = Math.Clamp((int)concurrent, 1, 16),
        };
    }
}

/// <summary>スクリプトの設定のキー (09 の設定。<see cref="Settings.BuiltInSettings"/> に定義)。</summary>
public static class ScriptSettingKeys
{
    /// <summary>実行時間の上限 (秒。0 は無制限)。</summary>
    public const string Timeout = "scripts.js.timeoutSeconds";

    /// <summary>メモリの上限 (MB)。</summary>
    public const string Memory = "scripts.js.memoryMB";

    /// <summary>再帰の深さの上限。</summary>
    public const string Recursion = "scripts.js.recursionDepth";

    /// <summary>同時に実行できるスクリプトの数。</summary>
    public const string MaxConcurrent = "scripts.maxConcurrent";

    /// <summary>組織向けの設定 (AUTO-34 の仕様 9): <c>none</c> (なし)、<c>disabled</c>、<c>signedPluginsOnly</c>。</summary>
    public const string Policy = "scripts.policy";

    public const string PolicyDisabled = "disabled";
    public const string PolicySignedPluginsOnly = "signedPluginsOnly";
}

/// <summary>スクリプトの実行に使うフォルダ (00 の 10 章、AUTO-02 の仕様 3、AUTO-17 の仕様 3)。</summary>
public sealed record ScriptFolders(string Settings, string Data)
{
    /// <summary><c>&lt;設定フォルダ&gt;/scripts/</c> (利用者のスクリプト)。</summary>
    public string Scripts => Path.Combine(Settings, "scripts");

    /// <summary>スクリプトライブラリ (<c>import</c> と <c>#load</c> で読める)。</summary>
    public string Library => Path.Combine(Scripts, "lib");

    /// <summary><c>script.storage</c> の保存先。</summary>
    public string Storage => Path.Combine(Scripts, "storage");

    /// <summary>信頼済みの一覧。</summary>
    public string TrustFile => Path.Combine(Scripts, "trust.json");

    /// <summary>C# スクリプトのコンパイル結果のキャッシュ (AUTO-03 の仕様 6)。</summary>
    public string CSharpCache => Path.Combine(Data, "cache", "csx");
}

/// <summary>
/// スクリプトの実行の管理 (アプリ全体で 1 つ。AUTO-01)。言語の判定、メタデータ、API のバージョン、組織の設定、信頼の確認、
/// パラメータの入力、同時実行の数、停止 (2 段階)、結果の記録を行う。
/// </summary>
public sealed class ScriptHost : IDisposable
{
    private readonly Lock _lock = new();
    private readonly List<ScriptRun> _running = [];
    private readonly SemaphoreSlim _slots;
    private readonly TimeProvider _time;
    private int _maxConcurrent;

    public ScriptHost(ScriptFolders folders, TrustStore trust, Func<ScriptLimits> limits, Func<string> policy,
        CSharpComponentManager? csharp = null, TimeProvider? time = null)
    {
        Folders = folders;
        Trust = trust;
        Limits = limits;
        Policy = policy;
        CSharp = csharp;
        _time = time ?? TimeProvider.System;
        _maxConcurrent = Math.Clamp(limits().MaxConcurrent, 1, 16);
        _slots = new SemaphoreSlim(_maxConcurrent, 16);
        Storage = new ScriptStorageStore(folders.Storage);
    }

    public ScriptFolders Folders { get; }

    public TrustStore Trust { get; }

    public Func<ScriptLimits> Limits { get; }

    public Func<string> Policy { get; }

    public CSharpComponentManager? CSharp { get; }

    public ScriptStorageStore Storage { get; }

    /// <summary>停止を求めてから強制的に中断するまでの猶予 (AUTO-01 の仕様 8)。</summary>
    public TimeSpan ForceStopGrace { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>同梱のサンプルのフォルダ (AUTO-46。信頼の確認を出さない)。</summary>
    public IReadOnlyList<string> BundledSampleFolders { get; init; } = [];

    /// <summary>実行中のスクリプト (待機中を含む)。</summary>
    public IReadOnlyList<ScriptRun> Running
    {
        get
        {
            lock (_lock)
            {
                return [.. _running];
            }
        }
    }

    /// <summary>直前に実行したスクリプト (F5 の再実行。AUTO-01 の仕様 2)。</summary>
    public ScriptRunRequest? LastRequest { get; private set; }

    /// <summary>実行中のスクリプトが増えた・減った。</summary>
    public event EventHandler? RunningChanged;

    /// <summary>実行を始めた (処理センター・InfoBar の表示に使う)。</summary>
    public event EventHandler<ScriptRun>? RunStarted;

    /// <summary>
    /// 実行する。すぐに戻り、結果は <see cref="ScriptRun.Completion"/> で受け取る。UI スレッドから呼んでよい。
    /// </summary>
    public ScriptRun Start(ScriptRunRequest request, IScriptEnvironment environment)
    {
        Msg.Current = environment.Language;
        string fallbackName = request.DisplayName ?? (request.Path is not null ? Path.GetFileName(request.Path) : "script");
        ScriptLanguage language = request.Language ?? (request.Path is not null ? LanguageOf(request.Path) ?? ScriptLanguage.JavaScript : ScriptLanguage.JavaScript);
        var run = new ScriptRun(this, fallbackName, request.Path, language, _time) { Origin = request.Origin };
        if (request.Origin is ScriptOrigin.File or ScriptOrigin.Editor or ScriptOrigin.BundledSample)
        {
            LastRequest = request;
        }

        lock (_lock)
        {
            _running.Add(run);
        }

        RaiseRunningChanged(environment);
        RunStarted?.Invoke(this, run);
        _ = Task.Run(() => RunAsync(run, request, environment));
        return run;
    }

    /// <summary>実行して結果を待つ (ヘッドレス実行・テスト)。</summary>
    public Task<ScriptRunResult> RunAndWaitAsync(ScriptRunRequest request, IScriptEnvironment environment) => Start(request, environment).Completion;

    /// <summary>拡張子から言語を決める (AUTO-01 の仕様 1)。対応しない拡張子は null。</summary>
    public static ScriptLanguage? LanguageOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".js" or ".mjs" => ScriptLanguage.JavaScript,
        ".csx" => ScriptLanguage.CSharp,
        _ => null,
    };

    private async Task RunAsync(ScriptRun run, ScriptRunRequest request, IScriptEnvironment env)
    {
        long started = Stopwatch.GetTimestamp();
        ScriptRunResult result;
        try
        {
            result = await RunCoreAsync(run, request, env, started).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = Fail(run, ScriptFailure.ScriptError, ex.Message, started, [], ex.StackTrace);
        }

        lock (_lock)
        {
            _running.Remove(run);
        }

        run.Context?.Dispose();
        run.ExecutionFinished = true;
        if (!request.Quiet)
        {
            WriteResultLine(run, result, env);
        }

        run.Complete(result);
        RaiseRunningChanged(env);
    }

    private async Task<ScriptRunResult> RunCoreAsync(ScriptRun run, ScriptRunRequest request, IScriptEnvironment env, long started)
    {
        // 1. 言語と本文
        if (request.Language is null && request.Path is not null && LanguageOf(request.Path) is null)
        {
            return Fail(run, ScriptFailure.Argument, Msg.T($"スクリプトとして実行できない拡張子です: {Path.GetExtension(request.Path)} (.js、.mjs、.csx を使えます)", $"This extension cannot be run as a script: {Path.GetExtension(request.Path)} (.js, .mjs and .csx are supported)"), started);
        }

        string code;
        try
        {
            code = request.Code ?? await File.ReadAllTextAsync(request.Path ?? throw new ArgumentError(Msg.T("スクリプトがありません。", "There is no script."))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(run, ScriptFailure.Io, Msg.T($"スクリプトを読めません: {ex.Message}", $"Cannot read the script: {ex.Message}"), started);
        }

        // 2. メタデータ (誤りは警告として出し、実行は続ける。AUTO-26 の仕様 5)
        ScriptMetadata meta = ScriptMetadata.Parse(code);
        run.Name = meta.DisplayName(env.Language, run.Name);
        foreach (ScriptMetadataWarning w in meta.Warnings)
        {
            env.Output.Write(run.Channel, ScriptOutputKind.Warning, Msg.T($"メタデータ: {w.Message}", $"Metadata: {w.Message}"), request.Path, w.Line);
        }

        // 3. API のバージョン (AUTO-04 の仕様 8。本文は実行しない)
        if (meta.Api is not null && ApiVersion.TryParse(meta.Api, out ApiVersion required) && !ApiVersion.Current.Satisfies(required))
        {
            return Fail(run, ScriptFailure.ScriptError,
                Msg.T($"このスクリプトは API {required} が必要ですが、アプリの API は {ApiVersion.Current} です (app.apiVersion)。", $"This script requires API {required}, but the app API is {ApiVersion.Current} (app.apiVersion)."), started);
        }

        // 4. 組織の設定 (AUTO-34 の仕様 9)
        if (Policy() == ScriptSettingKeys.PolicyDisabled)
        {
            return Fail(run, ScriptFailure.Permission, Msg.T("組織の設定 (scripts.policy) でスクリプトは無効にされています。", "Scripts are disabled by the organization setting (scripts.policy)."), started, status: ScriptRunStatus.NotRun);
        }

        // 5. 信頼の確認 (AUTO-34)
        TrustDecision trust = await new ScriptTrustCheck(this, env).CheckAsync(request, run, meta, code).ConfigureAwait(false);
        if (!trust.Allowed)
        {
            return Fail(run, ScriptFailure.Permission, trust.Reason, started, status: ScriptRunStatus.NotRun);
        }

        // 6. パラメータ (AUTO-26 の仕様 3)
        IReadOnlyDictionary<string, object?>? args = await new ScriptParameters(this, env).CollectAsync(request, run, meta).ConfigureAwait(false);
        if (args is null)
        {
            return Fail(run, ScriptFailure.Canceled, Msg.T("パラメータの入力がキャンセルされました。", "Parameter input was canceled."), started, status: ScriptRunStatus.NotRun);
        }

        // 信頼の確認とパラメータは元の本文で行った。実行する本文はその後に変換する (マクロの繰り返し再生。AUTO-28)。
        if (request.CodeWrapper is { } wrap)
        {
            code = wrap(code);
        }

        string? folder = request.Path is not null ? Path.GetDirectoryName(Path.GetFullPath(request.Path)) : null;
        var context = new ScriptContext(this, run, env, request, meta, args, folder);
        context.Grant(trust.Permissions);
        context.App = new Api.ScriptApp(context);
        run.Context = context;
        StartRunOperation(context, env);

        // 7. 同時に実行できる数 (AUTO-01 の仕様 6)
        AdjustSlots();
        try
        {
            await _slots.WaitAsync(context.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Task.Run(context.Finish).ConfigureAwait(false);
            return new ScriptRunResult(ScriptRunStatus.Stopped, ScriptFailure.Canceled, Msg.T("実行を待っている間に停止しました。", "Stopped while waiting to run."), [], null,
                Stopwatch.GetElapsedTime(started), [], null);
        }

        ScriptExecution exec;
        try
        {
            run.SetStatus(ScriptRunStatus.Running);
            ScriptLimits limits = Limits().WithRequest(meta);
            if (request.TimeLimitOverride is TimeSpan t)
            {
                limits = limits with { Time = t };
            }

            exec = run.Language == ScriptLanguage.CSharp
                ? await CSharpScriptRunner.RunAsync(context, code, request).ConfigureAwait(false)
                : await JsScriptRunner.RunAsync(context, code, limits).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            exec = ScriptExecution.Error(ScriptFailure.ScriptError, ex.Message, [new ScriptDiagnostic(ex.Message, run.Path, 0, 0)], ex.ToString());
        }
        finally
        {
            _slots.Release();
        }

        // 変更は例外で終わった場合も Undo の単位として残す (AUTO-01 の仕様 9)。
        IReadOnlyList<string> modified = await Task.Run(context.Finish).ConfigureAwait(false);
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
        return exec.Status switch
        {
            ScriptRunStatus.Succeeded => new ScriptRunResult(ScriptRunStatus.Succeeded, ScriptFailure.None, null, [], null, elapsed, modified, context.ExitCode),
            ScriptRunStatus.Stopped => new ScriptRunResult(ScriptRunStatus.Stopped, ScriptFailure.Canceled, exec.Message, [], null, elapsed, modified, context.ExitCode),
            _ => new ScriptRunResult(ScriptRunStatus.Failed, exec.Failure, exec.Message, exec.Diagnostics, exec.StackTrace, elapsed, modified, context.ExitCode),
        };
    }

    /// <summary>処理センターに「スクリプト: 名前」を出す (経過時間・進捗・停止ボタン。AUTO-01 の仕様 6)。</summary>
    private static void StartRunOperation(ScriptContext context, IScriptEnvironment env)
    {
        try
        {
            env.Dispatcher.Invoke(() =>
            {
                Operations.OperationCenter center = env.OperationsFor(env.ActiveDocument);
                _ = center.RunAsync(env.Localize(ScriptTexts.Operation, context.Name), Operations.OperationKind.ReadOnly, null, null, async op =>
                {
                    context.RunOperation = op;
                    using CancellationTokenRegistration reg = op.CancellationToken.Register(context.RequestStop);
                    await context.Ended.ConfigureAwait(false);
                });
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // 処理センターがない (ウィンドウが閉じられた) 場合も実行は続ける。
        }
    }

    private void AdjustSlots()
    {
        int wanted = Math.Clamp(Limits().MaxConcurrent, 1, 16);
        lock (_lock)
        {
            while (_maxConcurrent < wanted)
            {
                _slots.Release();
                _maxConcurrent++;
            }

            // 減らす場合は空きを取り上げる (実行中のものは止めない)。
            while (_maxConcurrent > wanted && _slots.Wait(0))
            {
                _maxConcurrent--;
            }
        }
    }

    private static ScriptRunResult Fail(ScriptRun run, ScriptFailure failure, string message, long started,
        IReadOnlyList<ScriptDiagnostic>? diagnostics = null, string? stack = null, ScriptRunStatus status = ScriptRunStatus.Failed) =>
        new(status, failure, message, diagnostics ?? [new ScriptDiagnostic(message, run.Path, 0, 0)], stack, Stopwatch.GetElapsedTime(started), [], null);

    /// <summary>停止の第 2 段階 (AUTO-01 の仕様 8)。猶予を過ぎても終わらなければ強制的に中断する。</summary>
    internal void ScheduleForceStop(ScriptContext context)
    {
        _ = Task.Run(async () =>
        {
            Task done = context.Run.Completion;
            if (await Task.WhenAny(done, Task.Delay(ForceStopGrace, _time)).ConfigureAwait(false) == done)
            {
                return;
            }

            if (context.ForceStop is { } force)
            {
                force();
            }
            else
            {
                // C# スクリプトは強制中断できない (AUTO-03 の仕様 7)。
                context.Run.IsNotResponding = true;
            }
        });
    }

    /// <summary>結果を出力パネルに 1 行で記録する (AUTO-01 の仕様 10)。エラーは位置付きで出す。</summary>
    private static void WriteResultLine(ScriptRun run, ScriptRunResult result, IScriptEnvironment env)
    {
        string elapsed = result.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";
        if (result.Status == ScriptRunStatus.Failed)
        {
            foreach (ScriptDiagnostic d in result.Diagnostics)
            {
                string where = d.Path is not null && d.Line > 0 ? $"{Path.GetFileName(d.Path)}({d.Line},{d.Column}): " : "";
                env.Output.Write(run.Channel, d.IsError ? ScriptOutputKind.Error : ScriptOutputKind.Warning, where + (d.Code is null ? "" : d.Code + ": ") + d.Message,
                    d.Path, d.Line, d.Column);
            }

            if (!string.IsNullOrEmpty(result.StackTrace))
            {
                env.Output.Write(run.Channel, ScriptOutputKind.Error, result.StackTrace);
            }
        }

        string modified = result.ModifiedDocuments.Count > 0 ? Msg.T(" / 変更: ", " / Modified: ") + string.Join(", ", result.ModifiedDocuments) : "";
        (ScriptOutputKind kind, string text) = result.Status switch
        {
            ScriptRunStatus.Succeeded => (ScriptOutputKind.Info, Msg.T($"成功 ({elapsed}){modified}", $"Succeeded ({elapsed}){modified}")),
            ScriptRunStatus.Stopped => (ScriptOutputKind.Info, Msg.T($"停止しました ({elapsed}){modified}", $"Stopped ({elapsed}){modified}")),
            ScriptRunStatus.NotRun => (ScriptOutputKind.Warning, Msg.T($"実行しませんでした: {result.Message}", $"Not run: {result.Message}")),
            _ => (ScriptOutputKind.Error, Msg.T($"失敗 ({elapsed}){modified}: {result.Message}", $"Failed ({elapsed}){modified}: {result.Message}")),
        };
        env.Output.Write(run.Channel, kind, text);
    }

    private void RaiseRunningChanged(IScriptEnvironment env)
    {
        int count = Running.Count;
        try
        {
            _ = env.Dispatcher.InvokeAsync(() => env.OnRunningScriptsChanged(count));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }

        RunningChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 外部のプログラムからの自動操作 (名前付きパイプの JSON-RPC。AUTO-45) の 1 つの接続の実行の状態を作る。本文は実行せず、API の
    /// 入口 (<see cref="ScriptContext.App"/>) だけを使う。<paramref name="permissions"/> は信頼の確認で与えた許可。
    /// 接続が終わったら <see cref="CloseExternalContext"/> を呼ぶ (スクリプトのスレッドで呼ぶのと同じく、UI スレッドから呼ばない)。
    /// </summary>
    public ScriptContext OpenExternalContext(string name, IScriptEnvironment environment, IEnumerable<string> permissions)
    {
        var run = new ScriptRun(this, name, null, ScriptLanguage.CSharp, _time) { Origin = ScriptOrigin.External };
        var request = new ScriptRunRequest { Origin = ScriptOrigin.External, DisplayName = name, Quiet = true };
        var context = new ScriptContext(this, run, environment, request, ScriptMetadata.Parse(string.Empty), new Dictionary<string, object?>(), null)
        {
            Thread = new CSharp.CSharpScriptThread(),
        };
        context.Grant(permissions);
        context.App = new Api.ScriptApp(context);
        run.Context = context;
        run.SetStatus(ScriptRunStatus.Running);
        return context;
    }

    /// <summary>外部からの自動操作の接続を終える (書き込みの排他を手放し、Undo の単位を閉じ、購読と強調表示を消す)。</summary>
    public void CloseExternalContext(ScriptContext context)
    {
        IReadOnlyList<string> modified = [];
        try
        {
            context.RequestStopWithoutForce();
            modified = context.Finish();
        }
        finally
        {
            context.Run.ExecutionFinished = true;
            context.Run.Complete(new ScriptRunResult(ScriptRunStatus.Succeeded, ScriptFailure.None, null, [], null,
                _time.GetUtcNow() - context.Run.StartedAt, modified, null));
            context.Dispose();
        }
    }

    /// <summary>すべての実行を止める (アプリの終了)。</summary>
    public void StopAll()
    {
        foreach (ScriptRun run in Running)
        {
            run.Stop();
        }
    }

    public void Dispose()
    {
        StopAll();
        _slots.Dispose();
    }
}

/// <summary>言語ごとの実行の結果。</summary>
internal sealed record ScriptExecution(ScriptRunStatus Status, ScriptFailure Failure, string? Message, IReadOnlyList<ScriptDiagnostic> Diagnostics, string? StackTrace)
{
    public static ScriptExecution Success { get; } = new(ScriptRunStatus.Succeeded, ScriptFailure.None, null, [], null);

    public static ScriptExecution Stopped(string? message = null) => new(ScriptRunStatus.Stopped, ScriptFailure.Canceled, message ?? Msg.T("停止しました。", "Stopped."), [], null);

    public static ScriptExecution Error(ScriptFailure failure, string message, IReadOnlyList<ScriptDiagnostic> diagnostics, string? stack = null) =>
        new(ScriptRunStatus.Failed, failure, message, diagnostics, stack);
}
