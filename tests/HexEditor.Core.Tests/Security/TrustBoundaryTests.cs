using System.IO.Compression;
using System.Text;
using HexEditor.Core.Patches;
using HexEditor.Core.Processes;
using HexEditor.Core.Tools.Files;

namespace HexEditor.Core.Tests.Security;

/// <summary>信頼の境界の個別の確かめ (フェーズ 4 の監査): 安全な削除の拒否 (TOOL-17)、自己適用型パッチの中身 (EDIT-44)、スナップショット (ENG-35)。</summary>
public sealed class TrustBoundaryTests
{
    /// <summary>リンクをたどった先だけを差し替えられる、最小のファイルシステム (中身は持たない)。</summary>
    private sealed class LinkedDisk(Dictionary<string, string> links, HashSet<string> files) : IShredFileSystem
    {
        public bool FileExists(string path) => files.Contains(path);

        public bool DirectoryExists(string path) => files.Any(f => f.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

        public IEnumerable<string> EnumerateFiles(string directory) => files.Where(f => f.StartsWith(directory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

        public long LengthOf(string path, string? streamName = null) => 0;

        public IShredStream OpenExclusive(string path, string? streamName = null) => throw new IOException("not used");

        public IReadOnlyList<string> NamedStreams(string path) => [];

        public string RenameRandom(string path) => path;

        public void DeleteFile(string path)
        {
        }

        public void DeleteEmptyDirectory(string path)
        {
        }

        public string FinalPath(string path) =>
            links.FirstOrDefault(l => path.StartsWith(l.Key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } link
                ? link.Value + path[link.Key.Length..]
                : path;
    }

    /// <summary>ジャンクション・シンボリックリンクでシステムフォルダやドライブのルートを指すパスも、安全な削除の対象にできない。</summary>
    [Fact]
    public void Secure_delete_checks_the_link_target()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var disk = new LinkedDisk(
            new(StringComparer.OrdinalIgnoreCase) { [@"V:\work\sys"] = windows, [@"V:\work\root"] = @"D:\", [@"V:\work\open-link.bin"] = @"V:\data\open.bin" },
            new(StringComparer.OrdinalIgnoreCase) { @"V:\work\sys\win.ini", @"V:\work\root\x.bin", @"V:\work\open-link.bin", @"V:\work\plain.bin" });
        Assert.Equal(ShredRejection.SystemFolder, SecureDelete.Check(@"V:\work\sys\win.ini", disk, []));
        Assert.Equal(ShredRejection.SystemFolder, SecureDelete.Check(@"V:\work\sys", disk, []));
        Assert.Equal(ShredRejection.OpenInApp, SecureDelete.Check(@"V:\work\open-link.bin", disk, [@"V:\data\open.bin"]));
        Assert.Equal(ShredRejection.None, SecureDelete.Check(@"V:\work\plain.bin", disk, [@"V:\data\open.bin"]));
    }

    /// <summary><c>\\?\</c> の形のパスでも、システムフォルダとドライブのルートの判定を逃れない。</summary>
    [Fact]
    public void Secure_delete_normalizes_device_paths()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var disk = new LinkedDisk([], []);
        Assert.Equal(ShredRejection.SystemFolder, SecureDelete.Check(@"\\?\" + Path.Combine(windows, "win.ini"), disk, []));
        Assert.Equal(ShredRejection.SystemFolder, SecureDelete.Check(@"\\.\" + windows, disk, []));
        Assert.Equal(ShredRejection.DriveRoot, SecureDelete.Check(@"\\?\C:\", disk, []));
        Assert.Equal(ShredRejection.DriveRoot, SecureDelete.Check(@"\\?\C:", disk, []));
        Assert.Equal(@"\\server\share\a", SecureDelete.WithoutDevicePrefix(@"\\?\UNC\server\share\a"));
    }

    /// <summary>
    /// 自己適用型パッチの既定の対象ファイル名はファイル名だけを使う (フォルダの部分で別の場所のファイルを勧めない)。表示する文字列の
    /// 書式が壊れていても、読み込みは誤りにならない (適用プログラムが落ちない)。
    /// </summary>
    [Theory]
    [InlineData(@"..\..\Windows\System32\kernel32.dll", "kernel32.dll")]
    [InlineData("C:/x/y/target.bin", "target.bin")]
    [InlineData("plain.bin", "plain.bin")]
    [InlineData("dir\\", null)]
    public void HexPatch_default_file_name_is_a_bare_file_name(string name, string? expected)
    {
        string identity = "{\"length\":4,\"headSha256\":\"\",\"middleSha256\":\"\",\"tailSha256\":\"\"}";
        string json = $"{{\"format\":\"hexpatch\",\"version\":1,\"source\":{identity},\"target\":{identity},\"ops\":[],"
            + $"\"defaultFileName\":{System.Text.Json.JsonSerializer.Serialize(name)},\"message\":5}}";
        HexPatchManifest manifest = HexPatchManifest.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(expected, manifest.DefaultFileName);
        Assert.Null(manifest.Message);
    }

    /// <summary>宣言より大きく展開される hexpatch.json (圧縮爆弾) は上限で止まる。</summary>
    [Fact]
    public void HexPatch_manifest_reading_is_bounded()
    {
        var stream = new LimitedReadStream(new MemoryStream(new byte[1000]), 100);
        byte[] buffer = new byte[64];
        Assert.Equal(64, stream.Read(buffer));
        Assert.Throws<InvalidDataException>(() => stream.Read(buffer));
    }

    /// <summary>スナップショットのメタデータの長さが巨大・負の値なら、大きな配列を確保せずに形式の誤りにする。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(0)]
    public void Snapshot_metadata_length_is_validated(int length)
    {
        var ms = new MemoryStream();
        ms.Write(HexSnapshot.Magic);
        ms.Write(BitConverter.GetBytes(length));
        ms.Write(new byte[16]);
        ms.Position = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => HexSnapshot.ReadMetadata(ms));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024);
    }

    /// <summary>重なる領域・アドレス空間の外の領域を持つスナップショットは開かない。</summary>
    [Theory]
    [InlineData("""[{"baseAddress":0,"size":8192,"protect":4,"type":"Private","fileOffset":4096},{"baseAddress":4096,"size":4096,"protect":4,"type":"Private","fileOffset":8192}]""")]
    [InlineData("""[{"baseAddress":9223372036854775000,"size":4096,"protect":4,"type":"Private","fileOffset":4096}]""")]
    [InlineData("""[{"baseAddress":0,"size":-1,"protect":4,"type":"Private","fileOffset":4096}]""")]
    [InlineData("""[{"baseAddress":0,"size":4096,"protect":4,"type":"Private","fileOffset":-4096}]""")]
    [InlineData("""[null]""")]
    public void Snapshot_regions_are_validated(string regions)
    {
        string json = "{\"processName\":\"x\",\"addressLimit\":1048576,\"regions\":" + regions + "}";
        byte[] meta = Encoding.UTF8.GetBytes(json);
        var ms = new MemoryStream();
        ms.Write(HexSnapshot.Magic);
        ms.Write(BitConverter.GetBytes(meta.Length));
        ms.Write(meta);
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => HexSnapshot.ReadMetadata(ms));
    }

    /// <summary>自己適用型パッチの中の data.bin が壊れていても (zip の圧縮方式が不明など)、適用はパッチの誤りになりドキュメントは変わらない。</summary>
    [Fact]
    public void Corrupt_hexpatch_data_is_a_patch_error()
    {
        byte[] source = [1, 2, 3, 4];
        string identity(byte[] d) => SourceIdentity.Compute(new MemoryStream(d)) is var id
            ? $"{{\"length\":{id.Length},\"headSha256\":\"{id.HeadSha256}\",\"middleSha256\":\"{id.MiddleSha256}\",\"tailSha256\":\"{id.TailSha256}\"}}"
            : string.Empty;
        string json = $"{{\"format\":\"hexpatch\",\"version\":1,\"source\":{identity(source)},\"target\":{identity([9, 2, 3, 4])},"
            + "\"ops\":[{\"op\":\"replace\",\"src\":0,\"srcLen\":1,\"dst\":0,\"dstLen\":1}]}";
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (Stream s = zip.CreateEntry("hexpatch.json").Open())
            {
                s.Write(Encoding.UTF8.GetBytes(json));
            }

            using (Stream s = zip.CreateEntry("data.bin", CompressionLevel.NoCompression).Open())
            {
                s.Write([9]);
            }
        }

        byte[] bytes = ms.ToArray();

        // data.bin の圧縮方式 (ローカルヘッダと中央ディレクトリ) を未知の値にする。
        byte[] name = Encoding.ASCII.GetBytes("data.bin");
        for (int i = 0; i + 4 < bytes.Length; i++)
        {
            if ((bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x03 && bytes[i + 3] == 0x04 && bytes.AsSpan(i + 30, name.Length).SequenceEqual(name))
                || (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x01 && bytes[i + 3] == 0x02 && bytes.AsSpan(i + 46, name.Length).SequenceEqual(name)))
            {
                int method = bytes[i + 2] == 0x03 ? i + 8 : i + 10;
                bytes[method] = 99;
            }
        }

        using var doc = new HexEditor.Core.Engine.Document(new HexEditor.Core.Sources.MemoryByteSource(source), Support.DocumentAssert.Options());
        using LoadedPatch patch = PatchFiles.Load(new MemoryStream(bytes));
        PatchException e = Assert.Throws<PatchException>(() => patch.ApplyTo(doc));
        Assert.Equal(PatchError.Corrupt, e.Error);
        Assert.Equal(source, Support.DocumentAssert.ReadAll(doc.Current));
    }
}
