using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Tests.Files;

/// <summary>ENG-14 の仕様 1 の 2 つ目: 開くときに書き込めない理由を見つける (読み取り専用のメディア、属性、権限、共有違反)。</summary>
public sealed class FileWriteProbeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-probe").FullName;

    public void Dispose()
    {
        foreach (string file in Directory.GetFiles(_dir))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_dir, recursive: true);
    }

    private sealed class FakeVolumes(bool readOnly) : IVolumeInfoProvider
    {
        public VolumeInfo? GetVolume(string folder) => new("X:", "NTFS", null) { IsReadOnly = readOnly };
    }

    private string NewFile(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    [Fact]
    public void WritableFileHasNoReason()
    {
        string path = NewFile("a.bin");
        DateTime written = File.GetLastWriteTimeUtc(path);
        Assert.Equal(ReadOnlyReason.None, FileWriteProbe.Probe(path, hasReadOnlyAttribute: false, new FakeVolumes(false)));

        // 確かめても内容・更新日時は変わらない。
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Equal(new byte[16], File.ReadAllBytes(path));
    }

    [Fact]
    public void FileHeldWithoutWriteSharingIsASharingViolation()
    {
        // 他のアプリが書き込みのために開き、書き込みを共有していない。
        string path = NewFile("b.bin");
        using var other = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using FileByteSource source = FileByteSource.Open(path);
        Assert.Equal(ReadOnlyReason.SharingViolation, FileWriteProbe.Probe(path, source.HasReadOnlyAttribute, new FakeVolumes(false)));
    }

    [Fact]
    public void OwnReadHandleDoesNotBlockTheProbe()
    {
        // 自分の読み取りのハンドル (読み書き・削除を共有) とは両立する。
        string path = NewFile("c.bin");
        using FileByteSource source = FileByteSource.Open(path);
        Assert.Equal(ReadOnlyReason.None, FileWriteProbe.Probe(path, source.HasReadOnlyAttribute, new FakeVolumes(false)));
    }

    [Fact]
    public void ReadOnlyAttributeIsReported()
    {
        string path = NewFile("d.bin");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        using FileByteSource source = FileByteSource.Open(path);
        Assert.True(source.HasReadOnlyAttribute);
        Assert.Equal(ReadOnlyReason.FileAttribute, FileWriteProbe.Probe(path, source.HasReadOnlyAttribute, new FakeVolumes(false)));
    }

    [Fact]
    public void ReadOnlyMediaWinsOverTheAttribute()
    {
        // 読み取り専用のメディアは解除できないため、属性より先に確かめる。
        string path = NewFile("e.bin");
        Assert.Equal(ReadOnlyReason.ReadOnlyMedia, FileWriteProbe.Probe(path, hasReadOnlyAttribute: true, new FakeVolumes(true)));
    }

    [Fact]
    public void AccessDeniedAndWriteProtectErrorsAreMapped()
    {
        string path = NewFile("f.bin");
        Assert.Equal(ReadOnlyReason.AccessDenied,
            FileWriteProbe.Probe(path, false, new FakeVolumes(false), _ => throw new UnauthorizedAccessException()));
        Assert.Equal(ReadOnlyReason.ReadOnlyMedia,
            FileWriteProbe.Probe(path, false, new FakeVolumes(false), _ => throw new IOException("write protected", unchecked((int)0x80070013))));
        Assert.Equal(ReadOnlyReason.SharingViolation,
            FileWriteProbe.Probe(path, false, new FakeVolumes(false), _ => throw new IOException("lock", unchecked((int)0x80070021))));

        // 理由の分からないエラーでは読み取り専用にしない (保存のときのエラーで知らせる)。
        Assert.Equal(ReadOnlyReason.None, FileWriteProbe.Probe(path, false, new FakeVolumes(false), _ => throw new IOException("other")));
    }
}
