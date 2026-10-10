using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HexEditor.TestData;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>ENG-24 ずらしながらのその場保存 (確認ダイアログ、中断の警告)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class ShiftSaveTests
{
    [Fact]
    [Trait(UiTest.TC, "TC-ENG-24-01")]
    public Task Shift_save_from_the_free_space_dialog_asks_every_time() => UiTestContext.RunAsync(async ctx =>
    {
        // 空き容量が足りない仮想ディスク (管理者の権限が要る) の代わりに、異常を再現する仕組みで空き容量を 20 MiB にする。安全な保存には
        // 16 MiB + 16 MiB が要るため足りず、ずらしながらの保存は伸びる 1 KiB と余裕の 16 MiB で足りる。
        string path = ctx.CopyTestData("TD-RANDOM-16M", "big.bin");
        byte[] original = File.ReadAllBytes(path);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [path], Hooks = new JsonObject { ["freeSpace"] = 20L << 20 } });
        await app.GoToAsync(0);
        await app.KeyAsync("Insert");
        await app.TypeAsync(string.Concat(Enumerable.Repeat("5A", 1024)));

        for (int round = 0; round < 2; round++)
        {
            // 1〜2. 空き容量不足のダイアログで「その場でずらしながら保存」。
            await app.KeyAsync("S", ctrl: true);
            await app.WaitForDialogAsync("SaveDialog");
            await app.InvokeDialogButtonAsync("Save in place by shifting data");

            // 3. 確認ダイアログの本文とボタン (実行のたびに出る)。
            var dialog = await app.WaitForDialogAsync("ShiftSaveDialog");
            string text = await app.WaitForDialogTextAsync(dialog, "will be rewritten in place", "damaged and can't be restored", "Amount to rewrite",
                "Temporary space needed");
            Assert.NotNull(app.Button("Save in place"));
            Assert.NotNull(app.Button("Cancel"));
            _ = text;
            await app.InvokeDialogButtonAsync("Save in place");
            await app.WaitUntilAsync(async () => !(await app.DocumentAsync())["modified"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(60)), "the save");
            if (round == 0)
            {
                byte[] saved = File.ReadAllBytes(path);
                Assert.Equal(original.Length + 1024, saved.Length);
                Assert.All(saved[..1024], b => Assert.Equal(0x5A, b));
                Assert.Equal(SHA256.HashData(original), SHA256.HashData(saved[1024..]));

                // 5. もう 1 バイト挿入して同じ方法で保存する。
                await app.GoToAsync(5000);
                await app.TypeAsync("01");
            }
        }
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-24-03")]
    [Trait("Category", "Nightly")]
    public Task Killed_shift_save_is_reported_at_startup() => UiTestContext.RunAsync(async ctx =>
    {
        string profile = ctx.NewProfile();
        string path = ctx.CopyTestData("TD-MARKERS-1G", "markers.bin");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), "{\"$schemaVersion\": 1, \"save.shiftInPlace\": true}");
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Profile = profile,
            Files = [path],
            Hooks = new JsonObject { ["killAt"] = "shiftWrite", ["killAtBytes"] = 512L << 20 },
        });

        // 1. 先頭に 1 バイト挿入し、Ctrl+S で「その場で保存」を選ぶ。2. プロセスの終了を待つ。
        await app.KeyAsync("Insert");
        await app.TypeAsync("00");
        await app.KeyAsync("S", ctrl: true);
        await app.WaitForDialogAsync("ShiftSaveDialog");
        try
        {
            await app.InvokeDialogButtonAsync("Save in place", idle: false);
        }
        catch (IOException)
        {
        }

        await app.WaitForExitAsync(TimeSpan.FromMinutes(5));
        long lengthAfterKill = new FileInfo(path).Length;

        // 3〜4. 起動すると、復旧の画面に中断の警告が出る。ファイルは自動では直さない。
        AppSession again = await ctx.StartAsync(new AppOptions { Profile = profile, WaitForEditor = false });
        var dialog = await again.WaitForDialogAsync("RecoveryDialog");
        await again.WaitForDialogTextAsync(dialog, "Saving markers.bin in place was interrupted last time", "may be damaged");
        Assert.Equal(lengthAfterKill, new FileInfo(path).Length);
    });
}
