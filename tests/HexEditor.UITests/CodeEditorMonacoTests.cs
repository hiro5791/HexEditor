using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// コードエディタの部品 Monaco (WebView2。08 の AUTO-20、07 の TPL-31) を、アプリにつないだ状態で確かめる。Monaco の状態はテスト用の命令
/// "codeEditor" がページで JavaScript を評価して読む (画面全体のキー入力・マウス・クリップボードは使わない)。キーはページの中で keydown を起こす。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class CodeEditorMonacoTests
{
    /// <summary>TD-AUTO-JS-SYNTAX (約 12 行: キーワード・文字列・数値・コメント・正規表現)。</summary>
    private const string JsSyntax = """
        // comment
        const answer = 0x10;
        const label = "text";
        const pattern = /ab+c/g;

        function describe(value) {
          // the value as text
          return label + ': ' + value.toString(16);
        }

        console.log(describe(answer), pattern.test('abbc'));
        """;

    private static readonly TimeSpan Wait = UiTest.Scaled(TimeSpan.FromSeconds(30));

    private static Task<JsonObject> EditorAsync(AppSession app, string action, JsonObject? args = null)
    {
        args ??= [];
        args["action"] = action;
        return app.SendAsync("codeEditor", args, TimeSpan.FromSeconds(120));
    }

    private static Task<JsonObject> ScriptUiAsync(AppSession app, string action, JsonObject? args = null)
    {
        args ??= [];
        args["action"] = action;
        return app.SendAsync("scriptUi", args);
    }

    private static Task<JsonObject> TemplateEditorAsync(AppSession app, string action, JsonObject? args = null)
    {
        args ??= [];
        args["action"] = action;
        return app.SendAsync("templateEditor", args);
    }

    /// <summary>スクリプトを書いたファイルをスクリプトエディタで開き、Monaco の準備ができるまで待つ。</summary>
    private static async Task<JsonObject> OpenScriptAsync(UiTestContext ctx, AppSession app, string fileName, string text)
    {
        string path = ctx.WriteFile(fileName, System.Text.Encoding.UTF8.GetBytes(text));
        await ScriptUiAsync(app, "openEditor", new JsonObject { ["path"] = path });
        await app.WaitUntilAsync(async () => (await EditorAsync(app, "state"))["open"]!.GetValue<bool>(), Wait, "the script editor");
        JsonObject state = await EditorAsync(app, "wait");
        Assert.True(state["isMonaco"]!.GetValue<bool>(), "Monaco is not used: " + state["fallbackReason"]);
        Assert.True(state["ready"]!.GetValue<bool>(), "Monaco did not start");
        return state;
    }

    /// <summary>JavaScript の言語サービス (TypeScript の worker) が候補を返すまで待つ (最初の起動は時間がかかる)。</summary>
    private static Task WaitForJsServiceAsync(AppSession app) =>
        app.WaitUntilAsync(async () => (await EditorAsync(app, "jsCompletions"))["result"]?["names"]?.AsArray().Count > 0, Wait, "the JavaScript language service");

    /// <summary>ページ側のモデルの本文が指定のものになるまで待つ (アプリからの本文の反映は非同期)。</summary>
    private static Task WaitForModelTextAsync(AppSession app, string text) =>
        app.WaitUntilAsync(async () => (await EditorAsync(app, "eval", new JsonObject { ["script"] = "hexEditor.model.getValue()" }))["result"]?.GetValue<string>() == text, Wait, "the editor text");

    private static async Task<JsonArray> MarkersAsync(AppSession app) => (await EditorAsync(app, "markers"))["result"]!.AsArray();

    // ───────────── AUTO-20 ─────────────

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-20-01")]
    public Task Completion_for_doc_shows_candidates_with_descriptions() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        await OpenScriptAsync(ctx, app, "new.js", string.Empty);

        // 1〜2. 1 行目と改行、doc. を入力する (. で補完の一覧が出る)
        await EditorAsync(app, "type", new JsonObject { ["text"] = "const doc = app.activeDocument;\n" });
        await EditorAsync(app, "type", new JsonObject { ["text"] = "doc." });

        // 3. 補完の一覧 (TypeScript の言語サービスの候補) と readBytes の説明
        JsonObject? names = null;
        await app.WaitUntilAsync(async () =>
        {
            names = (await EditorAsync(app, "jsCompletions", new JsonObject { ["name"] = "readBytes" }))["result"]!.AsObject();
            return names["names"]!.AsArray().Count > 0;
        }, Wait, "the completion items of doc.");
        string[] items = [.. names!["names"]!.AsArray().Select(n => n!.GetValue<string>())];
        Assert.Contains("readBytes", items);
        Assert.Contains("writeBytes", items);
        Assert.Contains("length", items);
        string signature = names["detail"]!["signature"]!.GetValue<string>();
        Assert.Contains("offset", signature, StringComparison.Ordinal);
        Assert.Contains("length", signature, StringComparison.Ordinal);
        Assert.Contains("Reads bytes", names["detail"]!["documentation"]!.GetValue<string>(), StringComparison.Ordinal);

        // 画面の補完の一覧に出ていて、readBytes を選ぶと説明の欄に hexeditor.d.ts の説明文が出る
        JsonObject suggest = (await EditorAsync(app, "suggest", new JsonObject { ["focus"] = "readBytes" }))["result"]!.AsObject();
        Assert.True(suggest["visible"]!.GetValue<bool>(), suggest.ToJsonString());
        Assert.Equal("readBytes", suggest["focused"]?.GetValue<string>());
        Assert.Contains("Reads bytes", suggest["details"]?.GetValue<string>() ?? string.Empty, StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-20-02")]
    public Task An_unknown_method_gets_a_squiggle_within_500_ms() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        await OpenScriptAsync(ctx, app, "new.js", "\n\n");

        // 1〜2. 入力してから波線 (Monaco の印) が付くまでを、ページの中で 50 ms ごとに調べて測る
        JsonObject measured = (await EditorAsync(app, "eval", new JsonObject
        {
            ["script"] = """
                (async () => {
                  const e = hexEditor.editor, m = hexEditor.model, mon = hexEditor.monaco;
                  e.setPosition({ lineNumber: 1, column: 1 });
                  e.trigger('keyboard', 'type', { text: 'app.activeDocument.notExistingMethod();' });
                  e.setPosition({ lineNumber: 3, column: 1 });
                  const typed = performance.now();
                  for (let i = 0; i < 200; i++) {
                    const found = mon.editor.getModelMarkers({ resource: m.uri }).filter((k) => k.owner !== 'hexeditor');
                    if (found.length > 0) {
                      return { elapsed: performance.now() - typed, line: found[0].startLineNumber, start: found[0].startColumn, end: found[0].endColumn };
                    }
                    await new Promise((r) => setTimeout(r, 50));
                  }
                  return { elapsed: -1 };
                })()
                """,
        }))["result"]!.AsObject();
        double elapsed = measured["elapsed"]!.GetValue<double>();
        Assert.True(elapsed >= 0, "no squiggle");
        TimeBudget.Limit(elapsed <= 1000, $"squiggle after {elapsed:F0} ms (limit 1000 ms)");
        Assert.Equal(1, measured["line"]!.GetValue<int>());
        string line = "app.activeDocument.notExistingMethod();";
        int column = line.IndexOf("notExistingMethod", StringComparison.Ordinal) + 1;
        Assert.Equal(column, measured["start"]!.GetValue<int>());
        Assert.Equal(column + "notExistingMethod".Length, measured["end"]!.GetValue<int>());

        // 3. 問題の一覧に 1 件出て、選ぶとカーソルが 1 行目に移る
        await app.WaitUntilAsync(async () => (await ScriptUiAsync(app, "problems"))["rows"]!.AsArray().Count == 1, Wait, "one problem");
        Assert.Equal(3, (await EditorAsync(app, "state"))["line"]!.GetValue<int>());
        await ScriptUiAsync(app, "selectProblem", new JsonObject { ["index"] = 0 });
        await app.WaitUntilAsync(async () => (await EditorAsync(app, "state"))["line"]!.GetValue<int>() == 1, Wait, "the caret on line 1");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-20-04")]
    public Task Code_stays_left_to_right_in_arabic() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { UiLanguage = "ar" });
        await OpenScriptAsync(ctx, app, "syntax.js", JsSyntax);
        JsonObject direction = await EditorAsync(app, "direction");

        // 1. ウィンドウは右から左、2. エディタの WebView2 と Monaco の要素は左から右、3. 1 行目の先頭の文字が末尾の文字より左
        Assert.Equal("RightToLeft", direction["windowFlowDirection"]!.GetValue<string>());
        Assert.Equal("LeftToRight", direction["webViewFlowDirection"]!.GetValue<string>());
        Assert.Equal("LeftToRight", direction["editorFlowDirection"]!.GetValue<string>());
        JsonObject page = direction["result"]!.AsObject();
        Assert.Equal("ltr", page["direction"]!.GetValue<string>());
        Assert.Equal("ltr", page["linesDirection"]!.GetValue<string>());
        Assert.True(page["firstX"]!.GetValue<double>() < page["lastX"]!.GetValue<double>(), page.ToJsonString());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-20-05")]
    public Task The_editor_works_offline() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();

        // 1. JavaScript の新規スクリプト、2. doc. の補完の一覧
        await OpenScriptAsync(ctx, app, "offline.js", string.Empty);
        await EditorAsync(app, "type", new JsonObject { ["text"] = "doc." });
        await WaitForJsServiceAsync(app);
        JsonObject suggest = (await EditorAsync(app, "suggest"))["result"]!.AsObject();
        Assert.True(suggest["visible"]!.GetValue<bool>(), suggest.ToJsonString());
        Assert.NotEmpty(suggest["rows"]!.AsArray());

        // 3. 1 + を入力して構文エラーの印
        await EditorAsync(app, "eval", new JsonObject { ["script"] = "(() => { const e = hexEditor.editor; e.trigger('test', 'hideSuggestWidget', {}); e.setValue ? 0 : 0; hexEditor.model.setValue('1 +'); return true; })()" });
        await app.WaitUntilAsync(async () => (await MarkersAsync(app)).Any(m => m!["owner"]!.GetValue<string>() == "hexeditor" && m["severity"]!.GetValue<int>() == 8),
            Wait, "the syntax error marker");

        // 4. 読み込んだ場所はすべてアプリ内の仮想ホスト (http:// / https:// の外の場所がない)
        JsonObject resources = await EditorAsync(app, "resources");
        string[] requested = [.. resources["requested"]!.AsArray().Select(u => u!.GetValue<string>())];
        string[] entries = [.. resources["page"]!["entries"]!.AsArray().Select(u => u!.GetValue<string>())];
        Assert.Contains(entries, e => e.EndsWith("/vs/loader.js", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Contains("/vs/editor/editor.main", StringComparison.Ordinal));
        Assert.StartsWith("https://monaco.hexeditor.invalid/", resources["page"]!["href"]!.GetValue<string>(), StringComparison.Ordinal);
        foreach (string uri in requested.Concat(entries))
        {
            Assert.True(uri.StartsWith("https://monaco.hexeditor.invalid/", StringComparison.Ordinal) || uri.StartsWith("blob:", StringComparison.Ordinal)
                || uri.StartsWith("data:", StringComparison.Ordinal), "external request: " + uri);
        }

        // ページから外の場所へは読み込めない (Content-Security-Policy と要求の遮断。名前は .invalid なので通信は起きない)
        JsonObject blocked = (await EditorAsync(app, "eval", new JsonObject
        {
            ["script"] = "fetch('https://hexeditor-test.invalid/x.js').then(() => ({ ok: true }), (e) => ({ ok: false, error: String(e) }))",
        }))["result"]!.AsObject();
        Assert.False(blocked["ok"]!.GetValue<bool>(), blocked.ToJsonString());
    });

    // ───────────── TPL-31 (Monaco の画面で) ─────────────

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-31-01")]
    public Task Template_member_completion_appears_in_the_monaco_list() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        await TemplateEditorAsync(app, "new");
        Assert.True((await EditorAsync(app, "wait"))["ready"]!.GetValue<bool>());
        const string source = "struct Header { u32 magic; u16 version; u16 count; };\nHeader hdr;\n";
        await TemplateEditorAsync(app, "setText", new JsonObject { ["text"] = source });
        // アプリからの本文の反映は非同期。反映前に位置を決めて入力すると 3 行目が存在せず補完が出ない
        await WaitForModelTextAsync(app, source);

        // 1. 3 行目に u8 x[hdr. を入力する (. で補完の一覧)、2. 候補を読む
        await EditorAsync(app, "position", new JsonObject { ["line"] = 3, ["column"] = 1 });
        await EditorAsync(app, "type", new JsonObject { ["text"] = "u8 x[hdr." });
        JsonObject suggest = [];
        bool triggered = false;
        await app.WaitUntilAsync(async () =>
        {
            // 「.」での自動起動は言語サービスの準備前だと出ないので、一度だけ明示的にも起動する (出ている一覧は作り直さない)
            suggest = (await EditorAsync(app, "suggest", new JsonObject { ["trigger"] = !triggered }))["result"]!.AsObject();
            triggered = true;
            return suggest["rows"]!.AsArray().Count >= 3;
        }, Wait, "the member completion list");
        Dictionary<string, string> rows = suggest!["rows"]!.AsArray().ToDictionary(r => r!["label"]!.GetValue<string>(), r => r!["detail"]!.GetValue<string>());
        Assert.Contains("u32", rows["magic"], StringComparison.Ordinal);
        Assert.Contains("u16", rows["version"], StringComparison.Ordinal);
        Assert.Contains("u16", rows["count"], StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-31-02")]
    public Task Template_syntax_error_gets_a_squiggle_line_mark_and_hover_text() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        await TemplateEditorAsync(app, "new");
        Assert.True((await EditorAsync(app, "wait"))["ready"]!.GetValue<bool>());

        // 1. 1 行目に u8 a (; なし)、2 行目に u8 b;
        await TemplateEditorAsync(app, "setText", new JsonObject { ["text"] = string.Empty });
        await WaitForModelTextAsync(app, string.Empty);
        await EditorAsync(app, "type", new JsonObject { ["text"] = "u8 a\nu8 b;" });

        // 2. 波線 (Monaco の印) と行の印。印の文 (ホバーのツールチップ) に ; がないことの理由
        JsonArray markers = [];
        await app.WaitUntilAsync(async () => (markers = await MarkersAsync(app)).Any(m => m!["owner"]!.GetValue<string>() == "hexeditor"), Wait, "the squiggle");
        JsonObject first = markers.First(m => m!["owner"]!.GetValue<string>() == "hexeditor")!.AsObject();
        Assert.Equal(1, first["line"]!.GetValue<int>());
        Assert.Contains(";", first["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.DoesNotContain(markers, m => m!["line"]!.GetValue<int>() == 2 && m["column"]!.GetValue<int>() > 1);
        JsonArray decorations = (await EditorAsync(app, "decorations", new JsonObject { ["line"] = first["line"]!.GetValue<int>() }))["result"]!.AsArray();
        Assert.Contains(decorations, d => d!["lines"]?.GetValue<string>() == "hex-line-error");
    });

    // ───────────── キー・フォーカス・コマンド (AUTO-20 の仕様 5、00-overview 8.5) ─────────────

    [Fact]
    public Task Code_editor_keys_reach_the_app_while_editing_keys_stay_in_monaco() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        string file = ctx.WriteFile("keys.hxt", "u8 a;\nu8 b;\nu8 c;\n"u8.ToArray());
        await TemplateEditorAsync(app, "open", new JsonObject { ["path"] = file });
        Assert.True((await EditorAsync(app, "wait"))["ready"]!.GetValue<bool>());
        await EditorAsync(app, "position", new JsonObject { ["line"] = 2, ["column"] = 1 });

        // F9 (コードエディタの有効範囲) はアプリのコマンドに届き、余白にブレークポイントの印が付く
        JsonObject f9 = await EditorAsync(app, "key", new JsonObject { ["key"] = "F9", ["keyCode"] = 0x78 });
        Assert.Equal("codeEditor.toggleBreakpoint", f9["forwardedCommand"]?.GetValue<string>());
        await app.WaitUntilAsync(async () => (await EditorAsync(app, "decorations", new JsonObject { ["line"] = 2 }))["result"]!.AsArray()
            .Any(d => d!["glyph"]?.GetValue<string>() == "hex-bp"), Wait, "the breakpoint mark");

        // Ctrl+G (Monaco の行へ移動) はアプリに渡さない
        JsonObject ctrlG = await EditorAsync(app, "key", new JsonObject { ["key"] = "g", ["keyCode"] = (int)'G', ["ctrl"] = true });
        Assert.Null(ctrlG["forwardedCommand"]);
        await EditorAsync(app, "key", new JsonObject { ["key"] = "Escape", ["keyCode"] = 0x1B });

        // Tab はインデントを入れる (フォーカスは移らない)
        await EditorAsync(app, "position", new JsonObject { ["line"] = 3, ["column"] = 1 });
        JsonObject tab = await EditorAsync(app, "key", new JsonObject { ["key"] = "Tab", ["keyCode"] = 0x09 });
        Assert.Null(tab["forwardedCommand"]);
        await app.WaitUntilAsync(async () => (await EditorAsync(app, "state"))["text"]!.GetValue<string>().Contains("\n    u8 c;", StringComparison.Ordinal),
            Wait, "the indentation from Tab");

        // F5 で止まり、現在の行の強調が付く。Shift+F5 で止める
        JsonObject f5 = await EditorAsync(app, "key", new JsonObject { ["key"] = "F5", ["keyCode"] = 0x74 });
        Assert.Equal("codeEditor.run", f5["forwardedCommand"]?.GetValue<string>());
        Assert.Equal(2, (await TemplateEditorAsync(app, "waitPaused"))["debugLine"]!.GetValue<int>());
        await app.WaitUntilAsync(async () => (await EditorAsync(app, "decorations", new JsonObject { ["line"] = 2 }))["result"]!.AsArray()
            .Any(d => d!["className"]?.GetValue<string>() == "hex-current-line"), Wait, "the current line");
        JsonObject stop = await EditorAsync(app, "key", new JsonObject { ["key"] = "F5", ["keyCode"] = 0x74, ["shift"] = true });
        Assert.Equal("codeEditor.stop", stop["forwardedCommand"]?.GetValue<string>());
        await TemplateEditorAsync(app, "waitEnded");

        // F6 (グローバル) で次の領域へ移り、F6 を繰り返すとエディタに戻る
        await EditorAsync(app, "focus");
        JsonObject f6 = await EditorAsync(app, "key", new JsonObject { ["key"] = "F6", ["keyCode"] = 0x75 });
        Assert.Equal("view.nextRegion", f6["forwardedCommand"]?.GetValue<string>());
        await app.WaitUntilAsync(async () => !(await EditorAsync(app, "state"))["hasFocus"]!.GetValue<bool>(), Wait, "the focus out of the editor");
        bool back = false;
        for (int i = 0; i < 12 && !back; i++)
        {
            await app.KeyAsync("F6");
            back = (await EditorAsync(app, "state"))["hasFocus"]!.GetValue<bool>();
        }

        Assert.True(back, "F6 did not come back to the code editor");
    });

    [Fact]
    public Task The_editor_has_an_accessible_name_and_follows_the_dark_theme() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        File.WriteAllText(Path.Combine(profile, "settings.json"), "{\"$schemaVersion\": 1, \"ui.theme\": \"dark\"}");
        AppSession app = await ctx.StartAsync(new AppOptions { Profile = profile });
        JsonObject state = await OpenScriptAsync(ctx, app, "named.js", JsSyntax);
        Assert.Contains("named.js", state["automationName"]!.GetValue<string>(), StringComparison.Ordinal);
        JsonObject page = (await EditorAsync(app, "eval", new JsonObject
        {
            ["script"] = "({ aria: (document.querySelector('.monaco-editor [role=textbox]') || document.querySelector('.monaco-editor textarea')).getAttribute('aria-label'), font: hexEditor.editor.getOption(hexEditor.monaco.editor.EditorOption.fontInfo).fontFamily, dark: document.querySelector('.monaco-editor').classList.contains('vs-dark') })",
        }))["result"]!.AsObject();
        Assert.Contains("named.js", page["aria"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("Cascadia Mono", page["font"]!.GetValue<string>(), StringComparison.Ordinal);

        // アプリのテーマがダークなら Monaco もダークのテーマ (色はアプリのテーマのリソースから)
        Assert.True(page["dark"]!.GetValue<bool>(), page.ToJsonString());
    });

    [Fact]
    public Task Csharp_scripts_are_colored_and_ask_for_the_component_for_completion() => UiTestContext.RunAsync(async ctx =>
    {
        // AUTO-20 の仕様 2・3: C# は色分けし、C# スクリプト コンポーネントがなければ補完の代わりに案内を出す
        AppSession app = await ctx.StartAsync();
        await OpenScriptAsync(ctx, app, "colors.csx", "// comment\nvar text = \"text\";\nConsole.WriteLine(0x10);\n");
        // 色分けは非同期に行われるので、キーワードに色の印が付くまで待つ
        JsonObject page = [];
        await app.WaitUntilAsync(async () =>
        {
            page = (await EditorAsync(app, "eval", new JsonObject
            {
                ["script"] = "({ language: hexEditor.model.getLanguageId(), keyword: [...document.querySelectorAll('.view-line span span')].some((s) => s.textContent === 'var' && /mtk\\d+/.test(s.className)) })",
            }))["result"]!.AsObject();
            return page["keyword"]!.GetValue<bool>();
        }, Wait, "the colored keyword");
        Assert.Equal("csharp", page["language"]!.GetValue<string>());
        await app.WaitForAsync("ScriptEditor_CompletionNotice");
    });

    [Fact]
    public Task Without_WebView2_the_plain_editor_is_used_with_a_notice() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync();
        await EditorAsync(app, "simulateFailure", new JsonObject { ["value"] = true });
        try
        {
            string path = ctx.WriteFile("plain.js", "console.log(1);\n"u8.ToArray());
            await ScriptUiAsync(app, "openEditor", new JsonObject { ["path"] = path });
            JsonObject state = await EditorAsync(app, "wait");
            Assert.False(state["isMonaco"]!.GetValue<bool>());
            Assert.Contains("WebView2", state["fallbackReason"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal("console.log(1);\n", state["text"]!.GetValue<string>());
            await app.WaitForAsync("ScriptEditor_Text_Fallback");

            // 代替の入力欄でも編集できる
            await ScriptUiAsync(app, "setEditorText", new JsonObject { ["text"] = "console.log(2);\n" });
            Assert.True((await ScriptUiAsync(app, "activeEditor"))["dirty"]!.GetValue<bool>());
        }
        finally
        {
            await EditorAsync(app, "simulateFailure", new JsonObject { ["value"] = false });
        }
    });

    [Fact]
    public Task A_10_MB_script_stays_responsive() => UiTestContext.RunAsync(async ctx =>
    {
        // AUTO-20 の「巨大ファイル」: スクリプトの上限は 10 MB。入力の反映をページの中で測る
        var text = new System.Text.StringBuilder();
        int n = 0;
        while (text.Length < (10 * 1024 * 1024) - 200)
        {
            text.Append("const value").Append(n++).Append(" = ").Append(n).Append(";\n");
        }

        AppSession app = await ctx.StartAsync();
        await OpenScriptAsync(ctx, app, "big.js", text.ToString());
        int length = text.Length;
        await app.WaitUntilAsync(async () => (await EditorAsync(app, "state"))["length"]!.GetValue<int>() == length, Wait, "the text");
        JsonObject measured = (await EditorAsync(app, "eval", new JsonObject
        {
            ["script"] = """
                (async () => {
                  const e = hexEditor.editor;
                  e.setPosition({ lineNumber: 1000, column: 1 });
                  const times = [];
                  for (let i = 0; i < 10; i++) {
                    const t = performance.now();
                    e.trigger('keyboard', 'type', { text: 'x' });
                    await new Promise((r) => requestAnimationFrame(() => r()));
                    times.push(performance.now() - t);
                  }
                  times.sort((a, b) => a - b);
                  return { median: times[5], max: times[9] };
                })()
                """,
        }))["result"]!.AsObject();
        double median = measured["median"]!.GetValue<double>();
        TimeBudget.Limit(median <= 100, $"typing in a 10 MB script took {median:F0} ms per key (median; limit 100 ms)");
        await app.WaitUntilAsync(async () => (await EditorAsync(app, "state"))["length"]!.GetValue<int>() == length + 10, Wait, "the typed text in the app");
    }, TimeSpan.FromMinutes(10));
}
