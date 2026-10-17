using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// ユーザー定義型 (INSP-22): 設定フォルダの <c>inspector/</c> に置いたテンプレートの型をインスペクタの「ユーザー定義」グループに出す。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class InspectorUserTypeTests
{
    private const string UserGroup = "User";

    /// <summary>設定フォルダの inspector/ に定義ファイルを置き、TD-INSP-VALUES を開いてインスペクタを表示し、カーソルを 0x10 に置く。</summary>
    private static async Task<(AppSession App, string File)> StartAsync(UiTestContext ctx, string fileName, string text)
    {
        string profile = ctx.NewProfile();
        string folder = Path.Combine(profile, "inspector");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, fileName);
        File.WriteAllText(file, text);
        AppSession app = await InspectorTests.StartAsync(ctx, profile: profile);
        await InspectorTests.GoAsync(app, 0x10);
        return (app, file);
    }

    private static async Task<List<JsonObject>> UserItemsAsync(AppSession app) =>
        [.. (await InspectorTests.StateAsync(app))["items"]!.AsArray().Select(i => i!.AsObject())
            .Where(i => i["group"]?.GetValue<string>() == UserGroup && i["kind"]!.GetValue<string>() != "Group")];

    private static async Task<JsonObject> UserRowAsync(AppSession app, string name)
    {
        JsonObject? row = null;
        await app.WaitUntilAsync(async () =>
        {
            row = (await UserItemsAsync(app)).FirstOrDefault(i => i["kind"]!.GetValue<string>() == "Row" && i["name"]!.GetValue<string>() == name);
            return row is not null && row["status"]!.GetValue<string>() != "Loading";
        }, TimeSpan.FromSeconds(15), $"the user-defined row {name}");
        return row!;
    }

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-22-01")]
    public Task Template_struct_row_expands_to_its_fields() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, _) = await StartAsync(ctx, "header6.hxt", TestDataCatalog.InspectorTemplateType);

        // 1: 「ユーザー定義」グループに展開できる Header6 の行がある。
        JsonObject row = await UserRowAsync(app, "Header6");
        Assert.True(row["canExpand"]!.GetValue<bool>(), row.ToJsonString());
        Assert.Contains((await InspectorTests.StateAsync(app))["items"]!.AsArray(), i => i!["id"]!.GetValue<string>() == "Inspector_Group_User");

        // 2: 展開すると id = 513、size = 1,027。
        await app.SendAsync("inspectorSelect", new JsonObject { ["id"] = row["id"]!.GetValue<string>() });
        await InspectorTests.PanelKeyAsync(app, "Space");
        await app.IdleAsync();
        List<JsonObject> fields = [.. (await UserItemsAsync(app)).Where(i => i["kind"]!.GetValue<string>() == "Component")];
        Assert.Equal(["id", "size"], fields.Select(f => f["name"]!.GetValue<string>()));
        Assert.Equal("513", fields[0]["value"]!.GetValue<string>());
        Assert.Equal("1,027", fields[1]["value"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-22-02")]
    public Task Infinite_loop_type_times_out_and_other_rows_stay_correct() => UiTestContext.RunAsync(async ctx =>
    {
        // テンプレートで定義した型の時間切れとエラー: Loop (無限ループ) と Throw (4 KB を超えて読むので失敗する)。プラグインの型は次のテスト。
        const string Types = """
            struct Loop { while (true) {} } [[inspector("Loop")]];
            struct Throw { le u32 head; u8 rest[8192]; } [[inspector("Throw")]];
            """;
        (AppSession app, _) = await StartAsync(ctx, "types.hxt", Types);

        // 1: Loop が「時間切れ」。
        Assert.Equal("Timed out", (await UserRowAsync(app, "Loop"))["value"]!.GetValue<string>());

        // 2: → を 10 回押しても、キー入力からインスペクタの更新までがすべて 50 ms 以内 (UI が固まらない。時間はアプリの中で測る)。
        var times = new List<double>();
        await app.FocusAsync("editor");
        for (int i = 0; i < 10; i++)
        {
            await app.SendAsync("inspectorKeyLatency", new JsonObject { ["key"] = "Right" });
            JsonObject? result = null;
            await app.WaitUntilAsync(async () => (result = await app.SendAsync("inspectorLatencyResult"))["pending"] is null,
                TimeSpan.FromSeconds(5), "the inspector to update");
            times.Add(result!["ms"]!.GetValue<double>());
        }

        TimeBudget.Limit(times.Max() <= 50, $"key to inspector update: max {times.Max():F1} ms ({string.Join(", ", times.Select(t => t.ToString("F1")))})");

        // 3: Throw は「エラー: …」、int32 は正しい値 (0x10 の 01 02 03 04)。
        await InspectorTests.GoAsync(app, 0x10);
        Assert.StartsWith("Error: ", (await UserRowAsync(app, "Throw"))["value"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("67,305,985", await InspectorTests.ValueAsync(app, "int32"));
        Assert.Equal("Timed out", (await UserRowAsync(app, "Loop"))["value"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-22-02")]
    public Task Plugin_types_loop_times_out_throw_shows_the_error_and_echo_is_correct() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: TD-INSP-PLUGIN-TYPES を入れて信頼済みにした設定フォルダ (署名の代わりにテスト用の信頼の記録。この PC に証明書を入れない)。
        string profile = ctx.NewProfile();
        TestPlugins.Trust(profile, TestPlugins.Install(Path.Combine(profile, "plugins"), TestPlugins.InspectorTypes), TestPlugins.InspectorTypes.Id);
        AppSession app = await InspectorTests.StartAsync(ctx, profile: profile);
        await app.SendAsync("plugins", new JsonObject { ["action"] = "waitLoaded" }, TimeSpan.FromSeconds(120));
        await InspectorTests.GoAsync(app, 0x10);

        // 1: Loop が「時間切れ」。
        Assert.Equal("Timed out", (await UserRowAsync(app, "Loop"))["value"]!.GetValue<string>());

        // 2: → を 10 回押しても、キー入力からインスペクタの更新までがすべて 50 ms 以内 (止まった呼び出しを待たない)。
        var times = new List<double>();
        await app.FocusAsync("editor");
        for (int i = 0; i < 10; i++)
        {
            await app.SendAsync("inspectorKeyLatency", new JsonObject { ["key"] = "Right" });
            JsonObject? result = null;
            await app.WaitUntilAsync(async () => (result = await app.SendAsync("inspectorLatencyResult"))["pending"] is null,
                TimeSpan.FromSeconds(5), "the inspector to update");
            times.Add(result!["ms"]!.GetValue<double>());
        }

        TimeBudget.Limit(times.Max() <= 50, $"key to inspector update: max {times.Max():F1} ms ({string.Join(", ", times.Select(t => t.ToString("F1")))})");

        // 3: Throw は「エラー: test failure」、Echo は起点の 4 バイト、int32 は正しい値 (0x10 の 01 02 03 04)。
        await InspectorTests.GoAsync(app, 0x10);
        Assert.Equal("Error: test failure", (await UserRowAsync(app, "Throw"))["value"]!.GetValue<string>());
        Assert.Equal("01 02 03 04", (await UserRowAsync(app, "Echo"))["value"]!.GetValue<string>());
        Assert.Equal("67,305,985", await InspectorTests.ValueAsync(app, "int32"));
        Assert.Equal("Timed out", (await UserRowAsync(app, "Loop"))["value"]!.GetValue<string>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-TPL-29-01")]
    public Task Registered_template_type_shows_follows_the_cursor_and_writes() => UiTestContext.RunAsync(async ctx =>
    {
        // 前提: 設定「インスペクタ用テンプレート」に TD-TPL-INSPECTOR を登録し、TD-TPL-RGB565-DATA を開く (カーソルは 0x0)。
        string profile = ctx.NewProfile();
        ViewOps.WriteSettings(profile, new JsonObject { ["templates.inspector"] = new JsonArray(TemplateUiTests.Fixture("inspector-rgb565.hxt")) });
        AppSession app = await InspectorTests.StartAsync(ctx, data: "TD-TPL-RGB565-DATA", profile: profile);

        // 1: 「RGB565 色」の行があり、値は 0x0 の 00 F8 = #FF0000。
        JsonObject row = await UserRowAsync(app, "RGB565 色");
        Assert.Equal("#FF0000", row["value"]!.GetValue<string>());

        // 2: カーソルを 0x2 に動かすと #00FF00 (E0 07)。
        await InspectorTests.GoAsync(app, 2);
        await app.WaitUntilAsync(async () => (await UserRowAsync(app, "RGB565 色"))["value"]!.GetValue<string>() == "#00FF00",
            TimeSpan.FromSeconds(15), "the value at 0x2");

        // 3・4: #0000FF に書き換えると 0x2〜0x3 が 1F 00 になり、Ctrl+Z で E0 07 に戻る。
        string type = row["id"]!.GetValue<string>()["Inspector_Row_".Length..];
        await InspectorTests.BeginEditAsync(app, type, "#0000FF");
        JsonObject enter = await InspectorTests.PanelKeyAsync(app, "Enter");
        await app.IdleAsync();
        JsonObject edited = await InspectorTests.RowAsync(app, type);
        if (edited["editing"]!.GetValue<bool>() && enter["handledBy"]?.GetValue<string>() != "inspector")
        {
            // Enter が入力欄に届かなかった (フォーカスの移動の途中など) ときだけ、もう一度押す。確定を拒んだ (誤り) ときは押し直さない
            enter = await InspectorTests.PanelKeyAsync(app, "Enter");
            await app.IdleAsync();
        }

        // テンプレートの型の書き込みは評価のあとに反映されるため、書き換わるのを待つ。失敗したら行の状態を出す (原因を調べるため)
        try
        {
            await app.WaitUntilAsync(async () => (await app.BytesAsync(2, 2)).SequenceEqual(new byte[] { 0x1F, 0x00 }),
                TimeSpan.FromSeconds(15), "the written bytes at 0x2");
        }
        catch (TimeoutException ex)
        {
            JsonObject now = await InspectorTests.RowAsync(app, type);
            throw new TimeoutException($"{ex.Message} Enter: {enter.ToJsonString()}; row: editing={now["editing"]}, editText={now["editText"]}, " +
                $"preview={now["preview"]}, error={now["error"]}, status={now["status"]}, value={now["value"]}", ex);
        }
        await app.FocusAsync("editor");
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(new byte[] { 0xE0, 0x07 }, await app.BytesAsync(2, 2));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-INSP-22-03")]
    public Task Saving_the_definition_file_again_updates_the_rows() => UiTestContext.RunAsync(async ctx =>
    {
        (AppSession app, string file) = await StartAsync(ctx, "header6.hxt", TestDataCatalog.InspectorTemplateType);
        await UserRowAsync(app, "Header6");

        // 1・2: 型名を Header6B に書き換えて保存すると、再起動せずに行の名前が変わる。
        File.WriteAllText(file, TestDataCatalog.InspectorTemplateType.Replace("Header6", "Header6B", StringComparison.Ordinal));
        JsonObject renamed = await UserRowAsync(app, "Header6B");
        Assert.Equal("Header6B", renamed["name"]!.GetValue<string>());

        // 3: 構文の誤りを入れて保存すると、InfoBar にファイル名・行番号・理由。
        File.WriteAllText(file, "struct Broken {\n    le u16 id\n} [[inspector(\"Broken\")]];\n");
        JsonObject notice = await app.WaitForNotificationAsync(m => m.Contains("header6.hxt", StringComparison.Ordinal), "the load error");
        string message = notice["message"]!.GetValue<string>();
        Assert.Matches(@"line [23]", message);
        Assert.True(message.Length > "Inspector type file header6.hxt, line 2: ".Length, message);
    });
}
