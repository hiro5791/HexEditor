using System.Globalization;
using System.Security.Cryptography;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Scripting;
using HexEditor.Core.Scripting.CSharp;
using HexEditor.Core.Scripting.Library;
using HexEditor.Core.Templates;
using HexEditor.Core.Tests.Engine;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Scripting;

/// <summary>
/// 同梱のサンプルスクリプト集 (AUTO-46)。仕様の 12 種 (JavaScript) と C# の 7 種がそろっていること、メタデータが英語と日本語を持つこと、
/// すべてのサンプルをヘッドレス (<c>hexed run --trust</c> と同じ経路。ファイルやフォルダは <c>--arg</c> で渡す) で実行して期待どおりに
/// なることを確かめる。
/// </summary>
public sealed class SampleScriptTests(ComponentFeedFixture feed) : IClassFixture<ComponentFeedFixture>, IDisposable
{
    private static readonly string Samples = Path.GetDirectoryName(SourceTests.FindRepoFile("samples/scripts/xor-decode.js"))!;

    private static readonly string[] JavaScript =
    [
        "xor-decode", "guess-xor-key", "bookmark-pattern", "strings-to-csv", "recalc-checksum", "high-entropy",
        "pe-summary", "auto-template", "hash-folder", "batch-patch", "save-selection", "struct-table",
    ];

