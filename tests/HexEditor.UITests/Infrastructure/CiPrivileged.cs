using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.UITests.Infrastructure;

/// <summary>
/// 管理者権限・仮想ディスク (VHDX)・配布形態のインストール・本物の補助プロセスの昇格が要るテスト (テスト方針 6.5 の「管理者 (CI)」
/// 「一般ユーザー (CI)」)。CI のジョブ ui-privileged が環境変数 HEXEDITOR_CI_PRIVILEGED=1 を設定したときだけ実行し、それ以外
/// (開発者の PC、ほかの CI のジョブ) では理由を付けてスキップする。この PC では昇格・仮想ディスクの作成・インストールをしない。
/// <paramref name="distributions"/> を指定すると、その配布形態のビルド (HEXEDITOR_APP_DISTRO。Install-TestBuild.ps1 が設定する) のときだけ実行する。
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CiPrivilegedFactAttribute : FactAttribute
{
    public const string Variable = "HEXEDITOR_CI_PRIVILEGED";

    public CiPrivilegedFactAttribute(params string[] distributions)
    {
        if (!CiPrivileged.Enabled)
        {
            Skip = $"CI の管理者権限のランナーでだけ実行する ({Variable}=1 と GITHUB_ACTIONS。管理者権限・仮想ディスク・インストールが要る)。";
        }
        else if (distributions.Length > 0 && !distributions.Contains(CiPrivileged.Distribution, StringComparer.OrdinalIgnoreCase))
        {
            Skip = $"{string.Join(" / ", distributions)} のビルドで実行する (このジョブは {CiPrivileged.Distribution})。";
        }
    }
}

/// <summary>CI の ui-privileged のジョブが用意する環境 (build/tests/Mount-TestVhd.ps1、Install-TestBuild.ps1)。</summary>
public static class CiPrivileged
{
    /// <summary>CI の管理者権限のランナーで、特権のテストを実行してよい。</summary>
    public static bool Enabled =>
        Environment.GetEnvironmentVariable(CiPrivilegedFactAttribute.Variable) == "1"
        && string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>テストするビルドの配布形態 (Msix / Installer / Portable。指定がなければ Development)。</summary>
    public static string Distribution => Environment.GetEnvironmentVariable("HEXEDITOR_APP_DISTRO") is { Length: > 0 } d ? d : "Development";

    /// <summary>接続した TD-VHDX-MBR の物理ディスクの番号 (Mount-TestVhd.ps1)。</summary>
    public static int VhdDisk => int.Parse(Required("HEXEDITOR_TEST_VHD_DISK"), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>TD-VHDX-MBR の NTFS のボリュームのドライブ文字 (例 "V")。</summary>
    public static string VhdVolumeLetter => Required("HEXEDITOR_TEST_VHD_VOLUME");

    /// <summary>TD-VHDX-MBR の物理ディスクのパス。</summary>
    public static string VhdDiskPath => $@"\\.\PhysicalDrive{VhdDisk}";

    /// <summary>特権のテストの中でだけ呼ぶ (この PC では昇格・デバイスの操作をしないことの確認)。</summary>
    public static void EnsureEnabled()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("This needs the privileged CI job (HEXEDITOR_CI_PRIVILEGED=1 on a GitHub Actions runner).");
        }
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v
        ? v
        : throw new InvalidOperationException($"{name} is not set (build/tests/Mount-TestVhd.ps1 sets it).");

