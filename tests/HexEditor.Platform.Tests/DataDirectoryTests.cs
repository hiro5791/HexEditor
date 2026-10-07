using HexEditor.Platform.Tests.Support;

namespace HexEditor.Platform.Tests;

/// <summary>データフォルダの書き込みの確認と一時フォルダの後始末 (PKG-06 の仕様 4・5、PKG-13 の仕様 4)。</summary>
public sealed class DataDirectoryTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TryPrepareCreatesTheFolderAndLeavesNoProbe()
    {
        string data = _temp.Sub("Data");
        Assert.True(DataDirectory.TryPrepare(data));
        Assert.True(Directory.Exists(data));
        Assert.False(File.Exists(Path.Combine(data, DataDirectory.WriteTestFileName)));
    }

    [Fact]
    public void TryPrepareReportsUnwritableFolder()
    {
        // ファイルと同じ名前のフォルダは作れない (書き込めないメディアの代わり)。
        string blocker = _temp.Sub("blocker");
        File.WriteAllText(blocker, string.Empty);
        Assert.False(DataDirectory.TryPrepare(Path.Combine(blocker, "Data")));
    }

    [Fact]
    public void CleanTempKeepsLockedFoldersAndRecovery()
    {
        string temp = _temp.Sub("temp");
        Directory.CreateDirectory(Path.Combine(temp, "old"));
        File.WriteAllText(Path.Combine(temp, "old", "a.bin"), "x");
        File.WriteAllText(Path.Combine(temp, "stray.tmp"), "x");
        Directory.CreateDirectory(Path.Combine(temp, "inuse"));
        string locked = Path.Combine(temp, "inuse", "lock");
        Directory.CreateDirectory(Path.Combine(temp, "recovery", "doc"));
        File.WriteAllText(Path.Combine(temp, "recovery", "doc", "state.json"), "{}");

        using (new FileStream(locked, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            int deleted = DataDirectory.CleanTemp(temp, keep: Path.Combine(temp, "recovery"));
            Assert.Equal(2, deleted);
            Assert.False(Directory.Exists(Path.Combine(temp, "old")));
            Assert.False(File.Exists(Path.Combine(temp, "stray.tmp")));
            Assert.True(File.Exists(locked));
            Assert.True(File.Exists(Path.Combine(temp, "recovery", "doc", "state.json")));
        }
    }

    [Fact]
    public void DeleteTempAtExitRemovesTheFolder()
    {
        string temp = _temp.Sub("HexEditor-12345678");
        Directory.CreateDirectory(Path.Combine(temp, "recovery", "doc"));
        File.WriteAllText(Path.Combine(temp, "recovery", "doc", "add.bin"), "x");
        DataDirectory.DeleteTempAtExit(temp);
        Assert.False(Directory.Exists(temp));
    }

    [Fact]
    public void DeleteTempAtExitLeavesFilesOfOtherInstances()
    {
        string temp = _temp.Sub("HexEditor-87654321");
        Directory.CreateDirectory(Path.Combine(temp, "other"));
        string locked = Path.Combine(temp, "other", "lock");
        using (new FileStream(locked, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            DataDirectory.DeleteTempAtExit(temp);
            Assert.True(File.Exists(locked));
        }
    }
}
