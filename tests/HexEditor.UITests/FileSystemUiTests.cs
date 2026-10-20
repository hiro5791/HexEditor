using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.TestData.FileSystemImages;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// パーティションとファイルシステムの UI (12 の FOR-06・FOR-08・FOR-09・FOR-10・FOR-11・FOR-15・FOR-17)。テスト用のイメージ (TD-FOR-*) は
/// テストデータの生成ツールがコードで作ったもの (スパースファイル) で、実機のディスクには触れない。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class FileSystemUiTests
{
    private static async Task<AppSession> OpenImageAsync(UiTestContext ctx, string id, int sectorSize = 512, bool copy = false, bool readOnly = true)
    {
        AppSession app = await ctx.StartAsync(new AppOptions());
        string path = copy ? ctx.CopyTestData(id) : ctx.TestData(id);
        await app.SendAsync("fsOpenImage", new JsonObject { ["path"] = path, ["sectorSize"] = sectorSize, ["readOnly"] = readOnly });
        return app;
    }

    private static async Task<JsonObject> PartitionsAsync(AppSession app)
    {
        JsonObject result = null!;
        await app.WaitUntilAsync(async () => (result = await app.SendAsync("fsPartitions"))["done"]!.GetValue<bool>(),
            UiTest.Scaled(TimeSpan.FromSeconds(20)), "the partition analysis");
        return result;
    }

    private static async Task<AppSession> OpenFileSystemAsync(UiTestContext ctx, string id)
    {
        AppSession app = await OpenImageAsync(ctx, id);
        JsonObject shown = await app.SendAsync("fsShow", null, UiTest.Scaled(TimeSpan.FromSeconds(30)));
        Assert.True(shown["opened"]!.GetValue<bool>(), shown.ToJsonString());
        return app;
    }

    private static Task<JsonObject> BrowserAsync(AppSession app, JsonObject? args = null) =>
        app.SendAsync("fsBrowser", args, UiTest.Scaled(TimeSpan.FromSeconds(60)));

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-06-05")]
    public Task A_4k_gpt_opened_with_512_byte_sectors_suggests_4096() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenImageAsync(ctx, "TD-FOR-GPT-4K", 512);
        await PartitionsAsync(app);

        // 手順 2: セクタサイズを 4096 に変えることを提案する InfoBar。
        await app.WaitUntilAsync(async () => (await app.StateAsync())["notifications"]!.AsArray()
            .Any(n => n!["message"]!.GetValue<string>().Contains("4096", StringComparison.Ordinal)), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the suggestion");
        JsonNode notice = (await app.StateAsync())["notifications"]!.AsArray().First(n => n!["message"]!.GetValue<string>().Contains("4096", StringComparison.Ordinal))!;
        Assert.Contains("4096", notice.ToJsonString(), StringComparison.Ordinal);

        // 手順 3: 提案に従ってセクタサイズを変えると、定義どおりのパーティション。
        JsonObject reopened = await app.SendAsync("fsReopenSector", new JsonObject { ["sectorSize"] = 4096 });
        Assert.Equal(4096, reopened["sectorSize"]!.GetValue<int>());
        JsonObject parts = await PartitionsAsync(app);
        JsonNode[] gpt = [.. parts["rows"]!.AsArray().Where(r => r!["scheme"]!.GetValue<string>() == "Gpt")!];
        Assert.Equal(PartitionImages.Gpt4KParts.Select(p => (p.First, p.Last)),
            gpt.Select(r => (r!["startLba"]!.GetValue<long>(), r["endLba"]!.GetValue<long>())));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-06-07")]
    public Task A_partition_tab_starts_at_the_partition_offset() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenImageAsync(ctx, "TD-FOR-GPT");
        JsonObject parts = await PartitionsAsync(app);
        Assert.True(parts["panelVisible"]!.GetValue<bool>());
        string disk = (await app.SendAsync("fsTab"))["name"]!.GetValue<string>();
        bool diskReadOnly = (await app.SendAsync("fsTab"))["readOnly"]!.GetValue<bool>();

        JsonObject tab = await app.SendAsync("fsPartitionAction", new JsonObject { ["number"] = 2, ["action"] = "openInNewTab" });
        Assert.Equal($"{disk} - Partition 2", tab["name"]!.GetValue<string>());
        Assert.Equal((36863L - 4096 + 1) * 512, tab["length"]!.GetValue<long>());
        Assert.Equal(0, tab["baseAddress"]!.GetValue<long>());
        Assert.Equal(diskReadOnly, tab["readOnly"]!.GetValue<bool>());
        byte[] partition = await app.BytesAsync(0, 512);
        JsonObject render = await app.RenderAsync();
        Assert.Equal(0, Convert.ToInt64(render["rows"]![0]!["offsetText"]!.GetValue<string>().Trim(), 16));

        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        byte[] onDisk = await app.BytesAsync(4096L * 512, 512);
        Assert.Equal(onDisk, partition);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-08-03")]
    public Task Scanning_a_huge_image_keeps_the_ui_responsive_and_can_be_cancelled() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenImageAsync(ctx, "TD-SPARSE-100G");
        await PartitionsAsync(app);
        using TestChannelClient monitor = (await TestChannelClient.ConnectAsync(app.Pid, () => app.Process.HasExited, TimeSpan.FromSeconds(10)))!;
        using var stop = new CancellationTokenSource();
        double worst = 0;
        Task watch = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                await monitor.SendAsync("ping");
                worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
                await Task.Delay(10);
            }
        });

        await app.SendAsync("fsScan", new JsonObject { ["intervals"] = "EverySector", ["wait"] = false });
        var until = Stopwatch.StartNew();
        while (until.Elapsed < TimeSpan.FromSeconds(10))
        {
            await app.KeyAsync("PageDown");
            await Task.Delay(50);
        }

        JsonObject state = await app.StateAsync();
        JsonNode op = state["operations"]!.AsArray().First(o => o!["state"]!.GetValue<string>() == "Running")!;
        Assert.True(op["processed"]!.GetValue<long>() > 0);
        Assert.NotNull(op["total"]);
        await app.SendAsync("fsCancel");
        await app.WaitUntilAsync(async () => (await app.StateAsync())["operations"]!.AsArray().All(o => o!["state"]!.GetValue<string>() != "Running"),
            UiTest.Scaled(TimeSpan.FromSeconds(10)), "the cancel");
        await stop.CancelAsync();
        await watch;
        JsonNode cancelled = (await app.StateAsync())["operations"]!.AsArray().First(o => o!["cancelToEndMs"] is not null)!;
        TimeBudget.Limit(cancelled["cancelToEndMs"]!.GetValue<double>() <= 200, $"stopped {cancelled["cancelToEndMs"]} ms after the cancel");
        TimeBudget.Limit(worst < 100, $"UI thread blocked for {worst:F0} ms");
        Assert.True(worst < 1500, $"UI thread blocked for {worst:F0} ms");
        JsonObject results = await app.SendAsync("fsResults");
        Assert.Equal("LostPartitions", results["kind"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(results["status"]?.GetValue<string>()));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-09-02")]
    public Task Double_click_selects_all_fragments_and_jumps_to_the_first() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenFileSystemAsync(ctx, "TD-FOR-NTFS");
        IReadOnlyList<(long Offset, long Length)> fragments = FileSystemImageSet.Ntfs().Fragments("\\frag.bin");
        JsonObject result = await BrowserAsync(app, new JsonObject { ["select"] = "frag.bin", ["action"] = "activate" });
        Assert.Equal(fragments[0].Offset, result["cursor"]!.GetValue<long>());
        JsonObject f = await app.SendAsync("fsFragments");
        Assert.Equal(fragments, f["selections"]!.AsArray().Select(s => (s!["start"]!.GetValue<long>(), s["length"]!.GetValue<long>())));
        Assert.Equal(3, result["fragments"]!.AsArray().Count);
    });

    [PerfEnvironmentFact]
    [Trait(UiTest.TC, "TC-FOR-09-05")]
    public Task A_folder_with_a_million_files_opens_without_freezing() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenFileSystemAsync(ctx, "TD-FOR-NTFS-1M");
        using TestChannelClient monitor = (await TestChannelClient.ConnectAsync(app.Pid, () => app.Process.HasExited, TimeSpan.FromSeconds(10)))!;
        using var stop = new CancellationTokenSource();
        double worst = 0;
        Task watch = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();
                await monitor.SendAsync("ping");
                worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds);
                await Task.Delay(10);
            }
        });
        await BrowserAsync(app, new JsonObject { ["folder"] = "\\many", ["waitLoaded"] = false, ["entries"] = false });
        for (int i = 0; i < 200; i++)
        {
            await app.KeyAsync("PageDown");
            await Task.Delay(50);
        }

        JsonObject done = await BrowserAsync(app, new JsonObject { ["entries"] = false, ["timeoutMs"] = 600_000 });
        await stop.CancelAsync();
        await watch;
        Assert.Equal(1_000_000, done["count"]!.GetValue<int>());
        Assert.True(worst < 50, $"UI thread blocked for {worst:F0} ms");
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-10-01")]
    public Task An_alternate_data_stream_is_listed_and_opens() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenFileSystemAsync(ctx, "TD-FOR-NTFS");
        JsonObject list = await BrowserAsync(app);
        Assert.Contains(list["entries"]!.AsArray(), e => e!["name"]!.GetValue<string>() == "file.txt:secret");
        JsonObject result = await BrowserAsync(app, new JsonObject { ["select"] = "file.txt:secret", ["action"] = "openFile", ["entries"] = false });
        Assert.True(result["opened"]!["readOnly"]!.GetValue<bool>());
        Assert.StartsWith("file.txt:secret (", result["opened"]!["name"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(Encoding.ASCII.GetBytes("TOP SECRET DATA\r\n"), await app.BytesAsync(0, 17));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-10-06")]
    public Task The_log_file_list_shows_lsns_and_operation_names() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenFileSystemAsync(ctx, "TD-FOR-NTFS");
        await BrowserAsync(app, new JsonObject { ["folder"] = "Metadata" });
        JsonObject list = await BrowserAsync(app, new JsonObject { ["select"] = "$LogFile", ["action"] = "logFile" });
        Assert.Equal("LogFile", list["kind"]!.GetValue<string>());
        string header = list["header"]!.GetValue<string>();
        Assert.Contains("LSN", header, StringComparison.Ordinal);
        Assert.Contains("Redo", header, StringComparison.Ordinal);
        Assert.Contains("Undo", header, StringComparison.Ordinal);
        JsonNode[] rows = [.. list["rows"]!.AsArray().Take(10)!];
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.True(r!["lsn"]!.GetValue<long>() > 0));
        Assert.Contains(rows, r => r!["redo"]!.GetValue<string>() == "InitializeFileRecordSegment");
        Assert.Contains(rows, r => r!["undo"]!.GetValue<string>() == "Noop");
        Assert.Contains(NtfsImageBuilder.CurrentLsn.ToString(), list["status"]!.GetValue<string>(), StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-11-02")]
    public Task Differing_fats_are_counted_and_the_used_fat_can_be_switched() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenFileSystemAsync(ctx, "TD-FOR-FAT32-FATDIFF");
        JsonObject root = null!;
        await app.WaitUntilAsync(async () => (root = await BrowserAsync(app))["fatDifferences"] is not null, UiTest.Scaled(TimeSpan.FromSeconds(30)), "FAT comparison");
        Assert.Equal(10, root["fatDifferences"]!.GetValue<long>());
        Assert.Contains("10", root["details"]!.GetValue<string>(), StringComparison.Ordinal);

        JsonObject second = await BrowserAsync(app, new JsonObject { ["fat"] = 1, ["select"] = "data.bin", ["action"] = "clusterChain" });
        Assert.Equal(2, second["rows"]!.AsArray().Count(r => r!["offset"] is not null));
        await BrowserAsync(app, new JsonObject { ["fat"] = 0 });
        JsonObject first = await BrowserAsync(app, new JsonObject { ["select"] = "data.bin", ["action"] = "clusterChain" });
        Assert.Equal(256, first["rows"]!.AsArray().Count(r => r!["offset"] is not null));
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-15-01")]
    public Task The_status_bar_shows_the_owner_of_a_fragmented_file_cluster() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenFileSystemAsync(ctx, "TD-FOR-NTFS");
        long offset = FileSystemImageSet.Ntfs().Fragments("\\frag.bin")[1].Offset + 0x10;
        await app.GoToAsync(offset);
        JsonObject status = await app.SendAsync("fsStatus");
        if (status["ownerLink"]!.GetValue<bool>())
        {
            status = await app.SendAsync("fsFindOwner", null, UiTest.Scaled(TimeSpan.FromSeconds(60)));
        }

        string text = status["text"]!.GetValue<string>();
        Assert.Contains("\\frag.bin", text, StringComparison.Ordinal);
        Assert.Contains($"LBA {offset / 512:N0}", text, StringComparison.Ordinal);
        Assert.Contains($"Cluster {offset / 4096:N0} + 0x10", text, StringComparison.Ordinal);
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-15-02")]
    public Task Go_to_cluster_2_of_fat32_jumps_to_the_data_area() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenFileSystemAsync(ctx, "TD-FOR-FAT32");
        (_, FatImageBuilder b) = FileSystemImageSet.Fat("TD-FOR-FAT32");
        long dataStart = (b.ReservedSectors + 2L * b.FatSectors) * 512;

        JsonObject dialog = await app.SendAsync("fsGoToCluster", new JsonObject { ["input"] = "2" });
        Assert.False(dialog["invalid"]!.GetValue<bool>());
        await DialogReadyAsync(app);
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
        await app.WaitUntilAsync(async () => (await app.SendAsync("openDialogs"))["count"]!.GetValue<int>() == 0, UiTest.Scaled(TimeSpan.FromSeconds(5)), "close");
        await app.WaitUntilAsync(async () => (await app.SendAsync("fsFragments"))["cursor"]!.GetValue<long>() == dataStart, UiTest.Scaled(TimeSpan.FromSeconds(5)), "the jump");

        await app.SendAsync("fsGoToCluster", new JsonObject { ["input"] = "cluster+1" });
        await DialogReadyAsync(app);
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "PrimaryButton" });
        await app.WaitUntilAsync(async () => (await app.SendAsync("openDialogs"))["count"]!.GetValue<int>() == 0, UiTest.Scaled(TimeSpan.FromSeconds(5)), "close");
        await app.WaitUntilAsync(async () => (await app.SendAsync("fsFragments"))["cursor"]!.GetValue<long>() == dataStart + 4096, UiTest.Scaled(TimeSpan.FromSeconds(5)), "the jump");

        JsonObject outOfRange = await app.SendAsync("fsGoToCluster", new JsonObject { ["input"] = (b.ClusterCount + 2).ToString() });
        Assert.True(outOfRange["invalid"]!.GetValue<bool>());
        await DialogReadyAsync(app);
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "CloseButton" });
    });

    private static Task DialogReadyAsync(AppSession app) =>
        app.WaitUntilAsync(async () => (await app.SendAsync("openDialogs"))["ready"]!.GetValue<int>() > 0, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the dialog");

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-15-03")]
    public Task Show_file_clusters_highlights_all_fragments_and_next_fragment_moves() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenFileSystemAsync(ctx, "TD-FOR-NTFS");
        IReadOnlyList<(long Offset, long Length)> fragments = FileSystemImageSet.Ntfs().Fragments("\\frag.bin");
        await BrowserAsync(app, new JsonObject { ["select"] = "frag.bin", ["action"] = "showClusters" });
        JsonObject f = null!;
        await app.WaitUntilAsync(async () => (f = await app.SendAsync("fsFragments"))["panel"]!.AsArray().Count == 3, UiTest.Scaled(TimeSpan.FromSeconds(10)), "the fragments panel");
        Assert.Equal(fragments, f["fragments"]!.AsArray().Select(x => (x!["offset"]!.GetValue<long>(), x["length"]!.GetValue<long>())));
        Assert.Equal(3, f["panel"]!.AsArray().Count);
        Assert.True(f["goMenu"]!.GetValue<bool>());

        await app.GoToAsync(fragments[0].Offset);
        JsonObject highlights = await app.SendAsync("highlights");
        JsonNode[] marks = [.. highlights["segments"]!.AsArray().Where(s => s!["tag"]!.GetValue<string>().StartsWith("fragment:", StringComparison.Ordinal))!];
        Assert.NotEmpty(marks);
        Assert.All(marks, m => Assert.NotNull(m!["border"]));

        await app.SendAsync("execute", new JsonObject { ["id"] = "forensics.fs.nextFragment" });
        await app.SendAsync("execute", new JsonObject { ["id"] = "forensics.fs.nextFragment" });
        Assert.Equal(fragments[2].Offset, (await app.SendAsync("fsFragments"))["cursor"]!.GetValue<long>());
        await app.SendAsync("execute", new JsonObject { ["id"] = "forensics.fs.previousFragment" });
        Assert.Equal(fragments[1].Offset, (await app.SendAsync("fsFragments"))["cursor"]!.GetValue<long>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-17-02")]
    public Task Overwritten_deleted_files_ask_before_export() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenFileSystemAsync(ctx, "TD-FOR-NTFS-OVERWRITE");
        JsonObject deleted = await BrowserAsync(app, new JsonObject { ["folder"] = "Deleted" });
        JsonNode[] entries = [.. deleted["entries"]!.AsArray()!];
        Assert.Equal("Overwritten", entries.First(e => e!["name"]!.GetValue<string>() == "gone.bin")!["recovery"]!.GetValue<string>());
        Assert.Equal("PartiallyOverwritten", entries.First(e => e!["name"]!.GetValue<string>() == "half.bin")!["recovery"]!.GetValue<string>());

        await BrowserAsync(app, new JsonObject { ["select"] = "gone.bin" });
        await app.SendAsync("fsExport", new JsonObject { ["folder"] = Path.Combine(ctx.Root, "out"), ["wait"] = false });
        await app.WaitUntilAsync(async () => (await app.SendAsync("fsDialog"))["open"]!.GetValue<bool>(), UiTest.Scaled(TimeSpan.FromSeconds(10)), "the dialog");
        JsonObject dialog = await app.SendAsync("fsDialog");
        Assert.Equal("Export", dialog["primary"]!.GetValue<string>());
        Assert.Equal("Cancel", dialog["close"]!.GetValue<string>());
        await DialogReadyAsync(app);
        await app.SendAsync("dialogButton", new JsonObject { ["name"] = "CloseButton" });
    });

    [Fact]
    [Trait(UiTest.TC, "TC-FOR-17-03")]
    public Task No_operation_writes_to_the_file_system() => UiTestContext.RunAsync(async ctx =>
    {
        AppSession app = await OpenFileSystemAsync(ctx, "TD-FOR-NTFS");
        string[] forbidden = ["undelete", "restore", "rename", "create file", "new file", "delete file", "write to", "format"];
        JsonObject normal = await BrowserAsync(app, new JsonObject { ["select"] = "readme.txt" });
        JsonObject deleted = await BrowserAsync(app, new JsonObject { ["folder"] = "Deleted", ["select"] = "deleted.txt" });
        IEnumerable<string> menu = normal["menu"]!.AsArray().Concat(deleted["menu"]!.AsArray()).Select(m => m!["id"]!.GetValue<string>() + " " + m["text"]!.GetValue<string>());
        JsonObject names = await app.SendAsync("fsCommandNames");
        foreach (string text in menu.Concat(names["names"]!.AsArray().Select(n => n!.GetValue<string>())))
        {
            string lower = text.ToLowerInvariant();
            // 単語として探す (「Encryption information」の「format」などは対象外)。
            Assert.DoesNotContain(forbidden, w => System.Text.RegularExpressions.Regex.IsMatch(lower, @"\b" + System.Text.RegularExpressions.Regex.Escape(w) + @"\b"));
        }
    });
}
