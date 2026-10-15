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
        // プラグインの拡張点ができるまでは、テンプレートの Loop (無限ループ) と Throw (4 KB を超えて読むので失敗する) で代用する (テストケースの備考)。
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
