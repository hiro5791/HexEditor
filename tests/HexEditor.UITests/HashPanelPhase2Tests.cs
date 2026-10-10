using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;
using static HexEditor.UITests.Infrastructure.ViewOps;

namespace HexEditor.UITests;

/// <summary>
/// ハッシュパネルのフェーズ 2 の機能: マルチ選択の範囲ごとの値 (ANA-18)、パラメータと絞り込み (ANA-19)、カスタム CRC (ANA-20)、
/// カーソル位置に書き込む (ANA-22)。パネルの状態はテスト用の命令 "hash" で読む。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class HashPanelPhase2Tests
{
    private static Task<JsonObject> HashAsync(AppSession app, string action = "state", JsonObject? args = null)
    {
        JsonObject request = args ?? [];
        request["action"] = action;
        return app.SendAsync("hash", request, TimeSpan.FromSeconds(60));
    }

    private static Task SelectAlgorithmsAsync(AppSession app, params string[] ids) =>
        HashAsync(app, "algorithms", new JsonObject { ["ids"] = new JsonArray([.. ids.Select(id => (JsonNode?)id)]) });

    private static async Task<JsonArray> ComputeAsync(AppSession app, int count)
    {
        // パネルを開いたときの自動の計算が終わってから計算する (後から始まった自動の計算に結果を置き換えられないように)。
        await app.IdleAsync();
        await HashAsync(app, "compute");
        JsonArray rows = [];
        JsonObject? last = null;
        try
        {
            await app.WaitUntilAsync(async () =>
            {
                JsonObject state = last = await HashAsync(app);
                rows = state["rows"]!.AsArray();
                return !state["computing"]!.GetValue<bool>() && rows.Count == count;
            }, UiTest.Scaled(TimeSpan.FromSeconds(30)), $"{count} hash results");
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message} Last state: {last?.ToJsonString()}", ex);
        }

        return rows;
    }

    private static string ValueOf(JsonArray rows, string id) =>
        rows.First(r => r!["id"]!.GetValue<string>() == id)!["value"]!.GetValue<string>();

    private static async Task<string> TextAsync(AppSession app, string id)
    {
        JsonObject e = null!;
        await app.WaitUntilAsync(async () => (e = await ElementAsync(app, id))["found"]!.GetValue<bool>(), TimeSpan.FromSeconds(10), id);
        return e["text"]?.GetValue<string>() ?? e["content"]?.GetValue<string>() ?? string.Empty;
    }

    /// <summary>CRC-32 (ISO-HDLC) の基準の実装。</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return ~crc;
    }

    // ---- ANA-18 ----

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-18-04")]
    public Task Multi_selection_gives_one_row_per_range_or_one_concatenated_value() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-SEQ-1M")] });
        (long Start, long Length)[] ranges = [(0, 0x100), (0x1000, 0x10), (0x2000, 1)];
        await app.SendAsync("multiSelection", new JsonObject
        {
            ["ranges"] = new JsonArray([.. ranges.Select(r => (JsonNode?)new JsonArray(r.Start, r.Length))]),
        });
        await HashAsync(app, "show");
        await SelectAlgorithmsAsync(app, "crc32");
        await HashAsync(app, "rangeMode", new JsonObject { ["index"] = 1 });
        Assert.True((await HashAsync(app))["multiRange"]!.GetValue<bool>());

        // 1. 範囲ごと: 3 行。各行はその範囲だけの CRC-32。
        JsonArray rows = await ComputeAsync(app, 3);
        var all = new List<byte>();
        for (int i = 0; i < ranges.Length; i++)
        {
            byte[] bytes = await app.BytesAsync(ranges[i].Start, ranges[i].Length);
            all.AddRange(bytes);
            Assert.Equal(Crc32(bytes).ToString("X8"), ValueOf(rows, $"crc32@{i + 1}"));
        }

        // 2. 連結: 1 行で、オフセット順に連結したバイト列の CRC-32。
        await HashAsync(app, "rangeMode", new JsonObject { ["index"] = 0 });
        rows = await ComputeAsync(app, 1);
        Assert.Equal(Crc32([.. all]).ToString("X8"), ValueOf(rows, "crc32"));
    });

    // ---- ANA-19 ----

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-19-07")]
    public Task Changing_the_xxhash64_seed_changes_the_value_and_the_row_name() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-CHECK9")] });
        await HashAsync(app, "show");
        await SelectAlgorithmsAsync(app, "xxh64");
        JsonArray rows = await ComputeAsync(app, 1);
        string seed0 = ValueOf(rows, "xxh64");

        await HashAsync(app, "setParameters", new JsonObject { ["id"] = "xxh64", ["seed"] = 0x1234 });
        await app.WaitUntilAsync(async () => (await HashAsync(app))["rows"]!.AsArray()
            .Any(r => r!["name"]!.GetValue<string>() == "xxHash64 (seed=0x1234)"), UiTest.Scaled(TimeSpan.FromSeconds(30)), "the seeded row");
        rows = await ComputeAsync(app, 1);
        Assert.Equal("xxHash64 (seed=0x1234)", rows[0]!["name"]!.GetValue<string>());
        Assert.NotEqual(seed0, ValueOf(rows, "xxh64"));
    });

    [Theory]
    [Trait(UiTest.TC, "TC-ANA-19-10")]
    [InlineData(null)]
    [InlineData("tr-TR")]
    public Task Filtering_the_algorithm_list_by_alias_is_culture_independent(string? culture) => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [ctx.TestData("TD-ANA-CHECK9")],
            Hooks = culture is null ? null : new JsonObject { ["culture"] = culture },
        });
        await HashAsync(app, "show");
        await HashAsync(app, "filter", new JsonObject { ["text"] = "ccitt-false" });
        Assert.Equal(["crc16-ibm3740"], (await HashAsync(app))["visible"]!.AsArray().Select(n => n!.GetValue<string>()));
        await HashAsync(app, "filter", new JsonObject { ["text"] = "crc-32/iscsi" });
        Assert.Contains("crc32c", (await HashAsync(app))["visible"]!.AsArray().Select(n => n!.GetValue<string>()));
    });

    // ---- ANA-20 ----

    private static async Task DefineMyCrc16Async(AppSession app, bool save)
    {
        await app.CommandAsync("Command_CustomCrc");
        await app.WaitForAsync("CustomCrcDialog");
        await app.IdleAsync();
        await app.UiaSetValueAsync("CustomCrc_Name", "MyCRC16");
        await app.UiaSetValueAsync("CustomCrc_Width", "16");
        await app.UiaSetValueAsync("CustomCrc_Poly", "0x8005");
        await app.UiaSetValueAsync("CustomCrc_Init", "0");
        await app.SendAsync("setChecked", new JsonObject { ["id"] = "CustomCrc_RefIn", ["value"] = true });
        await app.SendAsync("setChecked", new JsonObject { ["id"] = "CustomCrc_RefOut", ["value"] = true });
        await app.UiaSetValueAsync("CustomCrc_XorOut", "0");
        await app.IdleAsync();
        if (save)
        {
            await EditCommandTests.PressAsync(app);
        }
    }

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-20-01")]
    public Task Custom_crc_with_the_arc_parameters_shows_check_bb3d() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-CHECK9")] });
        await DefineMyCrc16Async(app, save: false);
        Assert.Equal("check: BB3D", await TextAsync(app, "CustomCrc_Check"));
        Assert.Equal("residue: 0000", await TextAsync(app, "CustomCrc_Residue"));
        Assert.True((await app.WaitForAsync("PrimaryButton")).IsEnabled);

        await app.UiaSetValueAsync("CustomCrc_Poly", "0x18005");
        await app.IdleAsync();
        Assert.True((await ElementAsync(app, "CustomCrc_Poly"))["errorBorder"]!.GetValue<bool>());
        Assert.Contains("16 bits", await TextAsync(app, "CustomCrc_Error"), StringComparison.Ordinal);
        Assert.False((await app.WaitForAsync("PrimaryButton")).IsEnabled);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-20-02")]
    public Task Custom_crc_is_still_listed_after_a_restart() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-CHECK9")], Profile = profile });
        await DefineMyCrc16Async(app, save: true);
        await ViewSettingsOps.ExitAsync(app);

        app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-CHECK9")], Profile = profile });
        await HashAsync(app, "show");
        await HashAsync(app, "filter", new JsonObject { ["text"] = "MyCRC16" });
        Assert.Equal(["custom:MyCRC16"], (await HashAsync(app))["visible"]!.AsArray().Select(n => n!.GetValue<string>()));
        await SelectAlgorithmsAsync(app, "custom:MyCRC16");
        JsonArray rows = await ComputeAsync(app, 1);
        Assert.Equal("BB3D", ValueOf(rows, "custom:MyCRC16"));
    });

    // ---- ANA-22 ----

    private static async Task<JsonArray> ComputeCustomRangeAsync(AppSession app, string start, string length, int count = 1)
    {
        await HashAsync(app, "custom", new JsonObject { ["start"] = start, ["length"] = length });
        return await ComputeAsync(app, count);
    }

    private static async Task OpenWriteDialogAsync(AppSession app)
    {
        await HashAsync(app, "write");
        await app.WaitForAsync("HashWriteDialog");
        await app.IdleAsync();
    }

    private static async Task<bool> WarningShownAsync(AppSession app)
    {
        JsonObject e = await ElementAsync(app, "HashWrite_Warning");
        return e["found"]!.GetValue<bool>() && e["visibility"]!.GetValue<string>() == "Visible";
    }

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-22-03")]
    public Task Writing_crc32_little_endian_at_the_cursor_overwrites_and_undoes() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-CHECK9-PAD")] });
        await HashAsync(app, "show");
        await SelectAlgorithmsAsync(app, "crc32");
        JsonArray rows = await ComputeCustomRangeAsync(app, "0", "9");
        Assert.Equal("CBF43926", ValueOf(rows, "crc32"));
        await app.GoToAsync(0x10);
        if (!(await app.DocumentAsync())["insertMode"]!.GetValue<bool>())
        {
            await app.KeyAsync("Insert");
        }

        await OpenWriteDialogAsync(app);
        await app.SendAsync("setChecked", new JsonObject { ["id"] = "HashWrite_LittleEndian", ["value"] = true });
        await app.IdleAsync();
        await EditCommandTests.PressAsync(app);
        Assert.Equal(new byte[] { 0x26, 0x39, 0xF4, 0xCB }, await app.BytesAsync(0x10, 4));
        Assert.Equal(20, (await app.DocumentAsync())["length"]!.GetValue<long>());

        await app.KeyAsync("Z", ctrl: true);
        await app.IdleAsync();
        Assert.Equal(new byte[4], await app.BytesAsync(0x10, 4));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ANA-22-04")]
    public Task Warning_only_when_the_destination_is_in_the_calculated_range() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [ctx.TestData("TD-ANA-CHECK9-PAD")] });
        await HashAsync(app, "show");
        await SelectAlgorithmsAsync(app, "crc32");

        // 1. 全体を対象に計算: 書き込み先 0x10 は対象範囲の中 → 警告。
        await ComputeCustomRangeAsync(app, "0", "0x14");
        await app.GoToAsync(0x10);
        await OpenWriteDialogAsync(app);
        Assert.True(await WarningShownAsync(app));
        await EditCommandTests.PressAsync(app, "CloseButton");

        // 2. 0x0〜0x8 を対象に計算し直す: 警告なし。
        await ComputeCustomRangeAsync(app, "0", "9");
        await app.GoToAsync(0x10);
        await OpenWriteDialogAsync(app);
        Assert.False(await WarningShownAsync(app));
        await EditCommandTests.PressAsync(app, "CloseButton");

        // 3. 全体を対象に、除外範囲 0x10 から 4 バイトを指定: 警告なし。
        await HashAsync(app, "exclusion", new JsonObject { ["start"] = "0x10", ["length"] = "4" });
        await ComputeCustomRangeAsync(app, "0", "0x14");
        await app.GoToAsync(0x10);
        await OpenWriteDialogAsync(app);
        Assert.False(await WarningShownAsync(app));
        await EditCommandTests.PressAsync(app, "CloseButton");
    });
}
