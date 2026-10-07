using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Sources;

/// <summary>ファイルが同じ内容のままかを調べるための値: 長さ、最終更新日時、ボリュームのシリアル番号とファイル ID。</summary>
public sealed record FileStamp(long Length, DateTime LastWriteTimeUtc, string FileId)
{
    /// <summary>開いているハンドルから読む。</summary>
    public static FileStamp FromHandle(SafeFileHandle handle)
    {
        long length = RandomAccess.GetLength(handle);
        if (OperatingSystem.IsWindows() && GetFileInformationByHandle(handle, out ByHandleFileInformation info))
        {
            long write = ((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow;
            string id = $"{info.VolumeSerialNumber:X8}-{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
            return new FileStamp(length, DateTime.FromFileTimeUtc(write), id);
        }

        return new FileStamp(length, DateTime.MinValue, string.Empty);
    }

    /// <summary>パスのファイルを開いて読む。開けなければ null。</summary>
    public static FileStamp? FromPath(string path)
    {
        try
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return FromHandle(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        // FILETIME は 4 バイト境界の 2 つの DWORD (long にすると 8 バイト境界に揃えられてずれる)。
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
}
