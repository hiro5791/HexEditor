using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.Infrastructure.ViewOps;

namespace HexEditor.UITests;

/// <summary>
/// データ演算 (EDIT-31〜EDIT-37) のダイアログとコマンド。ダイアログの欄は UI オートメーションの値で入力し、ボタンは Invoke で押す。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class DataOperationUiTests
{
    private static async Task<string> TextAsync(AppSession app, string id)
    {
        JsonObject e = null!;
        await app.WaitUntilAsync(async () => (e = await ElementAsync(app, id))["found"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), id);
        return e["text"]?.GetValue<string>() ?? e["content"]?.GetValue<string>() ?? string.Empty;
    }

    private static async Task OpenDialogAsync(AppSession app)
    {
        await app.CommandAsync("Command_DataOperation");
        await app.WaitForAsync("DataOperationDialog");
        await app.IdleAsync();
    }

    private static async Task<int> HistoryCountAsync(AppSession app) => (await app.DocumentAsync())["historyCount"]!.GetValue<int>();

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-31-02")]
    public Task Dialog_shows_the_trailing_note_and_the_preview() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await EditCommandTests.SelectRangeAsync(app, "0x10", "5");
        await OpenDialogAsync(app);
        await EditCommandTests.SelectItemAsync(app, "DataOp_Category", "Arithmetic");
        await EditCommandTests.SelectItemAsync(app, "DataOp_Kind", "Add");
        await EditCommandTests.SelectItemAsync(app, "DataOp_Size", "2");
        await EditCommandTests.SelectItemAsync(app, "DataOp_Endian", "Little endian");
        await app.UiaSetValueAsync("DataOp_Operand", "1");
        await app.UiaSetValueAsync("DataOp_Increment", "0");
        await app.IdleAsync();

        Assert.Contains("1 byte", await TextAsync(app, "DataOp_Note"));
        Assert.Contains("10 11 12 13 14", await TextAsync(app, "DataOp_PreviewBefore"));
        Assert.Contains("11 11 13 13 14", await TextAsync(app, "DataOp_PreviewAfter"));
        string marks = await TextAsync(app, "DataOp_PreviewMarks");
        Assert.Equal(2, marks.Split("^^").Length - 1);

        await EditCommandTests.PressAsync(app);
        Assert.Equal(new byte[] { 0x11, 0x11, 0x13, 0x13, 0x14, 0x15 }, await app.BytesAsync(0x10, 6));
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(new byte[] { 0x10, 0x11, 0x12, 0x13, 0x14 }, await app.BytesAsync(0x10, 5));

        // オペランド 0、増分 0 の加算は実行せず、「データは変わりません」を示す (編集履歴は増えない)。
        await app.SelectAsync(0x10, 5);
        int history = await HistoryCountAsync(app);
        await OpenDialogAsync(app);
        await app.UiaSetValueAsync("DataOp_Operand", "0");
        await app.UiaSetValueAsync("DataOp_Increment", "0");
        await app.IdleAsync();
        await EditCommandTests.PressAsync(app);
        Assert.Contains(await NoticesAsync(app), n => n.Contains("does not change", StringComparison.Ordinal));
        Assert.Equal(history, await HistoryCountAsync(app));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-31-03")]
    [Trait(UiTest.Category, "Nightly")]
    public Task Cancelling_a_ten_gigabyte_xor_changes_nothing_and_one_undo_restores() => UiTestContext.RunAsync(async ctx =>
    {
        string source = TestDataCatalog.Generate("TD-SPARSE-100G", ctx.Root);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [source], Hooks = new JsonObject { ["freeSpace"] = 1L << 40 } });
        await EditCommandTests.SelectRangeAsync(app, "0", "10G");
        await OpenDialogAsync(app);
        await EditCommandTests.SelectItemAsync(app, "DataOp_Category", "Bitwise");
        await EditCommandTests.SelectItemAsync(app, "DataOp_Kind", "XOR");
        await EditCommandTests.SelectItemAsync(app, "DataOp_OperandSource", "Number");
        await EditCommandTests.SelectItemAsync(app, "DataOp_Size", "1");
        await app.UiaSetValueAsync("DataOp_Operand", "FF");
        await app.UiaSetValueAsync("DataOp_Increment", "0");
        await app.UiaSetValueAsync("DataOp_Skip", "0");
        await app.IdleAsync();
        await EditCommandTests.PressAsync(app, waitForOperations: false);
        await CancelWhenProgressAsync(app, 0.10);
        Assert.Equal(TestDataCatalog.Marker(0), await app.BytesAsync(0, 17));
        Assert.Equal(TestDataCatalog.Marker(0x40000000), await app.BytesAsync(0x40000000, 17));
        Assert.Equal(0, (await app.DocumentAsync())["undoCount"]!.GetValue<int>());

        await app.SelectAsync(0, 10L * 1024 * 1024 * 1024);
        await app.CommandAsync("Command_RepeatDataOperation");
        await app.WaitUntilAsync(async () => (await app.StateAsync())["activeOperations"]!.GetValue<int>() == 0, TimeSpan.FromMinutes(30), "the XOR");
        await app.IdleAsync();
        Assert.Equal(0xBF, (await app.BytesAsync(0, 1))[0]);
        Assert.Equal(0xBF, (await app.BytesAsync(0x40000000, 1))[0]);
        Assert.Equal(0xFF, (await app.BytesAsync(0x27FFFFFFF, 1))[0]);
        Assert.Equal(0x00, (await app.BytesAsync(0x280000000, 1))[0]);
        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(TestDataCatalog.Marker(0), await app.BytesAsync(0, 17));
        Assert.Equal(TestDataCatalog.Marker(0x40000000), await app.BytesAsync(0x40000000, 17));
    }, TimeSpan.FromMinutes(45));

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-35-02")]
    [Trait(UiTest.Category, "Nightly")]
    public Task Reversing_three_gigabytes_twice_restores_the_data() => UiTestContext.RunAsync(async ctx =>
    {
        string source = TestDataCatalog.Generate("TD-SPARSE-100G", ctx.Root);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [source], Hooks = new JsonObject { ["freeSpace"] = 1L << 40 } });
        await EditCommandTests.SelectRangeAsync(app, "0", "3G");
        for (int round = 0; round < 2; round++)
        {
            await app.SendAsync("palette", new JsonObject { ["text"] = ">Reverse byte order" });
            Assert.True((await app.SendAsync("paletteEnter"))["invoked"]!.GetValue<bool>());
            await app.WaitUntilAsync(async () => (await app.StateAsync())["operations"]!.AsArray().Count > 0, TimeSpan.FromMinutes(1), "the progress");
            await app.WaitUntilAsync(async () => (await app.StateAsync())["activeOperations"]!.GetValue<int>() == 0, TimeSpan.FromMinutes(30), "the reverse");
            await app.IdleAsync();
            if (round == 0)
            {
                byte[] reversed = TestDataCatalog.Marker(0);
                Array.Reverse(reversed);
                Assert.Equal(reversed, await app.BytesAsync(0xBFFFFFEF, 17));
            }
        }

        Assert.Equal(TestDataCatalog.Marker(0), await app.BytesAsync(0, 17));
        Assert.Equal(TestDataCatalog.Marker(0x40000000), await app.BytesAsync(0x40000000, 17));
        Assert.Equal(TestDataCatalog.Marker(0x80000000), await app.BytesAsync(0x80000000, 17));
    }, TimeSpan.FromMinutes(30));

    /// <summary>処理センターの進捗が <paramref name="fraction"/> 以上になったら処理をキャンセルし、終わるまで待つ。</summary>
    private static async Task CancelWhenProgressAsync(AppSession app, double fraction)
    {
        await app.WaitUntilAsync(async () =>
        {
            JsonArray ops = (await app.StateAsync())["operations"]!.AsArray();
            return ops.Select(o => o!.AsObject()).Any(o => o["state"]!.GetValue<string>() == "Running"
                && o["total"]?.GetValue<long>() is long total && total > 0 && o["processed"]!.GetValue<long>() >= total * fraction);
        }, TimeSpan.FromMinutes(5), "the progress");
        await app.UiaInvokeAsync("Status_Operations");
        await app.UiaInvokeAsync("Operations_Cancel");
        await app.WaitUntilAsync(async () => (await app.StateAsync())["activeOperations"]!.GetValue<int>() == 0, TimeSpan.FromMinutes(2), "the cancel");
        await app.IdleAsync();
    }
}
