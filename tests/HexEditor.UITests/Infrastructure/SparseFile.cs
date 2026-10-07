using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.UITests.Infrastructure;

/// <summary>中身がすべて 00 の大きなファイルを、ディスクをほとんど使わないスパースファイルで作る。</summary>
public static class SparseFile
{
    public static void Create(string path, long length)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        const uint FsctlSetSparse = 0x000900C4;
        if (!DeviceIoControl(handle, FsctlSetSparse, 0, 0, 0, 0, out _, 0))
        {
            throw new IOException("スパースファイルにできません。NTFS のドライブが必要です。", Marshal.GetLastPInvokeError());
        }

        RandomAccess.SetLength(handle, length);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, nint inBuffer, uint inBufferSize,
        nint outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);
}
