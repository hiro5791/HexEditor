using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.Hooks;

namespace HexEditor.Core.Tests.Scripting;

/// <summary>
/// イベントで実行するスクリプト (フック。AUTO-25) の Core の振る舞い: 条件による絞り込み、保存の前の中止、失敗の数え方、
/// セーフモード、上限時間。起動時間とアプリの保存の経路は UI のテストで確かめる。
/// </summary>
public sealed class ScriptHookTests : IDisposable
{
    private readonly ScriptTestHost _host = new();
    private readonly MemoryHookConfigStore _store = new();
    private readonly List<HookNotice> _notices = [];

    public void Dispose() => _host.Dispose();

    private ScriptHookManager Manager(Func<bool>? safeMode = null)
    {
        var manager = new ScriptHookManager(_host.Host, () => _host.Env, _store, safeMode);
        manager.Notice += (_, n) => _notices.Add(n);
        return manager;
    }

    private string Hook(string name, string body)
    {
        string path = _host.WriteScript(name, body);
        _host.TrustScript(path);
        return path;
    }

    private void Register(HookKind kind, string script, HookCondition? condition = null) =>
        Update(c => c.Add(kind, new HookEntry(script, true, condition)));

    private void Update(Action<HookConfig> change)
    {
        HookConfig config = _store.Load();
        change(config);
        _store.Save(config);
    }

    private static HookTarget Target(string name, string kind = "file") => new("C:\\data\\" + name, name, kind, new byte[] { 0x4D, 0x5A, 0x90 });

    [Fact]
    [Trait("TC", "TC-AUTO-25-02")]
    public async Task An_extension_condition_runs_the_hook_only_for_that_extension()
    {
        string script = Hook("openhook.js", "console.log('hooked: ' + script.args.event.document.name);");
        Register(HookKind.Open, script, new HookCondition { Extension = ".bin" });
        ScriptHookManager manager = Manager();

        CoreDocumentHost dat = _host.Open([1], "a.dat");
        CoreDocumentHost bin = _host.Open([2], "a.bin");
        await manager.RunOpenAsync(dat, Target("a.dat"));
        await manager.RunOpenAsync(bin, Target("a.bin"));

        Assert.Equal(["hooked: a.bin"], _host.OutputText.Where(t => t.StartsWith("hooked:", StringComparison.Ordinal)));
    }

