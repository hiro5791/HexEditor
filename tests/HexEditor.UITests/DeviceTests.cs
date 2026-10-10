using System.Text;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// ディスク・プロセスを開く UI の結合テスト (ENG-29、ENG-32、ENG-33)。偽のデバイス・プロセス (fakeDevices / fakeProcesses) を使い、
/// 実機のディスク・プロセスには触れない。昇格もしない (開発中のビルドの経路で、補助プロセスは偽のアクセスに置き換える)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class DeviceTests
{
    private static JsonObject Hooks(UiTestContext ctx)
    {
        var devices = new JsonObject
        {
            ["disks"] = new JsonArray(new JsonObject
            {
                ["number"] = 0,
                ["model"] = "Fake Disk",
                ["serial"] = "DISK0",
                ["size"] = 64L * 1024 * 1024,
                ["sectorSize"] = 512,
                ["seed"] = 1,
                ["requiresAdmin"] = true,
            }),
            ["volumes"] = new JsonArray(new JsonObject
            {
                ["path"] = @"\\.\E:",
                ["driveLetter"] = "E:",
                ["fileSystem"] = "FAT32",
                ["disk"] = 0,
                ["offset"] = 1024 * 1024,
                ["size"] = 16 * 1024 * 1024,
                ["removableUsb"] = true,
                ["requiresAdmin"] = false,
            }),
        };
        var processes = new JsonObject
        {
            ["elevated"] = false,
            ["processes"] = new JsonArray(new JsonObject
            {
                ["pid"] = 4321,
                ["name"] = "TestTarget.exe",
                ["user"] = "tester",
                ["addressLimit"] = 0x200000,
                ["access"] = "Direct",
                ["currentUser"] = true,
                ["regions"] = new JsonArray(new JsonObject
                {
                    ["base"] = 0x10000,
                    ["size"] = 0x1000,
                    ["state"] = "Commit",
                    ["protect"] = 4,
                    ["type"] = "Private",
                    ["data"] = "4D5A9000",
                }),
                ["modules"] = new JsonArray(new JsonObject
                {
                    ["name"] = "TestTarget.exe",
                    ["baseAddress"] = 0x10000,
                    ["size"] = 0x1000,
                    ["path"] = @"C:\TestTarget.exe",
                }),
            }),
        };
        string devicePath = ctx.WriteFile("fake-devices.json", Encoding.UTF8.GetBytes(devices.ToJsonString()));
        string processPath = ctx.WriteFile("fake-processes.json", Encoding.UTF8.GetBytes(processes.ToJsonString()));
        return new JsonObject { ["fakeDevices"] = devicePath, ["fakeProcesses"] = processPath };
    }

    [Fact]
    [Trait("TC", "TC-ENG-29-01")]
    public Task Disk_list_marks_admin_only_items_and_usb_is_openable_without_a_helper() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx) });

        JsonObject list = await app.SendAsync("enumDevices");
        Assert.False(list["elevated"]!.GetValue<bool>());
        JsonObject disk = list["disks"]!.AsArray().Select(d => d!.AsObject()).Single(d => d["number"]!.GetValue<int>() == 0);
        Assert.True(disk["needsAdmin"]!.GetValue<bool>());
        JsonObject usb = list["volumes"]!.AsArray().Select(v => v!.AsObject()).Single(v => v["path"]!.GetValue<string>() == @"\\.\E:");
        Assert.False(usb["needsAdmin"]!.GetValue<bool>());

        // USB ストレージのボリュームは直接開ける (補助プロセスを使わない。ENG-29 の仕様 3 の 1)。
        JsonObject opened = await app.SendAsync("openDisk", new JsonObject { ["path"] = @"\\.\E:" });
        Assert.Equal("Direct", opened["route"]!.GetValue<string>());
        Assert.False(opened["helper"]!.GetValue<bool>());
    });

    [Fact]
    [Trait("TC", "TC-ENG-28-02")]
    public Task Physical_disk_opens_through_the_helper_route_and_shows_the_shield() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx) });

        // 物理ディスクは管理者権限が必要なため補助プロセスの経路で開く (開発中のビルドでは偽の昇格アクセスに置き換える)。
        JsonObject opened = await app.SendAsync("openDisk", new JsonObject { ["path"] = @"\\.\PhysicalDrive0" });
        Assert.Equal("Helper", opened["route"]!.GetValue<string>());
        Assert.Equal(64L * 1024 * 1024, opened["length"]!.GetValue<long>());
        Assert.True(opened["helper"]!.GetValue<bool>());
    });

    [Fact]
    [Trait("TC", "TC-ENG-32-01")]
    public Task A_same_user_process_opens_without_a_helper() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx) });

        JsonObject opened = await app.SendAsync("openProcess", new JsonObject { ["pid"] = 4321 });
        Assert.Equal("Direct", opened["route"]!.GetValue<string>());
        Assert.True(opened["modules"]!.GetValue<int>() >= 1);
    });

    [Fact]
    [Trait("TC", "TC-ENG-33-01")]
    public Task Opening_a_process_shows_the_memory_map_with_modules() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await ctx.StartAsync(new AppOptions { Hooks = Hooks(ctx) });
        await app.SendAsync("openProcess", new JsonObject { ["pid"] = 4321 });

        JsonObject map = await app.SendAsync("memoryMap");
        Assert.True(map["panelVisible"]!.GetValue<bool>());
        Assert.Contains(map["modules"]!.AsArray(), m => m!.GetValue<string>() == "TestTarget.exe");
        Assert.Contains(map["regions"]!.AsArray(), r => r!["access"]!.GetValue<string>() == "Readable");
    });
}