    private static readonly string[] CSharp =
    [
        "xor-decode", "guess-xor-key", "bookmark-pattern", "recalc-checksum", "pe-summary", "hash-folder", "struct-table",
    ];

    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (IDisposable d in _disposables)
        {
            d.Dispose();
        }
    }

    private ScriptTestHost Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "HexEditor", "sample-tests", Guid.NewGuid().ToString("N"));
        var manager = new CSharpComponentManager(Path.Combine(root, "components"), feed.Manifest, new LocalComponentFeed(feed.ZipPath), new FakeVerifier(), () => true);
        manager.InstallFromZipAsync(feed.ZipPath, CancellationToken.None).GetAwaiter().GetResult();
        var host = new ScriptTestHost(csharp: manager, language: "en");
        host.Env.Templates = new TemplateScriptEngine(TemplateCatalog.CreateDefault(Path.Combine(host.Root, "templates")));
        _disposables.Add(host);
        return host;
    }

    private static string SamplePath(string name, string extension) => Path.Combine(Samples, name + extension);

    /// <summary>サンプルを <c>hexed run --trust</c> と同じ条件 (ヘッドレス・信頼・引数) で実行する。</summary>
    private static async Task<ScriptRunResult> RunAsync(ScriptTestHost host, string name, string extension, params (string Name, string Value)[] args)
    {
        ScriptRunResult result = await host.Host.RunAndWaitAsync(new ScriptRunRequest
        {
            Path = SamplePath(name, extension),
            Headless = true,
            CommandLineTrust = true,
            CommandLineArgs = args.ToDictionary(a => a.Name, a => a.Value),
        }, host.Env).WaitAsync(TimeSpan.FromSeconds(120));
        Assert.True(result.Status == ScriptRunStatus.Succeeded, $"{name}{extension}: {result.Status} {result.Message}\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        return result;
    }

    private static uint Crc32Of(ReadOnlySpan<byte> data)
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

    private static byte[] Sequence(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)i)];

    private static byte[] Bytes(ScriptTestHost t, CoreDocumentHost doc) => t.OnUi(() => CSharpScriptTests.ReadAll(doc));

    // ---- 同梱 ----

    [Fact]
    [Trait("TC", "TC-AUTO-46-01")]
    public void All_listed_samples_are_bundled_with_english_and_japanese_metadata()
    {
        foreach (string name in JavaScript)
        {
            Assert.True(File.Exists(SamplePath(name, ".js")), name + ".js");
        }

        foreach (string name in CSharp)
        {
            Assert.True(File.Exists(SamplePath(name, ".csx")), name + ".csx");
        }

        // 一覧の元で読んだ表示名が、英語と日本語の両方で違う (@name と @name:ja を持つ)。
        foreach (string file in Directory.EnumerateFiles(Samples))
        {
            ScriptMetadata meta = ScriptMetadata.Parse(File.ReadAllText(file));
            Assert.True(meta.HasHeader, file);
            Assert.Empty(meta.Warnings);
            string fallback = Path.GetFileNameWithoutExtension(file);
            Assert.NotEqual(fallback, meta.DisplayName("en", fallback));
            Assert.NotEqual(meta.DisplayName("en", fallback), meta.DisplayName("ja", fallback));
            Assert.False(string.IsNullOrWhiteSpace(meta.DisplayDescription("en")), file);
            Assert.NotEqual(meta.DisplayDescription("en"), meta.DisplayDescription("ja"));
            Assert.Equal("1.0", meta.Api);
        }

        Assert.Equal(JavaScript.Length + CSharp.Length, Directory.GetFiles(Samples).Length);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-46-01")]
    public void The_sample_comments_are_in_english()
    {
        foreach (string file in Directory.EnumerateFiles(Samples))
        {
            foreach (string line in File.ReadLines(file).Where(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.Contains("@description:ja", StringComparison.Ordinal)
                && !l.Contains("@name:ja", StringComparison.Ordinal)))
            {
                Assert.DoesNotMatch("[\\u3040-\\u30FF\\u4E00-\\u9FFF]", line);
            }
        }
    }

    // ---- JavaScript と C# で同じ結果 ----

    [Theory]
    [InlineData(".js")]
    [InlineData(".csx")]
    [Trait("TC", "TC-AUTO-46-01")]
    public async Task Xor_decode_xors_the_whole_file_or_the_selection(string extension)
    {
        ScriptTestHost t = Create();
        CoreDocumentHost doc = t.Open(Sequence(256));
        ScriptRunResult r = await RunAsync(t, "xor-decode", extension, ("key", "5A"), ("whole", "true"));
        Assert.Contains("XOR whole file: 256 bytes", t.OutputText);
        Assert.Equal(Sequence(256).Select(b => (byte)(b ^ 0x5A)), Bytes(t, doc));
        Assert.Equal([doc.Name], r.ModifiedDocuments);

        // 選択範囲だけ (whole がオフで選択があるとき)。
        t.OnUi(() => doc.Editor.Select(16, 16));
        await RunAsync(t, "xor-decode", extension, ("key", "FF"), ("whole", "false"));
        byte[] after = Bytes(t, doc);
        Assert.Equal((byte)(16 ^ 0x5A ^ 0xFF), after[16]);
        Assert.Equal((byte)(32 ^ 0x5A), after[32]);
    }

    [Theory]
    [InlineData(".js")]
    [InlineData(".csx")]
    [Trait("TC", "TC-AUTO-46-01")]
    public async Task Guess_xor_key_finds_the_single_byte_key(string extension)
    {
        ScriptTestHost t = Create();
        byte[] plain = new byte[2000];
        for (int i = 0; i < plain.Length; i += 7)
        {
            plain[i] = (byte)(1 + i % 13);
        }

        t.Open([.. plain.Select(b => (byte)(b ^ 0x5A))]);
        await RunAsync(t, "guess-xor-key", extension, ("plain", "00"));
        Assert.Contains("Most likely key: 5A", t.OutputText);
    }

    [Theory]
    [InlineData(".js")]
    [InlineData(".csx")]
    [Trait("TC", "TC-AUTO-46-01")]
    public async Task Bookmark_pattern_adds_one_bookmark_per_match(string extension)
    {
        ScriptTestHost t = Create();
        byte[] data = new byte[300];
        foreach (int at in new[] { 10, 100, 250 })
        {
            new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }.CopyTo(data, at);
        }

        CoreDocumentHost doc = t.Open(data);
        await RunAsync(t, "bookmark-pattern", extension, ("pattern", "DE AD BE EF"));
        Assert.Contains("Added 3 bookmarks", t.OutputText);
        Assert.Equal([10L, 100L, 250L], t.OnUi(() => BookmarkCollection.For(doc.Document)!.All.Select(b => b.Start).Order().ToArray()));
    }

    [Theory]
    [InlineData(".js")]
    [InlineData(".csx")]
    [Trait("TC", "TC-AUTO-46-01")]
    public async Task Recalc_checksum_writes_the_crc32_of_the_range(string extension)
    {
        ScriptTestHost t = Create();
        byte[] data = Sequence(32);
        CoreDocumentHost doc = t.Open(data);
        await RunAsync(t, "recalc-checksum", extension, ("range", "0:16"), ("target", "16"), ("endian", "le"));
        uint expected = Crc32Of(data.AsSpan(0, 16));
        Assert.Equal(expected, BitConverter.ToUInt32(Bytes(t, doc), 16));

        await RunAsync(t, "recalc-checksum", extension, ("range", "0:16"), ("target", "20"), ("endian", "be"));
        Assert.Equal(expected, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(Bytes(t, doc).AsSpan(20)));
        Assert.Contains(t.OutputText, l => l.StartsWith("CRC-32 " + expected.ToString("X8", CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(".js")]
    [InlineData(".csx")]
    [Trait("TC", "TC-AUTO-46-01")]
    public async Task Pe_summary_prints_the_header_values(string extension)
    {
        ScriptTestHost t = Create();
        byte[] pe = TestDataCatalog.PeX64();
        t.Open(pe, "pe.exe");
        await RunAsync(t, "pe-summary", extension);
        uint lfanew = BitConverter.ToUInt32(pe, 0x3C);
        ushort machine = BitConverter.ToUInt16(pe, (int)lfanew + 4);
        ushort sections = BitConverter.ToUInt16(pe, (int)lfanew + 6);
        Assert.Contains("e_lfanew: 0x" + lfanew.ToString("X", CultureInfo.InvariantCulture), t.OutputText);
        Assert.Contains("Machine: 0x" + machine.ToString("X", CultureInfo.InvariantCulture), t.OutputText);
        Assert.Contains("Sections: " + sections.ToString(CultureInfo.InvariantCulture), t.OutputText);
    }

    [Theory]
    [InlineData(".js")]
    [InlineData(".csx")]
    [Trait("TC", "TC-AUTO-46-01")]
    public async Task Hash_folder_lists_the_hash_of_every_file(string extension)
    {
        ScriptTestHost t = Create();
        string folder = Path.Combine(t.Root, "files");
        Directory.CreateDirectory(folder);
        byte[][] contents = [[1, 2, 3], [], Sequence(300)];
        for (int i = 0; i < contents.Length; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"f{i}.bin"), contents[i]);
        }

        await RunAsync(t, "hash-folder", extension, ("folder", folder), ("algorithm", "sha256"));
        for (int i = 0; i < contents.Length; i++)
        {
            Assert.Contains($"{Convert.ToHexStringLower(SHA256.HashData(contents[i]))}  f{i}.bin", t.OutputText);
        }
    }

    [Theory]
    [InlineData(".js")]
    [InlineData(".csx")]
    [Trait("TC", "TC-AUTO-46-01")]
    public async Task Struct_table_reads_the_records(string extension)
    {
        ScriptTestHost t = Create();
        byte[] data = new byte[8 * 4];
        for (int i = 0; i < 4; i++)
        {
            BitConverter.GetBytes((ushort)(i + 1)).CopyTo(data, i * 8);
            BitConverter.GetBytes((ushort)(0x10 * i)).CopyTo(data, i * 8 + 2);
            BitConverter.GetBytes((uint)(1000 * i)).CopyTo(data, i * 8 + 4);
        }

        t.Open(data);
        await RunAsync(t, "struct-table", extension, ("start", "0"), ("count", "10"));
        Assert.Equal(["#0: id=1 flags=0x0000 value=0", "#1: id=2 flags=0x0010 value=1000", "#2: id=3 flags=0x0020 value=2000", "#3: id=4 flags=0x0030 value=3000"],
            t.OutputText.Where(l => l.StartsWith('#')));
    }

    // ---- JavaScript だけ ----

    [Fact]
    [Trait("TC", "TC-AUTO-46-01")]
    public async Task Strings_to_csv_saves_the_found_strings()
    {
        ScriptTestHost t = Create();
        t.Open([.. "\0\0hello world\0\0second string here\0"u8.ToArray()]);
        string csv = Path.Combine(t.Root, "out.csv");
        await RunAsync(t, "strings-to-csv", ".js", ("minLength", "5"), ("save", csv));
        string[] lines = File.ReadAllLines(csv);
        Assert.Equal("offset,length,encoding,text", lines[0]);
        Assert.Contains(lines, l => l.EndsWith(",\"hello world\"", StringComparison.Ordinal) && l.StartsWith("0x2,11,", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith(",\"second string here\"", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-46-01")]
    public async Task High_entropy_lists_the_ranges_above_the_threshold()
    {
        ScriptTestHost t = Create();
        byte[] data = new byte[3 * 4096];
        new Random(7).NextBytes(data.AsSpan(4096, 4096));
        t.Open(data);
        await RunAsync(t, "high-entropy", ".js", ("blockSize", "4096"), ("threshold", "7"));
        Assert.Single(t.OutputText, l => l.StartsWith("0x1000 +4096 entropy 7.", StringComparison.Ordinal));
        Assert.Contains("1 region(s) with entropy >= 7", t.OutputText);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-46-01")]
    public async Task Auto_template_applies_the_detected_template_to_the_open_documents()
    {
        ScriptTestHost t = Create();
        t.Open(TestDataCatalog.PeX64(), "pe.exe");
        t.Open([1, 2, 3, 4], "plain.bin");
        await RunAsync(t, "auto-template", ".js", ("once", "true"));
        Assert.Contains(t.OutputText, l => l.StartsWith("pe.exe: applied ", StringComparison.Ordinal));
        Assert.Contains("plain.bin: no template matches", t.OutputText);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-46-01")]
    public async Task Batch_patch_replaces_the_pattern_in_every_file_of_the_folder()
    {
        ScriptTestHost t = Create();
        string folder = Path.Combine(t.Root, "files");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), [0, 0xDE, 0xAD, 0, 0xDE, 0xAD]);
        File.WriteAllBytes(Path.Combine(folder, "b.bin"), [1, 2, 3]);
        t.Open([9, 9, 9], "anchor.bin");

        await RunAsync(t, "batch-patch", ".js", ("folder", folder), ("find", "DE AD"), ("replace", "BE EF"));

        Assert.Equal([0, 0xBE, 0xEF, 0, 0xBE, 0xEF], File.ReadAllBytes(Path.Combine(folder, "a.bin")));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(folder, "b.bin")));
        Assert.Contains("1 file(s) patched", t.OutputText);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-46-01")]
    public async Task Save_selection_writes_the_selected_range_to_a_file()
    {
        ScriptTestHost t = Create();
        CoreDocumentHost doc = t.Open(Sequence(256));
        t.OnUi(() => doc.Editor.Select(16, 32));
        string target = Path.Combine(t.Root, "part.bin");
        await RunAsync(t, "save-selection", ".js", ("save", target));
        Assert.Equal(Sequence(256)[16..48], File.ReadAllBytes(target));
    }
}
