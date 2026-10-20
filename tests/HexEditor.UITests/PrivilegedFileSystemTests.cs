using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HexEditor.TestData.FileSystemImages;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// ファイルシステムの解析と Windows の比較 (12 の TC-FOR-09-01、TC-FOR-09-03、TC-FOR-10-05)。CI の管理者権限のランナーでだけ、Windows で
/// フォーマットした固定サイズの VHD に TD-FOR-FILESET を OS のファイル API で書き込み、OS の一覧 (パス・サイズ・更新日時・SHA-256) を記録してから
/// 切り離し、アプリでその VHD をディスクイメージとして開いて同じ一覧と内容になるかを確かめる。この PC では仮想ディスクを作らない。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class PrivilegedFileSystemTests
{
    private sealed record OsFile(string Path, long Size, DateTime ModifiedUtc, string Sha256);

    private static async Task<IReadOnlyList<OsFile>> CreateVhdAsync(UiTestContext ctx, string fileSystem, string vhd)
    {
        CiPrivileged.EnsureEnabled();
        string source = Path.Combine(ctx.Root, "fileset-" + fileSystem);
        Directory.CreateDirectory(source);
        foreach (FileSetItem item in FileSet.Common.Where(i => i.Name != "deleted.txt"))
        {
            string path = Path.Combine(source, item.Name);
            File.WriteAllBytes(path, item.Content);
            File.SetLastWriteTimeUtc(path, item.ModifiedUtc);
        }

        string streams = string.Empty, compress = string.Empty;
        if (fileSystem == "NTFS")
        {
            File.WriteAllBytes(Path.Combine(source, "file.txt"), FileSet.FileTxt);
            File.WriteAllBytes(Path.Combine(source, "compressed.bin"), FileSet.Compressed);
            streams = "file.txt:secret=TOP SECRET DATA\r\n";
            compress = "compressed.bin";
        }

        string listing = Path.Combine(ctx.Root, fileSystem + ".json");
        string script = Path.Combine(AppLocator.RepositoryRoot, "build", "tests", "New-FileSystemVhd.ps1");
        var start = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Path", vhd, "-FileSystem", fileSystem, "-Source", source,
                "-Listing", listing, "-Streams", streams, "-Compress", compress },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using Process process = Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, output);
        return [.. JsonNode.Parse(File.ReadAllText(listing))!.AsArray().Select(n => new OsFile(n!["path"]!.GetValue<string>(), n["size"]!.GetValue<long>(),
            new DateTime(n["modifiedUtcTicks"]!.GetValue<long>(), DateTimeKind.Utc), n["sha256"]!.GetValue<string>()))];
    }

    private static async Task<(AppSession App, JsonObject Root)> OpenAsync(UiTestContext ctx, string vhd)
    {
        AppSession app = await ctx.StartAsync(new AppOptions());
        await app.SendAsync("fsOpenImage", new JsonObject { ["path"] = vhd, ["sectorSize"] = 512 });
        await app.WaitUntilAsync(async () => (await app.SendAsync("fsPartitions"))["done"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(30)), "partitions");
        await app.SendAsync("fsPartitionAction", new JsonObject { ["number"] = 1, ["action"] = "showFileSystem" }, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        JsonObject root = await app.SendAsync("fsBrowser", null, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        return (app, root);
    }

    [CiPrivilegedFact]
    [Trait(UiTest.TC, "TC-FOR-09-01")]
    public Task File_lists_match_windows_for_ntfs_fat32_and_exfat() => UiTestContext.RunAsync(async ctx =>
    {
        foreach (string fs in new[] { "NTFS", "FAT32", "exFAT" })
        {
            string vhd = Path.Combine(ctx.Root, fs + ".vhd");
            IReadOnlyList<OsFile> os = await CreateVhdAsync(ctx, fs, vhd);
            (AppSession app, JsonObject root) = await OpenAsync(ctx, vhd);
            JsonNode[] entries = [.. root["entries"]!.AsArray().Where(e => !e!["directory"]!.GetValue<bool>() && !e["name"]!.GetValue<string>().Contains(':'))!];
            foreach (OsFile file in os.Where(f => f.Path.LastIndexOf('\\') == 0))
            {
                JsonNode e = Assert.Single(entries, x => x!["path"]!.GetValue<string>() == file.Path)!;
                Assert.Equal(file.Size, e["size"]!.GetValue<long>());
                DateTime modified = DateTime.Parse(e["modified"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
                Assert.True(Math.Abs((modified - file.ModifiedUtc).TotalSeconds) <= (fs == "NTFS" ? 0.001 : 2), $"{fs} {file.Path}: {modified:o} / {file.ModifiedUtc:o}");
            }

            await app.DisposeAsync();
            File.Delete(vhd);
        }
    }, TimeSpan.FromMinutes(10));

    [CiPrivilegedFact]
    [Trait(UiTest.TC, "TC-FOR-09-03")]
    [Trait(UiTest.TC, "TC-FOR-10-05")]
    public Task File_contents_opened_in_tabs_match_windows() => UiTestContext.RunAsync(async ctx =>
    {
        foreach (string fs in new[] { "NTFS", "FAT32", "exFAT" })
        {
            string vhd = Path.Combine(ctx.Root, fs + ".vhd");
            IReadOnlyList<OsFile> os = await CreateVhdAsync(ctx, fs, vhd);
            (AppSession app, _) = await OpenAsync(ctx, vhd);
            foreach (OsFile file in os.Where(f => f.Path.LastIndexOf('\\') == 0))
            {
                await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
                JsonObject opened = await app.SendAsync("fsBrowser", new JsonObject { ["select"] = file.Path.TrimStart('\\'), ["action"] = "openFile", ["entries"] = false },
                    UiTest.Scaled(TimeSpan.FromSeconds(60)));
                JsonNode tab = opened["opened"]!;
                Assert.True(tab["readOnly"]!.GetValue<bool>());
                Assert.EndsWith(")", tab["name"]!.GetValue<string>(), StringComparison.Ordinal);
                byte[] content = await app.BytesAsync(0, tab["length"]!.GetValue<long>());
                Assert.Equal(file.Sha256, Convert.ToHexStringLower(SHA256.HashData(content)));
                if (file.Path == "\\compressed.bin")
                {
                    // TC-FOR-10-05: 1 MiB で、断片の合計 (圧縮されたままの位置) は 1 MiB より小さい。
                    Assert.Equal(1 << 20, content.Length);
                    await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
                    JsonObject details = await app.SendAsync("fsBrowser", new JsonObject { ["select"] = "compressed.bin", ["action"] = "showClusters", ["entries"] = false });
                    _ = details;
                    long total = (await app.SendAsync("fsFragments"))["fragments"]!.AsArray().Sum(f => f!["length"]!.GetValue<long>());
                    Assert.True(total < 1 << 20, $"fragments {total}");
                }
            }

            await app.DisposeAsync();
            File.Delete(vhd);
        }
    }, TimeSpan.FromMinutes(10));
}
