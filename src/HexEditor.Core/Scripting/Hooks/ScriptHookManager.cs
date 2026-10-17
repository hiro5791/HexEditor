using HexEditor.Sdk;

namespace HexEditor.Core.Scripting.Hooks;

/// <summary>フックの実行で起きたこと (InfoBar で知らせる。AUTO-25 の仕様 5)。</summary>
public enum HookNoticeKind
{
    /// <summary>スクリプトがエラーで終わった。</summary>
    Failed,

    /// <summary>上限時間を超えたので停止した。</summary>
    TimedOut,

    /// <summary>3 回続けて失敗したので、そのスクリプトを自動で無効にした。</summary>
    AutoDisabled,

    /// <summary>保存の前のフックが保存を中止した (理由は <see cref="HookNotice.Message"/>)。</summary>
    SaveCanceled,
}

public sealed record HookNotice(HookKind Kind, string Script, HookNoticeKind Type, string Message)
{
    /// <summary>スクリプトの名前 (ファイル名)。</summary>
    public string ScriptName => Path.GetFileName(Script);
}

/// <summary>保存の前のフックの結果 (AUTO-25 の仕様 1・5)。</summary>
public sealed record HookSaveDecision(bool Proceed, string? Reason);

/// <summary>
/// イベントで実行するスクリプト (フック。AUTO-25)。設定のフックに登録したスクリプトを、起動時・ファイルを開いたとき・保存の前後・
/// 閉じる前に、並び順に実行する。実行は他のスクリプトと同じ仕組み (<see cref="ScriptHost"/>) で、UI を止めない。
/// </summary>
public sealed class ScriptHookManager
{
    /// <summary>続けて失敗したら自動で無効にする回数 (仕様 5)。</summary>
    public const int MaxConsecutiveFailures = 3;

    private readonly ScriptHost _host;
    private readonly Func<IScriptEnvironment> _environment;
    private readonly IHookConfigStore _store;
    private readonly Func<bool> _safeMode;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<(HookKind Kind, string Script), int> _failures = [];

