using HexEditor.Core.FileSystems;
using HexEditor.Core.Sources;
using HexEditor.TestData.FileSystemImages;

namespace HexEditor.Core.Tests.FileSystems;

/// <summary>テスト用イメージ (TD-FOR-*) をメモリ上で読む (ディスクに書かない)。</summary>
public sealed class SparseImageReader(SparseImage image) : IVolumeReader
{
    public long Length => image.Length;

    public int Reads;

    public ReadResult Read(long offset, Span<byte> buffer)
    {
        Interlocked.Increment(ref Reads);
        if (offset < 0 || offset >= image.Length)
        {
            return new ReadResult(0);
        }

        int n = (int)Math.Min(buffer.Length, image.Length - offset);
        image.Read(offset, buffer[..n]);
        return new ReadResult(n);
    }
}

/// <summary>テスト用イメージの読み込みと、ファイルシステムを開く補助。</summary>
public static class FsTestImages
{
    public static IVolumeReader Reader(string id) => new SparseImageReader(FileSystemImageSet.Image(id));

    public static IFileSystem Open(string id, FileSystemOpenOptions? options = null) => FileSystemRegistry.Open(Reader(id), options);

    /// <summary>ディレクトリの項目を名前で探す。</summary>
    public static FsEntry Child(IFileSystem fs, FsEntry dir, string name) =>
        fs.ListDirectory(dir).FirstOrDefault(e => e.Name == name) ?? throw new KeyNotFoundException(name);

    public static byte[] ReadAll(IVolumeReader reader)
    {
        byte[] b = new byte[reader.Length];
        reader.Read(0, b);
        return b;
    }

    public static string Sha256(byte[] data) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
}
