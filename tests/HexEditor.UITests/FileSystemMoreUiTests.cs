using System.Text.Json.Nodes;
using HexEditor.TestData.FileSystemImages;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// LDM・LVM2 の論理ボリューム (FOR-07) と ISO 9660 / ext / APFS のブラウザ (FOR-12〜FOR-14) の UI。テスト用のイメージ (TD-FOR-*) はテストデータの
/// 生成ツールがコードで作ったもの (スパースファイル) で、実機のディスクには触れない。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class FileSystemMoreUiTests
{
    private static async Task<AppSession> OpenImageAsync(UiTestContext ctx, string id)
    {
        AppSession app = await ctx.StartAsync(new AppOptions());
        await app.SendAsync("fsOpenImage", new JsonObject { ["path"] = ctx.TestData(id), ["sectorSize"] = 512 });
        await app.WaitUntilAsync(async () => (await app.SendAsync("fsPartitions"))["done"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(20)),
            "the partition analysis");
        return app;
    }

    private static Task<JsonObject> BrowserAsync(AppSession app, JsonObject? args = null) =>
        app.SendAsync("fsBrowser", args, UiTest.Scaled(TimeSpan.FromSeconds(60)));

    private static string[] Names(JsonObject browser) => [.. browser["entries"]!.AsArray().Select(e => e!["name"]!.GetValue<string>())];

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-07-03")]
    public Task A_missing_member_disk_is_named_and_nothing_opens() => UiTestContext.RunAsync(async ctx =>
    {
        // 手順 1〜2: LDM のストライプの 1 台目だけを開き、論理ボリュームを開く。
        AppSession app = await OpenImageAsync(ctx, "TD-FOR-LDM-STRIPE-A");
        JsonObject shown = await app.SendAsync("fsLogicalVolumes", new JsonObject { ["action"] = "show" }, UiTest.Scaled(TimeSpan.FromSeconds(30)));
        JsonNode volume = Assert.Single(shown["volumes"]!.AsArray())!;
        Assert.Equal("Striped", volume["kind"]!.GetValue<string>());
        JsonObject result = await app.SendAsync("fsLogicalVolumes", new JsonObject { ["action"] = "open", ["name"] = "Volume1" });
        string guid = LogicalVolumeImages.LdmDiskB.ToString("D");
        Assert.Contains(guid, result["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(guid, result["status"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(result["opened"]);
        Assert.Equal(1, result["documents"]!.GetValue<int>());

        // 手順 3: LDM のディスクを閉じ、LVM2 の 1 つ目の PV だけを開いて、lv0 で同じことをする。
        await app.SendAsync("execute", new JsonObject { ["id"] = "file.close" });
        await app.SendAsync("fsOpenImage", new JsonObject { ["path"] = ctx.TestData("TD-FOR-LVM-LINEAR-A"), ["sectorSize"] = 512 });
        await app.SendAsync("fsLogicalVolumes", new JsonObject { ["action"] = "show" }, UiTest.Scaled(TimeSpan.FromSeconds(30)));
        result = await app.SendAsync("fsLogicalVolumes", new JsonObject { ["action"] = "open", ["name"] = "lv0" });
        Assert.True(result["message"] is not null, result.ToJsonString());
        Assert.Contains(LogicalVolumeImages.LvmUuidText(LogicalVolumeImages.LvmPvB), result["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(result["opened"]);
        Assert.Equal(1, result["documents"]!.GetValue<int>());
    });

    [Fact]
    public Task Both_ldm_disks_open_the_striped_volume_read_only() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenImageAsync(ctx, "TD-FOR-LDM-STRIPE-A");
        await app.SendAsync("fsOpenImage", new JsonObject { ["path"] = ctx.TestData("TD-FOR-LDM-STRIPE-B"), ["sectorSize"] = 512 });
        await app.SendAsync("fsLogicalVolumes", new JsonObject { ["action"] = "show" }, UiTest.Scaled(TimeSpan.FromSeconds(30)));
        JsonObject result = await app.SendAsync("fsLogicalVolumes", new JsonObject { ["action"] = "open", ["name"] = "Volume1" }, UiTest.Scaled(TimeSpan.FromSeconds(30)));
        JsonNode tab = result["opened"]!;
        Assert.Equal(FileSystemImageSet.LengthOf("TD-FOR-LDM-STRIPE-VOL"), tab["length"]!.GetValue<long>());
        Assert.True(tab["readOnly"]!.GetValue<bool>());
        byte[] head = await app.BytesAsync(3, 8);
        Assert.Equal("NTFS    "u8.ToArray(), head);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-14-01")]
    public Task Joliet_rock_ridge_and_basic_names_can_be_switched() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenImageAsync(ctx, "TD-FOR-ISO-JRR");
        JsonObject shown = await app.SendAsync("fsShow", null, UiTest.Scaled(TimeSpan.FromSeconds(30)));
        Assert.Equal("Iso9660", shown["kind"]!.GetValue<string>());

        // 手順 1: 既定は Joliet。
        JsonObject joliet = await BrowserAsync(app);
        JsonNode choice = joliet["choices"]!.AsArray().Single(c => c!["key"]!.GetValue<string>() == "NameKind")!;
        Assert.Equal(0, choice["selected"]!.GetValue<int>());
        Assert.Equal("Joliet", choice["options"]![0]!.GetValue<string>());
        Assert.Contains("日本語の名前.txt", Names(joliet));
        Assert.Contains("MixedCase_Long_Name.txt", Names(joliet));
        Assert.Contains("colon_name.txt", Names(joliet));

        // 手順 2: Rock Ridge の名前と PX のモード。
        JsonObject rr = await BrowserAsync(app, new JsonObject { ["choice"] = new JsonObject { ["key"] = "NameKind", ["index"] = 1 } });
        Assert.Contains("colon:name.txt", Names(rr));
        Assert.All(rr["entries"]!.AsArray(), e => Assert.Equal("-rw-r--r--", e!["attributes"]!.GetValue<string>()));

        // 手順 3: ISO 9660 の基本の名前。
        JsonObject basic = await BrowserAsync(app, new JsonObject { ["choice"] = new JsonObject { ["key"] = "NameKind", ["index"] = 2 } });
        Assert.Contains("MIXEDCAS.TXT;1", Names(basic));
        Assert.DoesNotContain("colon:name.txt", Names(basic));
    });

    [Fact]
    public Task Encrypted_apfs_volume_shows_no_file_list() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenImageAsync(ctx, "TD-FOR-APFS-ENC");
        JsonObject shown = await app.SendAsync("fsShow", null, UiTest.Scaled(TimeSpan.FromSeconds(30)));
        Assert.Equal("Apfs", shown["kind"]!.GetValue<string>());
        JsonObject root = await BrowserAsync(app);
        JsonNode volume = Assert.Single(root["entries"]!.AsArray())!;
        Assert.Equal("Secret", volume["name"]!.GetValue<string>());
        Assert.True(volume["encrypted"]!.GetValue<bool>());
        JsonObject inside = await BrowserAsync(app, new JsonObject { ["folder"] = "\\Secret" });
        Assert.Equal(0, inside["count"]!.GetValue<int>());
        Assert.False(string.IsNullOrEmpty(inside["status"]!.GetValue<string>()));
        Assert.Contains("Secret", root["warnings"]!.GetValue<string>(), StringComparison.Ordinal);
    });

    [Fact]
    public Task Ext_inode_can_be_shown_and_jumped_to() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenImageAsync(ctx, "TD-FOR-EXT4");
        await app.SendAsync("fsShow", null, UiTest.Scaled(TimeSpan.FromSeconds(30)));
        (_, ExtImageBuilder b) = FileSystemImageSet.Ext(ExtVariant.Ext4);
        int readme = b.Nodes.First(n => n.Name == "readme.txt").Inode;
        JsonObject shown = await BrowserAsync(app, new JsonObject { ["select"] = "readme.txt", ["action"] = "inode" });
        Assert.Equal(b.InodeOffset(readme), shown["cursor"]!.GetValue<long>());
        Assert.Contains(shown["menu"]!.AsArray(), m => m!["id"]!.GetValue<string>() == "Fs_Menu_Inode" && m["visible"]!.GetValue<bool>());

        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await app.SendAsync("execute", new JsonObject { ["id"] = "forensics.fs.goToInode", ["argument"] = "2" });
        await app.WaitUntilAsync(async () => (await BrowserAsync(app, new JsonObject { ["entries"] = false }))["cursor"]!.GetValue<long>() == b.InodeOffset(2),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the jump to inode 2");
    });
}
