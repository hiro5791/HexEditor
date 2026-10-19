using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.TestData.FileSystemImages;

/// <summary>
/// テスト用のディスク・ボリュームのイメージ (TD-FOR-*)。書いた範囲だけを 64 KiB のブロックで持ち、それ以外は 0。数百 MiB のイメージでも
/// メモリは書いた量だけ使う。ファイルにはスパースファイルとして保存する (書いていないブロックは書かない)。
/// </summary>
public sealed class SparseImage
{
    public const int BlockSize = 64 * 1024;

    private readonly Dictionary<long, byte[]> _blocks = [];

    public SparseImage(long length) => Length = length;

    public long Length { get; private set; }

    /// <summary>書いたブロックの数 (メモリの使用量の目安)。</summary>
    public int BlockCount => _blocks.Count;

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset + data.Length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"write at 0x{offset:X} (+{data.Length}) is outside the image (0x{Length:X})");
        }

        int done = 0;
        while (done < data.Length)
        {
            long at = offset + done;
            long index = at / BlockSize;
            int within = (int)(at % BlockSize);
            int n = Math.Min(data.Length - done, BlockSize - within);
            if (!_blocks.TryGetValue(index, out byte[]? block))
            {
                if (data.Slice(done, n).IndexOfAnyExcept((byte)0) < 0)
                {
                    done += n;
                    continue;
                }

                block = new byte[BlockSize];
                _blocks[index] = block;
            }

            data.Slice(done, n).CopyTo(block.AsSpan(within));
            done += n;
        }
    }

    public void Read(long offset, Span<byte> destination)
    {
        destination.Clear();
        int done = 0;
        while (done < destination.Length && offset + done < Length)
        {
            long at = offset + done;
            long index = at / BlockSize;
            int within = (int)(at % BlockSize);
            int n = (int)Math.Min(Math.Min(destination.Length - done, BlockSize - within), Length - at);
            if (_blocks.TryGetValue(index, out byte[]? block))
            {
                block.AsSpan(within, n).CopyTo(destination.Slice(done, n));
            }

            done += n;
        }
    }

    public byte[] Read(long offset, int count)
    {
        byte[] b = new byte[count];
        Read(offset, b);
        return b;
    }

    /// <summary>1 バイトを XOR で変える (壊したイメージの作成)。</summary>
    public void Xor(long offset, byte mask)
    {
        byte[] b = Read(offset, 1);
        b[0] ^= mask;
        Write(offset, b);
    }

    /// <summary>範囲を 0 にする。</summary>
    public void Zero(long offset, long count)
    {
        byte[] zero = new byte[(int)Math.Min(count, BlockSize)];
        for (long done = 0; done < count; done += zero.Length)
        {
            int n = (int)Math.Min(zero.Length, count - done);
            long at = offset + done;
            long index = at / BlockSize;
            if (_blocks.ContainsKey(index) || _blocks.ContainsKey((at + n - 1) / BlockSize))
            {
                // 書いてあるブロックだけ 0 で上書きする (書いていないブロックは元から 0)。
                byte[] part = new byte[n];
                WriteRaw(at, part);
            }
        }
    }

    private void WriteRaw(long offset, ReadOnlySpan<byte> data)
    {
        int done = 0;
        while (done < data.Length)
        {
            long at = offset + done;
            long index = at / BlockSize;
            int within = (int)(at % BlockSize);
            int n = Math.Min(data.Length - done, BlockSize - within);
            if (_blocks.TryGetValue(index, out byte[]? block))
            {
                data.Slice(done, n).CopyTo(block.AsSpan(within));
            }

            done += n;
        }
    }

    /// <summary>別のイメージの内容を <paramref name="offset"/> に置く (パーティションの中身)。</summary>
    public void Place(long offset, SparseImage inner)
    {
        foreach ((long index, byte[] block) in inner._blocks.OrderBy(b => b.Key))
        {
            long at = index * BlockSize;
            int n = (int)Math.Min(BlockSize, inner.Length - at);
            Write(offset + at, block.AsSpan(0, n));
        }
    }

    /// <summary>スパースファイルとして保存する。</summary>
    public void Save(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        MakeSparse(handle);
        RandomAccess.SetLength(handle, Length);
        foreach ((long index, byte[] block) in _blocks.OrderBy(b => b.Key))
        {
            long at = index * BlockSize;
            int n = (int)Math.Min(BlockSize, Length - at);
            RandomAccess.Write(handle, block.AsSpan(0, n), at);
        }
    }

    internal static void MakeSparse(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const uint FsctlSetSparse = 0x000900C4;
        if (!DeviceIoControl(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            throw new IOException("Cannot make a sparse file (an NTFS drive is needed).", Marshal.GetLastPInvokeError());
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer,
        uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
