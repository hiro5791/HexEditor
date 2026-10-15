using HexEditor.Core.Scripting;

namespace HexEditor.Core.Tests.Scripting;

/// <summary>信頼の確認と許可 (AUTO-34)、メタデータとパラメータ (AUTO-26)、実行の基本 (AUTO-01)。</summary>
public sealed class ScriptTrustTests
{
    private const string Perms = """
        // ==HexEditorScript==
        // @name         perms
        // @author       Example
        // @permission   file-read
        // @permission   network
        // ==/HexEditorScript==
        console.log("ran");
        """;

    [Fact]
    [Trait("TC", "TC-AUTO-34-01")]
    public async Task First_run_shows_the_confirmation_with_permissions_and_deny_does_not_run()
    {
        using var t = new ScriptTestHost();
        string path = t.WriteScript("perms.js", Perms);
        t.Ui.TrustAnswer = TrustAnswer.Deny;
        ScriptRunResult r = await t.RunFileAsync(path);
        Assert.Equal(ScriptRunStatus.NotRun, r.Status);
        TrustPrompt p = Assert.Single(t.Ui.TrustPrompts);
        Assert.Equal("perms", p.Name);
        Assert.Equal(path, p.Path);
        Assert.Equal("Example", p.Author);
        Assert.Equal(["file-read", "network"], p.Permissions);
        Assert.False(p.FullTrust);
        Assert.DoesNotContain("ran", t.OutputText);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-34-02")]
    public async Task Always_allow_skips_the_confirmation_until_the_file_changes()
    {
        using var t = new ScriptTestHost();
        string path = t.WriteScript("hello.js", "console.log('hello from js');");
        t.Ui.TrustAnswer = TrustAnswer.AlwaysAllow;
        ScriptTestHost.AssertSucceeded(await t.RunFileAsync(path));
        ScriptTestHost.AssertSucceeded(await t.RunFileAsync(path));
        Assert.Single(t.Ui.TrustPrompts);
        File.AppendAllText(path, "\nconsole.log(\"changed\");");
        ScriptTestHost.AssertSucceeded(await t.RunFileAsync(path));
        Assert.Equal(2, t.Ui.TrustPrompts.Count);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-34-03")]
    public async Task Run_once_asks_again_next_time_and_is_not_recorded()
    {
        using var t = new ScriptTestHost();
        string path = t.WriteScript("hello.js", "console.log('hello from js');");
        t.Ui.TrustAnswer = TrustAnswer.RunOnce;
        ScriptTestHost.AssertSucceeded(await t.RunFileAsync(path));
        Assert.Contains("hello from js", t.OutputText);
        ScriptTestHost.AssertSucceeded(await t.RunFileAsync(path));
        Assert.Equal(2, t.Ui.TrustPrompts.Count);
        Assert.Empty(t.Trust.Records);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-34-04")]
    public async Task Csharp_confirmation_says_full_trust()
    {
        using var t = new ScriptTestHost();
        string path = t.WriteScript("hello.csx", "Console.WriteLine(\"hello from csx\");");
        t.Ui.TrustAnswer = TrustAnswer.Deny;
        await t.RunFileAsync(path);
        TrustPrompt p = Assert.Single(t.Ui.TrustPrompts);
        Assert.True(p.FullTrust);
        Assert.Equal(ScriptLanguage.CSharp, p.Language);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-34-05")]
    public async Task Internet_files_get_an_extra_warning()
    {
        using var t = new ScriptTestHost();
        string local = t.WriteScript("local.js", "1;");
        string motw = t.WriteScript("motw.js", "1;");
        File.WriteAllText(motw + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        Assert.True(MarkOfTheWeb.IsFromInternet(motw));
        Assert.False(MarkOfTheWeb.IsFromInternet(local));
        t.Ui.TrustAnswer = TrustAnswer.Deny;
        await t.RunFileAsync(local);
        await t.RunFileAsync(motw);
        Assert.False(t.Ui.TrustPrompts[0].FromInternet);
        Assert.True(t.Ui.TrustPrompts[1].FromInternet);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-34-06")]
    public async Task Headless_run_without_trust_ends_with_exit_code_5()
    {
        using var t = new ScriptTestHost();
        string path = t.WriteScript("perms.js", Perms.Replace("console.log(\"ran\");", "fs.readText('C:/Windows/win.ini'); console.log('ran');", StringComparison.Ordinal));
        ScriptRunResult untrusted = await t.Host.RunAndWaitAsync(new ScriptRunRequest { Path = path, Headless = true }, t.Env);
        Assert.Equal(5, untrusted.ProcessExitCode);
        Assert.Contains("--trust", untrusted.Message);
        ScriptRunResult noAllow = await t.Host.RunAndWaitAsync(new ScriptRunRequest { Path = path, Headless = true, CommandLineTrust = true }, t.Env);
        Assert.Equal(5, noAllow.ProcessExitCode);
        ScriptRunResult allowed = await t.Host.RunAndWaitAsync(new ScriptRunRequest
        {
            Path = path,
            Headless = true,
            CommandLineTrust = true,
            CommandLineAllow = ["file-read", "network"],
        }, t.Env);
        Assert.Equal(0, allowed.ProcessExitCode);
        Assert.Empty(t.Ui.TrustPrompts);
    }

    [Fact]
    public async Task Headless_run_with_an_always_allow_record_runs_with_its_permissions()
    {
        using var t = new ScriptTestHost();
        string path = t.WriteScript("perms.js", Perms);
        t.TrustScript(path, "file-read");
        ScriptRunResult r = await t.Host.RunAndWaitAsync(new ScriptRunRequest { Path = path, Headless = true }, t.Env);
        Assert.Equal(0, r.ProcessExitCode);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-34-07")]
    public async Task Elevated_app_asks_every_time_even_with_always_allow()
    {
        using var t = new ScriptTestHost();
        t.Env.IsElevated = true;
        string path = t.WriteScript("hello.js", "console.log('hello from js');");
        t.TrustScript(path);
        t.Ui.TrustAnswer = TrustAnswer.RunOnce;
        ScriptTestHost.AssertSucceeded(await t.RunFileAsync(path));
        Assert.Contains("hello from js", t.OutputText);
        ScriptTestHost.AssertSucceeded(await t.RunFileAsync(path));
        Assert.Equal(2, t.Ui.TrustPrompts.Count);
        Assert.All(t.Ui.TrustPrompts, p => Assert.True(p.Elevated && p.ElevationOnly));

        // 管理者として実行中のヘッドレス実行は --trust がないと実行しない (仕様 8)。
        ScriptRunResult headless = await t.Host.RunAndWaitAsync(new ScriptRunRequest { Path = path, Headless = true }, t.Env);
        Assert.Equal(5, headless.ProcessExitCode);

        // アプリ内で保存したスクリプトは管理者として実行中でも確認しない。
        string saved = t.WriteScript("saved.js", "console.log('saved');");
        t.Trust.TrustSavedScript(saved, File.ReadAllText(saved), TrustSource.SavedInApp);
        ScriptTestHost.AssertSucceeded(await t.RunFileAsync(saved));
        Assert.Equal(2, t.Ui.TrustPrompts.Count);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-34-09")]
    public async Task Policy_disabled_refuses_to_run_with_the_reason()
    {
        using var t = new ScriptTestHost(policy: ScriptSettingKeys.PolicyDisabled);
        string path = t.WriteScript("hello.js", "console.log('hello');");
        t.TrustScript(path);
        ScriptRunResult r = await t.RunFileAsync(path);
        Assert.Equal(ScriptRunStatus.NotRun, r.Status);
        Assert.Contains("scripts.policy", r.Message);
        Assert.DoesNotContain("hello", t.OutputText);
        Assert.Empty(t.Ui.TrustPrompts);
    }

    [Fact]
    public async Task Exempt_origins_skip_the_confirmation()
    {
        using var t = new ScriptTestHost();
        string macro = t.WriteScript("macros/m.js", "console.log('macro');");
        t.Trust.TrustSavedScript(macro, File.ReadAllText(macro), TrustSource.RecordedMacro);
        ScriptTestHost.AssertSucceeded(await t.RunFileAsync(macro));
        ScriptTestHost.AssertSucceeded(await t.RunCodeAsync("console.log('console');"));
        ScriptTestHost.AssertSucceeded(await t.Host.RunAndWaitAsync(new ScriptRunRequest { Code = "1;", Origin = ScriptOrigin.UnsavedMacro }, t.Env));

        // エディタの未保存の内容は確認しない。開いたファイルを書き換えずに実行した場合はファイルと同じく確認する。
        string file = t.WriteScript("e.js", "console.log('file');");
        ScriptTestHost.AssertSucceeded(await t.RunEditorAsync("console.log('edited');", file));
        Assert.Empty(t.Ui.TrustPrompts);
        t.Ui.TrustAnswer = TrustAnswer.Deny;
        await t.RunEditorAsync(File.ReadAllText(file), file);
        Assert.Single(t.Ui.TrustPrompts);

        // 変更されたマクロは確認する (AUTO-28 の仕様 7)。
        File.AppendAllText(macro, "\n// edited outside");
        await t.RunFileAsync(macro);
        Assert.Equal(2, t.Ui.TrustPrompts.Count);
    }

    [Fact]
    public async Task Undeclared_permission_is_asked_at_use_and_deny_raises_permission_error()
    {
        using var t = new ScriptTestHost();
        string file = Path.Combine(t.Root, "outside.txt");
        File.WriteAllText(file, "outside");
        string literal = System.Text.Json.JsonSerializer.Serialize(file);
        t.Ui.PermissionAnswer = false;
        ScriptRunResult r = await t.RunCodeAsync($"try {{ fs.readText({literal}); }} catch (e) {{ console.log(e.name + ' ' + e.permission); }} try {{ fs.readText({literal}); }} catch (e) {{ console.log('again'); }}");
        ScriptTestHost.AssertSucceeded(r);
        Assert.Equal(["file-read"], t.Ui.PermissionPrompts);
        Assert.Contains("PermissionError file-read", t.OutputText);
        t.Ui.PermissionAnswer = true;
        ScriptTestHost.AssertSucceeded(await t.RunCodeAsync($"console.log(fs.readText({literal}));"));
        Assert.Contains("outside", t.OutputText);
    }

    [Fact]
    public void Trust_store_persists_and_revokes()
    {
        using var t = new ScriptTestHost();
        string path = t.WriteScript("a.js", "1;");
        t.TrustScript(path, "network");
        var reloaded = new TrustStore(t.Folders.TrustFile);
        TrustRecord record = Assert.Single(reloaded.Records);
        Assert.Equal(["network"], record.Permissions);
        Assert.True(reloaded.Revoke(TrustStore.ScriptKind, record.Key));
        Assert.Empty(new TrustStore(t.Folders.TrustFile).Records);
    }

    // ---- AUTO-26 ----

    private const string Params = """
        // ==HexEditorScript==
        // @name         XOR デコード
        // @name:en      XOR decode
        // @description  選択範囲を XOR する
        // @version      1.0.0
        // @api          1.0
        // @param        key hex "5A" キー
        // @param        whole checkbox false 全体
        // @param        mode choice "fast|safe" モード
        // @timeout      300
        // @memory       1024
        // @on           open *.bin
        // @menu         変換/XOR デコード
        // @context      selection
        // ==/HexEditorScript==
        console.log(JSON.stringify({ key: Array.from(script.args.key), whole: script.args.whole, mode: script.args.mode }));
        """;

    [Fact]
    public void Metadata_is_parsed_with_localized_names_and_warnings()
    {
        ScriptMetadata m = ScriptMetadata.Parse(Params);
        Assert.True(m.HasHeader);
        Assert.Equal("XOR decode", m.DisplayName("en", "x.js"));
        Assert.Equal("XOR decode", m.DisplayName("en-US", "x.js"));
        Assert.Equal("XOR デコード", m.DisplayName("ja", "x.js"));
        Assert.Equal("1.0", m.Api);
        Assert.Equal(300, m.TimeoutSeconds);
        Assert.Equal(1024, m.MemoryMegabytes);
        Assert.Equal("selection", m.Context);
        Assert.Equal("変換/XOR デコード", m.Menu);
        Assert.Equal(["open *.bin"], m.On);
        Assert.Equal(["key", "whole", "mode"], m.Parameters.Select(p => p.Name));
        Assert.Equal("5A", m.Parameters[0].Default);
        Assert.Equal(false, m.Parameters[1].Default);
        Assert.Equal(["fast", "safe"], m.Parameters[2].Choices!);
        Assert.Empty(m.Warnings);

        ScriptMetadata bad = ScriptMetadata.Parse("// ==HexEditorScript==\n// @bogus 1\n// @param x weird 1 y\n// @permission teleport\n// @context sometimes\n");
        Assert.Equal(5, bad.Warnings.Count);
        ScriptMetadata none = ScriptMetadata.Parse("const a = 1;\n// ==HexEditorScript==\n");
        Assert.False(none.HasHeader);
        Assert.Equal("file.js", none.DisplayName("ja", "file.js"));
    }

    [Fact]
    public async Task Metadata_warnings_go_to_the_output_and_the_script_still_runs()
    {
        using var t = new ScriptTestHost();
        ScriptRunResult r = await t.RunCodeAsync("// ==HexEditorScript==\n// @bogus 1\n// ==/HexEditorScript==\nconsole.log('ran');");
        ScriptTestHost.AssertSucceeded(r);
        Assert.Contains(t.OutputLines, l => l.Kind == ScriptOutputKind.Warning && l.Text.Contains("@bogus"));
        Assert.Contains("ran", t.OutputText);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-26-01")]
    public async Task Parameters_show_a_form_and_remember_the_last_values()
    {
        using var t = new ScriptTestHost();
        string path = t.WriteScript("params.js", Params);
        t.TrustScript(path);
        t.Ui.FormAnswer = fields => new Dictionary<string, string> { ["key"] = "A5", ["whole"] = "true", ["mode"] = "safe" };
        ScriptTestHost.AssertSucceeded(await t.RunFileAsync(path));
        IReadOnlyList<ScriptFormField> first = t.Ui.Forms[0];
        Assert.Equal(("キー", "hex", "5A"), (first[0].Label, first[0].Type, (string?)first[0].Default));
        Assert.Equal(("全体", "checkbox", "false"), (first[1].Label, first[1].Type, (string?)first[1].Default));
        Assert.Contains("{\"key\":[165],\"whole\":true,\"mode\":\"safe\"}", t.OutputText);

        t.Ui.FormAnswer = _ => null;
        ScriptRunResult canceled = await t.RunFileAsync(path);
        Assert.Equal(ScriptRunStatus.NotRun, canceled.Status);
        IReadOnlyList<ScriptFormField> second = t.Ui.Forms[1];
        Assert.Equal("A5", second[0].Default);
        Assert.Equal("true", second[1].Default);
        Assert.Equal("safe", second[2].Default);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-26-02")]
    public async Task Localized_name_is_used_for_the_run_and_output_channel()
    {
        foreach ((string lang, string expected) in new[] { ("en", "XOR decode"), ("ja", "XOR デコード") })
        {
            using var t = new ScriptTestHost(language: lang);
            string path = t.WriteScript("params.js", Params);
            t.TrustScript(path);
            t.Ui.FormAnswer = _ => new Dictionary<string, string> { ["key"] = "00", ["whole"] = "false", ["mode"] = "fast" };
            ScriptRun run = t.Start(new ScriptRunRequest { Path = path });
            await run.Completion;
            Assert.Equal(expected, run.Name);
            Assert.Contains(t.OutputLines, l => l.Channel == ScriptRun.ChannelPrefix + expected);
        }
    }

    [Fact]
    [Trait("TC", "TC-AUTO-26-03")]
    public void Context_selection_is_available_from_the_metadata()
    {
        // 右クリックメニューに出す条件 (メニューへの登録はスクリプト一覧 (AUTO-19) と同じ時期に App が行う)。
        Assert.Equal("selection", ScriptMetadata.Parse(Params).Context);
        Assert.Null(ScriptMetadata.Parse("1;").Context);
    }

    [Fact]
    public async Task Headless_params_come_from_command_line_and_defaults()
    {
        using var t = new ScriptTestHost();
        string path = t.WriteScript("params.js", Params);
        ScriptRunResult r = await t.Host.RunAndWaitAsync(new ScriptRunRequest
        {
            Path = path,
            Headless = true,
            CommandLineTrust = true,
            CommandLineArgs = new Dictionary<string, string> { ["key"] = "A5 5A", ["extra"] = "x" },
        }, t.Env);
        ScriptTestHost.AssertSucceeded(r);
        Assert.Contains("{\"key\":[165,90],\"whole\":false,\"mode\":\"fast\"}", t.OutputText);
        Assert.Empty(t.Ui.Forms);
    }

    // ---- AUTO-01 ----

    [Fact]
    public async Task Unknown_extensions_are_errors()
    {
        using var t = new ScriptTestHost();
        string path = t.WriteScript("x.py", "print(1)");
        ScriptRunResult r = await t.RunFileAsync(path);
        Assert.Equal(ScriptRunStatus.Failed, r.Status);
        Assert.Equal(2, r.ProcessExitCode);
        Assert.Equal(ScriptLanguage.JavaScript, ScriptHost.LanguageOf("a.mjs"));
        Assert.Equal(ScriptLanguage.CSharp, ScriptHost.LanguageOf("a.csx"));
        Assert.Null(ScriptHost.LanguageOf("a.txt"));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-01-01")]
    public async Task Run_writes_the_output_and_one_result_line()
    {
        using var t = new ScriptTestHost();
        t.Open(new byte[16]);
        string path = t.WriteScript("hello.js", "console.log('hello from js');");
        t.TrustScript(path);
        ScriptRun run = t.Start(new ScriptRunRequest { Path = path });
        ScriptRunResult r = await run.Completion;
        ScriptTestHost.AssertSucceeded(r);
        Assert.Equal(path, t.Host.LastRequest?.Path);
        List<ScriptOutputLine> lines = [.. t.OutputLines.Where(l => l.Channel == run.Channel)];
        Assert.Equal("hello from js", lines[0].Text);
        Assert.Single(lines, l => l.Text.StartsWith("成功", StringComparison.Ordinal));
        Assert.Equal(0, t.Env.RunningScripts);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-01-02")]
    public async Task Written_document_rejects_user_edits_while_other_documents_stay_editable()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost a = t.Open(new byte[256], "a.bin");
        CoreDocumentHost b = t.Open(new byte[256], "b.bin", activate: false);
        t.OnUi(() => t.Env.ActiveDocument = a);
        ScriptRun run = t.Start(new ScriptRunRequest
        {
            Code = "doc.writeBytes(0, 'AA'); console.log('written'); while (!script.signal.aborted) {}",
            Origin = ScriptOrigin.Console,
            DisplayName = "slowwrite.js",
        });
        await JsRuntimeTests.WaitUntilAsync(() => t.OutputText.Contains("written"));
        Assert.Same(run, a.WritingRun);
        Assert.True(t.OnUi(() => a.Document.IsEditLocked));
        HexEditor.Core.View.EditResult typed = t.OnUi(() =>
        {
            a.Editor.GoTo(0x10);
            return a.Editor.TypeHexDigit('F');
        });
        Assert.NotEqual(HexEditor.Core.View.EditResult.Done, typed);
        Assert.Equal(0, t.OnUi(() => CSharpScriptTests.ReadAll(a))[0x10]);
        HexEditor.Core.View.EditResult other = t.OnUi(() =>
        {
            b.Editor.GoTo(0x10);
            return b.Editor.TypeHexDigit('A');
        });
        Assert.Equal(HexEditor.Core.View.EditResult.Done, other);
        Assert.Contains(t.Env.Operations.Active, o => o.Name.Contains("slowwrite.js"));
        run.Stop();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        await JsRuntimeTests.WaitUntilAsync(() => !t.OnUi(() => a.Document.IsEditLocked) && a.WritingRun is null);
    }

    [Fact]
    public async Task Second_script_writing_the_same_document_waits_for_the_first()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost a = t.Open(new byte[16]);
        ScriptRun first = t.Start(new ScriptRunRequest { Code = "doc.writeBytes(0, '01'); console.log('first'); while (!script.signal.aborted) {}", Origin = ScriptOrigin.Console });
        await JsRuntimeTests.WaitUntilAsync(() => t.OutputText.Contains("first"));
        ScriptRun second = t.Start(new ScriptRunRequest { Code = "console.log('second started'); doc.writeBytes(0, '02'); console.log('second wrote');", Origin = ScriptOrigin.Console });
        await JsRuntimeTests.WaitUntilAsync(() => t.OutputText.Contains("second started"));
        Assert.DoesNotContain("second wrote", t.OutputText);
        first.Stop();
        ScriptTestHost.AssertSucceeded(await second.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(2, t.OnUi(() => CSharpScriptTests.ReadAll(a))[0]);
    }

    [Fact]
    public async Task Concurrency_limit_queues_extra_scripts()
    {
        using var t = new ScriptTestHost(new ScriptLimits { MaxConcurrent = 1 });
        ScriptRun first = t.Start(new ScriptRunRequest { Code = "console.log('a'); while (!script.signal.aborted) {}", Origin = ScriptOrigin.Console });
        await JsRuntimeTests.WaitUntilAsync(() => t.OutputText.Contains("a"));
        ScriptRun second = t.Start(new ScriptRunRequest { Code = "console.log('b');", Origin = ScriptOrigin.Console });
        await JsRuntimeTests.WaitUntilAsync(() => second.Status == ScriptRunStatus.Waiting && t.Host.Running.Count == 2);
        Assert.DoesNotContain("b", t.OutputText);
        first.Stop();
        ScriptTestHost.AssertSucceeded(await second.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task App_stop_and_exit_end_the_script_normally()
    {
        using var t = new ScriptTestHost();
        ScriptRunResult stopped = await t.RunCodeAsync("console.log('before'); app.stop(); console.log('after');");
        ScriptTestHost.AssertSucceeded(stopped);
        Assert.DoesNotContain("after", t.OutputText);
        ScriptRunResult exit = await t.RunCodeAsync("app.exit(70);");
        Assert.Equal(70, exit.ProcessExitCode);
    }
}
