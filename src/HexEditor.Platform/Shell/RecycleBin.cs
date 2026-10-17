using System.Runtime.InteropServices;

namespace HexEditor.Platform.Shell;

/// <summary>
/// ファイルをごみ箱へ移動する (08 の AUTO-19 の仕様 3: スクリプトの削除)。Windows のシェルの操作 (SHFileOperation) で、確認の表示は出さない。
/// </summary>
public static class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    /// <summary>ごみ箱へ移動する。移動できなければ <see cref="IOException"/>。</summary>
    public static void Delete(string path)
    {
        var operation = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent,
        };
        int result = SHFileOperationW(ref operation);
        if (result != 0 || operation.fAnyOperationsAborted)
        {
            throw new IOException($"Cannot move the file to the recycle bin: {path} (0x{result:X})");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public nint hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public nint hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref ShFileOpStruct operation);
}
