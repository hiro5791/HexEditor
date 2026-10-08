using HexEditor.Core.Files;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Files;

/// <summary>ENG-16 最近使ったファイル、UI-32 の一覧の操作 (Core の部分)。</summary>
public sealed class RecentFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-recent").FullName;
    private DateTime _now = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>TD-ENG-FILES-30: f01.bin〜f30.bin (TD-SEQ-1M の先頭 4 KiB、オフセット 0 をファイルの番号にしたもの)。</summary>
    private string[] Files30()
    {
        string folder = Path.Combine(_dir, "files30");
        Directory.CreateDirectory(folder);
        return [.. Enumerable.Range(1, 30).Select(i =>
        {
            byte[] data = new byte[4096];
            for (int k = 0; k < data.Length; k++)
            {
                data[k] = (byte)k;
            }

            data[0] = (byte)i;
            string path = Path.Combine(folder, $"f{i:00}.bin");
            File.WriteAllBytes(path, data);
            return path;
        })];
    }

    /// <summary>開いて閉じる (どちらでも一覧の先頭に移す。UI-32 の仕様 1)。</summary>
    private void OpenAndClose(RecentFileList list, string path)
    {
        list.Record(path, Path.GetFileName(path), _now = _now.AddSeconds(1));
        list.Record(path, Path.GetFileName(path), _now = _now.AddSeconds(1));
    }

    [Fact]
    [Trait(TC, "TC-ENG-16-01")]
    public void KeepsNewestTwentyFiveAndPinnedItems()
    {
        string[] files = Files30();
        var store = new RecentFileStore(_dir);
        var list = new RecentFileList();

        // 1. f01〜f30 を順に開いて閉じる。
        foreach (string f in files)
        {
            OpenAndClose(list, f);
            store.Save(list);
        }

        // 2. recent.json と一覧は f30〜f06 の 25 件で、新しい順。
        var reloaded = new RecentFileList();
        store.Load(reloaded);
        foreach (RecentFileList l in new[] { list, reloaded })
        {
            Assert.Equal(Enumerable.Range(6, 25).Reverse().Select(i => $"f{i:00}.bin"), l.Items.Select(i => i.DisplayName));
        }

        // 3. f10 をピン留めし、f10 以外をもう一度順に開いて閉じる。
        Assert.True(list.Pin(files[9]));
        foreach (string f in files.Where((_, i) => i != 9))
        {
            OpenAndClose(list, f);
        }

        store.Save(list);
        reloaded = new RecentFileList();
        store.Load(reloaded);

        // 4. 先頭が f10 (ピン留め)、その後に新しい 25 件。
        foreach (RecentFileList l in new[] { list, reloaded })
        {
            Assert.Equal("f10.bin", l.Items[0].DisplayName);
            Assert.True(l.Items[0].Pinned);
            Assert.Equal(
                Enumerable.Range(1, 30).Where(i => i != 10).Reverse().Take(25).Select(i => $"f{i:00}.bin"),
                l.Items.Skip(1).Select(i => i.DisplayName));
        }
    }

    [Fact]
    public void ClearKeepsPinnedAndUndoRestoresOrder()
    {
        var list = new RecentFileList();
        string[] paths = [.. Enumerable.Range(1, 4).Select(i => Path.Combine(_dir, $"file{i:00}.bin"))];
        foreach (string p in paths)
        {
            OpenAndClose(list, p);
        }

        list.Pin(paths[0]);
        IReadOnlyList<string> before = [.. list.Items.Select(i => i.Path)];

        RecentClearUndo undo = list.ClearUnpinned();
        Assert.Equal([paths[0]], list.Items.Select(i => i.Path));

        list.Undo(undo);
        Assert.Equal(before, list.Items.Select(i => i.Path));
    }

    [Fact]
    public void ZeroMaxItemsClearsEverythingAndStopsRecording()
    {
        var list = new RecentFileList();
        string a = Path.Combine(_dir, "a.bin");
        OpenAndClose(list, a);
        list.Pin(a);
        int changes = 0;
        list.Changed += (_, _) => changes++;

        list.MaxItems = 0;
        Assert.Empty(list.Items);
        Assert.Equal(1, changes);

        OpenAndClose(list, a);
        Assert.Empty(list.Items);
    }

    [Fact]
    public void MenuShowsPinnedThenTenRecent()
    {
        var list = new RecentFileList();
        for (int i = 1; i <= 15; i++)
        {
            OpenAndClose(list, Path.Combine(_dir, $"m{i:00}.bin"));
        }

        list.Pin(Path.Combine(_dir, "m01.bin"));
        IReadOnlyList<RecentItem> menu = list.MenuEntries();
        Assert.Equal(11, menu.Count);
        Assert.Equal("m01.bin", menu[0].DisplayName);
        Assert.Equal("m15.bin", menu[1].DisplayName);
    }

    [Fact]
    public void PortablePathsAreRelativeToTheExeFolder()
    {
        // UI-32 の仕様 8: E:\HexEditor\ から見た E:\data\seq.bin は ..\data\seq.bin。ドライブ文字が F: に変わっても開ける。
        var onE = new RecentPathMapper(@"E:\HexEditor");
        Assert.Equal(@"..\data\seq.bin", onE.ToStored(@"E:\data\seq.bin"));
        Assert.Equal(@"C:\other\x.bin", onE.ToStored(@"C:\other\x.bin"));
        Assert.Equal(@"\\server\share\x.bin", onE.ToStored(@"\\server\share\x.bin"));

        var list = new RecentFileList();
        list.Record(@"E:\data\seq.bin", "seq.bin", _now);
        string json = list.ToJson(onE);
        Assert.Contains(@"..\\data\\seq.bin", json);

        var onF = new RecentFileList();
        onF.Load(json, new RecentPathMapper(@"F:\HexEditor"));
        Assert.Equal(@"F:\data\seq.bin", onF.Items.Single().Path);
    }

    [Fact]
    public void NetworkPathsAreRecognizedWithoutTouchingTheNetwork()
    {
        Assert.True(RecentFileList.IsNetworkPath(@"\\192.0.2.1\share\remote.bin"));
        Assert.True(RecentFileList.IsNetworkPath("sftp://host/file.bin"));
        Assert.False(RecentFileList.IsNetworkPath(@"\\.\PhysicalDrive1"));
        Assert.False(RecentFileList.IsNetworkPath(Path.Combine(_dir, "a.bin")));
    }

    [Fact]
    public void PositionsAreKeptPerPathAndClampedToTheLength()
    {
        var store = new DocumentDataStore(Path.Combine(_dir, "documents"));
        string path = Path.Combine(_dir, "seq.bin");
        Assert.Null(store.GetPosition(path));
        Assert.Null(store.SetPosition(new DocumentPosition { Path = path, Cursor = 0x12345, Length = 0x100000 }));

        DocumentPosition? read = store.GetPosition(path.ToUpperInvariant());
        Assert.Equal(0x12345, read!.Cursor);

        // 前回の位置がファイルの長さを超える場合は末尾に置く (ENG-16 の仕様 5)。
        Assert.Equal(0x10000, read.ClampTo(0x10000).Cursor);
    }
}
