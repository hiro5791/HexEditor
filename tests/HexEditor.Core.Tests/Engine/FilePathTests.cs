using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Engine;

/// <summary>
/// ENG-11 の受け入れ基準 4: 長いパス・絵文字の名前・UNC パス・<c>\\?\</c> で始まるパスのファイルを開ける。タブの名前は
/// データソースの表示名 (<see cref="IByteSource.DisplayName"/>)。
/// </summary>
public sealed class FilePathTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-paths").FullName;

    public void Dispose() => Directory.Delete(@"\\?\" + _dir, recursive: true);

    private static void AssertOpens(string path, string expectedName, byte[] expectedHead)
    {
        using var doc = new Document(FileByteSource.Open(path), Options());
        Assert.Equal(expectedName, doc.Source.DisplayName);
        Assert.Equal(expectedHead, Read(doc.Current, 0, 16));
    }

    private static byte[] Sequence16() => [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

    [Fact]
    [Trait(TC, "TC-ENG-11-03")]
    public void OpensLongEmojiAndExtendedLengthPaths()
    {
        // 前提: TD-ENG-PATH-300 と TD-ENG-EMOJI-NAME を生成する。
        string longPath = TestDataCatalog.Generate("TD-ENG-PATH-300", _dir);
        string emoji = TestDataCatalog.Generate("TD-ENG-EMOJI-NAME", _dir);
        Assert.Equal(300, longPath.Length);
        Assert.Equal("テスト_😀_📦.bin", Path.GetFileName(emoji));

        // 手順 1: 300 文字のパス。手順 2: 絵文字を含む名前。手順 4: \\?\ で始まる 300 文字のパス。
        AssertOpens(longPath, Path.GetFileName(longPath), Sequence16());
        AssertOpens(emoji, "テスト_😀_📦.bin", Sequence16());
        AssertOpens(@"\\?\" + longPath, Path.GetFileName(longPath), Sequence16());
    }

    [UncFact]
    [Trait(TC, "TC-ENG-11-03")]
    public void OpensUncPathThroughTheAdministrativeShare()
    {
        // 手順 3: \\localhost\C$\ で始まる TD-SEQ-1M の UNC パス。
        string local = TestDataCatalog.Get("TD-SEQ-1M");
        string unc = UncFactAttribute.ToAdministrativeShare(local);
        Assert.StartsWith(@"\\localhost\", unc, StringComparison.OrdinalIgnoreCase);
        AssertOpens(unc, "TD-SEQ-1M.bin", Sequence16());
    }

    /// <summary>管理共有 (\\localhost\C$) を参照できない環境 (共有の無効化・ポリシー) では実行しない。</summary>
    private sealed class UncFactAttribute : FactAttribute
    {
        public UncFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || !Directory.Exists(ToAdministrativeShare(Path.GetTempPath())))
            {
                Skip = @"管理共有 \\localhost\<ドライブ>$ を参照できない環境です (ファイルとプリンターの共有、または管理共有が無効)。";
            }
        }

        public static string ToAdministrativeShare(string localPath)
        {
            string full = Path.GetFullPath(localPath);
            return $@"\\localhost\{full[0]}${full[2..]}";
        }
    }
}
