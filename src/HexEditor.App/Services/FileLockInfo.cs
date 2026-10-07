using System.Runtime.InteropServices;

namespace HexEditor.App.Services;

/// <summary>
/// ファイルを使用中のプロセスの名前 (ENG-11 の「エラー」の共有違反)。Windows の Restart Manager で調べる。
/// 調べられない場合は空の一覧を返す。
/// </summary>
public static class FileLockInfo
{
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;
    private const int ErrorMoreData = 234;

    public static IReadOnlyList<string> ProcessesUsing(string path)
    {
        var names = new List<string>();
        if (RmStartSession(out uint session, 0, Guid.NewGuid().ToString("N")) != 0)
        {
            return names;
        }

        try
        {
            if (RmRegisterResources(session, 1, [path], 0, null, 0, null) != 0)
            {
                return names;
            }

            uint needed = 0;
            uint count = 0;
            int result = RmGetList(session, out needed, ref count, null, out _);
            if (result == ErrorMoreData && needed > 0)
            {
                var infos = new RmProcessInfo[needed];
                count = needed;
                if (RmGetList(session, out needed, ref count, infos, out _) == 0)
                {
                    names.AddRange(infos.Take((int)count).Select(i => i.AppName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());
                }
            }
        }
        finally
        {
            RmEndSession(session);
        }

        return names;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications,
        RmUniqueProcess[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RmProcessInfo[]? rgAffectedApps, out uint lpdwRebootReasons);
}
