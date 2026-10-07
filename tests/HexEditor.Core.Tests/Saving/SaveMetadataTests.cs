using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Saving;

/// <summary>ENG-22 の安全な保存で、ファイルの付随情報 (ACL・属性・作成日時・代替データストリーム) とスパースを保つ。</summary>
[SupportedOSPlatform("windows")]
public sealed class SaveMetadataTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-savemeta").FullName;

    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_dir, recursive: true);
    }

    private SaveSettings Settings() => new() { JournalDirectory = Path.Combine(_dir, "journal") };

    /// <summary>保存の計画を立てて実行し、完了を反映する (アプリの保存と同じ順)。</summary>
    private SaveMethod Save(Document doc)
    {
        SavePlan plan = SavePlanner.Plan(doc, null, Settings());
        Assert.True(plan.CanExecute, $"{plan.Method} {plan.Issue}");
        SavePlanner.Complete(plan, SavePlanner.Execute(plan));
        return plan.Method;
    }

    [Fact]
    [Trait("TC", "TC-ENG-22-02")]
    public void SafeSaveKeepsAclAttributesCreationTimeAndStreams()
    {
        // 前提: TD-ENG-ADS をコピーし、隠し属性とアーカイブ属性、Everyone の読み取りの明示的な許可、作成日時を設定する。
        string path = Path.Combine(_dir, "ads.bin");
        File.Copy(TestDataCatalog.Get("TD-ENG-ADS"), path);
        File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Archive);
        var info = new FileInfo(path);
        FileSecurity security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read,
            AccessControlType.Allow));
        info.SetAccessControl(security);
        var created = new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, created);
        Snapshot before = Snapshot.Of(path);
        Assert.Contains("WD", before.Sddl, StringComparison.Ordinal);
        Assert.Equal(TestDataCatalog.AdsStreams.Keys.Order(), before.Streams.Keys.Order());

        // 手順 1: オフセット 0 に 4 バイト挿入して保存する (長さが変わるので安全な保存)。
        using (var doc = new Document(FileByteSource.Open(path), Options() with { TempDirectory = Path.Combine(_dir, "recovery") }))
        {
            doc.Insert(0, [0xDE, 0xAD, 0xBE, 0xEF]);
            Assert.Equal(SaveMethod.Safe, Save(doc));
        }

        // 手順 2: 期待結果。
        Snapshot after = Snapshot.Of(path);
        Assert.Equal(before.Sddl, after.Sddl);
        Assert.Equal(before.Attributes, after.Attributes);
        Assert.Equal(created, after.Created);
        Assert.Equal(before.Streams.Keys.Order(), after.Streams.Keys.Order());
        foreach ((string name, byte[] content) in TestDataCatalog.AdsStreams)
        {
            Assert.Equal(content, after.Streams[name]);
        }

        byte[] saved = File.ReadAllBytes(path);
        Assert.Equal(1028, saved.Length);
        Assert.Equal([0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01], saved[..6]);
    }

    [Fact]
    [Trait("TC", "TC-ENG-22-04")]
    public void SavingSparseFileKeepsItSparse()
    {
        // 前提: TD-ENG-SPARSE-100G-1M をテスト用フォルダに作る (スパースのまま)。
        string path = TestDataCatalog.Generate("TD-ENG-SPARSE-100G-1M", _dir);
        long allocatedBefore = AllocatedSize(path);
        Assert.True(allocatedBefore < 16 * TestDataCatalog.MiB, $"{allocatedBefore:N0}");

        // 手順 1: オフセット 2^31 に 1 バイト挿入して保存する。
        using (var doc = new Document(FileByteSource.Open(path), Options() with { TempDirectory = Path.Combine(_dir, "recovery") }))
        {
            doc.Insert(1L << 31, [0x5A]);
            Assert.True(DocumentSaver.EstimateSize(doc.Current) < 16 * TestDataCatalog.MiB);
            Assert.Equal(SaveMethod.Safe, Save(doc));
        }

        // 手順 2: スパース属性と実使用量。
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.SparseFile));
        long allocated = AllocatedSize(path);
        Assert.True(allocated < 16 * TestDataCatalog.MiB, $"保存後の実使用量: {allocated:N0} バイト");
        Assert.Equal(100 * TestDataCatalog.GiB + 1, new FileInfo(path).Length);

        // 手順 3: 50 GiB + 1 からの 1 MiB が、元の乱数の範囲と一致する。
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(path);
        byte[] actual = new byte[TestDataCatalog.MiB];
        Assert.Equal(actual.Length, RandomAccess.Read(handle, actual, TestDataCatalog.SparseRandomOffset + 1));
        byte[] expected = new byte[TestDataCatalog.MiB];
        TestDataCatalog.Expected("TD-ENG-SPARSE-100G-1M", TestDataCatalog.SparseRandomOffset, expected);
        Assert.Equal(expected, actual);
        byte[] inserted = new byte[3];
        RandomAccess.Read(handle, inserted, (1L << 31) - 1);
        Assert.Equal([0x00, 0x5A, 0x00], inserted);
    }

    /// <summary>ファイルの付随情報の記録。</summary>
    private sealed record Snapshot(string Sddl, FileAttributes Attributes, DateTime Created, Dictionary<string, byte[]> Streams)
    {
        public static Snapshot Of(string path)
        {
            string sddl = new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
            var streams = new Dictionary<string, byte[]>();
            foreach (string name in AlternateStreams(path))
            {
                streams[name] = File.ReadAllBytes(path + ":" + name);
            }

            return new Snapshot(sddl, File.GetAttributes(path), File.GetCreationTimeUtc(path), streams);
        }
    }

    /// <summary>代替データストリームの名前の一覧 (FindFirstStreamW)。メインのストリーム (::$DATA) は含めない。</summary>
    private static List<string> AlternateStreams(string path)
    {
        var names = new List<string>();
        IntPtr find = FindFirstStreamW(path, 0, out StreamData data, 0);
        if (find == new IntPtr(-1))
        {
            return names;
        }

        try
        {
            do
            {
                // 名前は ":secret:$DATA" の形。
                string name = data.Name;
                if (name != "::$DATA")
                {
                    names.Add(name.Split(':')[1]);
                }
            }
            while (FindNextStreamW(find, out data));
        }
        finally
        {
            FindClose(find);
        }

        return names;
    }

    /// <summary>実使用量 (GetCompressedFileSize: スパースの未割り当ての範囲を含まない)。</summary>
    private static long AllocatedSize(string path)
    {
        uint low = GetCompressedFileSizeW(path, out uint high);
        Assert.False(low == uint.MaxValue && Marshal.GetLastPInvokeError() != 0, "GetCompressedFileSize に失敗しました。");
        return ((long)high << 32) | low;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        public long Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string Name;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string fileName, int infoLevel, out StreamData data, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(IntPtr find, out StreamData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr find);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);
}
