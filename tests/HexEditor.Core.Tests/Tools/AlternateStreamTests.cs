using System.Diagnostics;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.Core.Tools.Files;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Tools;

/// <summary>ENG-37 NTFS 代替データストリーム (ドキュメントとして開く・保存) と TOOL-21 の管理の処理。NTFS のテスト用フォルダを使う。</summary>
public sealed class AlternateStreamTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-ads").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static bool Ntfs => OperatingSystem.IsWindows() && AlternateStreams.IsSupported(Path.GetTempPath());

    private string Generate() => TestDataCatalog.Generate("TD-ENG-ADS", _dir);

    private DocumentOptions Options() => new() { TempDirectory = Path.Combine(_dir, "recovery") };

    private SaveSettings Settings() => new() { JournalDirectory = Path.Combine(_dir, "journal") };

    /// <summary>保存の計画 → 実行 → 完了。計画の方式を返す。</summary>
    private SaveMethod Save(Document doc)
    {
        SavePlan plan = SavePlanner.Plan(doc, null, Settings());
        Assert.True(plan.CanExecute, $"{plan.Method} {plan.Issue}");
        SavePlanner.Complete(plan, SavePlanner.Execute(plan));
        return plan.Method;
    }

    private static string DirR(string path)
    {
        var info = new ProcessStartInfo("cmd.exe", $"/c dir /r \"{path}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using Process process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    [Fact]
    [Trait(TC, "TC-TOOL-21-01")]
    public void ZoneIdentifierIsListedWithItsDescriptionAndHeadBytes()
    {
        if (!Ntfs)
        {
            return;
        }

        string path = Generate();
        IReadOnlyList<StreamInfo> streams = AlternateStreams.List(path);
        Assert.Equal(["::$DATA", "Zone.Identifier", "secret"], streams.Select(s => s.DisplayName).OrderBy(n => n == "::$DATA" ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal));
        StreamInfo zone = streams.Single(s => s.Name == "Zone.Identifier");
        Assert.Equal(TestDataCatalog.AdsStreams["Zone.Identifier"].Length, zone.Size);
        Assert.Equal("ZoneIdentifier", AlternateStreams.KnownKey(zone.Name));
        Assert.Null(AlternateStreams.KnownKey("secret"));
        Assert.Equal([0x5B, 0x5A, 0x6F, 0x6E], AlternateStreams.ReadHead(path, "Zone.Identifier")[..4]);
        Assert.Equal(1024, streams.Single(s => s.IsDefault).Size);
    }

    [Fact]
    [Trait(TC, "TC-TOOL-21-02")]
    public void StreamsCanBeCreatedAndDeletedAndMatchDirR()
    {
        if (!Ntfs)
        {
            return;
        }

        string path = Path.Combine(_dir, "ads.bin");
        File.Copy(TestDataCatalog.Generate("TD-SEQ-1M", _dir), path);

        Assert.False(AlternateStreams.IsValidName("a:b"));
        Assert.False(AlternateStreams.IsValidName("a\\b"));
        Assert.False(AlternateStreams.IsValidName("a/b"));
        Assert.False(AlternateStreams.IsValidName("a\u0001b"));
        Assert.False(AlternateStreams.IsValidName(string.Empty));
        Assert.Equal(StreamError.InvalidName, Assert.Throws<StreamException>(() => AlternateStreams.Create(path, "a:b")).Error);

        AlternateStreams.Create(path, "note");
        Assert.Equal(StreamError.AlreadyExists, Assert.Throws<StreamException>(() => AlternateStreams.Create(path, "note")).Error);
        using (var doc = new Document(FileByteSource.Open(path + ":note"), Options()))
        {
            doc.Insert(0, [0x41, 0x42]);
            Assert.Equal(SaveMethod.StreamRewrite, Save(doc));
        }

        Assert.Contains("ads.bin:note:$DATA", DirR(path), StringComparison.Ordinal);
        Assert.Contains(" 2 ads.bin:note:$DATA", DirR(path).Replace("\r", string.Empty), StringComparison.Ordinal);
        Assert.Equal(2, AlternateStreams.List(path).Single(s => s.Name == "note").Size);

        AlternateStreams.Delete(path, "note");
        Assert.DoesNotContain("note", DirR(path), StringComparison.Ordinal);
        Assert.DoesNotContain(AlternateStreams.List(path), s => s.Name == "note");
        Assert.Equal(StreamError.DefaultStream, Assert.Throws<StreamException>(() => AlternateStreams.Delete(path, string.Empty)).Error);
    }

    [Fact]
    public void RenameExportAndImportKeepTheContent()
    {
        if (!Ntfs)
        {
            return;
        }

        string path = Generate();
        AlternateStreams.Rename(path, "secret", "hidden");
        Assert.DoesNotContain(AlternateStreams.List(path), s => s.Name == "secret");
        Assert.Equal(TestDataCatalog.AdsStreams["secret"].Length, AlternateStreams.List(path).Single(s => s.Name == "hidden").Size);
        Assert.Equal(StreamError.AlreadyExists, Assert.Throws<StreamException>(() => AlternateStreams.Rename(path, "hidden", "Zone.Identifier")).Error);

        string exported = Path.Combine(_dir, "out.bin");
        AlternateStreams.Export(path, "hidden", exported);
        Assert.Equal(TestDataCatalog.AdsStreams["secret"], File.ReadAllBytes(exported));

        File.WriteAllBytes(exported, [1, 2, 3]);
        AlternateStreams.Import(path, "hidden", exported);
        Assert.Equal([1, 2, 3], AlternateStreams.ReadHead(path, "hidden"));
        Assert.Equal(3, AlternateStreams.List(path).Single(s => s.Name == "hidden").Size);
        AlternateStreams.Import(path, "fresh", exported);
        Assert.Contains(AlternateStreams.List(path), s => s.Name == "fresh");
    }

    [Fact]
    public void FoldersAreListedAndStreamPathsAreRecognised()
    {
        if (!Ntfs)
        {
            return;
        }

        string folder = Directory.CreateDirectory(Path.Combine(_dir, "sub")).FullName;
        Assert.Empty(AlternateStreams.List(folder));
        AlternateStreams.Create(folder, "tag");
        Assert.Equal(["tag"], AlternateStreams.List(folder).Select(s => s.Name));

        Assert.True(AlternateStreams.IsStreamPath(@"C:\dir\a.bin:Zone.Identifier"));
        Assert.False(AlternateStreams.IsStreamPath(@"C:\dir\a.bin"));
        Assert.False(AlternateStreams.IsStreamPath(@"C:\di:r\a.bin"));
        Assert.Equal((@"C:\dir\a.bin", "Zone.Identifier"), AlternateStreams.SplitStreamPath(@"C:\dir\a.bin:Zone.Identifier:$DATA"));
        Assert.Null(AlternateStreams.SplitStreamPath(@"C:\dir\a.bin"));
        Assert.Equal("Zone.Identifier", AlternateStreams.ParseName(":Zone.Identifier:$DATA"));
        Assert.Equal(string.Empty, AlternateStreams.ParseName("::$DATA"));
        Assert.Null(AlternateStreams.ParseName(":$I30:$INDEX_ALLOCATION"));
    }

    [Fact]
    [Trait(TC, "TC-TOOL-21-03")]
    public void VolumesWithoutNamedStreamsAreReportedAsUnsupported()
    {
        // FAT32 のボリュームの情報 (FILE_NAMED_STREAMS なし) を返す代わりのプロバイダ。仮想ディスクでの確認は UI のテスト (管理者の CI)。
        Assert.False(AlternateStreams.IsSupported(Path.Combine(_dir, "x.txt"), new FakeVolumes(new VolumeInfo("F:", "FAT32", 1) { SupportsNamedStreams = false })));
        Assert.True(AlternateStreams.IsSupported(Path.Combine(_dir, "x.txt"), new FakeVolumes(new VolumeInfo("C:", "NTFS", 1))));
        Assert.True(AlternateStreams.IsSupported(Path.Combine(_dir, "x.txt"), new FakeVolumes(new VolumeInfo("R:", "ReFS", 1))));
        Assert.True(AlternateStreams.IsRefs(Path.Combine(_dir, "x.txt"), new FakeVolumes(new VolumeInfo("R:", "ReFS", 1))));
        Assert.True(AlternateStreams.IsSupported(Path.Combine(_dir, "x.txt"), new FakeVolumes(null)));
    }

    private sealed class FakeVolumes(VolumeInfo? volume) : IVolumeInfoProvider
    {
        public VolumeInfo? GetVolume(string folder) => volume;
    }

    [Fact]
    [Trait(TC, "TC-ENG-37-01")]
    public void ZoneIdentifierCanBeOpenedAsADocument()
    {
        if (!Ntfs)
        {
            return;
        }

        string path = Generate();
        using var doc = new Document(FileByteSource.Open(path + ":Zone.Identifier"), Options());
        Assert.Equal("ads.bin:Zone.Identifier", doc.Source.DisplayName);
        byte[] content = new byte[doc.Length];
        doc.Current.Read(0, content);
        Assert.Equal(TestDataCatalog.AdsStreams["Zone.Identifier"], content);
        Assert.StartsWith("[ZoneTransfer]", Encoding.ASCII.GetString(content), StringComparison.Ordinal);
        Assert.Contains("ZoneId=3", Encoding.ASCII.GetString(content), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TC, "TC-ENG-37-02")]
    public void WritingToAStreamInPlaceAndWithALengthChangeMatchesDirR()
    {
        if (!Ntfs)
        {
            return;
        }

        string path = Generate();
        string stream = path + ":secret";
        using var doc = new Document(FileByteSource.Open(stream), Options());
        Assert.Equal(16, doc.Length);

        // 1. 長さが変わらない: その場保存。
        doc.Overwrite(0, [0xDE, 0xAD, 0xBE, 0xEF]);
        Assert.Equal(SaveMethod.InPlace, Save(doc));

        // 2. 末尾に 100 バイト挿入: 長さが変わる保存 (置き換えは使えない)。
        doc.Insert(doc.Length, Enumerable.Repeat((byte)0x7A, 100).ToArray());
        Assert.Equal(SaveMethod.StreamRewrite, Save(doc));

        Assert.Contains(" 116 ads.bin:secret:$DATA", DirR(path).Replace("\r", string.Empty), StringComparison.Ordinal);
        byte[] expected = new byte[doc.Length];
        doc.Current.Read(0, expected);
        Assert.Equal(116, expected.Length);
        Assert.Equal([0xDE, 0xAD, 0xBE, 0xEF], expected[..4]);
        using (var direct = new FileStream(stream, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            byte[] actual = new byte[direct.Length];
            direct.ReadExactly(actual);
            Assert.Equal(expected, actual);
        }

        // 保存の後も、もう一度編集して保存できる (新しいデータソースが使われている)。
        doc.Delete(0, 50);
        Assert.Equal(SaveMethod.StreamRewrite, Save(doc));
        Assert.Equal(66, AlternateStreams.List(path).Single(s => s.Name == "secret").Size);
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(_dir, "journal"), "*", SearchOption.AllDirectories), f => f.EndsWith(".adsold", StringComparison.Ordinal) || f.EndsWith(".adsnew", StringComparison.Ordinal));
        // ファイルのメインのストリームは変わらない。
        Assert.Equal(1024, new FileInfo(path).Length);
    }

    [Fact]
    public void StreamRewriteAboveTheJournalLimitAsksFirstAndCanBeWrittenWithoutAJournal()
    {
        if (!Ntfs)
        {
            return;
        }

        string path = Generate();
        using var doc = new Document(FileByteSource.Open(path + ":secret"), Options());
        doc.Insert(0, [1]);
        SavePlan plan = SavePlanner.Plan(doc, null, Settings() with { JournalLimit = 8 });
        Assert.Equal((SaveMethod.StreamRewrite, SaveIssue.JournalTooLarge), (plan.Method, plan.Issue));
        Assert.True(plan.IsStream);
        SavePlan unprotected = SavePlanner.WriteWithoutJournal(plan);
        Assert.Equal(SaveMethod.StreamRewriteUnprotected, unprotected.Method);
        SavePlanner.Complete(unprotected, SavePlanner.Execute(unprotected));
        Assert.Equal(17, AlternateStreams.List(path).Single(s => s.Name == "secret").Size);
    }

    [Fact]
    [Trait(TC, "TC-ENG-37-03")]
    public void SafeSaveOfTheMainStreamKeepsTheAlternateStreams()
    {
        if (!Ntfs)
        {
            return;
        }

        string path = Generate();
        var before = AlternateStreams.List(path).Where(s => !s.IsDefault).ToDictionary(s => s.Name, s => AlternateStreams.ReadHead(path, s.Name, 1024));
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Insert(0, new byte[10]);
        Assert.Equal(SaveMethod.Safe, Save(doc));
        Assert.Equal(1034, new FileInfo(path).Length);

        var after = AlternateStreams.List(path).Where(s => !s.IsDefault).ToDictionary(s => s.Name, s => AlternateStreams.ReadHead(path, s.Name, 1024));
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach ((string name, byte[] content) in before)
        {
            Assert.Equal(content, after[name]);
        }
    }

    [Fact]
    public void FailedRewriteRestoresTheOldContentFromTheJournal()
    {
        if (!Ntfs)
        {
            return;
        }

        string path = Generate();
        using var doc = new Document(FileByteSource.Open(path + ":secret"), Options());
        doc.Insert(0, [1, 2, 3]);

        // 保存の途中で書けなくなる状況 (ディスクの空き容量不足など) を、最初の書き込みの直後の失敗で作る。
        SavePlan plan = SavePlanner.Plan(doc, null, Settings());
        Assert.Equal(SaveMethod.StreamRewrite, plan.Method);
        StreamRewriteSaver.AfterFirstWrite = () => throw new IOException("disk full");
        try
        {
            Assert.IsType<InPlaceSaveRolledBackException>(Assert.ThrowsAny<IOException>(() => SavePlanner.Execute(plan)));
        }
        finally
        {
            StreamRewriteSaver.AfterFirstWrite = null;
        }

        SavePlanner.Abort(plan);
        Assert.Equal(TestDataCatalog.AdsStreams["secret"], AlternateStreams.ReadHead(path, "secret", 64));
        Assert.True(doc.IsModified);
    }
}