    /// <summary>
    /// 物理ディスク・ボリュームの先頭から <paramref name="length"/> バイトを読む (テストのプロセスは CI のランナーの管理者権限で動く)。
    /// 読み取りのアクセス権だけで開く。
    /// </summary>
    public static byte[] ReadDevice(string path, int length)
    {
        EnsureEnabled();
        using SafeFileHandle handle = Native.CreateFile(path, Native.GenericRead, Native.FileShareRead | Native.FileShareWrite, 0, Native.OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open {path}");
        }

        byte[] buffer = new byte[length];
        using var stream = new FileStream(handle, FileAccess.Read, 0);
        stream.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>
    /// ボリュームを開いてロックできるか (FSCTL_LOCK_VOLUME。TC-ENG-28-08)。ロックできたらすぐに解除して閉じる。書き込みはしない。
    /// </summary>
    public static bool TryLockVolume(string letter, out int error)
    {
        EnsureEnabled();
        using SafeFileHandle handle = Native.CreateFile($@"\\.\{letter}:", Native.GenericRead | Native.GenericWrite,
            Native.FileShareRead | Native.FileShareWrite, 0, Native.OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        if (!Native.DeviceIoControl(handle, Native.FsctlLockVolume, 0, 0, 0, 0, out _, 0))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        Native.DeviceIoControl(handle, Native.FsctlUnlockVolume, 0, 0, 0, 0, out _, 0);
        error = 0;
        return true;
    }

    /// <summary>プロセスが管理者として動いているか (トークンの昇格。TokenElevation)。</summary>
    public static bool IsElevated(int pid)
    {
        using Process process = Process.GetProcessById(pid);
        if (!Native.OpenProcessToken(process.Handle, Native.TokenQuery, out SafeAccessTokenHandle token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        using (token)
        {
            return Native.GetTokenInformation(token, Native.TokenElevation, out int elevated, sizeof(int), out _) ? elevated != 0
                : throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    // ---- 一般ユーザーの権限での起動 ----

    /// <summary>
    /// アプリを一般ユーザーの権限 (昇格していないトークン) で起動する (テスト方針 6.5 の「一般ユーザー (CI)」)。CI のランナーのテストは管理者として
    /// 動くため、同じセッションの昇格していないエクスプローラーのトークンを使う (UAC の分割されたトークンのうち制限付きのもの。補助プロセスの
    /// <c>runas</c> がランナーの「確認なしで昇格」で通る)。エクスプローラーがない・昇格している場合は、制限付きのトークン (SAFER の
    /// 一般ユーザー、整合性レベル中) を作って起動する。
    /// </summary>
    public static Process StartRestricted(string exe, IReadOnlyList<string> arguments, string workingDirectory, bool noWindow = false)
    {
        EnsureEnabled();
        uint flags = Native.CreateUnicodeEnvironment | (noWindow ? Native.CreateNoWindow : 0);
        string commandLine = string.Join(' ', new[] { exe }.Concat(arguments).Select(Quote));
        if (ExplorerToken() is { } explorer)
        {
            using (explorer)
            {
                if (Native.DuplicateTokenEx(explorer, Native.TokenAllAccess, 0, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out SafeAccessTokenHandle primary))
                {
                    using (primary)
                    {
                        var si = new Native.StartupInfo { cb = Marshal.SizeOf<Native.StartupInfo>() };
                        if (Native.CreateProcessWithTokenW(primary, 0, null, commandLine, flags, 0, workingDirectory, ref si, out Native.ProcessInformation pi))
                        {
                            return Started(pi);
                        }
                    }
                }
            }
        }

        return StartSafer(commandLine, workingDirectory, flags);
    }

    private static Process StartSafer(string commandLine, string workingDirectory, uint flags)
    {
        if (!Native.SaferCreateLevel(2 /* SAFER_SCOPEID_USER */, 0x20000 /* SAFER_LEVELID_NORMALUSER */, 1, out nint level, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SaferCreateLevel");
        }

        try
        {
            if (!Native.SaferComputeTokenFromLevel(level, 0, out SafeAccessTokenHandle token, 0, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SaferComputeTokenFromLevel");
            }

            using (token)
            {
                SetMediumIntegrity(token);
                var si = new Native.StartupInfo { cb = Marshal.SizeOf<Native.StartupInfo>() };
                if (!Native.CreateProcessAsUser(token, null, commandLine, 0, 0, false, flags, 0, workingDirectory, ref si, out Native.ProcessInformation pi))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser");
                }

                return Started(pi);
            }
        }
        finally
        {
            Native.SaferCloseLevel(level);
        }
    }

    private static Process Started(Native.ProcessInformation pi)
    {
        try
        {
            return Process.GetProcessById(pi.dwProcessId);
        }
        finally
        {
            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess);
        }
    }

    /// <summary>同じセッションの、昇格していないエクスプローラーのトークン (なければ null)。</summary>
    private static SafeAccessTokenHandle? ExplorerToken()
    {
        int session = Process.GetCurrentProcess().SessionId;
        foreach (Process p in Process.GetProcessesByName("explorer"))
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != session || !Native.OpenProcessToken(p.Handle, Native.TokenDuplicate | Native.TokenQuery | Native.TokenAssignPrimary, out SafeAccessTokenHandle token))
                    {
                        continue;
                    }

                    if (Native.GetTokenInformation(token, Native.TokenElevation, out int elevated, sizeof(int), out _) && elevated == 0)
                    {
                        return token;
                    }

                    token.Dispose();
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                }
            }
        }

        return null;
    }

    private static void SetMediumIntegrity(SafeAccessTokenHandle token)
    {
        if (!Native.ConvertStringSidToSid("S-1-16-8192", out nint sid))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ConvertStringSidToSid");
        }

        try
        {
            var label = new Native.TokenMandatoryLabel { Sid = sid, Attributes = 0x20 /* SE_GROUP_INTEGRITY */ };
            int size = Marshal.SizeOf<Native.TokenMandatoryLabel>() + Native.GetLengthSid(sid);
            if (!Native.SetTokenInformation(token, 25 /* TokenIntegrityLevel */, ref label, size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetTokenInformation");
            }
        }
        finally
        {
            Native.LocalFree(sid);
        }
    }

    private static string Quote(string a) => a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c == '"') ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    private static class Native
    {
        public const uint GenericRead = 0x80000000;
        public const uint GenericWrite = 0x40000000;
        public const uint FileShareRead = 1;
        public const uint FileShareWrite = 2;
        public const uint OpenExisting = 3;
        public const uint FsctlLockVolume = 0x00090018;
        public const uint FsctlUnlockVolume = 0x0009001C;
        public const uint TokenAssignPrimary = 0x0001;
        public const uint TokenDuplicate = 0x0002;
        public const uint TokenQuery = 0x0008;
        public const uint TokenAllAccess = 0xF01FF;
        public const int TokenElevation = 20;
        public const uint CreateUnicodeEnvironment = 0x00000400;
        public const uint CreateNoWindow = 0x08000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct StartupInfo
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public nint lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInformation
        {
            public nint hProcess;
            public nint hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TokenMandatoryLabel
        {
            public nint Sid;
            public uint Attributes;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint inBuffer, int inSize, nint outBuffer, int outSize, out int returned, nint overlapped);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(nint handle);

        [DllImport("kernel32.dll")]
        public static extern nint LocalFree(nint memory);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int value, int length, out int returned);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool SetTokenInformation(SafeAccessTokenHandle token, int infoClass, ref TokenMandatoryLabel value, int length);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, nint attributes, int impersonation, int type, out SafeAccessTokenHandle newToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessWithTokenW(SafeAccessTokenHandle token, int logonFlags, string? application, string commandLine, uint creationFlags,
            nint environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string? application, string commandLine, nint processAttributes, nint threadAttributes,
            bool inheritHandles, uint creationFlags, nint environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool SaferCreateLevel(uint scope, uint level, uint flags, out nint levelHandle, nint reserved);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool SaferComputeTokenFromLevel(nint level, nint inToken, out SafeAccessTokenHandle outToken, uint flags, nint reserved);

        [DllImport("advapi32.dll")]
        public static extern bool SaferCloseLevel(nint level);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool ConvertStringSidToSid(string sid, out nint sidPointer);

        [DllImport("advapi32.dll")]
        public static extern int GetLengthSid(nint sid);
    }
}

/// <summary>
/// プロセスの作成の見張り (TC-ENG-28-03、TC-PKG-14-03、TC-PKG-14-06): 見張りの間に新しく現れた <c>consent.exe</c> (UAC の確認) と
/// <c>HexEditor.Elevated.exe</c> のプロセス ID を集める。20 ms ごとに一覧を取る (UAC の確認の画面は数百 ms 以上残る)。
/// </summary>
public sealed class ProcessWatch : IAsyncDisposable
{
    private static readonly string[] Names = ["consent", "HexEditor.Elevated"];
    private readonly HashSet<int> _before;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly object _lock = new();
    private readonly List<string> _seen = [];

    public ProcessWatch()
    {
        _before = [.. Names.SelectMany(Process.GetProcessesByName).Select(p => { using (p) { return p.Id; } })];
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                foreach (string name in Names)
                {
                    foreach (Process p in Process.GetProcessesByName(name))
                    {
                        using (p)
                        {
                            if (!_before.Contains(p.Id))
                            {
                                lock (_lock)
                                {
                                    string entry = $"{name}.exe ({p.Id})";
                                    if (!_seen.Contains(entry))
                                    {
                                        _seen.Add(entry);
                                    }
                                }
                            }
                        }
                    }
                }

                try
                {
                    await Task.Delay(20, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    /// <summary>見張りの間に作られたプロセス (「consent.exe (1234)」の形)。</summary>
    public IReadOnlyList<string> Seen
    {
        get
        {
            lock (_lock)
            {
                return [.. _seen];
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }
}
