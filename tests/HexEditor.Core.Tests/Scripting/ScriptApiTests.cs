using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.Core.Annotations;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Inspector;
using HexEditor.Core.Scripting;
using HexEditor.Core.Sources;
using HexEditor.Sdk;

namespace HexEditor.Core.Tests.Scripting;

/// <summary>自動化 API (AUTO-04〜AUTO-18) をスクリプトから使う。</summary>
public sealed class ScriptApiTests
{
    private static byte[] Seq(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)i)];

    private static async Task<IReadOnlyList<string>> RunOk(ScriptTestHost t, string code)
    {
        ScriptRunResult r = await t.RunCodeAsync(code);
        ScriptTestHost.AssertSucceeded(r);
        return t.OutputText;
    }

    private static byte[] Bytes(ScriptTestHost t, CoreDocumentHost doc) => t.OnUi(() => CSharpScriptTests.ReadAll(doc));

    // ---- AUTO-04 ----

    [Fact]
    [Trait("TC", "TC-AUTO-04-03")]
    public async Task Range_and_number_arguments_accept_input_expressions()
    {
        using var t = new ScriptTestHost();
        t.Open(Seq(1024 * 1024));
        IReadOnlyList<string> output = await RunOk(t, """
            doc.goTo("end-0x10");
            console.log("cursor=" + doc.cursor);
            doc.selection.set("0x100:0x20");
            console.log("sel=" + doc.selection.start + "," + doc.selection.length);
            doc.selection.set("0x100..0x120");
            console.log("sel=" + doc.selection.start + "," + doc.selection.length);
            console.log("bytes=" + Array.from(doc.readBytes("0x10", "4")).map(b => b.toString(16).toUpperCase().padStart(2, "0")).join(" "));
            const [crc] = await hash.compute(doc, "sel", ["crc32"]);
            console.log("crc32=" + crc.hex);
            console.log("obj=" + doc.readBytes({ start: 0, length: 1 }.start, "end-0xFFFFF").length);
            """);
        uint crc32 = Crc32Of(Seq(1024 * 1024).AsSpan(0x100, 0x20));
        Assert.Contains("cursor=1048560", output);
        Assert.Equal(2, output.Count(l => l == "sel=256,32"));
        Assert.Contains("bytes=10 11 12 13", output);
        Assert.Contains($"crc32={crc32:x8}", output);
        Assert.Contains("obj=1", output);
    }

    private static uint Crc32Of(ReadOnlySpan<byte> data)
    {
        IHasher h = HashCatalog.Get("crc32").CreateHasher();
        h.Append(data);
        byte[] v = h.Finish();
        return (uint)(v[0] << 24 | v[1] << 16 | v[2] << 8 | v[3]);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-04-04")]
    public async Task Unsatisfiable_api_version_fails_before_running_the_body()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost doc = t.Open(new byte[16]);
        string path = t.WriteScript("api99.js", "// ==HexEditorScript==\n// @api 99.0\n// ==/HexEditorScript==\ndoc.writeBytes(0, 'FF');\n");
        t.TrustScript(path);
        ScriptRunResult r = await t.RunFileAsync(path);
        Assert.Equal(ScriptRunStatus.Failed, r.Status);
        Assert.Contains("99.0", r.Message);
        Assert.Contains(ApiInfo.Version, r.Message);
        Assert.Equal(0, Bytes(t, doc)[0]);
        ScriptRunResult headless = await t.Host.RunAndWaitAsync(new ScriptRunRequest { Path = path, Headless = true, CommandLineTrust = true }, t.Env);
        Assert.Equal(4, headless.ProcessExitCode);
        Assert.Equal(0, Bytes(t, doc)[0]);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-04-02")]
    public async Task Same_processing_in_javascript_and_csharp_gives_the_same_result()
    {
        // JavaScript と C# で同じ API を同じ名前の対応 (camelCase / PascalCase) で使う。C# はコンポーネントが必要なため、
        // ここでは名前の対応と結果の一致を JavaScript の 2 つの書き方 (同期 API と一括処理 API) で確かめ、C# は CSharpScriptTests で確かめる。
        using var t = new ScriptTestHost();
        CoreDocumentHost a = t.Open(Seq(4096), "a.bin");
        IReadOnlyList<string> output = await RunOk(t, """
            await doc.transform("all", "xor", "5A");
            const [h] = await hash.compute(doc, null, "sha256");
            console.log("js=" + h.hex);
            """);
        byte[] expected = [.. Seq(4096).Select(b => (byte)(b ^ 0x5A))];
        Assert.Contains("js=" + Convert.ToHexStringLower(SHA256.HashData(expected)), output);
        Assert.Equal(expected, Bytes(t, a));
    }

    // ---- AUTO-05 ----

    [Fact]
    [Trait("TC", "TC-AUTO-05-01")]
    public async Task FindAll_on_100_gb_stops_within_one_second_with_canceled_error()
    {
        using var t = new ScriptTestHost();
        t.Open(new VirtualByteSource(100L << 30, VirtualContent.Fill, fill: 0), "sparse100g.bin");
        ScriptRun run = t.Start(new ScriptRunRequest
        {
            Code = "console.log('start'); try { await doc.findAll('AA BB CC DD'); } catch (e) { console.log(e.name); throw e; }",
            Origin = ScriptOrigin.Console,
        });
        await JsRuntimeTests.WaitUntilAsync(() => t.OutputText.Contains("start") && t.Env.Operations.Active.Any(o => o.Name.Contains("findAll")));
        DateTimeOffset stop = DateTimeOffset.UtcNow;
        run.Stop();
        ScriptRunResult r = await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(ScriptRunStatus.Stopped, r.Status);
        Assert.Contains("CanceledError", t.OutputText);
        ScriptOutputLine canceled = t.OutputLines.First(l => l.Text == "CanceledError");
        Support.TimeBudget.Limit(canceled.Time - stop < TimeSpan.FromSeconds(1), $"{canceled.Time - stop}");
    }

    [Fact]
    [Trait("TC", "TC-AUTO-05-02")]
    public async Task Sync_api_over_16_mb_is_a_limit_error()
    {
        using var t = new ScriptTestHost();
        t.Open(new VirtualByteSource(1L << 30), "markers1g.bin");
        IReadOnlyList<string> output = await RunOk(t, """
            for (const f of [() => doc.readBytes(0, 17 * 1024 * 1024), () => doc.writeBytes(0, new Uint8Array(17 * 1024 * 1024)), () => doc.readBytes(0, 16 * 1024 * 1024).length]) {
              try { console.log(String(f())); } catch (e) { console.log(e.name); }
            }
            """);
        Assert.Equal(["LimitError", "LimitError", "16777216"], output.Take(3));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-05-04")]
    public async Task Two_hashes_run_at_the_same_time_with_promise_all()
    {
        using var t = new ScriptTestHost();
        byte[] random = RandomNumberGenerator.GetBytes(1 << 20);
        CoreDocumentHost first = t.Open(new FaultyByteSource(new MemoryByteSource(random)) { Delay = TimeSpan.FromMilliseconds(5) }, "random.bin");
        t.Open(new FaultyByteSource(new MemoryByteSource(Seq(1 << 20))) { Delay = TimeSpan.FromMilliseconds(5) }, "seq.bin");
        int maxConcurrent = 0;
        t.Env.Operations.Changed += (_, _) => maxConcurrent = Math.Max(maxConcurrent, t.Env.Operations.Active.Count(o => o.Name.Contains("hash")));
        IReadOnlyList<string> output = await RunOk(t, """
            const [a, b] = await Promise.all(app.documents.map(d => hash.compute(d, null, ["sha256"])));
            console.log(a[0].hex); console.log(b[0].hex);
            """);
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(random)), output);
        Assert.Contains(Convert.ToHexStringLower(SHA256.HashData(Seq(1 << 20))), output);
        Assert.Equal(2, maxConcurrent);
        _ = first;
    }

    [Fact]
    [Trait("TC", "TC-AUTO-02-06")]
    public async Task Waiting_for_a_bulk_api_does_not_count_toward_the_time_limit()
    {
        using var t = new ScriptTestHost(new ScriptLimits { Time = TimeSpan.FromSeconds(1) });
        t.Open(new FaultyByteSource(new MemoryByteSource(new byte[4 << 20])) { Delay = TimeSpan.FromMilliseconds(400) }, "slow.bin");
        t.Ui.ContinueAnswer = _ => throw new InvalidOperationException("時間の上限の確認が出てはいけない");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<string> output = await RunOk(t, "await doc.transform('all', 'xor', 0xFF); console.log('done');");
        Assert.True(watch.Elapsed > TimeSpan.FromSeconds(1), "上限を超える時間の完了待ちが起きていない");
        Assert.Contains("done", output);
        Assert.Equal(0, t.Ui.TimeoutPrompts);
    }

    // ---- AUTO-06 ----

    private sealed class TestCommands : IScriptCommandService
    {
        public List<(string Id, IReadOnlyDictionary<string, object?> Args)> Executed { get; } = [];

        public IReadOnlyList<CommandInfo> List() =>
            [new("edit.selectAll", "Select all", "edit", []), new("edit.fill", "Fill", "edit", ["range", "pattern"]), new("go.goTo", "Go to", "go", ["offset"])];

        public bool Exists(string id) => List().Any(c => c.Id == id);

        public Task<object?> ExecuteAsync(string id, IReadOnlyDictionary<string, object?> args, IScriptDocumentHost? target, bool headless)
        {
            Executed.Add((id, args));
            return Task.FromResult<object?>(id);
        }
    }

    [Fact]
    [Trait("TC", "TC-AUTO-06-01")]
    [Trait("TC", "TC-AUTO-06-02")]
    public async Task Commands_are_listed_and_executed_by_fixed_id()
    {
        foreach (string language in new[] { "en", "ja", "ar" })
        {
            using var t = new ScriptTestHost(language: language);
            var commands = new TestCommands();
            t.Env.Commands = commands;
            IReadOnlyList<string> output = await RunOk(t, """
                console.log(JSON.stringify(app.commands.list().map(c => c.id)));
                await app.commands.execute("edit.fill", { range: "sel", pattern: "00" });
                try { await app.commands.execute("no.such"); } catch (e) { console.log(e.name); }
                """);
            Assert.Contains("[\"edit.selectAll\",\"edit.fill\",\"go.goTo\"]", output);
            Assert.Contains("ArgumentError", output);
            (string id, IReadOnlyDictionary<string, object?> args) = Assert.Single(commands.Executed);
            Assert.Equal("edit.fill", id);
            Assert.Equal("sel", args["range"]);
            Assert.Equal("00", args["pattern"]);
        }
    }

    [Fact]
    [Trait("TC", "TC-AUTO-06-03")]
    public async Task BeforeSave_cancel_blocks_the_save_and_shows_the_reason()
    {
        using var t = new ScriptTestHost();
        string file = Path.Combine(t.Root, "seq.bin");
        File.WriteAllBytes(file, Seq(256));
        CoreDocumentHost doc = t.OnUi(() => t.Env.OpenFile(file));
        ScriptRun run = t.Start(new ScriptRunRequest
        {
            Code = "app.on('beforeSave', e => e.cancel('blocked-by-test-42')); console.log('ready');",
            Origin = ScriptOrigin.Console,
        });
        await JsRuntimeTests.WaitUntilAsync(() => run.IsResident && t.OutputText.Contains("ready"));
        t.OnUi(() => doc.Document.Overwrite(0, [0xFF]));
        await Assert.ThrowsAsync<IoError>(() => t.Env.Dispatcher.InvokeAsync(async () =>
        {
            await doc.SaveAsync(CancellationToken.None);
            return true;
        }));
        Assert.Contains(t.Env.Notices, n => n.Contains("blocked-by-test-42"));
        Assert.Equal(Seq(256), File.ReadAllBytes(file));
        Assert.True(t.OnUi(() => doc.Document.IsModified));
        run.Stop();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(run.IsResident);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-06-04")]
    public async Task Selection_changes_are_reported_at_most_ten_times_per_second()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost doc = t.Open(Seq(4096));
        ScriptRun run = t.Start(new ScriptRunRequest
        {
            Code = "app.on('selectionChanged', e => console.log('sel ' + e.document.cursor)); console.log('ready');",
            Origin = ScriptOrigin.Console,
        });
        await JsRuntimeTests.WaitUntilAsync(() => run.IsResident && t.OutputText.Contains("ready"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int moves = 0;
        while (watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            t.OnUi(() => doc.Editor.MoveRight());
            moves++;
            await Task.Yield();
        }

        long last = t.OnUi(() => doc.Editor.Cursor);
        await JsRuntimeTests.WaitUntilAsync(() => t.OutputText.Contains("sel " + last));
        List<DateTimeOffset> times = [.. t.OutputLines.Where(l => l.Text.StartsWith("sel ", StringComparison.Ordinal)).Select(l => l.Time)];
        int maxPerSecond = times.Select(start => times.Count(x => x >= start && x < start.AddSeconds(1))).Max();
        Assert.True(moves > 30, $"moves {moves}");
        Assert.True(maxPerSecond <= 11, $"最大 {maxPerSecond} 回 / 秒");
        run.Stop();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Document_events_and_active_document()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost a = t.Open(Seq(16), "a.bin");
        IReadOnlyList<string> output = await RunOk(t, """
            const events = [];
            const s1 = app.on('documentOpened', e => events.push('opened ' + e.document.name));
            const s2 = app.on('activeDocumentChanged', e => events.push('active ' + (e.document && e.document.name)));
            const nd = app.newDocument({ length: 8, fill: 'AB' });
            console.log(nd.kind, nd.length, nd.readByte(7).toString(16), nd.path);
            app.activeDocument = app.documents[0];
            await app.documents[0].find('00');
            s1.off(); s2.off();
            console.log(events.join('|'));
            """);
        Assert.Contains("memory 8 ab null", output);
        Assert.Contains(output, l => l.Contains("opened new") && l.Contains("active a.bin"));
        _ = a;
    }

    // ---- AUTO-07 ----

    [Fact]
    [Trait("TC", "TC-AUTO-07-01")]
    public async Task Undo_group_of_three_writes_is_one_undo()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost doc = t.Open(Seq(256));
        await RunOk(t, "doc.beginUndoGroup('g1'); doc.writeBytes(0x10, 'FF'); doc.writeBytes(0x20, 'FF'); doc.writeBytes(0x30, 'FF'); doc.endUndoGroup();");
        Assert.Equal("g1", t.OnUi(() => doc.Document.History.UndoDescription));
        t.OnUi(() => doc.Document.Undo());
        byte[] after = Bytes(t, doc);
        Assert.Equal([0x10, 0x20, 0x30], new[] { after[0x10], after[0x20], after[0x30] });
        Assert.False(t.OnUi(() => doc.Document.History.CanUndo));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-01-03")]
    [Trait("TC", "TC-AUTO-09-05")]
    public async Task One_run_is_one_undo_unit_including_bulk_changes()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost doc = t.Open(Seq(1024));
        await RunOk(t, "doc.writeBytes(0, 'AA BB'); doc.fill('0x100:0x10', 'CC'); await doc.transform('all', 'xor', '5A'); await doc.replaceAll('00', '11');");
        Assert.NotEqual(Seq(1024), Bytes(t, doc));
        Assert.Equal("スクリプト: test.js", t.OnUi(() => doc.Document.History.UndoDescription));
        t.OnUi(() => doc.Document.Undo());
        Assert.Equal(Seq(1024), Bytes(t, doc));
        Assert.False(t.OnUi(() => doc.Document.History.CanUndo));
    }

    [Fact]
    public async Task Changes_before_an_exception_remain_as_one_undo_unit()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost doc = t.Open(Seq(16));
        ScriptRunResult r = await t.RunCodeAsync("doc.writeBytes(0, 'FF'); doc.writeBytes(1, 'FF'); throw new Error('x');");
        Assert.Equal(ScriptRunStatus.Failed, r.Status);
        Assert.Equal(0xFF, Bytes(t, doc)[1]);
        t.OnUi(() => doc.Document.Undo());
        Assert.Equal(Seq(16), Bytes(t, doc));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-07-02")]
    public async Task Highlights_disappear_when_the_script_ends()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost doc = t.Open(Seq(1024));
        var gate = new TaskCompletionSource<bool>();
        int seen = 0;
        t.Ui.ConfirmAnswer = 0;
        ScriptRun run = t.Start(new ScriptRunRequest
        {
            Code = "doc.highlights.add({start: 0x100, length: 16}, 'yellow', 'hl'); console.log('added'); while (!script.signal.aborted) { await doc.find('00'); }",
            Origin = ScriptOrigin.Console,
        });
        await JsRuntimeTests.WaitUntilAsync(() => t.OutputText.Contains("added") || run.Completion.IsCompleted);
        IReadOnlyList<PlacedAnnotation> during = t.OnUi(() => AnnotationLayer.For(doc.Document).QueryVisible(0x100, 0x110));
        seen = during.Count(a => a.Annotation.Label == "hl" && a.Source.Origin == AnnotationOrigin.Script);
        run.Stop();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, seen);
        Assert.DoesNotContain(t.OnUi(() => AnnotationLayer.For(doc.Document).QueryVisible(0, 1024)), a => a.Source.Origin == AnnotationOrigin.Script);
        Assert.Equal(0, t.OnUi(() => BookmarkCollection.For(doc.Document)?.Count ?? 0));
        _ = gate;
    }

    [Fact]
    [Trait("TC", "TC-AUTO-07-03")]
    public async Task Closed_document_access_is_a_clear_error()
    {
        foreach ((string lang, string word) in new[] { ("en", "closed"), ("ja", "閉じられています") })
        {
            using var t = new ScriptTestHost(language: lang);
            t.Open(Seq(16));
            IReadOnlyList<string> output = await RunOk(t, "const d = doc; await d.close({ discard: true }); try { d.length; } catch (e) { console.log(e.name + ': ' + e.message); }");
            string line = Assert.Single(output, l => l.StartsWith("ArgumentError", StringComparison.Ordinal));
            Assert.Contains(word, line);
        }
    }

    [Fact]
    public async Task Save_and_save_as_use_the_safe_save()
    {
        using var t = new ScriptTestHost();
        string file = Path.Combine(t.Root, "a.bin");
        File.WriteAllBytes(file, Seq(64));
        t.OnUi(() => t.Env.OpenFile(file));
        string other = Path.Combine(t.Root, "copy.bin");
        ScriptRunResult r = await t.Host.RunAndWaitAsync(new ScriptRunRequest
        {
            Code = $"doc.writeBytes(0, 'FF'); await doc.save(); console.log(doc.isModified); await doc.saveAs({System.Text.Json.JsonSerializer.Serialize(other)});",
            Origin = ScriptOrigin.Console,
        }, t.Env);
        Assert.Equal(ScriptFailure.Permission, r.Failure);
        Assert.Equal(0xFF, File.ReadAllBytes(file)[0]);
        Assert.Contains("false", t.OutputText);
        Assert.False(File.Exists(other));
    }

    // ---- AUTO-08 ----

    [Fact]
    [Trait("TC", "TC-AUTO-08-01")]
    public async Task ReadInt_endianness_and_64_bit_bigint()
    {
        using var t = new ScriptTestHost();
        t.Open([0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4]);
        IReadOnlyList<string> output = await RunOk(t, """
            console.log(String(doc.readInt(0, "u32", "be")));
            const v = doc.readInt(0, "u64");
            console.log(typeof v + " " + v.toString());
            console.log(doc.readInt(0, "i8"), doc.readInt(0, "u24", "be"), doc.readInt(1, "i16", "le"), doc.readFloat(0, "f32", "be").toPrecision(4));
            """);
        Assert.Equal("3735928559", output[0]);
        Assert.Equal("bigint 289077008422317534", output[1]);
        Assert.Equal("-34 14593470 -16723 -6.260e+18", output[2]);
    }

    [Fact]
    public async Task Write_and_read_values_and_strings()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost doc = t.Open(new byte[32]);
        IReadOnlyList<string> output = await RunOk(t, """
            doc.writeInt(0, "u32", 0x11223344, "be");
            doc.writeInt(4, "i64", -2n, "le");
            doc.writeFloat(12, "f64", 1.5, "le");
            const n = doc.writeString(20, "Aあ", "utf-8");
            console.log(n, doc.readString(20, 10, "utf-8"), doc.readInt(4, "i64", "le").toString());
            try { doc.writeInt(0, "u8", 256); } catch (e) { console.log(e.name); }
            """);
        Assert.Equal("4 Aあ -2", output[0]);
        Assert.Equal("RangeError", output[1]);
        Assert.Equal([0x11, 0x22, 0x33, 0x44, 0xFE, 0xFF], Bytes(t, doc)[..6]);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-08-02")]
    [Trait("Category", "Performance")]
    public async Task Fill_of_50_gb_in_a_100_gb_document_returns_within_100_ms()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost doc = t.Open(new VirtualByteSource(100L << 30), "sparse100g.bin");
        IReadOnlyList<string> output = await RunOk(t, """
            const times = [];
            for (let i = 0; i < 10; i++) { const s = Date.now(); doc.fill({ start: 0, length: "50G" }, "00"); times.push(Date.now() - s); }
            times.sort((a, b) => a - b);
            console.log("median=" + times[5]);
            """);
        int median = int.Parse(output.First(l => l.StartsWith("median=", StringComparison.Ordinal))[7..]);
        Support.TimeBudget.Limit(median <= 100, $"中央値 {median} ms");
        Assert.Equal(100L << 30, t.OnUi(() => doc.Document.Length));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-08-03")]
    public async Task WriteBytes_marks_modified_bytes_like_typed_input()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost doc = t.Open(new byte[0x2000]);
        t.OnUi(() => doc.Document.Overwrite(0x200, [0x00]));
        await RunOk(t, "doc.writeBytes(0, 'AA'); doc.writeBytes(0x100, 'BB'); doc.writeBytes(0x1000, 'CC');");
        List<(long Offset, long Length)> modified = t.OnUi(() => doc.Document.Current.EnumerateModifiedRanges().ToList());
        foreach (long offset in new long[] { 0x200, 0, 0x100, 0x1000 })
        {
            Assert.Contains(modified, m => offset >= m.Offset && offset < m.Offset + m.Length);
        }

        Assert.DoesNotContain(modified, m => 1 >= m.Offset && 1 < m.Offset + m.Length);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-08-04")]
    public async Task InsertBytes_on_a_fixed_length_disk_is_read_only_error()
    {
        using var t = new ScriptTestHost();
        byte[] disk = new byte[512 * 8];
        disk[0] = 0x33;
        t.Env.DiskOpener = id => new MemoryByteSource(disk, id, SourceCapabilities.CanWrite);
        ScriptRunResult r = await t.Host.RunAndWaitAsync(new ScriptRunRequest
        {
            Code = "const d = await app.openDisk('PhysicalDrive9'); console.log('canResize=' + d.canResize); try { d.insertBytes(0, '00'); } catch (e) { console.log(e.name); }",
            Path = t.WriteScript("diskinsert.js", "//"),
            Headless = true,
            CommandLineTrust = true,
            CommandLineAllow = [ScriptPermissions.Disk],
        }, t.Env);
        ScriptTestHost.AssertSucceeded(r);
        Assert.Contains("canResize=false", t.OutputText);
        Assert.Contains("ReadOnlyError", t.OutputText);
        Assert.Equal(0x33, disk[0]);
    }

    [Fact]
    public async Task Opening_a_disk_needs_the_disk_permission()
    {
        using var t = new ScriptTestHost();
        t.Env.DiskOpener = id => new MemoryByteSource(new byte[512], id);
        IReadOnlyList<string> output = await RunOk(t, "try { await app.openDisk('PhysicalDrive9'); } catch (e) { console.log(e.name + ' ' + e.permission); }");
        Assert.Contains("PermissionError disk", output);
        Assert.Contains(ScriptPermissions.Disk, t.Ui.PermissionPrompts);
    }

    [Fact]
    public async Task Insert_delete_resize_and_random_fill()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost doc = t.Open(Seq(16));
        await RunOk(t, "doc.insertBytes(0, [1, 2]); doc.delete('2:4'); doc.resize(20, 'EE'); doc.fill('0:2', 'random'); doc.fill('0:3', '09', { insert: false });");
        byte[] after = Bytes(t, doc);
        Assert.Equal(20, after.Length);
        Assert.Equal(0xEE, after[^1]);
    }

    // ---- AUTO-09 ----


    [Fact]
    public async Task Chunks_stream_a_range_in_order()
    {
        using var t = new ScriptTestHost();
        t.Open(Seq(5000));
        IReadOnlyList<string> output = await RunOk(t, """
            const parts = [];
            for await (const c of doc.chunks({ start: 10, length: 4000 }, 1024)) parts.push(c.offset + ":" + c.data.length + ":" + c.data[0]);
            console.log(parts.join(" "));
            try { doc.chunks(null, 17 * 1024 * 1024); } catch (e) { console.log(e.name); }
            """);
        Assert.Equal("10:1024:10 1034:1024:10 2058:1024:10 3082:928:10", output[0]);
        Assert.Equal("LimitError", output[1]);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-09-04")]
    public async Task CopyRange_of_50_gb_does_not_copy_data()
    {
        using var t = new ScriptTestHost();
        var source = new VirtualByteSource(100L << 30);
        t.Open(source, "sparse100g.bin");
        long before = GC.GetTotalMemory(true);
        IReadOnlyList<string> output = await RunOk(t, """
            const dest = app.newDocument();
            await doc.copyRange({ start: 0, length: "50G" }, dest, 0, { insert: true });
            console.log("length=" + dest.length);
            console.log(Array.from(dest.readBytes(0x80000000, 8)).join(","));
            """);
        long after = GC.GetTotalMemory(true);
        Assert.Contains("length=53687091200", output);
        byte[] expected = new byte[8];
        source.Read(0x80000000, expected);
        Assert.Contains(string.Join(",", expected), output);
        Assert.True(after - before < 200L * 1024 * 1024, $"{(after - before) / 1024 / 1024} MB");
    }

    [Fact]
    public async Task Find_count_replace_histogram_entropy_compare_strings()
    {
        using var t = new ScriptTestHost();
        byte[] data = Encoding.ASCII.GetBytes("hello world hello HEXEDITOR text\0\0\0\0more strings here");
        t.Open(data, "a.bin");
        t.Open(data, "b.bin", activate: false);
        IReadOnlyList<string> output = await RunOk(t, """
            const a = app.documents[0], b = app.documents[1];
            const all = await a.findAll("hello", { type: "text" });
            console.log("findAll=" + all.length + " " + all[1].start + " " + all.truncated);
            console.log("limited=" + (await a.findAll("6C", { max: 2 })).truncated);
            const first = await a.find("68 65 ?? 6C", { from: 1 });
            console.log("find=" + first.start);
            console.log("none=" + (await a.find("FF FF")));
            console.log("count=" + await a.count("o", { type: "text" }));
            const h = await a.histogram();
            console.log("hist=" + h[0x6C] + "/" + h.length);
            console.log("entropy=" + (await a.entropy()).toFixed(3) + " blocks=" + (await a.entropy(null, { blockSize: 16 })).length);
            b.writeBytes(0, "00"); b.writeBytes(10, "00 00");
            console.log("diff=" + JSON.stringify(await a.compare(b)));
            console.log("strings=" + (await a.strings({ minLength: 4 })).map(s => s.text).join("|"));
            console.log("replaced=" + await a.replaceAll("hello", "HOLA", { type: "text" }) + " " + a.readString(0, 4));
            """);
        Assert.Contains("findAll=2 12 false", output);
        Assert.Contains("limited=true", output);
        Assert.Contains("find=12", output);
        Assert.Contains("none=null", output);
        Assert.Contains(output, l => l.StartsWith("count=", StringComparison.Ordinal) && int.Parse(l[6..]) >= 4);
        Assert.Contains($"hist={data.Count(x => x == 0x6C)}/256", output);
        Assert.Contains(output, l => l.StartsWith("entropy=", StringComparison.Ordinal) && l.EndsWith($"blocks={(data.Length + 15) / 16}", StringComparison.Ordinal));
        Assert.Contains("diff=[{\"start\":0,\"length\":1},{\"start\":10,\"length\":2}]", output);
        Assert.Contains(output, l => l.StartsWith("strings=", StringComparison.Ordinal) && l.Contains("more strings here"));
        Assert.Contains("replaced=2 HOLA", output);
    }

    [Fact]
    public async Task TransformChunks_rewrites_and_checks_the_length()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost doc = t.Open(Seq(3 * 1024 * 1024));
        IReadOnlyList<string> output = await RunOk(t, """
            await doc.transformChunks("0:0x200000", (data, offset) => data.map(b => b ^ 0xFF));
            try { await doc.transformChunks("0x200000:16", data => data.slice(1)); } catch (e) { console.log(e.name); }
            """);
        Assert.Contains("ArgumentError", output);
        byte[] after = Bytes(t, doc);
        Assert.Equal((byte)(5 ^ 0xFF), after[5]);
        Assert.Equal((byte)(0x200005 & 0xFF), after[0x200005]);
    }

    [Fact]
    public async Task Export_range_needs_file_write_or_a_picked_path()
    {
        using var t = new ScriptTestHost();
        t.Open(Seq(256));
        string target = Path.Combine(t.Root, "out.bin");
        t.Ui.PickAnswer = kind => kind == "save" ? [target] : null;
        IReadOnlyList<string> output = await RunOk(t, $$"""
            try { await doc.exportRange("0:16", {{System.Text.Json.JsonSerializer.Serialize(target)}}); } catch (e) { console.log(e.name); }
            const p = await ui.saveFile();
            await doc.exportRange("0x10:0x20", p);
            console.log("ok");
            """);
        Assert.Equal("PermissionError", output[0]);
        Assert.Equal(Seq(256)[0x10..0x30], File.ReadAllBytes(target));
    }

    // ---- AUTO-10 ----

    [Fact]
    [Trait("TC", "TC-AUTO-10-01")]
    [Trait("TC", "TC-AUTO-10-02")]
    [Trait("TC", "TC-AUTO-10-03")]
    public async Task Selection_set_from_results_and_user_ranges()
    {
        using var t = new ScriptTestHost();
        byte[] data = new byte[0x10000];
        for (int i = 1; i < 16; i++)
        {
            new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }.CopyTo(data, i * 0x1000);
        }

        CoreDocumentHost doc = t.Open(data);
        await RunOk(t, "doc.selection.set('0x100:0x20');");
        Assert.Equal((0x100L, 0x20L), t.OnUi(() => (doc.Editor.SelectionStart, doc.Editor.SelectionLength)));

        IReadOnlyList<string> output = await RunOk(t, "const r = await doc.findAll('CA FE BA BE'); doc.selection.fromResults(r); console.log('n=' + doc.selection.ranges.length);");
        Assert.Contains("n=15", output);
        List<Selection.ByteRange> ranges = t.OnUi(() => doc.Editor.SelectedRanges.ToList());
        Assert.Equal(new Selection.ByteRange(0x1000, 4), ranges[0]);
        Assert.Equal(new Selection.ByteRange(0x2000, 4), ranges[1]);

        t.OnUi(() =>
        {
            doc.Editor.Select(0x10, 0x10);
            doc.Editor.AddSelection(0x40, 0x10);
            doc.Editor.AddSelection(0x100, 8);
        });
        output = await RunOk(t, "console.log(JSON.stringify(doc.selection.ranges));");
        Assert.Contains("[{\"start\":16,\"length\":16},{\"start\":64,\"length\":16},{\"start\":256,\"length\":8}]", output);
    }

    [Fact]
    public async Task Selection_limits_rectangle_and_active_column()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost doc = t.Open(new byte[4096]);
        IReadOnlyList<string> output = await RunOk(t, """
            doc.selection.setRectangle(0x20, 3, 4);
            console.log(doc.selection.isRectangular, doc.selection.ranges.length);
            doc.selection.clear();
            console.log(doc.selection.isEmpty);
            doc.activeColumn = "text"; console.log(doc.activeColumn);
            doc.cursor = 0x30; console.log(doc.cursor);
            try { doc.selection.fromResults(Array.from({ length: 1000001 }, (_, i) => ({ start: 0, length: 1 }))); } catch (e) { console.log(e.name); }
            try { doc.selection.set("0:5000"); } catch (e) { console.log(e.name); }
            """);
        Assert.Equal(["true 3", "true", "text", "48", "LimitError", "RangeError"], output.Take(6));
        _ = doc;
    }

    // ---- AUTO-11 ----

    [Fact]
    [Trait("TC", "TC-AUTO-11-01")]
    public async Task Bookmarks_added_by_a_script_are_in_the_list()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost doc = t.Open(new byte[4096]);
        IReadOnlyList<string> output = await RunOk(t, """
            doc.bookmarks.add({ start: 0x200, length: 4, name: "script-bm", color: "green", comment: "c1" });
            for (const b of doc.bookmarks) console.log(b.name, b.start, b.length, b.color, b.comment, b.number);
            console.log(doc.bookmarks.length, doc.bookmarks.find("script-bm").start);
            try { doc.bookmarks.add({ start: 0, color: "chartreuse" }); } catch (e) { console.log(e.name); }
            try { doc.bookmarks.add({ start: 5000 }); } catch (e) { console.log(e.name); }
            """);
        Assert.Equal(["script-bm 512 4 green c1 null", "1 512", "ArgumentError", "RangeError"], output.Take(4));
        Bookmark b = t.OnUi(() => BookmarkCollection.For(doc.Document)!.All.First(x => x.Name == "script-bm"));
        Assert.Equal(BookmarkColor.Palette(4), b.Color);
        Assert.Equal("c1", b.Comment);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-11-02")]
    public async Task AddFromResults_adds_ten_thousand_bookmarks_within_a_second()
    {
        using var t = new ScriptTestHost();
        byte[] data = new byte[10000 * 16];
        for (int i = 0; i < 10000; i++)
        {
            data[i * 16] = 0xAB;
        }

        CoreDocumentHost doc = t.Open(data);
        IReadOnlyList<string> output = await RunOk(t, """
            const r = await doc.findAll("AB");
            const s = Date.now();
            const n = doc.bookmarks.addFromResults(r, "hit {index}");
            console.log("ms=" + (Date.now() - s) + " n=" + n);
            """);
        string line = output.First(l => l.StartsWith("ms=", StringComparison.Ordinal));
        int ms = int.Parse(line[3..line.IndexOf(' ')]);
        Support.TimeBudget.Limit(ms <= 1000, $"{ms} ms");
        List<Bookmark> all = t.OnUi(() => BookmarkCollection.For(doc.Document)!.All.ToList());
        Assert.Equal(10000, all.Count);
        Assert.Equal("hit 1", all[0].Name);
        Assert.Equal("hit 10000", all[^1].Name);
    }

    // ---- AUTO-12 ----

    [Fact]
    [Trait("TC", "TC-AUTO-12-01")]
    public async Task Inspector_decode_matches_the_panel_text_for_all_types()
    {
        using var t = new ScriptTestHost();
        byte[] random = new byte[1 << 16];
        new Random(42).NextBytes(random);
        CoreDocumentHost doc = t.Open(random);
        IReadOnlyList<string> output = await RunOk(t, """
            const rows = [];
            for (const off of [0, 0x1001, 0xFFF0]) for (const endian of ["le", "be"]) for (const ty of inspector.types)
              rows.push(off + "|" + endian + "|" + ty.id + "|" + inspector.decode(doc, off, ty.id, { endian }).text);
            console.log(inspector.types.length);
            console.log(JSON.stringify(rows));
            """);
        Assert.Equal(InspectorTypes.All.Count.ToString(), output[0]);
        string[] rows = System.Text.Json.JsonSerializer.Deserialize<string[]>(output[1])!;
        Assert.Equal(InspectorTypes.All.Count * 6, rows.Length);
        foreach (string row in rows)
        {
            string[] p = row.Split('|', 4);
            long off = long.Parse(p[0]);
            Endianness endian = p[1] == "be" ? Endianness.Big : Endianness.Little;
            byte[] window = random[(int)off..(int)Math.Min(random.Length, off + InspectorDecoder.MaxReadLength)];
            string expected = InspectorDecoder.Decode(p[2], window, [], endian, new Core.Inspector.InspectorOptions()).Text;
            Assert.True(expected == p[3], $"{p[2]} @{off} {p[1]}: {expected} != {p[3]}");
        }

        _ = doc;
    }

    [Fact]
    public async Task Inspector_raw_values_encode_and_panel_rows()
    {
        using var t = new ScriptTestHost();
        t.Open([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0x80, 0x3F]);
        IReadOnlyList<string> output = await RunOk(t, """
            console.log(inspector.decode(doc, 0, "int32").value, typeof inspector.decode(doc, 0, "uint64").value, inspector.decode(doc, 8, "float").value);
            console.log(Array.from(inspector.encode("uint16", "0x1234", { endian: "be" })).join(","));
            try { inspector.decode(doc, 0, "nope"); } catch (e) { console.log(e.name); }
            try { inspector.encode("uint8", "300"); } catch (e) { console.log(e.name); }
            inspector.show("guid"); inspector.hide("int8");
            """);
        Assert.Equal(["-1 bigint 1", "18,52", "ArgumentError", "ArgumentError"], output.Take(4));
        var panel = (CoreScriptEnvironment.DefaultInspectorPanel)t.Env.Inspector;
        Assert.Contains("guid", panel.Visible);
        Assert.DoesNotContain("int8", panel.Visible);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-12-03")]
    public async Task Text_decode_handles_shift_jis_euc_jp_and_ebcdic()
    {
        using var t = new ScriptTestHost();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string japanese = "日本語ﾃｷｽﾄ①㈱～";
        const string ebcdic = "HELLO EBCDIC 123";
        byte[] sjis = Encoding.GetEncoding(932).GetBytes(japanese);
        byte[] euc = Encoding.GetEncoding("euc-jp").GetBytes("日本語ﾃｷｽﾄ");
        byte[] e37 = Encoding.GetEncoding(37).GetBytes(ebcdic);
        t.Open(sjis, "sjis.txt");
        t.Open(euc, "euc.txt", activate: false);
        t.Open(e37, "ebcdic.txt", activate: false);
        IReadOnlyList<string> output = await RunOk(t, """
            const [a, b, c] = app.documents;
            console.log(text.decode(a.readBytes(0, a.length), 932));
            console.log(text.decode(b.readBytes(0, b.length), "euc-jp"));
            console.log(text.decode(c.readBytes(0, c.length), 37));
            console.log(Array.from(text.encode("A", "utf-16le")).join(","), text.encodings.some(e => e.codePage === 932));
            try { text.decode([0xFF, 0xFE, 0x81], "utf-8", { invalid: "error" }); } catch (e) { console.log(e.name); }
            """);
        Assert.Equal(japanese, output[0]);
        Assert.Equal("日本語ﾃｷｽﾄ", output[1]);
        Assert.Equal(ebcdic, output[2]);
        Assert.Equal("65,0 true", output[3]);
        Assert.Equal("ArgumentError", output[4]);
        Assert.DoesNotContain(output.Take(3), l => l.Contains('\uFFFD'));
    }

    // ---- AUTO-13 ----

    private sealed class FakeTemplateEngine : IScriptTemplateEngine
    {
        public IReadOnlyList<TemplateInfo> List() => [new("pe", "pe.hexpat", [".exe", ".dll"])];

        public IReadOnlyList<TemplateInfo> Detect(Document document, string? path) => List();

        public HexEditor.Core.Templates.Extensions.TemplateExtensionRegistry Extensions { get; } = new();

        public Task<ITemplateResultNode> ApplyAsync(IScriptDocumentHost document, string nameOrPath, long offset, bool show, CancellationToken cancellationToken) =>
            nameOrPath == "pe"
                ? Task.FromResult<ITemplateResultNode>(new Node("root", "struct", 0, 0x40, null, document,
                    [new Node("header", "IMAGE_DOS_HEADER", 0, 0x40, null, document, [new Node("e_lfanew", "u32", 0x3C, 4, 0x3CL, document, [])])]))
                : throw new TemplateScriptException("テンプレートが見つかりません: " + nameOrPath, null, 0, 0);

        private sealed class Node(string name, string type, long start, long length, object? value, IScriptDocumentHost doc, IReadOnlyList<ITemplateResultNode> children)
            : ITemplateResultNode
        {
            public string Name => name;

            public string Type => type;

            public long Start => start;

            public long Length => length;

            public object? Value
            {
                get
                {
                    if (type != "u32")
                    {
                        return value;
                    }

                    byte[] b = new byte[4];
                    doc.Document.Current.Read(start, b);
                    return (long)BitConverter.ToUInt32(b);
                }
            }

            public string DisplayValue => Value?.ToString() ?? "";

            public IReadOnlyList<ITemplateResultNode> Children => children;

            public void SetValue(object? v) => doc.Document.Overwrite(start, BitConverter.GetBytes(Convert.ToUInt32(v)));

            public JsonNode ToJson() => new JsonObject { ["name"] = name, ["children"] = new JsonArray([.. children.Select(c => c.ToJson())]) };
        }
    }

    [Fact]
    public async Task Templates_apply_get_tojson_and_value_assignment_is_undoable()
    {
        using var t = new ScriptTestHost();
        byte[] pe = new byte[0x200];
        pe[0] = (byte)'M';
        pe[1] = (byte)'Z';
        pe[0x3C] = 0xE8;
        CoreDocumentHost doc = t.Open(pe, "a.exe");
        t.Env.Templates = new FakeTemplateEngine();
        IReadOnlyList<string> output = await RunOk(t, """
            console.log(templates.list()[0].name, templates.detect(doc).length);
            const root = await doc.applyTemplate("pe", { show: false });
            console.log(root.get("header.e_lfanew").value, doc.readInt(0x3C, "u32", "le"), JSON.stringify(root.toJSON()).length > 10);
            root.get("header.e_lfanew").value = 0x80;
            console.log(doc.readInt(0x3C, "u32", "le"), root.get("nope.x"));
            try { await doc.applyTemplate("zip"); } catch (e) { console.log(e.name); }
            """);
        Assert.Equal(["pe 1", "232 232 true", "128 null", "ArgumentError"], output.Take(4));
        t.OnUi(() => doc.Document.Undo());
        Assert.Equal(pe, Bytes(t, doc));
    }

    // ---- AUTO-14 ----

    [Fact]
    [Trait("TC", "TC-AUTO-14-01")]
    public async Task Hash_compute_matches_the_hash_engine_for_all_algorithms()
    {
        using var t = new ScriptTestHost();
        byte[] data = RandomNumberGenerator.GetBytes(0x20000);
        CoreDocumentHost doc = t.Open(data);
        IReadOnlyList<string> output = await RunOk(t, """
            const ids = hash.algorithms.map(a => a.id);
            const r = await hash.compute(doc, "0x1000:0x10000", ids);
            console.log(JSON.stringify(r.map(x => [x.algorithm, x.hex])));
            """);
        string[][] results = System.Text.Json.JsonSerializer.Deserialize<string[][]>(output[0])!;
        List<Core.Hashing.HashAlgorithmInfo> all = [.. HashCatalog.All.Where(a => a.IsAvailable)];
        Assert.Equal(all.Count, results.Length);
        HashComputation panel = HashEngine.Compute(t.OnUi(() => doc.Document.Current), new HashRequest
        {
            Ranges = [new HashRange(0x1000, 0x10000)],
            Algorithms = [.. all.Select(a => new HashAlgorithmChoice(a))],
        });
        for (int i = 0; i < all.Count; i++)
        {
            Assert.Equal(all[i].Id, results[i][0]);
            Assert.Equal(Convert.ToHexStringLower(panel.Rows[i].Value), results[i][1]);
        }
    }

    [Fact]
    [Trait("TC", "TC-AUTO-14-02")]
    public async Task Three_algorithms_read_the_data_once()
    {
        using var t = new ScriptTestHost();
        byte[] data = RandomNumberGenerator.GetBytes(16 << 20);
        var counting = new CountingByteSource(new MemoryByteSource(data));
        t.Open(counting);
        IReadOnlyList<string> output = await RunOk(t, "const r = await hash.compute(doc, null, ['md5', 'sha1', 'sha256']); for (const x of r) console.log(x.hex);");
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(data)), output[0]);
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(data)), output[1]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), output[2]);
        Assert.True(counting.BytesRead <= data.Length * 1.1, $"{counting.BytesRead}");
    }

    [Fact]
    public async Task Hash_bytes_custom_crc_and_files()
    {
        using var t = new ScriptTestHost();
        string file = t.WriteScript("data.bin", "123456789");
        IReadOnlyList<string> output = await RunOk(t, $$"""
            console.log(hash.computeBytes("31 32 33 34 35 36 37 38 39", "crc32").hex);
            console.log(hash.computeBytes(new Uint8Array([0x31,0x32,0x33,0x34,0x35,0x36,0x37,0x38,0x39]), { id: "crc", width: 16, poly: 0x1021, init: 0xFFFF, refIn: false, refOut: false, xorOut: 0 }).hex);
            try { hash.computeBytes("00", "nope"); } catch (e) { console.log(e.name); }
            try { await hash.computeFile({{System.Text.Json.JsonSerializer.Serialize(file)}}, ["sha256"]); } catch (e) { console.log(e.name); }
            """);
        Assert.Equal(["cbf43926", "29b1", "ArgumentError", "PermissionError"], output.Take(4));
    }

    // ---- AUTO-15 ----

    [Fact]
    [Trait("TC", "TC-AUTO-15-01")]
    public async Task CopyAs_c_array_matches_the_copy_as_formatter()
    {
        using var t = new ScriptTestHost();
        CoreDocumentHost doc = t.Open(Seq(256));
        t.OnUi(() => doc.Editor.SelectAll());
        await RunOk(t, "clipboard.copyAs(doc, 'sel', 'c-array'); console.log(clipboard.formatAs(doc, '0:4', 'hex'));");
        string expected = CopyFormatter.Format(CopyFormat.ArrayC, Seq(256), 0, new CopyOptions());
        Assert.Equal(expected, ((MemoryScriptClipboard)t.Env.Clipboard).Text);
        Assert.Contains("00 01 02 03", t.OutputText);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-15-02")]
    public async Task Clipboard_read_without_permission_is_a_permission_error()
    {
        using var t = new ScriptTestHost();
        t.Env.Clipboard.SetText("secret-1234");
        string path = t.WriteScript("clipread.js", "try { console.log(clipboard.getText()); } catch (e) { console.log(e.name + ': ' + e.message); }");
        ScriptRunResult r = await t.Host.RunAndWaitAsync(new ScriptRunRequest { Path = path, Headless = true, CommandLineTrust = true }, t.Env);
        ScriptTestHost.AssertSucceeded(r);
        string line = Assert.Single(t.OutputText, l => l.StartsWith("PermissionError", StringComparison.Ordinal));
        Assert.Contains("clipboard-read", line);
        Assert.DoesNotContain(t.OutputText, l => l.Contains("secret-1234"));

        // 許可を与えれば読める。書き込みは許可なしでよい。
        string allowed = t.WriteScript("clipread2.js", "clipboard.setBytes('DE AD'); console.log(clipboard.getText(), clipboard.getBytes().length);");
        ScriptTestHost.AssertSucceeded(await t.Host.RunAndWaitAsync(new ScriptRunRequest
        {
            Path = allowed,
            Headless = true,
            CommandLineTrust = true,
            CommandLineAllow = [ScriptPermissions.ClipboardRead],
        }, t.Env));
        Assert.Contains("DE AD 2", t.OutputText);
    }

    // ---- AUTO-16 ----

    [Fact]
    [Trait("TC", "TC-AUTO-16-01")]
    public async Task Form_returns_typed_values_and_passes_labels()
    {
        using var t = new ScriptTestHost();
        t.Ui.FormAnswer = fields => new Dictionary<string, string> { ["text"] = "abc", ["number"] = "0x20", ["check"] = "true", ["choice"] = "B" };
        IReadOnlyList<string> output = await RunOk(t, """
            const r = await ui.form([
              { name: "text", label: "Text label", type: "text" },
              { name: "number", label: "Number label", type: "number" },
              { name: "check", label: "Check label", type: "checkbox" },
              { name: "choice", label: "Choice label", type: "choice", choices: ["A", "B", "C"] }]);
            console.log(JSON.stringify(r));
            """);
        Assert.Contains("{\"text\":\"abc\",\"number\":32,\"check\":true,\"choice\":\"B\"}", output);
        IReadOnlyList<ScriptFormField> fields = Assert.Single(t.Ui.Forms);
        Assert.Equal(["Text label", "Number label", "Check label", "Choice label"], fields.Select(f => f.Label));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-16-02")]
    public async Task Prompt_number_accepts_input_expressions()
    {
        using var t = new ScriptTestHost();
        t.Open(new byte[1024 * 1024]);
        var answers = new Queue<string>(["0x10", "10h", "end-1"]);
        t.Ui.FormAnswer = _ => new Dictionary<string, string> { ["value"] = answers.Dequeue() };
        IReadOnlyList<string> output = await RunOk(t, "for (let i = 0; i < 3; i++) console.log(String(await ui.prompt('n', { type: 'number' })));");
        Assert.Equal(["16", "16", "1048575"], output.Take(3));
        Assert.All(t.Ui.FormValidations, v => Assert.Null(v.Error));
    }

    [Fact]
    public async Task Prompt_validation_rejects_bad_input_and_cancel_returns_null()
    {
        using var t = new ScriptTestHost();
        t.Ui.FormAnswer = _ => new Dictionary<string, string> { ["value"] = "zz+" };
        await t.RunCodeAsync("await ui.prompt('n', { type: 'number' });");
        Assert.NotNull(t.Ui.FormValidations.Single().Error);
        t.Ui.FormAnswer = _ => null;
        IReadOnlyList<string> output = await RunOk(t, "console.log(String(await ui.prompt('x')), String(await ui.form([{ name: 'a' }])), await ui.confirm('ok?', ['Yes', 'No']));");
        Assert.Contains("null null -1", output);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-16-03")]
    public async Task ShowResults_passes_rows_with_start_and_length()
    {
        using var t = new ScriptTestHost();
        t.Open(new byte[0x10000]);
        await RunOk(t, "ui.showResults('r', [{ start: 0x1234, length: 4, note: 'a' }, { start: 0x8000, length: 2, note: 'b' }], ['start', 'length', 'note']); ui.status('done'); ui.output('o', 'warning'); ui.progress.report(0.5, 'half'); await ui.message('m', { kind: 'warning' });");
        (string title, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) = Assert.Single(t.Ui.Results);
        Assert.Equal("r", title);
        Assert.Equal(0x8000L, Convert.ToInt64(rows[1]["start"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("a", rows[0]["note"]);
        Assert.Contains(t.OutputLines, l => l.Text == "o" && l.Kind == ScriptOutputKind.Warning);
        Assert.Contains(t.Ui.Messages, m => m.Text == "m" && m.Kind == "warning");
    }

    [Fact]
    [Trait("TC", "TC-AUTO-16-04")]
    public async Task Headless_prompt_uses_command_line_args()
    {
        using var t = new ScriptTestHost();
        string path = t.WriteScript("promptarg.js", "console.log(String(await ui.prompt('count', { type: 'number' })));");
        ScriptRunResult ok = await t.Host.RunAndWaitAsync(new ScriptRunRequest
        {
            Path = path,
            Headless = true,
            CommandLineTrust = true,
            CommandLineArgs = new Dictionary<string, string> { ["count"] = "0x20" },
        }, t.Env);
        Assert.Equal(0, ok.ProcessExitCode);
        Assert.Contains("32", t.OutputText);
        ScriptRunResult missing = await t.Host.RunAndWaitAsync(new ScriptRunRequest { Path = path, Headless = true, CommandLineTrust = true }, t.Env);
        Assert.Equal(4, missing.ProcessExitCode);
        Assert.Contains("ArgumentError", missing.Message);
        Assert.Contains("count", missing.Message);
        Assert.Contains("ヘッドレス", missing.Message);
    }

    // ---- AUTO-17 ----

    [Fact]
    [Trait("TC", "TC-AUTO-17-01")]
    public async Task Script_storage_survives_a_restart()
    {
        using var t = new ScriptTestHost();
        string path = t.WriteScript("storage.js", "if (script.args.mode === 'set') script.storage.set('k', { n: 42 }); else console.log(JSON.stringify(script.storage.get('k')));");
        ScriptRunResult set = await t.Host.RunAndWaitAsync(new ScriptRunRequest
        {
            Path = path,
            Headless = true,
            CommandLineTrust = true,
            CommandLineArgs = new Dictionary<string, string> { ["mode"] = "set" },
        }, t.Env);
        ScriptTestHost.AssertSucceeded(set);

        // 別のホスト (再起動の代わり) で読む。
        using var restarted = new ScriptHost(t.Folders, new TrustStore(t.Folders.TrustFile), () => new ScriptLimits(), () => "");
        ScriptRunResult get = await restarted.RunAndWaitAsync(new ScriptRunRequest
        {
            Path = path,
            Headless = true,
            CommandLineTrust = true,
            CommandLineArgs = new Dictionary<string, string> { ["mode"] = "get" },
        }, t.Env);
        ScriptTestHost.AssertSucceeded(get);
        Assert.Contains("{\"n\":42}", t.OutputText);
        Assert.NotEmpty(Directory.GetFiles(t.Folders.Storage));
    }

    [Fact]
    public async Task Script_storage_has_a_one_megabyte_limit()
    {
        using var t = new ScriptTestHost();
        IReadOnlyList<string> output = await RunOk(t, "try { script.storage.set('big', 'x'.repeat(1100000)); } catch (e) { console.log(e.name); } script.storage.set('a', 1); script.storage.remove('a'); console.log(String(script.storage.get('a')));");
        Assert.Equal(["LimitError", "null"], output.Take(2));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-17-02")]
    public async Task Settings_set_without_permission_is_a_permission_error()
    {
        using var t = new ScriptTestHost();
        var settings = (CoreScriptEnvironment.MemorySettings)t.Env.Settings;
        string path = t.WriteScript("settingsset.js", "console.log(settings.get('view.tooltips')); try { settings.set('view.tooltips', false); } catch (e) { console.log(e.name + ': ' + e.message); } try { settings.get('no.such.key'); } catch (e) { console.log(e.name); }");
        ScriptRunResult r = await t.Host.RunAndWaitAsync(new ScriptRunRequest { Path = path, Headless = true, CommandLineTrust = true }, t.Env);
        ScriptTestHost.AssertSucceeded(r);
        Assert.Contains(t.OutputText, l => l.StartsWith("PermissionError", StringComparison.Ordinal) && l.Contains("settings"));
        Assert.Contains("ArgumentError", t.OutputText);
        Assert.Empty(settings.Values);
        ScriptTestHost.AssertSucceeded(await t.Host.RunAndWaitAsync(new ScriptRunRequest
        {
            Code = "settings.set('view.tooltips', false);",
            Path = path,
            Headless = true,
            CommandLineTrust = true,
            CommandLineAllow = [ScriptPermissions.Settings],
        }, t.Env));
        Assert.False(settings.Values["view.tooltips"]!.GetValue<bool>());
    }

    // ---- AUTO-18 ----

    private sealed class RecordingHttp : IScriptHttp
    {
        public List<Uri> Requests { get; } = [];

        public Task<(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)> SendAsync(string method, Uri url,
            IReadOnlyDictionary<string, string> headers, byte[]? body, long maxResponseBytes, CancellationToken cancellationToken)
        {
            Requests.Add(url);
            return Task.FromResult<(int, IReadOnlyDictionary<string, string>, byte[])>((200, new Dictionary<string, string>(), "pong"u8.ToArray()));
        }
    }

    [Fact]
    [Trait("TC", "TC-AUTO-18-01")]
    public async Task File_and_network_without_permission_are_permission_errors()
    {
        using var t = new ScriptTestHost();
        var http = new RecordingHttp();
        t.Env.Http = http;
        string path = t.WriteScript("noperm.js", "for (const f of [() => fs.readText('C:/Windows/win.ini'), () => net.fetch(script.args.url)]) { try { await f(); console.log('allowed'); } catch (e) { console.log(e.name + ': ' + e.message); } }");
        ScriptRunResult r = await t.Host.RunAndWaitAsync(new ScriptRunRequest
        {
            Path = path,
            Headless = true,
            CommandLineTrust = true,
            CommandLineArgs = new Dictionary<string, string> { ["url"] = "http://localhost:9/ping" },
        }, t.Env);
        ScriptTestHost.AssertSucceeded(r);
        Assert.StartsWith("PermissionError", t.OutputText[0]);
        Assert.Contains("file-read", t.OutputText[0]);
        Assert.StartsWith("PermissionError", t.OutputText[1]);
        Assert.Contains("network", t.OutputText[1]);
        Assert.Empty(http.Requests);

        // 許可を与えたスクリプトだけが通信する (偽の HTTP。実際には通信しない)。
        ScriptTestHost.AssertSucceeded(await t.Host.RunAndWaitAsync(new ScriptRunRequest
        {
            Code = "const r = await net.fetch('https://example.test/ping'); console.log(r.status + ' ' + r.body);",
            Path = path,
            Headless = true,
            CommandLineTrust = true,
            CommandLineAllow = [ScriptPermissions.Network],
        }, t.Env));
        Assert.Contains("200 pong", t.OutputText);
        Assert.Single(http.Requests);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-18-02")]
    public async Task Picked_file_is_readable_without_permission()
    {
        using var t = new ScriptTestHost();
        string file = Path.Combine(t.Root, "picked.txt");
        File.WriteAllText(file, "The quick brown fox jumps over");
        t.Ui.PickAnswer = kind => kind == "open" ? [file] : null;
        IReadOnlyList<string> output = await RunOk(t, "const p = await ui.pickFile(); console.log(fs.readText(p).slice(0, 20)); try { fs.readText('C:/Windows/win.ini'); } catch (e) { console.log(e.name); }");
        Assert.Equal(["The quick brown fox ", "PermissionError"], output.Take(2));
    }

    [Fact]
    public async Task Script_folder_is_readable_without_permission()
    {
        using var t = new ScriptTestHost();
        t.WriteScript("tools/data.txt", "local data");
        string path = t.WriteScript("tools/reader.js", "console.log(fs.readText(script.path.replace('reader.js', 'data.txt'))); try { fs.writeText(script.path + '.out', 'x'); } catch (e) { console.log(e.name); }");
        t.TrustScript(path);
        ScriptTestHost.AssertSucceeded(await t.RunFileAsync(path));
        Assert.Equal(["local data", "PermissionError"], t.OutputText.Take(2));
    }

    [Fact]
    public async Task App_open_does_not_bypass_the_file_permissions()
    {
        using var t = new ScriptTestHost();
        t.Env.Commands = new SaveCommandStub();
        string outside = Path.Combine(t.Root, "outside", "secret.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllBytes(outside, [1, 2, 3, 4]);
        byte[] before = File.ReadAllBytes(outside);
        string path = t.WriteScript("opener/open.js",
            "try { await app.open(script.args.file); console.log('opened'); } catch (e) { console.log(e.name + ' ' + e.permission); }");

        // 許可なし: 開いていないファイルは開けない (file-read)。
        ScriptRunResult r = await t.Host.RunAndWaitAsync(new ScriptRunRequest
        {
            Path = path, Headless = true, CommandLineTrust = true,
            CommandLineArgs = new Dictionary<string, string> { ["file"] = outside },
        }, t.Env);
        ScriptTestHost.AssertSucceeded(r);
        Assert.Equal("PermissionError file-read", t.OutputText[0]);

        // file-read だけ: 開いて読めるが、保存 (save とコマンドの file.save) は file-write がないので PermissionError。ファイルは変わらない。
        string writer = t.WriteScript("opener/write.js",
            "const d = await app.open(script.args.file); console.log(d.readBytes(0, 4).length); d.writeBytes(0, new Uint8Array([255]));"
            + " try { await d.save(); console.log('saved'); } catch (e) { console.log(e.name + ' ' + e.permission); }"
            + " try { await app.commands.execute('file.save'); console.log('saved'); } catch (e) { console.log(e.name + ' ' + e.permission); }");
        ScriptTestHost.AssertSucceeded(await t.Host.RunAndWaitAsync(new ScriptRunRequest
        {
            Path = writer, Headless = true, CommandLineTrust = true, CommandLineAllow = [ScriptPermissions.FileRead],
            CommandLineArgs = new Dictionary<string, string> { ["file"] = outside },
        }, t.Env));
        // 1 回目の出力 (1 行) と実行結果の行の後。
        Assert.Equal(["4", "PermissionError file-write", "PermissionError file-write"], t.OutputText.Skip(2).Take(3));
        Assert.Equal(before, File.ReadAllBytes(outside));
    }

    /// <summary><c>file.save</c> だけがある偽のコマンド (実行しても何もしない。確認で止まらなければ「saved」になる)。</summary>
    private sealed class SaveCommandStub : IScriptCommandService
    {
        public IReadOnlyList<CommandInfo> List() => [new CommandInfo("file.save", "Save", "file", [])];

        public bool Exists(string id) => id == "file.save";

        public Task<object?> ExecuteAsync(string id, IReadOnlyDictionary<string, object?> args, IScriptDocumentHost? target, bool headless) =>
            Task.FromResult<object?>(true);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-18-03")]
    public async Task Process_run_passes_each_argument_unchanged()
    {
        using var t = new ScriptTestHost();
        string exe = Path.Combine(AppContext.BaseDirectory, "testtarget", "TestTarget.exe");
        Assert.True(File.Exists(exe), exe);
        string path = t.WriteScript("procrun.js", "const r = await process.run(script.args.exe, ['--echo-args', 'a b', 'x&y', '\"q\"', '%PATH%', 'c|d']); console.log(r.exitCode + ' ' + r.stdout);");
        ScriptRunResult r = await t.Host.RunAndWaitAsync(new ScriptRunRequest
        {
            Path = path,
            Headless = true,
            CommandLineTrust = true,
            CommandLineAllow = [ScriptPermissions.Process],
            CommandLineArgs = new Dictionary<string, string> { ["exe"] = exe },
        }, t.Env);
        ScriptTestHost.AssertSucceeded(r);
        string line = t.OutputText.First(l => l.StartsWith("0 ", StringComparison.Ordinal));
        string[] args = System.Text.Json.JsonSerializer.Deserialize<string[]>(line[2..])!;
        Assert.Equal(["a b", "x&y", "\"q\"", "%PATH%", "c|d"], args);
    }
}

/// <summary>読んだバイト数を数えるデータソース (TC-AUTO-14-02)。</summary>
public sealed class CountingByteSource(IByteSource inner) : ByteSourceBase
{
    private long _read;

    public long BytesRead => Interlocked.Read(ref _read);

    public override string DisplayName => inner.DisplayName;

    public override string Identity => inner.Identity + "#counting";

    public override long Length => inner.Length;

    public override SourceCapabilities Capabilities => inner.Capabilities & ~SourceCapabilities.CanWrite;

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        ReadResult r = inner.Read(offset, buffer);
        Interlocked.Add(ref _read, r.BytesReturned);
        return r;
    }
}
