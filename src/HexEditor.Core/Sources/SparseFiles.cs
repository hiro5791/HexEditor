using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Sources;

/// <summary>
/// スパースファイル (NTFS) の操作 (ENG-22 の仕様 5、ENG-25 の仕様 1)。スパース属性を付ける・割り当て済みの範囲を問い合わせる。
/// </summary>
public static class SparseFiles
{
    private const uint FsctlSetSparse = 0x000900C4;
    private const uint FsctlQueryAllocatedRanges = 0x000940CF;
    private const int ErrorMoreData = 234;

    /// <summary>ファイルにスパース属性を付ける。付けられなければ (NTFS 以外など) false。</summary>
    public static bool TryMakeSparse(SafeFileHandle handle) =>
        OperatingSystem.IsWindows() && DeviceIoControl(handle, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

    /// <summary>
    /// [<paramref name="offset"/>, <paramref name="offset"/> + <paramref name="length"/>) の中で、ディスクの領域が割り当てられている範囲
    /// (それ以外は 00 として読める) をオフセットの昇順で返す。問い合わせられない場合は範囲全体を返す (全体を読むことになるだけで、内容は正しい)。
    /// </summary>
    public static IReadOnlyList<(long Offset, long Length)> AllocatedRanges(SafeFileHandle handle, long offset, long length)
    {
        if (length <= 0)
        {
            return [];
        }

        if (!OperatingSystem.IsWindows())
        {
            return [(offset, length)];
        }

        var result = new List<(long, long)>();
        long[] input = [offset, length];
        long[] output = new long[2 * 512];
        while (true)
        {
            bool ok = DeviceIoControl(handle, FsctlQueryAllocatedRanges, input, sizeof(long) * 2, output,
                (uint)(output.Length * sizeof(long)), out uint returned, IntPtr.Zero);
            int error = ok ? 0 : Marshal.GetLastPInvokeError();
            if (!ok && error != ErrorMoreData)
            {
                return [(offset, length)];
            }

            int count = (int)(returned / (sizeof(long) * 2));
            for (int i = 0; i < count; i++)
            {
                result.Add((output[2 * i], output[2 * i + 1]));
            }

            if (ok || count == 0)
            {
                return result;
            }

            // 続き: 最後に返した範囲の終わりから問い合わせ直す。
            long next = output[2 * (count - 1)] + output[2 * (count - 1) + 1];
            input = [next, offset + length - next];
            if (input[1] <= 0)
            {
                return result;
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, IntPtr inBuffer, uint inBufferSize,
        IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    /// <summary>FILE_ALLOCATED_RANGE_BUFFER (オフセットと長さの組) の配列を渡す形。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, [In] long[] inBuffer, uint inBufferSize,
        [Out] long[] outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);
}