    [Fact]
    public void Conditions_match_extension_path_magic_and_source()
    {
        HookTarget pe = Target("tool.EXE");
        Assert.True(new HookCondition { Extension = "exe,dll" }.Matches(pe));
        Assert.True(new HookCondition { Extension = "*.exe" }.Matches(pe));
        Assert.False(new HookCondition { Extension = ".bin" }.Matches(pe));
        Assert.True(new HookCondition { PathPattern = "c:\\data\\*.exe" }.Matches(pe));
        Assert.False(new HookCondition { PathPattern = "d:\\*" }.Matches(pe));
        Assert.True(new HookCondition { Magic = "4D 5A" }.Matches(pe));
        Assert.False(new HookCondition { Magic = "7F 45" }.Matches(pe));
        Assert.True(new HookCondition { Source = "file" }.Matches(pe));
        Assert.False(new HookCondition { Source = "disk" }.Matches(pe));
        // 指定した条件はすべて満たす必要がある。
        Assert.False(new HookCondition { Extension = ".exe", Magic = "00" }.Matches(pe));
        Assert.True(new HookCondition().IsEmpty);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-25-03")]
    public async Task A_before_save_hook_can_cancel_the_save_with_a_reason()
    {
        string script = Hook("savehook.js", "script.args.event.cancel('hook-abort-7');");
        Register(HookKind.BeforeSave, script);
        ScriptHookManager manager = Manager();
        CoreDocumentHost doc = _host.Open([1, 2, 3], "a.bin");

        HookSaveDecision decision = await manager.RunBeforeSaveAsync(doc);

        Assert.False(decision.Proceed);
        Assert.Equal("hook-abort-7", decision.Reason);
        Assert.Contains(_notices, n => n.Type == HookNoticeKind.SaveCanceled && n.Message == "hook-abort-7");
    }

    [Fact]
    public async Task A_before_save_hook_that_does_not_cancel_lets_the_save_proceed_and_after_save_runs()
    {
        string before = Hook("before.js", "console.log('before');");
        string after = Hook("after.js", "console.log('after:' + script.args.event.name);");
        Register(HookKind.BeforeSave, before);
        Register(HookKind.AfterSave, after);
        ScriptHookManager manager = Manager();
        CoreDocumentHost doc = _host.Open([1], "a.bin");

        Assert.True((await manager.RunBeforeSaveAsync(doc)).Proceed);
        await manager.RunAfterSaveAsync(doc);

        Assert.Contains("before", _host.OutputText);
        Assert.Contains("after:afterSave", _host.OutputText);
    }

    [Fact]
    public async Task A_failing_before_save_hook_asks_whether_to_save()
    {
        string script = Hook("broken.js", "throw new Error('hook-fail');");
        Register(HookKind.BeforeSave, script);
        ScriptHookManager manager = Manager();
        var asked = new List<HookNotice>();
        manager.ConfirmSaveAfterFailure = notice =>
        {
            asked.Add(notice);
            return Task.FromResult(false);
        };
        CoreDocumentHost doc = _host.Open([1], "a.bin");

        HookSaveDecision decision = await manager.RunBeforeSaveAsync(doc);

        Assert.False(decision.Proceed);
        Assert.Single(asked);
        Assert.Contains("hook-fail", asked[0].Message);

        manager.ConfirmSaveAfterFailure = _ => Task.FromResult(true);
        Assert.True((await manager.RunBeforeSaveAsync(doc)).Proceed);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-25-04")]
    public async Task A_hook_that_fails_three_times_in_a_row_is_disabled()
    {
        string script = Hook("failhook.js", "console.log('fail-count');\nthrow new Error('hook-fail');");
        Register(HookKind.Open, script);
        ScriptHookManager manager = Manager();

        for (int i = 1; i <= 4; i++)
        {
            CoreDocumentHost doc = _host.Open([1], i + ".bin");
            await manager.RunOpenAsync(doc, Target(i + ".bin"));
        }

        Assert.Equal(3, _host.OutputText.Count(t => t == "fail-count"));
        Assert.Equal(3, _notices.Count(n => n.Type == HookNoticeKind.Failed));
        Assert.Single(_notices, n => n.Type == HookNoticeKind.AutoDisabled);
        Assert.False(_store.Load().Entries(HookKind.Open).Single().Enabled);
    }

    [Fact]
    public async Task A_success_resets_the_failure_count()
    {
        string script = Hook("flaky.js", "if (script.args.event.document.name.startsWith('bad')) throw new Error('x');");
        Register(HookKind.Open, script);
        ScriptHookManager manager = Manager();

        foreach (string name in new[] { "bad1", "bad2", "good", "bad3", "bad4" })
        {
            CoreDocumentHost doc = _host.Open([1], name);
            await manager.RunOpenAsync(doc, Target(name));
        }

        Assert.True(_store.Load().Entries(HookKind.Open).Single().Enabled);
        Assert.DoesNotContain(_notices, n => n.Type == HookNoticeKind.AutoDisabled);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-25-05")]
    public async Task Safe_mode_runs_no_hooks()
    {
        string start = Hook("start.js", "console.log('started');");
        string open = Hook("open.js", "console.log('hooked: ' + script.args.event.document.name);");
        string save = Hook("save.js", "script.args.event.cancel('no');");
        Register(HookKind.Startup, start);
        Register(HookKind.Open, open);
        Register(HookKind.BeforeSave, save);

        ScriptHookManager safe = Manager(() => true);
        CoreDocumentHost doc = _host.Open([1], "a.bin");
        await safe.RunStartupAsync();
        await safe.RunOpenAsync(doc, Target("a.bin"));
        Assert.True((await safe.RunBeforeSaveAsync(doc)).Proceed);
        Assert.DoesNotContain("started", _host.OutputText);
        Assert.DoesNotContain(_host.OutputText, t => t.StartsWith("hooked:", StringComparison.Ordinal));

        // 登録は変わらず、通常の起動なら動く。
        ScriptHookManager normal = Manager(() => false);
        await normal.RunStartupAsync();
        await normal.RunOpenAsync(doc, Target("a.bin"));
        Assert.Contains("started", _host.OutputText);
        Assert.Contains("hooked: a.bin", _host.OutputText);
    }

    [Fact]
    public async Task Startup_hooks_run_in_the_registered_order_and_disabled_ones_are_skipped()
    {
        string a = Hook("a.js", "console.log('first');");
        string b = Hook("b.js", "console.log('second');");
        string c = Hook("c.js", "console.log('third');");
        Update(cfg =>
        {
            cfg.Add(HookKind.Startup, new HookEntry(a));
            cfg.Add(HookKind.Startup, new HookEntry(b, false));
            cfg.Add(HookKind.Startup, new HookEntry(c));
        });

        await Manager().RunStartupAsync();

        Assert.Equal(["first", "third"], _host.OutputText.Where(t => t is "first" or "second" or "third"));
    }

    [Fact]
    public async Task A_hook_that_exceeds_its_time_limit_is_stopped_and_reported()
    {
        string script = Hook("slow.js", "for (;;) {}");
        Update(c => c.Add(HookKind.Open, new HookEntry(script) { TimeoutSeconds = 0.3 }));
        ScriptHookManager manager = Manager();
        CoreDocumentHost doc = _host.Open([1], "a.bin");

        await manager.RunOpenAsync(doc, Target("a.bin")).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Single(_notices, n => n.Type is HookNoticeKind.TimedOut or HookNoticeKind.Failed);
        Assert.Empty(_host.Host.Running);
    }

    [Fact]
    public async Task A_hook_that_subscribes_to_events_stays_resident_beyond_the_time_limit()
    {
        string script = Hook("resident.js", "app.on('documentOpened', () => {});");
        Update(c => c.Add(HookKind.Startup, new HookEntry(script) { TimeoutSeconds = 10 }));
        ScriptHookManager manager = Manager();

        await manager.RunStartupAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(_notices);
        Assert.Single(_host.Host.Running);
        _host.Host.StopAll();
    }

    [Fact]
    public void The_hook_configuration_round_trips_through_json()
    {
        var config = new HookConfig();
        config.Add(HookKind.Open, new HookEntry("C:\\s\\a.js", true, new HookCondition { Extension = ".bin", Magic = "4D 5A" }) { TimeoutSeconds = 3 });
        config.Add(HookKind.Startup, new HookEntry("C:\\s\\b.js", false));

        HookConfig back = HookConfig.FromJson(config.ToJson());

        HookEntry open = Assert.Single(back.Entries(HookKind.Open));
        Assert.Equal(".bin", open.Condition!.Extension);
        Assert.Equal("4D 5A", open.Condition.Magic);
        Assert.Equal(3, open.TimeoutSeconds);
        Assert.False(Assert.Single(back.Entries(HookKind.Startup)).Enabled);
        Assert.Empty(back.Entries(HookKind.AfterSave));
    }

    [Fact]
    public void Default_time_limits_follow_the_specification()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), HookKinds.DefaultTimeout(HookKind.Startup));
        Assert.Equal(TimeSpan.FromSeconds(10), HookKinds.DefaultTimeout(HookKind.Open));
        Assert.Equal(TimeSpan.FromSeconds(5), HookKinds.DefaultTimeout(HookKind.BeforeSave));
        Assert.Equal(TimeSpan.FromSeconds(10), HookKinds.DefaultTimeout(HookKind.AfterSave));
        Assert.Equal(TimeSpan.FromSeconds(5), HookKinds.DefaultTimeout(HookKind.BeforeClose));
        Assert.Equal(HookKind.Open, HookKinds.FromCandidate("open *.bin"));
        Assert.Null(HookKinds.FromCandidate("unknown"));
    }
}
