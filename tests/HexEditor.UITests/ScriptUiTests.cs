using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using HexEditor.TestData;

namespace HexEditor.UITests;

/// <summary>
/// スクリプトの作成と管理の画面 (AUTO-19〜28、46) を、アプリにつないだ状態で確かめる。
/// 振る舞いの細部 (デバッガ・REPL・フック・マクロ) は Core のテスト (HexEditor.Core.Tests/Scripting) が確かめる。ここでは画面のつながりを見る。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ScriptUiTests
{
    private const string Hello = """
        // ==HexEditorScript==
        // @name hello
        // ==/HexEditorScript==
        console.log('hello from js');
        """;

    private static string ScriptsFolder(string profile)
    {
        string dir = Path.Combine(profile, "scripts");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Task<JsonObject> UiAsync(AppSession app, string action, JsonObject? args = null)
    {
        args ??= [];
        args["action"] = action;
        return app.SendAsync("scriptUi", args);
    }

    private static async Task<List<JsonObject>> RowsAsync(AppSession app) =>
        [.. (await UiAsync(app, "outputRows"))["rows"]!.AsArray().Select(r => r!.AsObject())];

    private static Task WaitForRowAsync(AppSession app, string text) =>
        app.WaitUntilAsync(async () => (await RowsAsync(app)).Any(r => r["text"]!.GetValue<string>() == text),
            UiTest.Scaled(TimeSpan.FromSeconds(20)), $"the output line '{text}'");

    private static async Task RunTrustedAsync(AppSession app, string path)
    {
        await app.SendAsync("script", new JsonObject { ["action"] = "run", ["path"] = path });
        await app.WaitForDialogAsync("ScriptTrustDialog");
        await app.InvokeDialogButtonAsync("Always allow");
    }

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-19-03")]
    public Task A_script_with_a_shortcut_runs_on_that_key_before_and_after_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string script = Path.Combine(ScriptsFolder(profile), "hello.js");
        File.WriteAllText(script, Hello);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });

        // 信頼済みにする (最初の実行で「常に許可する」)。
        await RunTrustedAsync(app, script);
        await WaitForRowAsync(app, "hello from js");

        // 1. 一覧の「ショートカットの割り当て」と同じ: コマンドにキーを割り当てる。
        await app.WaitUntilAsync(async () => await CommandIdAsync(app) is not null, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the script command");
        string id = (await CommandIdAsync(app))!;
        JsonObject assigned = await app.SendAsync("assignKey", new JsonObject { ["command"] = id, ["key"] = "Ctrl+Alt+F8" });
        Assert.True(assigned["added"]!.GetValue<bool>());

        // 2〜4. 出力を消して、キーを押す。
        await UiAsync(app, "outputClear");
        await app.KeyAsync("F8", ctrl: true, alt: true);
        await WaitForRowAsync(app, "hello from js");

        // 5. 再起動後も同じ。
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));
        app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await app.WaitUntilAsync(async () => await CommandIdAsync(app) is not null, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the script command");
        await UiAsync(app, "outputClear");
        await app.KeyAsync("F8", ctrl: true, alt: true);
        await WaitForRowAsync(app, "hello from js");

        // ショートカットの設定画面 (キーの割り当て一覧) で「スクリプト: hello」に割り当てられている。
        JsonObject bindings = await app.SendAsync("keyBindings", new JsonObject { ["command"] = id });
        Assert.Contains(bindings["bindings"]!.AsArray(), b => b!["key"]!.GetValue<string>() == "Ctrl+Alt+F8");
        JsonObject item = (await app.SendAsync("commands"))["items"]!.AsArray().Select(i => i!.AsObject()).First(i => i["id"]!.GetValue<string>() == id);
        Assert.Equal("hello", item["title"]!.GetValue<string>().Split(": ").Last());
    });

    private static async Task<string?> CommandIdAsync(AppSession app) =>
        (await app.SendAsync("commands"))["items"]!.AsArray()
            .Select(i => i!["id"]!.GetValue<string>()).FirstOrDefault(id => id.StartsWith("script.hello", StringComparison.Ordinal));

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-19-04")]
    public Task The_script_list_is_operable_from_the_keyboard() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string dir = Path.Combine(ScriptsFolder(profile), "sub");
        Directory.CreateDirectory(dir);
        string script = Path.Combine(dir, "hello.js");
        File.WriteAllText(script, Hello);
        string data = ctx.WriteFile("data.bin", [.. Enumerable.Range(0, 256).Select(i => (byte)i)]);
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [data] });
        await RunTrustedAsync(app, script);
        await WaitForRowAsync(app, "hello from js");
        await UiAsync(app, "outputClear");

        // 1. F6 でスクリプト一覧に移る (一覧のパネルを開いたうえで)。フォーカスの位置は UI オートメーションで取れる。
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.panel.scripts" });
        await app.FocusAsync("editor");

        // F6 は領域を順に巡る: 一覧に着くまで押す。
        var visited = new List<string>();
        for (int i = 0; i < 6 && !(visited.LastOrDefault() ?? "").EndsWith(":ScriptList_Filter", StringComparison.Ordinal); i++)
        {
            await app.KeyAsync("F6");
            await app.IdleAsync();
            visited.Add(await app.FocusedAsync() ?? "");
        }

        Assert.True(visited.Last().EndsWith(":ScriptList_Filter", StringComparison.Ordinal), string.Join(" > ", visited));

        // 2〜3. 絞り込みに入力すると、フォルダ内の hello が見える。
        await app.UiaSetValueAsync("ScriptList_Filter", "hel");
        await app.WaitUntilAsync(async () => (await UiAsync(app, "list"))["items"]!.AsArray()
            .Any(i => i!["name"]!.GetValue<string>() == "hello" && i["script"]!.GetValue<bool>()), UiTest.Scaled(TimeSpan.FromSeconds(10)), "hello in the list");

        // 4. Enter (= 実行)。信頼済みなので確認なしで出力される。
        await UiAsync(app, "listActivate", new JsonObject { ["path"] = script });
        await WaitForRowAsync(app, "hello from js");

        // 5. 右クリックメニューの「編集」(= エディタで開く)。
        await UiAsync(app, "openEditor", new JsonObject { ["path"] = script });
        JsonObject editor = await UiAsync(app, "activeEditor");
        Assert.True(editor["open"]!.GetValue<bool>());
        Assert.EndsWith("hello.js", editor["path"]!.GetValue<string>());
    });


    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-22-03")]
    public Task Csharp_debug_dialog_and_step_message_say_it_cannot_step_in_the_app() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string script = Path.Combine(ScriptsFolder(profile), "dbg.csx");
        File.WriteAllText(script, "Console.WriteLine(1);\n");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await UiAsync(app, "openEditor", new JsonObject { ["path"] = script });

        // 1. 「デバッグ実行」: アタッチ待ちのダイアログに、アプリ内ではステップ実行できない旨が書かれている。
        await UiAsync(app, "start", new JsonObject { ["id"] = "scripting.debug" });
        var dialog = await app.WaitForDialogAsync("CSharpDebugDialog");
        await app.WaitForDialogTextAsync(dialog, "cannot be stepped through in the app", "external debugger");
        await app.InvokeDialogButtonAsync("Cancel");

        // 2. F10 で、アプリ内ではステップ実行できない旨がステータスバーに出る。
        await app.SendAsync("execute", new JsonObject { ["id"] = "codeEditor.stepOver" });
        await app.WaitUntilAsync(async () => (await UiAsync(app, "status"))["text"]!.GetValue<string>().Contains("cannot be stepped", StringComparison.OrdinalIgnoreCase),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the status message");

        // 3. 同梱のヘルプ (英語・日本語) は、ヘルプの仕組み自体がまだないため、この手順は保留 (ヘルプができたら足す)。
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-23-02")]
    public Task Console_history_is_recalled_with_up_and_survives_a_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await app.SendAsync("execute", new JsonObject { ["id"] = "scripting.showConsole" });

        // 1〜2. 2 つ入力し、↑ で後の入力が戻る。
        await UiAsync(app, "consoleSubmit", new JsonObject { ["code"] = "1 + 1" });
        await UiAsync(app, "consoleSubmit", new JsonObject { ["code"] = "\"history-test\"" });
        await UiAsync(app, "consoleUp");
        Assert.Equal("\"history-test\"", (await UiAsync(app, "consoleInput"))["text"]!.GetValue<string>());

        // 3〜4. 再起動しても残る (↑ を 2 回で最初の入力)。
        await app.CommandAsync("Command_Exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(30));
        app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await app.SendAsync("execute", new JsonObject { ["id"] = "scripting.showConsole" });
        await UiAsync(app, "consoleUp");
        await UiAsync(app, "consoleUp");
        Assert.Equal("1 + 1", (await UiAsync(app, "consoleInput"))["text"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-24-01")]
    public Task Output_rows_carry_a_kind_icon_time_and_channel() => UiTestContext.RunAsync(async ctx =>
    {
        string script = ctx.WriteFile("loglevels.js", Encoding.UTF8.GetBytes("""
            // ==HexEditorScript==
            // @name loglevels
            // ==/HexEditorScript==
            console.log("L1");
            console.warn("W1");
            console.error("E1");
            """));
        AppSession app = await ctx.StartAsync();
        await app.SendAsync("script", new JsonObject { ["action"] = "run", ["path"] = script });
        await app.WaitForDialogAsync("ScriptTrustDialog");
        await app.InvokeDialogButtonAsync("Run once");
        await WaitForRowAsync(app, "E1");

        List<JsonObject> rows = await RowsAsync(app);
        JsonObject Row(string text) => rows.First(r => r["text"]!.GetValue<string>() == text);
        Assert.Equal("Error", Row("E1")["kind"]!.GetValue<string>());
        Assert.Equal("Warning", Row("W1")["kind"]!.GetValue<string>());
        Assert.Equal("Information", Row("L1")["kind"]!.GetValue<string>());
        foreach (string text in new[] { "L1", "W1", "E1" })
        {
            Assert.Matches(@"^\d\d:\d\d:\d\d\.\d{3}$", Row(text)["time"]!.GetValue<string>());
            Assert.Equal("Script: loglevels", Row(text)["channel"]!.GetValue<string>());
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-24-02")]
    public Task Clicking_the_error_link_opens_the_editor_at_that_line() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string script = Path.Combine(ScriptsFolder(profile), "syntaxerr.js");
        File.WriteAllText(script, "// 1\n// 2\n// 3\n// 4\n// 5\n// 6\n// 7\n// 8\nlet = ;\n");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        await RunTrustedAsync(app, script);

        // 1. 構文エラーが出力パネルに出て、スクリプトは実行されない。
        await app.WaitUntilAsync(async () => (await RowsAsync(app)).Any(r => r["kind"]!.GetValue<string>() == "Error" && r["link"]!.GetValue<string>() == "syntaxerr.js:9"),
            UiTest.Scaled(TimeSpan.FromSeconds(20)), "the syntax error row");
        List<JsonObject> rows = await RowsAsync(app);
        int index = rows.FindIndex(r => r["link"]!.GetValue<string>() == "syntaxerr.js:9");

        // 2〜3. リンクを押すと、syntaxerr.js のエディタが 9 行目で開く。
        await UiAsync(app, "outputLink", new JsonObject { ["index"] = index });
        await app.WaitUntilAsync(async () => (await UiAsync(app, "activeEditor"))["line"]?.GetValue<int>() == 9, UiTest.Scaled(TimeSpan.FromSeconds(10)), "caret on line 9");
        JsonObject editor = await UiAsync(app, "activeEditor");
        Assert.EndsWith("syntaxerr.js", editor["path"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-27-05")]
    public Task A_recorded_macro_is_edited_in_the_script_editor_and_runs_without_trust_prompt() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string seq = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile, Files = [seq] });

        // 前提: カーソル 0 で DE AD を入力する操作を記録して dead の名前で保存する。
        await UiAsync(app, "start", new JsonObject { ["id"] = "macro.record" });
        await app.WaitForDialogAsync("MacroPositionDialog");
        await app.InvokeDialogButtonAsync("Start");
        await app.WaitUntilAsync(async () => (await UiAsync(app, "macroState"))["recording"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)),
            "the recording to start (buttons: " + string.Join(",", app.Window.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button)).Select(AppSession.NameOf)) + ")");
        await app.SendAsync("goto", new JsonObject { ["offset"] = 0 });
        await app.TypeAsync("DEAD");
        Assert.Equal(new byte[] { 0xDE, 0xAD }, await app.BytesAsync(0, 2));
        Assert.True((await UiAsync(app, "macroState"))["recording"]!.GetValue<bool>(), (await UiAsync(app, "macroState")).ToJsonString());
        Assert.True((await UiAsync(app, "macroState"))["count"]!.GetValue<int>() > 0, "nothing recorded");
        await UiAsync(app, "start", new JsonObject { ["id"] = "macro.record" });
        try
        {
            await app.WaitForDialogAsync("MacroSaveDialog");
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("status: " + (await UiAsync(app, "status"))["text"]);
        }

        await app.UiaSetValueAsync("MacroSave_Name", "dead");
        await app.InvokeDialogButtonAsync("Save");
        string path = "";
        await app.WaitUntilAsync(async () =>
        {
            path = (await UiAsync(app, "list"))["items"]!.AsArray().Select(i => i!["path"]!.GetValue<string>())
                .FirstOrDefault(p => Path.GetFileName(p) == "dead.js") ?? "";
            return path.Length > 0;
        }, UiTest.Scaled(TimeSpan.FromSeconds(15)), "the saved macro in the script list");

        // 1〜2. エディタで開き、0xDE, 0xAD を 0xBE, 0xEF に書き換えて保存する。
        await UiAsync(app, "openEditor", new JsonObject { ["path"] = path });
        string source = (await UiAsync(app, "editorText"))["text"]!.GetValue<string>();
        Assert.Contains("0xDE, 0xAD", source, StringComparison.OrdinalIgnoreCase);
        await UiAsync(app, "setEditorText", new JsonObject { ["text"] = source.Replace("0xDE, 0xAD", "0xBE, 0xEF", StringComparison.OrdinalIgnoreCase) });
        await app.SendAsync("execute", new JsonObject { ["id"] = "file.save" });
        await app.WaitUntilAsync(async () => !(await UiAsync(app, "activeEditor"))["dirty"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the save");

        // 3〜4. 0x10 に置いて実行する。信頼の確認は出ず、BE EF が入る。
        await app.SendAsync("goto", new JsonObject { ["offset"] = 0x10 });
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await UiAsync(app, "listActivate", new JsonObject { ["path"] = path });
        await app.WaitUntilAsync(async () => (await app.BytesAsync(0x10, 2)).SequenceEqual(new byte[] { 0xBE, 0xEF }), UiTest.Scaled(TimeSpan.FromSeconds(15)), "the replayed macro");
        Assert.Null(app.Find("ScriptTrustDialog"));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-46-02")]
    public Task Opening_a_sample_copies_it_to_the_user_folder_and_leaves_the_bundled_file_unchanged() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        string bundled = (await UiAsync(app, "samples"))["folder"]!.GetValue<string>();
        string original = Path.Combine(bundled, "xor-decode.js");
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)));

        // 1〜2. ツール > スクリプト > サンプル から開く。
        await app.CommandAsync("Sample_xor-decode.js");
        await app.WaitUntilAsync(async () => (await UiAsync(app, "activeEditor"))["open"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the editor");
        string path = (await UiAsync(app, "activeEditor"))["path"]!.GetValue<string>();
        Assert.StartsWith(Path.Combine(profile, "scripts"), path, StringComparison.OrdinalIgnoreCase);

        // 3. 1 行目にコメントを足して保存する。
        string source = (await UiAsync(app, "editorText"))["text"]!.GetValue<string>();
        await UiAsync(app, "setEditorText", new JsonObject { ["text"] = "// edited\n" + source });
        await app.SendAsync("execute", new JsonObject { ["id"] = "file.save" });
        await app.WaitUntilAsync(async () => File.ReadAllText(path).StartsWith("// edited", StringComparison.Ordinal), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the save");

        // 4. 同梱のファイルは変わらない。
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original))));
    });

    // ---- 性能テスト (性能テスト用の環境でだけ実行する) ----

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-AUTO-24-03")]
    [Trait(UiTest.Category, PerformanceTests.Performance)]
    public Task Flooding_the_output_keeps_the_ui_responsive() => UiTestContext.RunAsync(async ctx =>
    {
        string seq = ctx.CopyTestData("TD-SEQ-1M", "seq.bin");
        string script = ctx.WriteFile("flood.js", Encoding.UTF8.GetBytes("for (let i = 1; i <= 100000; i++) console.log('line ' + i);"));
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [seq] });
        await app.SendAsync("execute", new JsonObject { ["id"] = "view.panel.output" });
        await app.SendAsync("script", new JsonObject { ["action"] = "run", ["path"] = script });
        await app.WaitForDialogAsync("ScriptTrustDialog");
        await app.InvokeDialogButtonAsync("Run once");

        // 実行中の入力の応答時間 (50 回ずつ)。
        var times = new List<double>();
        for (int i = 0; i < 50; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await app.KeyAsync("PageDown");
            await app.RenderAsync();
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        Assert.True(FrameLog.Percentile(times, 0.95) <= 100, $"95%: {FrameLog.Percentile(times, 0.95):F1} ms");
        Assert.True(times.Max() <= 500, $"max: {times.Max():F1} ms");

        // 終了後は保持する行数 (既定 10,000) で、最後の行が 100,000 番目。
        await app.WaitUntilAsync(async () => (await RowsAsync(app)).Any(r => r["text"]!.GetValue<string>() == "line 100000"), TimeSpan.FromMinutes(2), "the last line");
        Assert.Equal(10000, (await UiAsync(app, "outputRows"))["total"]!.GetValue<int>());
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-AUTO-25-01")]
    [Trait(UiTest.Category, PerformanceTests.Performance)]
    public Task A_startup_hook_does_not_delay_startup() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string script = Path.Combine(ScriptsFolder(profile), "starthook.js");
        File.WriteAllText(script, "const end = Date.now() + 10000; while (Date.now() < end) {}\nconsole.log('started');");
        File.WriteAllText(Path.Combine(profile, "scripts", "hooks.json"), $$"""{"startup":[{"script":{{System.Text.Json.JsonSerializer.Serialize(script)}},"enabled":true}]}""");
        var started = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
            started.Add(watch.Elapsed.TotalSeconds);
            await app.WaitUntilAsync(async () => (await RowsAsync(app)).Any(r => r["text"]!.GetValue<string>() == "started"), TimeSpan.FromSeconds(60), "the hook output");
            await app.CommandAsync("Command_Exit");
            await app.WaitForExitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.True(started.Order().ElementAt(started.Count / 2) <= 2.0, $"median: {started.Order().ElementAt(started.Count / 2):F2} s");
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-AUTO-46-03")]
    [Trait(UiTest.Category, PerformanceTests.Performance)]
    public Task The_xor_sample_takes_about_as_long_as_the_data_operation() => UiTestContext.RunAsync(async ctx =>
    {
        string big = TestDataCatalog.Generate("TD-AUTO-RANDOM-10G", ctx.Root);
        string sample = File.ReadAllText(Path.Combine(AppLocator.RepositoryRoot, "samples", "scripts", "xor-decode.js"))
            .Replace("script.args.key", "\"5A\"").Replace("script.args.whole", "true");
        string script = ctx.WriteFile("xor.js", Encoding.UTF8.GetBytes(sample));
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [big] });
        var menu = new List<double>();
        var viaScript = new List<double>();
        for (int round = 0; round < 3; round++)
        {
            // 1. メニューのデータ演算 (XOR 5A を全体に)。
            await app.SelectAsync(0, 10L * 1024 * 1024 * 1024);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await DataOperationUiTests.OpenDialogAsync(app);
            await EditCommandTests.SelectItemAsync(app, "DataOp_Category", "Bitwise");
            await EditCommandTests.SelectItemAsync(app, "DataOp_Kind", "XOR");
            await EditCommandTests.SelectItemAsync(app, "DataOp_OperandSource", "Number");
            await app.UiaSetValueAsync("DataOp_Operand", "5A");
            await EditCommandTests.PressAsync(app, waitForOperations: false);
            await app.WaitUntilAsync(async () => (await app.StateAsync())["activeOperations"]!.GetValue<int>() == 0, TimeSpan.FromMinutes(30), "the XOR");
            menu.Add(watch.Elapsed.TotalSeconds);
            await app.KeyAsync("Z", ctrl: true);
            await app.IdleAsync();

            // 2. サンプル (信頼の確認は初回だけ「常に許可する」)。
            watch.Restart();
            await app.SendAsync("script", new JsonObject { ["action"] = "run", ["path"] = script });
            if (round == 0)
            {
                await app.WaitForDialogAsync("ScriptTrustDialog");
                await app.InvokeDialogButtonAsync("Always allow");
                watch.Restart();
            }

            await app.WaitUntilAsync(async () => (await app.SendAsync("script", new JsonObject { ["action"] = "running" }))["count"]!.GetValue<int>() == 0,
                TimeSpan.FromMinutes(30), "the sample");
            viaScript.Add(watch.Elapsed.TotalSeconds);
            await app.KeyAsync("Z", ctrl: true);
            await app.IdleAsync();
        }

        double menuMedian = menu.Order().ElementAt(1);
        double scriptMedian = viaScript.Order().ElementAt(1);
        Assert.True(scriptMedian <= menuMedian * 1.1, $"menu {menuMedian:F1} s, sample {scriptMedian:F1} s");
    }, TimeSpan.FromHours(2));

    // ---- Monaco エディタが要るテスト ----
    // スクリプトエディタは Monaco (WebView2) を同梱できていない (ダウンロードが禁止の環境で作ったため)。仕様の代替 (簡易エディタ) で動いており、
    // 補完・波線・色分けはない。Monaco を同梱したら外す。

    private const string MonacoSkip = "Monaco is not bundled yet; the plain-text fallback editor has no completion, squiggles or syntax colors (AUTO-20 spec 9).";

    [Fact(Skip = MonacoSkip)]
    [Trait(UiTest.TC, "TC-AUTO-20-01")]
    public void Completion_for_doc_shows_candidates_with_descriptions() { }

    [Fact(Skip = MonacoSkip)]
    [Trait(UiTest.TC, "TC-AUTO-20-02")]
    public void An_unknown_method_gets_a_squiggle_within_500_ms() { }

    [Fact(Skip = MonacoSkip)]
    [Trait(UiTest.TC, "TC-AUTO-20-04")]
    public void Code_stays_left_to_right_in_arabic() { }

    [Fact(Skip = MonacoSkip)]
    [Trait(UiTest.TC, "TC-AUTO-20-05")]
    public void The_editor_works_offline() { }

    [Fact(Skip = "Needs an external debugger (Visual Studio or vsdbg) to attach to the app process; verified manually.")]
    [Trait(UiTest.TC, "TC-AUTO-22-01")]
    public void An_external_debugger_attaches_and_stops_on_a_csx_line() { }
}