    public ScriptHookManager(ScriptHost host, Func<IScriptEnvironment> environment, IHookConfigStore store, Func<bool>? safeMode = null, TimeProvider? time = null)
    {
        _host = host;
        _environment = environment;
        _store = store;
        _safeMode = safeMode ?? (() => false);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>知らせたいこと (InfoBar)。どのスレッドからでも発生する。</summary>
    public event EventHandler<HookNotice>? Notice;

    /// <summary>
    /// 保存の前のフックが失敗したとき、保存を続けるか利用者に尋ねる (ボタン: 「保存する」「保存しない」。仕様 5)。
    /// 設定されていなければ保存を続ける。
    /// </summary>
    public Func<HookNotice, Task<bool>>? ConfirmSaveAfterFailure { get; set; }

    /// <summary>保存の前のフックを実行中か (保存の進捗表示に「スクリプトを実行中」と出す。仕様の「巨大ファイル・長時間処理」)。</summary>
    public event EventHandler<bool>? BeforeSaveRunning;

    /// <summary>セーフモードではすべてのフックを実行しない (仕様 6)。</summary>
    public bool SafeMode => _safeMode();

    public HookConfig Config => _store.Load();

    /// <summary>登録を変える (設定画面)。</summary>
    public void Update(Action<HookConfig> change)
    {
        HookConfig config = _store.Load();
        change(config);
        _store.Save(config);
    }

    /// <summary>連続した失敗の回数 (テスト・表示用)。</summary>
    public int FailureCount(HookKind kind, string script)
    {
        lock (_lock)
        {
            return _failures.GetValueOrDefault((kind, script));
        }
    }

    // ---- 各フック ----

    /// <summary>起動時のフック。スクリプトの完了を待つので、呼び出し側は待たずに続ける (起動を遅らせない)。</summary>
    public async Task RunStartupAsync()
    {
        if (SafeMode)
        {
            return;
        }

        foreach (HookEntry entry in Config.Entries(HookKind.Startup).Where(e => e.Enabled).ToList())
        {
            await RunEntryAsync(HookKind.Startup, entry, null).ConfigureAwait(false);
        }
    }

    /// <summary>ファイルを開いたとき。条件に合う登録だけを実行する。</summary>
    public async Task RunOpenAsync(IScriptDocumentHost document, HookTarget target)
    {
        if (SafeMode)
        {
            return;
        }

        foreach (HookEntry entry in Config.Entries(HookKind.Open).Where(e => e.Enabled && (e.Condition is null || e.Condition.Matches(target))).ToList())
        {
            await RunEntryAsync(HookKind.Open, entry, document).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 保存の前。スクリプトが <c>event.cancel(理由)</c> を呼ぶと保存を中止する。失敗したスクリプトがあれば
    /// <see cref="ConfirmSaveAfterFailure"/> で保存を続けるか尋ねる。
    /// </summary>
    public async Task<HookSaveDecision> RunBeforeSaveAsync(IScriptDocumentHost document)
    {
        if (SafeMode)
        {
            return new HookSaveDecision(true, null);
        }

        List<HookEntry> entries = [.. Config.Entries(HookKind.BeforeSave).Where(e => e.Enabled)];
        if (entries.Count == 0)
        {
            return new HookSaveDecision(true, null);
        }

        BeforeSaveRunning?.Invoke(this, true);
        try
        {
            foreach (HookEntry entry in entries)
            {
                HookRunOutcome outcome = await RunEntryAsync(HookKind.BeforeSave, entry, document).ConfigureAwait(false);
                if (outcome.CancelReason is { } reason)
                {
                    Raise(new HookNotice(HookKind.BeforeSave, entry.Script, HookNoticeKind.SaveCanceled, reason));
                    return new HookSaveDecision(false, reason);
                }

                if (outcome.Failure is { } failure && ConfirmSaveAfterFailure is { } confirm && !await confirm(failure).ConfigureAwait(false))
                {
                    return new HookSaveDecision(false, failure.Message);
                }
            }

            return new HookSaveDecision(true, null);
        }
        finally
        {
            BeforeSaveRunning?.Invoke(this, false);
        }
    }

    public async Task RunAfterSaveAsync(IScriptDocumentHost document)
    {
        if (SafeMode)
        {
            return;
        }

        foreach (HookEntry entry in Config.Entries(HookKind.AfterSave).Where(e => e.Enabled).ToList())
        {
            await RunEntryAsync(HookKind.AfterSave, entry, document).ConfigureAwait(false);
        }
    }

    /// <summary>閉じる前 (中止はできない)。閉じる処理が待つのは上限時間までなので、呼び出し側は待ってよい。</summary>
    public async Task RunBeforeCloseAsync(IScriptDocumentHost document)
    {
        if (SafeMode)
        {
            return;
        }

        foreach (HookEntry entry in Config.Entries(HookKind.BeforeClose).Where(e => e.Enabled).ToList())
        {
            await RunEntryAsync(HookKind.BeforeClose, entry, document).ConfigureAwait(false);
        }
    }

    // ---- 実行 ----

    private sealed record HookRunOutcome(string? CancelReason, HookNotice? Failure);

    private async Task<HookRunOutcome> RunEntryAsync(HookKind kind, HookEntry entry, IScriptDocumentHost? document)
    {
        TimeSpan timeout = entry.TimeoutSeconds is double s and > 0 ? TimeSpan.FromSeconds(s) : HookKinds.DefaultTimeout(kind);
        var state = new HookEventState();
        ScriptRun run;
        try
        {
            run = _host.Start(new ScriptRunRequest
            {
                Path = entry.Script,
                Origin = ScriptOrigin.Hook,
                SkipParameterDialog = true,
                StopOnTimeLimit = true,
                TimeLimitOverride = timeout,
                PresetArgs = new Dictionary<string, object?>
                {
                    ["event"] = new ScriptLazyArg(ctx => new HookEvent(kind, ctx.WrapOrNull(document), state)),
                },
            }, _environment());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return Failed(kind, entry, HookNoticeKind.Failed, ex.Message);
        }

        Task completion = run.Completion;
        Task first = await Task.WhenAny(completion, Task.Delay(timeout, _time)).ConfigureAwait(false);
        if (first != completion)
        {
            // 常駐するスクリプト (イベントを購読している) は、上限時間では止めない。
            if (run.IsResident)
            {
                Succeeded(kind, entry);
                return new HookRunOutcome(state.CancelReason, null);
            }

            run.Stop();
            await Task.WhenAny(completion, Task.Delay(_host.ForceStopGrace + TimeSpan.FromSeconds(1), _time)).ConfigureAwait(false);
            return Failed(kind, entry, HookNoticeKind.TimedOut,
                Msg.T($"上限時間 {timeout.TotalSeconds:0.#} 秒を超えたので停止しました。", $"Stopped because it exceeded the time limit of {timeout.TotalSeconds:0.#} seconds."));
        }

        ScriptRunResult result = await run.Completion.ConfigureAwait(false);
        switch (result.Status)
        {
            case ScriptRunStatus.Succeeded:
                Succeeded(kind, entry);
                return new HookRunOutcome(state.CancelReason, null);
            case ScriptRunStatus.Stopped:
                return new HookRunOutcome(state.CancelReason, null);
            default:
                HookNoticeKind type = result.Failure == ScriptFailure.Limit ? HookNoticeKind.TimedOut : HookNoticeKind.Failed;
                return Failed(kind, entry, type, result.Message ?? Msg.T("失敗しました。", "Failed."));
        }
    }

    private void Succeeded(HookKind kind, HookEntry entry)
    {
        lock (_lock)
        {
            _failures.Remove((kind, entry.Script));
        }
    }

    private HookRunOutcome Failed(HookKind kind, HookEntry entry, HookNoticeKind type, string message)
    {
        int count;
        lock (_lock)
        {
            count = _failures[(kind, entry.Script)] = _failures.GetValueOrDefault((kind, entry.Script)) + 1;
        }

        var notice = new HookNotice(kind, entry.Script, type, message);
        Raise(notice);
        if (count >= MaxConsecutiveFailures)
        {
            DisableEntry(kind, entry.Script);
            lock (_lock)
            {
                _failures.Remove((kind, entry.Script));
            }

            Raise(new HookNotice(kind, entry.Script, HookNoticeKind.AutoDisabled, message));
        }

        return new HookRunOutcome(null, notice);
    }

    private void DisableEntry(HookKind kind, string script)
    {
        HookConfig config = _store.Load();
        IReadOnlyList<HookEntry> entries = config.Entries(kind);
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Script == script && entries[i].Enabled)
            {
                config.Replace(kind, i, entries[i] with { Enabled = false });
            }
        }

        _store.Save(config);
    }

    private void Raise(HookNotice notice) => Notice?.Invoke(this, notice);

    // ---- フックのイベント (script.args.event) ----

    private sealed class HookEventState
    {
        private string? _cancelReason;

        public string? CancelReason => Volatile.Read(ref _cancelReason);

        public void Cancel(string reason) => Interlocked.CompareExchange(ref _cancelReason, reason, null);
    }

    private sealed class HookEvent(HookKind kind, IDocument? document, HookEventState state) : ScriptEventArgs
    {
        public override string Name => HookKinds.Name(kind);

        public override IDocument? Document => document;

        public override void Cancel(string reason)
        {
            if (kind != HookKind.BeforeSave)
            {
                throw new ArgumentError(Msg.T($"{Name} のフックは中止できません (中止できるのは beforeSave だけです)。", $"A {Name} hook cannot be canceled (only beforeSave can)."));
            }

            state.Cancel(reason);
        }
    }
}
