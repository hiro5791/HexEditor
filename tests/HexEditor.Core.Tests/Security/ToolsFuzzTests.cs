using System.Diagnostics;
using System.Net;
using System.Text;
using HexEditor.Core.Printing;
using HexEditor.Core.Remote.Gdb;
using HexEditor.Core.Remote.Udp;
using HexEditor.Core.Templates.Online;
using HexEditor.Core.Tools.Checksums;

namespace HexEditor.Core.Tests.Security;

/// <summary>
/// ツールと通信の信頼できない入力 (TOOL-03 の TrueType のサブセット、TOOL-19 のチェックサムファイル、TPL-36 の索引、ENG-40 の GDB の応答、
/// ENG-42 の UDP) のファジング (フェーズ 4 の監査)。
/// </summary>
public sealed class ToolsFuzzTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "fuzz-tools-" + Guid.NewGuid().ToString("N"));

    public ToolsFuzzTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- TrueType ----

    /// <summary>壊れたフォントは読み込みで null になるか、読み込めたものはグリフの問い合わせ・サブセットが例外にならない。</summary>
    [Fact]
    public void TrueType_reader_and_subsetter_survive_mutated_fonts()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "consola.ttf");
        Assert.True(File.Exists(path), "Consolas (consola.ttf) is part of every Windows installation");
        byte[] font = File.ReadAllBytes(path);
        TrueTypeFont original = TrueTypeFont.Load(font)!;
        Assert.NotNull(original);

        // 表のディレクトリ・head・hhea・maxp・cmap・loca の付近に変異を集める (ファイル全体の乱数の変異はほとんどグリフの中に当たる)。
        var random = new Random(301);
        var cases = new List<byte[]>();
        for (int i = 0; i < 1500; i++)
        {
            byte[] data = (byte[])font.Clone();
            int edits = random.Next(1, 4);
            for (int e = 0; e < edits; e++)
            {
                int at = random.Next(3) == 0 ? random.Next(12 + 16 * 24) : random.Next(Math.Min(data.Length, 64 * 1024));
                data[at] = random.Next(3) switch { 0 => 0xFF, 1 => 0x00, _ => (byte)random.Next(256) };
            }

            cases.Add(data);
        }

        cases.Add(font[..100]);
        cases.Add([.. "ttcf"u8, 0, 1, 0, 0, 0x7F, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0]);
        Fuzz.Run(cases, data =>
        {
            TrueTypeFont? f = TrueTypeFont.Load(data);
            int faces = TrueTypeFont.FaceCount(data);
            Assert.InRange(faces, 0, Math.Max(1, data.Length / 4));
            if (f is null)
            {
                return;
            }

            var glyphs = new List<int>();
            foreach (int cp in new[] { 'A', 'z', '0', 0x3042, 0x1F600, 0x10FFFF, -1, 0 })
            {
                int g = f.GlyphIndex(cp);
                glyphs.Add(g);
                _ = f.Advance(g);
                _ = f.AdvanceEm(g);
            }

            _ = f.IsRegular;
            byte[] subset = f.Subset(glyphs);
            Assert.True(subset.Length <= data.Length + 64 * 1024 + 8 * f.NumGlyphs, $"subset {subset.Length} bytes from {data.Length}");
        }, describe: d => $"{d.Length} bytes", maxAllocatedBytes: 256L * 1024 * 1024);
    }

    /// <summary>ttc の書体の数が巨大な値でも、数えるだけで止まる (書体ごとに読み込みを試さない大きさ)。</summary>
    [Fact]
    public void TrueType_collection_face_count_is_bounded_by_the_file_size()
    {
        byte[] ttc = [.. "ttcf"u8, 0, 1, 0, 0, 0x7F, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0];
        Assert.InRange(TrueTypeFont.FaceCount(ttc), 0, ttc.Length / 4);
        Assert.Null(TrueTypeFont.Load(ttc, 5));
    }

    // ---- チェックサムファイル ----

    [Fact]
    public void Checksum_parser_is_linear_on_pathological_lines()
    {
        string spaces = "a" + new string(' ', 200_000) + "x";
        string tabs = string.Concat(Enumerable.Repeat("a \t", 70_000));
        string parens = "MD5 (" + new string(')', 200_000) + " = zz";
        string hex = new string('0', 200_000) + " " + new string(' ', 50_000);
        string many = string.Join('\n', Enumerable.Repeat("0123456789abcdef0123456789abcdef  file name.bin", 100_000));
        foreach ((string content, string name) in new[] { (spaces, "x.sfv"), (spaces, "x.txt"), (tabs, "x.sfv"), (parens, "x.md5"), (hex, "SHA256SUMS"), (many, "x.md5") })
        {
            var watch = Stopwatch.StartNew();
            Fuzz.Run([content], c => ChecksumReader.Parse(c, Path.Combine(_dir, name)), timeoutMs: 20_000, describe: c => name + " " + c.Length);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"{name}: {watch.Elapsed}");
        }
    }

    [Fact]
    public void Checksum_parser_survives_mutated_files()
    {
        string seed = "d41d8cd98f00b204e9800998ecf8427e  empty.bin\n\\e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 *a\\\\b\\nc\n"
            + "SHA256 (x y.bin) = e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\n; comment\nfile.bin DEADBEEF\n#x\n";
        var random = new Random(1901);
        var cases = Enumerable.Range(0, 3000).Select(_ => Fuzz.MutateText(random, seed, "0123456789abcdefABCDEF ()=*\\\n\t;#")).ToList();
        string[] names = ["x.md5", "x.sfv", "SHA256SUMS", "x.txt", "checksums.sha1"];
        int n = 0;
        Fuzz.Run(cases, c => ChecksumReader.Parse(c, Path.Combine(_dir, names[n++ % names.Length])), describe: c => c);
    }

    // ---- オンラインのテンプレートの索引 ----

    [Fact]
    public void Template_index_parser_only_throws_its_format_error()
    {
        string seed = """
            { "name": "Test", "templates": [ { "id": "hxtest", "name": { "en": "HX", "ja": "HX" }, "description": "d", "author": "a",
              "version": "1.0.0", "license": "MIT", "categories": ["test"], "extensions": ["hxts"], "mime": [], "minAppVersion": "0.1.0",
              "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "size": 123, "kind": "hxt", "usesExtensions": false,
              "file": "hxtest-1.0.0.hxt" } ] }
            """;
        var random = new Random(3601);
        var cases = new List<string> { seed, "{\"templates\":[],\"templates\":[]}", "{\"templates\":[{\"id\":\"a\",\"id\":\"b\"}]}", "[]", "null", "\"x\"",
            new string('[', 100) + new string(']', 100) };
        cases.AddRange(Enumerable.Range(0, 3000).Select(_ => Fuzz.MutateText(random, seed, "{}[],:\"0123456789abcdefnulltrue ")));
        Fuzz.Run(cases, c => TemplateRepositoryIndex.Parse(Encoding.UTF8.GetBytes(c)), allowed: ex => ex is TemplateIndexFormatException, describe: c => c);
    }

    /// <summary>Windows の予約されたデバイス名 (CON・NUL など) は、テンプレートの ID (保存するファイル名) に使えない。</summary>
    [Theory]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("aux.x")]
    [InlineData("com1")]
    [InlineData("LPT9.y")]
    public void Template_ids_reject_reserved_device_names(string id) => Assert.False(TemplateRepositoryIndex.IsSafeId(id));

    // ---- GDB ----

    /// <summary>決まった応答 (変異させたもの) を返すストリーム。書き込みは捨てる。</summary>
    private sealed class ScriptedStream(byte[] replies) : Stream
    {
        private int _pos;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, replies.Length - _pos);
            Array.Copy(replies, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }

    private static byte[] Packet(string text) => [.. "+"u8, .. GdbClient.Frame(Encoding.ASCII.GetBytes(text))];

    [Fact]
    public void Gdb_client_survives_mutated_replies()
    {
        byte[] script =
        [
            .. Packet("PacketSize=4000;QStartNoAckMode+;multiprocess+"),
            .. Packet("OK"),
            .. GdbClient.Frame("S05"u8),
            .. GdbClient.Frame("O48656c6c6f"u8),
            .. GdbClient.Frame(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 64).Select(i => i.ToString("x2"))))),
            .. GdbClient.Frame("E14"u8),
            .. GdbClient.Frame("00*}00"u8),
            .. GdbClient.Frame("OK"u8),
            .. GdbClient.Frame("OK"u8),
        ];
        Fuzz.Run(Fuzz.Cases(4001, [script], mutations: 3000, randoms: 200), replies =>
        {
            GdbClient client;
            try
            {
                client = GdbClient.OpenAsync(new ScriptedStream(replies), TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch (GdbConnectionException)
            {
                return;
            }

            using (client)
            {
                Assert.InRange(client.PacketSize, 64, GdbClient.MaxPacketSize);
                byte[] buffer = new byte[64];
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        int n = client.ReadMemoryAsync(0x1000, buffer).GetAwaiter().GetResult();
                        Assert.InRange(n, 0, buffer.Length);
                    }
                    catch (GdbErrorReplyException)
                    {
                    }
                }

                client.WriteMemoryAsync(0x1000, new byte[] { 1, 2, 3 }).GetAwaiter().GetResult();
            }
        }, allowed: ex => ex is GdbConnectionException or GdbErrorReplyException);
    }

    /// <summary>連長圧縮 (<c>*</c>) で巨大に展開される応答は、展開の上限で止まる (短い応答から巨大な文字列を作らない)。</summary>
    [Fact]
    public void Gdb_run_length_expansion_is_bounded()
    {
        var raw = new List<byte>();
        for (int i = 0; i < 10_000; i++)
        {
            raw.AddRange([(byte)'0', (byte)'*', 0xFF]);
        }

        Assert.Null(GdbClient.Decode(raw, 64 * 1024));

        // '*' の次の文字 - 29 回繰り返す (' ' は 3 回)。
        Assert.Equal("0000", GdbClient.Decode("0* "u8.ToArray()));
    }

    // ---- UDP ----

    /// <summary>長さ 0 のデータグラムを大量に受けても、記録する件数に上限がある (メモリが際限なく増えない)。</summary>
    [Fact]
    public void Udp_capture_bounds_the_number_of_recorded_datagrams()
    {
        using UdpCapture capture = UdpCapture.Start(new UdpCaptureSettings { Port = 0, Limit = UdpCaptureSettings.MinimumLimit, TempDirectory = _dir });
        var from = new IPEndPoint(IPAddress.Loopback, 1);
        for (int i = 0; i < UdpCapture.MaxPackets + 10; i++)
        {
            capture.Record([], from);
        }

        Assert.Equal(UdpCaptureState.LimitReached, capture.State);
        Assert.Equal(UdpCapture.MaxPackets, capture.PacketCount);

        // 古いデータを捨てる設定では、件数の上限でも古いものから捨てて受け続ける。
        capture.Clear();
        capture.DiscardOldData();
        for (int i = 0; i < UdpCapture.MaxPackets + 10; i++)
        {
            capture.Record(i % 2 == 0 ? [] : [(byte)i], from);
        }

        Assert.Equal(UdpCaptureState.Receiving, capture.State);
        Assert.Equal(UdpCapture.MaxPackets, capture.PacketCount);
        Assert.Equal(UdpCapture.MaxPackets + 10, capture.PacketAtIndex(capture.PacketCount - 1)!.Number);
    }
}
