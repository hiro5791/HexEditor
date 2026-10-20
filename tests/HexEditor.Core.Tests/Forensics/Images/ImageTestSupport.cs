using System.Security.Cryptography;
using HexEditor.Core.Forensics.Images;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Tests.Forensics.Images;

/// <summary>イメージのテストの共通の部品。出力はテスト用の一時フォルダだけに書き、終わったら消す。</summary>
internal sealed class ImageTempDir : IDisposable
{
    public ImageTempDir(string name = "hexeditor-images") => Path = Directory.CreateTempSubdirectory(name).FullName;

    public string Path { get; }

    public string File(string name)
    {
        string p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        return p;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal static class ImageTestSupport
{
    public const long MiB = 1024 * 1024;
    public const long GiB = 1024 * MiB;

    /// <summary>データソース全体のハッシュ。</summary>
    public static byte[] Hash(IByteSource source, HashAlgorithmName algorithm)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        byte[] buffer = new byte[1 << 20];
        for (long pos = 0; pos < source.Length; pos += buffer.Length)
        {
            int n = (int)Math.Min(buffer.Length, source.Length - pos);
            source.Read(pos, buffer.AsSpan(0, n));
            hash.AppendData(buffer, 0, n);
        }

        return hash.GetHashAndReset();
    }

    /// <summary>ファイルを順に連結したハッシュ。</summary>
    public static byte[] HashFiles(IEnumerable<string> files, HashAlgorithmName algorithm)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        byte[] buffer = new byte[1 << 20];
        foreach (string file in files)
        {
            using FileStream stream = System.IO.File.OpenRead(file);
            int n;
            while ((n = stream.Read(buffer)) > 0)
            {
                hash.AppendData(buffer, 0, n);
            }
        }

        return hash.GetHashAndReset();
    }

    public static byte[] ReadAll(IByteSource source, long offset, int length)
    {
        byte[] data = new byte[length];
        source.Read(offset, data);
        return data;
    }

    /// <summary>取得の入力元の説明 (テスト用)。</summary>
    public static AcquisitionSourceInfo Info(IByteSource source, string path = "test-source") => new()
    {
        Path = path,
        DisplayName = source.DisplayName,
        Capacity = source.Length,
        SectorSize = 512,
        Model = "Test Model",
        SerialNumber = "SERIAL-1",
    };
}

/// <summary>取得中の問い合わせの偽物。</summary>
internal sealed class FakePrompts(bool keep = true, int retries = 0) : IAcquisitionPrompts
{
    public int WriteErrors { get; private set; }

    public bool AskedKeep { get; private set; }

    public Task<bool> RetryAfterWriteErrorAsync(IOException error)
    {
        WriteErrors++;
        return Task.FromResult(WriteErrors <= retries);
    }

    public Task<bool> KeepPartialFilesAsync()
    {
        AskedKeep = true;
        return Task.FromResult(keep);
    }
}
