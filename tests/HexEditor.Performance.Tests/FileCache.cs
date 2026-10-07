using System.Runtime.InteropServices;

namespace HexEditor.Performance.Tests;

/// <summary>
/// 性能テスト用の固定の計測機 (テスト方針 6.6) でだけ実行するテスト。環境変数 HEXEDITOR_PERF_MACHINE が 1 のときだけ実行し、
/// それ以外 (開発者の PC、共有の CI ランナー) では理由を付けてスキップする。
/// </summary>
public sealed class PerfMachineFactAttribute : FactAttribute
{
    public const string Variable = "HEXEDITOR_PERF_MACHINE";

    public PerfMachineFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"性能テスト用の固定の計測機 (NVMe SSD、管理者として実行。テスト方針 6.6) でだけ実行する ({Variable}=1 で実行する)。" +
                "10 GiB のテストデータを実際に書き、OS のファイルキャッシュを消すため";
        }
    }
}

/// <summary>
/// OS のファイルキャッシュ (スタンバイリスト) を消す。管理者権限 (SeProfileSingleProcessPrivilege) が要る。計測機だけで使う。
/// </summary>
internal static class FileCache
{
    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;
    private const uint SePrivilegeEnabled = 0x2;
    private const uint TokenAdjustPrivileges = 0x20;
    private const uint TokenQuery = 0x8;

    /// <summary>スタンバイリストを消す。できなければ例外 (計測の前提を満たせない)。</summary>
    public static void Purge()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token))
        {
            throw new InvalidOperationException("プロセスのトークンを開けません。");
        }

        try
        {
            if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out long luid))
            {
                throw new InvalidOperationException("特権の値を取れません。");
            }

            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastPInvokeError() != 0)
            {
                throw new InvalidOperationException("OS のファイルキャッシュを消すには管理者として実行してください (SeProfileSingleProcessPrivilege)。");
            }
        }
        finally
        {
            CloseHandle(token);
        }

        int command = MemoryPurgeStandbyList;
        int status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        if (status != 0)
        {
            throw new InvalidOperationException($"OS のファイルキャッシュを消せません (NTSTATUS 0x{status:X8})。");
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState, int length, IntPtr previous,
        IntPtr returnLength);
}
