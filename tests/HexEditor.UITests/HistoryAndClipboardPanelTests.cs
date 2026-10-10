using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.Infrastructure.ViewOps;

namespace HexEditor.UITests;

/// <summary>
/// 履歴パネル (EDIT-20)、ユーザークリップボードとクリップボード履歴 (EDIT-28)、ハッシュパネルのマルチ選択 (ANA-18 の仕様 1)。
/// パネルの中身はテスト用の命令 (historyPanel / clipboardPanel / hash) で読む。システムのクリップボードはテスト用のビルドの代わりのものを読む。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class HistoryAndClipboardPanelTests
{
    private static async Task TypeAtAsync(AppSession app, long offset, string text)
    {
        await app.GoToAsync(offset);
        await app.TypeAsync(text);
        await app.IdleAsync();
    }

    // ---- EDIT-20 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-20-01")]
    public Task History_panel_goes_to_any_point() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-SEQ-1M")] });

        // 1〜3. 0x10・0x20・0x30 に入力して保存し、0x40・0x50 に入力する (5 つの編集)。
        await TypeAtAsync(app, 0x10, "A1");
        await TypeAtAsync(app, 0x20, "A2");
        await TypeAtAsync(app, 0x30, "A3");
        await app.KeyAsync("S", ctrl: true);
        await app.WaitUntilAsync(async () => !(await app.DocumentAsync())["modified"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(30)), "the save");
        await TypeAtAsync(app, 0x40, "A4");
        await TypeAtAsync(app, 0x50, "A5");

        // 4. 履歴パネル: 開いた時点の行 (番号 0) と 5 行、操作名は「入力」、範囲は各 1 バイト、長さの変化は 0、3 番目の編集の行に「保存」の印。
        //    日時は日付と時刻。
        Assert.True(await SelectionTests.ExecuteAsync(app, "view.panel.history"));
        await app.WaitForAsync("History_List");
        JsonArray rows = (await app.SendAsync("historyPanel"))["rows"]!.AsArray();
        Assert.Equal(6, rows.Count);
        Assert.Equal(0, rows[0]!["index"]!.GetValue<int>());
        Assert.Equal(string.Empty, rows[0]!["range"]!.GetValue<string>());
        Assert.All(rows.Skip(1), r => Assert.Equal("Typing", r!["name"]!.GetValue<string>()));
        Assert.Equal(["0x10 (1 bytes)", "0x20 (1 bytes)", "0x30 (1 bytes)", "0x40 (1 bytes)", "0x50 (1 bytes)"],
            rows.Skip(1).Select(r => r!["range"]!.GetValue<string>().Replace("00000", string.Empty, StringComparison.Ordinal)));
        Assert.All(rows.Skip(1), r => Assert.Equal("0", r!["delta"]!.GetValue<string>()));
        Assert.Equal([false, false, false, true, false, false], rows.Select(r => r!["saved"]!.GetValue<bool>()));
        Assert.Contains(DateTime.Now.Year.ToString(System.Globalization.CultureInfo.InvariantCulture), rows[1]!["time"]!.GetValue<string>(),
            StringComparison.Ordinal);

        // 5〜6. 1 番目の編集の行 (ダブルクリック・Enter と同じ処理): 0x10 だけが A1。その行が色・太字・印で強調され、後ろの行は薄い。
        await app.SendAsync("historyGoTo", new JsonObject { ["index"] = 1 });
        await app.IdleAsync();
        Assert.Equal(new byte[] { 0xA1, 0x20, 0x30, 0x40, 0x50 }, await ValuesAsync(app));
        rows = (await app.SendAsync("historyPanel"))["rows"]!.AsArray();
        Assert.True(rows[1]!["current"]!.GetValue<bool>() && rows[1]!["bold"]!.GetValue<bool>() && rows[1]!["colorBar"]!.GetValue<bool>());
        Assert.False(rows[0]!["colorBar"]!.GetValue<bool>());
        Assert.All(rows.Skip(2), r => Assert.True(r!["redo"]!.GetValue<bool>() && r["opacity"]!.GetValue<double>() < 1));

        // 7〜8. Ctrl+Y で 2 番目の編集がやり直される。
        await app.KeyAsync("Y", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(new byte[] { 0xA1, 0xA2, 0x30, 0x40, 0x50 }, await ValuesAsync(app));

        // 開いた時点の行へ移るとすべての編集を元に戻す。
        await app.SendAsync("historyGoTo", new JsonObject { ["index"] = 0 });
        await app.IdleAsync();
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 }, await ValuesAsync(app));
        rows = (await app.SendAsync("historyPanel"))["rows"]!.AsArray();
        Assert.True(rows[0]!["current"]!.GetValue<bool>());
        Assert.All(rows.Skip(1), r => Assert.True(r!["redo"]!.GetValue<bool>()));
    });

    private static async Task<byte[]> ValuesAsync(AppSession app) =>
        [.. await Task.WhenAll(new long[] { 0x10, 0x20, 0x30, 0x40, 0x50 }.Select(async o => (await app.BytesAsync(o, 1))[0]))];

    // ---- EDIT-28 ----

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-28-01")]
    public Task User_clipboards_are_independent_of_the_system_clipboard() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.KeyAsync("Insert");
        await app.SendAsync("setClipboard", new JsonObject { ["text"] = string.Empty });
        string before = (await app.SendAsync("clipboard")).ToJsonString();

        // 1〜2. 「ユーザークリップボード 3 にコピー」: システムのクリップボードは変わらない。
        await app.SelectAsync(0x10, 4);
        Assert.True(await SelectionTests.ExecuteAsync(app, "edit.userClipboard.copy3"));
        Assert.Equal(before, (await app.SendAsync("clipboard")).ToJsonString());

        // 3〜5. 通常のコピーをしても、3 番から貼ると 3 番の内容。
        await app.SelectAsync(0x80, 2);
        await app.KeyAsync("C", ctrl: true);
        await app.IdleAsync();
        await app.GoToAsync(0x200);
        Assert.True(await SelectionTests.ExecuteAsync(app, "edit.userClipboard.paste3"));
        Assert.Equal(new byte[] { 0x10, 0x11, 0x12, 0x13, 0x00 }, await app.BytesAsync(0x200, 5));

        // 6. 空の番号からの貼り付けは無効。
        JsonObject commands = await app.SendAsync("commands");
        Assert.False(commands["items"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == "edit.userClipboard.paste5")!["enabled"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-28-02")]
    public Task User_clipboards_up_to_16_MiB_survive_a_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        WriteSettings(profile, new JsonObject { ["clipboard.user.persist"] = true });
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-RANDOM-16M"), ctx.TestData("TD-MARKERS-1G")], Profile = profile });

        // 1. TD-RANDOM-16M の全体 (16 MiB) を 1 番に。
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await app.IdleAsync();
        await app.KeyAsync("A", ctrl: true);
        Assert.True(await SelectionTests.ExecuteAsync(app, "edit.userClipboard.copy1"));

        // 2. TD-MARKERS-1G の 16 MiB + 1 バイトを 2 番に。
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 1 });
        await app.IdleAsync();
        await app.SelectAsync(0, 16_777_217);
        Assert.True(await SelectionTests.ExecuteAsync(app, "edit.userClipboard.copy2"));

        // 3. 2 番に「終了時に消えます」、1 番には出ない。
        JsonArray slots = (await app.SendAsync("clipboardPanel"))["slots"]!.AsArray();
        Assert.Equal(string.Empty, slots[0]!["note"]!.GetValue<string>());
        Assert.Equal("Cleared on exit", slots[1]!["note"]!.GetValue<string>());

        // 4〜5. 起動し直すと 1 番は 16 MiB で残り、2 番は空。
        await app.SendAsync("exit");
        await app.WaitForExitAsync(TimeSpan.FromSeconds(60));
        app = await ctx.StartAsync(new AppOptions { Files = [ctx.CopyTestData("TD-ZERO-1M")], Profile = profile });
        slots = (await app.SendAsync("clipboardPanel"))["slots"]!.AsArray();
        Assert.Equal(16L * 1024 * 1024, slots[0]!["length"]!.GetValue<long>());
        Assert.True(slots[1]!["empty"]!.GetValue<bool>());

        // 6. 挿入モードでオフセット 0 に 1 番から貼ると、TD-RANDOM-16M と同じ 16 MiB。
        await app.KeyAsync("Insert");
        await app.GoToAsync(0);
        Assert.True(await SelectionTests.ExecuteAsync(app, "edit.userClipboard.paste1"));
        byte[] expected = File.ReadAllBytes(ctx.TestData("TD-RANDOM-16M"));
        foreach (long offset in new long[] { 0, 0x123456, expected.Length - 64 })
        {
            Assert.Equal(expected.AsSpan((int)offset, 64).ToArray(), await app.BytesAsync(offset, 64));
        }

        Assert.Equal(16L * 1024 * 1024 + 1024 * 1024, (await app.DocumentAsync())["length"]!.GetValue<long>());
    }, TimeSpan.FromMinutes(5));

    [Fact]
    [Trait(UiTest.TC, "TC-EDIT-28-03")]
    public Task Clipboard_history_keeps_the_last_twenty_copies_newest_first() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        for (int k = 1; k <= 21; k++)
        {
            await app.SelectAsync(k * 0x10, k);
            await app.KeyAsync("C", ctrl: true);
            await app.IdleAsync();
        }

        Assert.True(await SelectionTests.ExecuteAsync(app, "view.panel.clipboard"));
        JsonArray history = (await app.SendAsync("clipboardPanel"))["history"]!.AsArray();
        Assert.Equal(20, history.Count);
        Assert.Equal((0x150L, 21L), (history[0]!["offset"]!.GetValue<long>(), history[0]!["length"]!.GetValue<long>()));
        Assert.Equal("50 51 52 53 54 55 56 57 58 59 5A 5B 5C 5D 5E 5F", history[0]!["head"]!.GetValue<string>());
        Assert.Equal((0x20L, 2L), (history[^1]!["offset"]!.GetValue<long>(), history[^1]!["length"]!.GetValue<long>()));
    });

    // ---- ANA-18 の仕様 1 ----

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-18-04")]
    public Task Hash_per_range_and_concatenated_for_a_multi_selection() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        await app.SelectAsync(0, 0x100);
        await EditCommandTests.SelectRangeAsync(app, "0x1000", "0x10", add: true);
        await EditCommandTests.SelectRangeAsync(app, "0x2000", "1", add: true);
        Assert.Equal(3, (await app.DocumentAsync())["selectionCount"]!.GetValue<long>());

        byte[] data = File.ReadAllBytes(ctx.TestData("TD-SEQ-1M"));
        byte[][] parts = [data[..0x100], data[0x1000..0x1010], data[0x2000..0x2001]];
        static string Crc(byte[] bytes)
        {
            // CRC-32/ISO-HDLC (反射、多項式 0xEDB88320)。
            uint crc = 0xFFFFFFFF;
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
                }
            }

            return (~crc).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        }

        // 1. 範囲ごとに値を求める: 3 行。
        await app.CommandAsync("Command_Hash");
        await app.SendAsync("hash", new JsonObject { ["action"] = "algorithms", ["ids"] = new JsonArray("crc32") });

        // 計算方法の選択は、マルチ選択のときだけ出る。
        await app.WaitUntilAsync(async () => (await ElementAsync(app, "Hash_RangeMode"))["visibility"]?.GetValue<string>() == "Visible",
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the calculation method choice");
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Hash_RangeMode", ["index"] = 1 });
        await app.SendAsync("hash", new JsonObject { ["action"] = "compute" }, TimeSpan.FromSeconds(60));
        JsonArray rows = await WaitForHashRowsAsync(app, 3);
        Assert.Equal(parts.Select(Crc), rows.Select(r => r!["value"]!.GetValue<string>()));

        // 2. 連結して 1 つの値を求める: 1 行。
        await app.SendAsync("setSelectedIndex", new JsonObject { ["id"] = "Hash_RangeMode", ["index"] = 0 });
        await app.SendAsync("hash", new JsonObject { ["action"] = "compute" }, TimeSpan.FromSeconds(60));
        rows = await WaitForHashRowsAsync(app, 1);
        Assert.Equal(Crc([.. parts.SelectMany(p => p)]), rows[0]!["value"]!.GetValue<string>());
    });

    private static async Task<JsonArray> WaitForHashRowsAsync(AppSession app, int count)
    {
        JsonArray rows = [];
        await app.WaitUntilAsync(async () =>
        {
            JsonObject state = await app.SendAsync("hash", new JsonObject { ["action"] = "state" });
            rows = state["rows"]!.AsArray();
            return !state["computing"]!.GetValue<bool>() && rows.Count == count;
        }, UiTest.Scaled(TimeSpan.FromSeconds(30)), $"{count} hash rows");
        return rows;
    }
}
