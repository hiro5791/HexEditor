using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>保存の前の確認: ハードリンク (ENG-22 の仕様 4) と空き容量不足 (ENG-25)。</summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed partial class SaveDialogTests
{
    [Fact]
    [Trait(UiTest.TC, "TC-ENG-22-05")]
    public Task Saving_a_file_with_hard_links_asks_first() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.CopyTestData("TD-SEQ-1M", "a.bin");
        string link = Path.Combine(ctx.Root, "link.bin");
        Assert.True(CreateHardLink(link, a, 0), "cannot create a hard link");
        byte[] original = await File.ReadAllBytesAsync(a);
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a] });

        // 前提: オフセット 0 に 1 バイト挿入する (長さが変わるので安全な保存になる)。
        await app.KeyAsync("Insert");
        await app.TypeAsync("00");

        // 1〜2. 確認のダイアログの本文とボタン。長さが変わる場合の「その場で保存」(ENG-24) はフェーズ 2 のため出ない。
        await app.KeyAsync("S", ctrl: true);
        var dialog = await app.WaitForDialogAsync("SaveDialog");
        await app.WaitForDialogTextAsync(dialog, "This file has 2 hard links. Saving safely breaks the links");
        Assert.NotNull(app.Button("Save safely (break the links)"));
        Assert.NotNull(app.Button("Cancel"));
        Assert.Null(app.Button("Save in place (keep the links; the file may be damaged if saving is interrupted)"));

        // 3〜4. 「安全に保存 (リンクを切る)」: a.bin は保存後の内容、link.bin は元の内容のまま (リンクが切れた)。
        await app.InvokeDialogButtonAsync("Save safely (break the links)");
        await app.WaitUntilAsync(async () => !(await app.DocumentAsync())["modified"]!.GetValue<bool>(), TimeSpan.FromSeconds(30), "the save");
        Assert.Equal([0x00, .. original], await File.ReadAllBytesAsync(a));
        Assert.Equal(original, await File.ReadAllBytesAsync(link));
    });

    [Fact]
    public Task Cancelling_the_hard_link_dialog_does_not_save() => UiTestContext.RunAsync(async ctx =>
    {
        string a = ctx.CopyTestData("TD-SEQ-1M", "a.bin");
        string link = Path.Combine(ctx.Root, "link.bin");
        Assert.True(CreateHardLink(link, a, 0), "cannot create a hard link");
        string before = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(a)));
        AppSession app = await ctx.StartAsync(new AppOptions { Files = [a] });
        await app.KeyAsync("Insert");
        await app.TypeAsync("00");
        await app.KeyAsync("S", ctrl: true);
        await app.WaitForDialogAsync("SaveDialog");
        await app.InvokeDialogButtonAsync("Cancel");
        Assert.True((await app.DocumentAsync())["modified"]!.GetValue<bool>());
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(link))));
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(a))));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-ENG-25-01")]
    public Task Not_enough_free_space_dialog_and_save_elsewhere() => UiTestContext.RunAsync(async ctx =>
    {
        // 空き容量 1 GiB の仮想ディスク (TD-ENG-VHDX-3G。管理者の権限が要る) の代わりに、異常を再現する仕組みで保存先の
        // 空き容量を 1 MiB にし、1 MiB のファイルに 1 バイト挿入して保存する (必要: 1 MiB + 1 バイト + 16 MiB)。
        string path = ctx.CopyTestData("TD-SEQ-1M", "data.bin");
        DateTime written = File.GetLastWriteTimeUtc(path);
        string before = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        AppSession app = await ctx.StartAsync(new AppOptions
        {
            Files = [path],
            Hooks = new JsonObject { ["freeSpace"] = 1L << 20, ["savePicker"] = string.Empty },
        });
        await app.KeyAsync("Insert");
        await app.TypeAsync("00");

        // 1〜2. ダイアログの本文 (ドライブ・必要量・空き容量を単位付きで) とボタン。「その場でずらしながら保存」は
        // ずらしながらのその場保存 (ENG-24、フェーズ 2) と同時に加える。
        await app.KeyAsync("S", ctrl: true);
        var dialog = await app.WaitForDialogAsync("SaveDialog");
        string text = await app.WaitForDialogTextAsync(dialog, "Available: 1.00 MB (1,048,576 bytes)");
        string drive = Path.GetPathRoot(path)!.TrimEnd('\\');
        Assert.Contains($"There isn't enough free space on the destination drive ({drive}", text, StringComparison.Ordinal);
        Assert.Contains("Required: 17.00 MB (17,825,793 bytes)", text, StringComparison.Ordinal);
        Assert.Contains("Available: 1.00 MB (1,048,576 bytes)", text, StringComparison.Ordinal);
        Assert.NotNull(app.Button("Save to another location"));
        Assert.NotNull(app.Button("Cancel"));

        // 3. ファイルは変わらず、一時ファイルもない。
        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Empty(Directory.GetFiles(ctx.Root, "*.tmp"));

        // 4. 「別の場所に保存」で「名前を付けて保存」のダイアログ (の差し替え) が開く。
        await app.InvokeDialogButtonAsync("Save to another location");
        await app.WaitForLogAsync(l => l.Contains("Test hooks: save picker (data.bin)", StringComparison.Ordinal), "the save as dialog");
        Assert.True((await app.DocumentAsync())["modified"]!.GetValue<bool>());
    });

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string link, string existing, nint securityAttributes);
}
