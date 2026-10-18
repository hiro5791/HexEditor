using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Tools.Checksums;

/// <summary>
/// ファイルのハッシュを、ファイル全体をメモリに読まずに計算する (TOOL-18・TOOL-19)。<c>RandomAccess.Read</c> で 1 MiB ずつ読み、
/// 1 チャンクごとにキャンセルを確かめる。他のプログラムが開いているファイルも読めるよう、共有を許可して開く。
/// </summary>
public static class FileHashing
{
    public const int ChunkSize = 1024 * 1024;

    /// <summary>
    /// <paramref name="path"/> のハッシュ値を返す。<paramref name="operation"/> があれば、処理済みのバイト数を
    /// <paramref name="baseBytes"/> からの累計で報告する。
    /// </summary>
    public static byte[] Compute(string path, HashAlgorithmInfo algorithm, LongRunningOperation? operation = null, long baseBytes = 0,
        CancellationToken cancellationToken = default, HashParameters? parameters = null)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle =
            File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        long length = RandomAccess.GetLength(handle);
        IHasher hasher = algorithm.CreateHasher(parameters);
        (hasher as ILengthPrefixedHasher)?.DeclareLength(length);
        byte[] buffer = new byte[(int)Math.Min(ChunkSize, Math.Max(1, length))];
        long position = 0;
        while (position < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - position)), position);
            if (read <= 0)
            {
                break;
            }

            hasher.Append(buffer.AsSpan(0, read));
            position += read;
            operation?.Report(baseBytes + position);
        }

        return hasher.Finish();
    }
}
