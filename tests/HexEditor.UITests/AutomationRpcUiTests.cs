using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using HexEditor.UITests.Infrastructure;

namespace HexEditor.UITests;

/// <summary>
/// 外部からの自動操作 (08 の AUTO-45)。テストではパイプ名をテストごとに変える (設定 <c>test.automation.pipeName</c>。クライアントには環境変数
/// <c>HEXEDITOR_RPC_PIPE</c> で渡す) ので、利用者の HexEditor やほかのテストのパイプと重ならない。Python・PowerShell 7 がない環境では、
/// それを使う手順を理由付きで飛ばす (CI のランナーにはある)。
/// </summary>
[Trait(UiTest.Category, UiTest.UI)]
public sealed class AutomationRpcUiTests
{
    private static readonly TimeSpan Wait = UiTest.Scaled(TimeSpan.FromSeconds(30));

    private static Task<JsonObject> RpcAsync(AppSession app, string action = "state", JsonObject? args = null)
    {
        JsonObject request = args ?? [];
        request["action"] = action;
        return app.SendAsync("rpc", request);
    }

    private static string NewPipeName() => "HexEditor.uitest." + Guid.NewGuid().ToString("N") + ".rpc";

    private static async Task<AppSession> StartAsync(UiTestContext ctx, string pipe, bool enabled, params string[] files)
    {
        string profile = ctx.NewProfile();
        ViewOps.WriteSettings(profile, new JsonObject { ["test.automation.pipeName"] = pipe, ["automation.rpc.enabled"] = enabled });
        return await ctx.StartAsync(new AppOptions { Profile = profile, Files = files, WaitForEditor = files.Length > 0 });
    }

    /// <summary>実行ファイルのフルパス (<c>sys.executable</c> など)。見つからなければ null。</summary>
    private static string? Executable(string file, string args)
    {
        try
        {
            var start = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using Process? p = Process.Start(start);
            if (p is null)
            {
                return null;
            }

            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(30_000);
            return p.ExitCode == 0 && File.Exists(output) ? output : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static string? Python() => Executable("python", "-c \"import sys; print(sys.executable)\"");

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string args, string pipe, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.Environment["HEXEDITOR_RPC_PIPE"] = pipe;
        using Process p = Process.Start(start)!;
        Task<string> stdout = p.StandardOutput.ReadToEndAsync();
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Path.GetFileName(file)} did not exit.");
        }

        string error = (await stderr).Trim();
        // 標準エラー (PowerShell の進捗の CLIXML など) は失敗したときだけ付ける。
        return (p.ExitCode, (await stdout).Trim() + (error.Length > 0 && p.ExitCode != 0 ? "\n" + error : string.Empty));
    }

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-45-01")]
    public Task Pipe_exists_only_while_the_setting_is_on() => UiTestContext.RunAsync(async ctx =>
    {
        string pipe = NewPipeName();
        AppSession app = await StartAsync(ctx, pipe, enabled: false);

        // 手順 2: 既定 (オフ) ではパイプがない。
        JsonObject state = await RpcAsync(app);
        Assert.False(state["running"]!.GetValue<bool>());
        Assert.False(state["pipeExists"]!.GetValue<bool>());
        Assert.False(state["statusVisible"]!.GetValue<bool>());

        // 手順 3: オンにするとパイプがあり、ステータスバーに「外部からの自動操作: 有効」がある。
        await app.SendAsync("settingSet", new JsonObject { ["key"] = "automation.rpc.enabled", ["value"] = true });
        await app.WaitUntilAsync(async () => (state = await RpcAsync(app))["pipeExists"]!.GetValue<bool>(), Wait, "the pipe");
        await app.WaitUntilAsync(async () => (state = await RpcAsync(app))["statusVisible"]!.GetValue<bool>(), Wait, "the status bar icon");
        Assert.Equal("External automation: on", state["statusName"]!.GetValue<string>());
        Assert.Equal("External automation: on", await app.UiaNameAsync("Status_Automation"));

        // 手順 4: オフに戻すとパイプがなくなる。
        await app.SendAsync("settingSet", new JsonObject { ["key"] = "automation.rpc.enabled", ["value"] = false });
        await app.WaitUntilAsync(async () => !(state = await RpcAsync(app))["pipeExists"]!.GetValue<bool>(), Wait, "no pipe");
        Assert.False(state["statusVisible"]!.GetValue<bool>());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-45-02")]
    public Task Python_opens_a_file_and_reads_bytes() => UiTestContext.RunAsync(async ctx =>
    {
        string? python = Python();
        if (python is null)
        {
            Console.WriteLine("Python 3 is not installed: TC-AUTO-45-02 is checked on the CI runners.");
            return;
        }

        string pipe = NewPipeName();
        AppSession app = await StartAsync(ctx, pipe, enabled: true);
        await app.WaitUntilAsync(async () => (await RpcAsync(app))["pipeExists"]!.GetValue<bool>(), Wait, "the pipe");
        await RpcAsync(app, "trust", new JsonObject { ["path"] = python });

        string bytes = ctx.TestData("TD-BYTES-256");
        string client = ctx.TestData("TD-AUTO-RPC-PY");
        (int exit, string output) = await RunAsync(python, $"\"{client}\" open-read \"{bytes}\"", pipe, TimeSpan.FromSeconds(120));
        Assert.True(exit == 0, output);
        Assert.Equal("10 11 12 13 14 15 16 17 18 19 1A 1B 1C 1D 1E 1F", output);
        Assert.Contains("TD-BYTES-256.bin", await app.TabNamesAsync());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-45-04")]
    public Task First_connection_asks_and_denying_disconnects() => UiTestContext.RunAsync(async ctx =>
    {
        string? python = Python();
        if (python is null)
        {
            Console.WriteLine("Python 3 is not installed: TC-AUTO-45-04 is checked on the CI runners.");
            return;
        }

        string pipe = NewPipeName();
        AppSession app = await StartAsync(ctx, pipe, enabled: true);
        await app.WaitUntilAsync(async () => (await RpcAsync(app))["pipeExists"]!.GetValue<bool>(), Wait, "the pipe");
        Assert.Empty((await RpcAsync(app, "allowed"))["paths"]!.AsArray());

        string bytes = ctx.TestData("TD-BYTES-256");
        string client = ctx.TestData("TD-AUTO-RPC-PY");
        Task<(int, string)> run = RunAsync(python, $"\"{client}\" open-read \"{bytes}\"", pipe, TimeSpan.FromSeconds(120));

        // 手順 2: 確認ダイアログに、接続してきた実行ファイル (python.exe) のパスがある。
        var dialog = await app.WaitForDialogAsync("RpcTrustDialog");
        await app.WaitForDialogTextAsync(dialog, Path.GetFileName(python));
        Assert.Equal(python, await app.UiaNameAsync("ScriptTrust_Path"), ignoreCase: true);

        // 手順 3〜4: 「許可しない」で接続が切られ、Python は 0 以外で終わる。タブは開かない。
        await app.InvokeDialogButtonAsync("Don't allow");
        int exit;
        string output;
        try
        {
            (exit, output) = await run;
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(ex.Message + " Log: " + string.Join(" | ", (await app.LogAsync()).TakeLast(30)), ex);
        }

        Assert.NotEqual(0, exit);
        Assert.Contains("did not allow", output);
        Assert.Empty(await app.TabNamesAsync());
    });

    [Fact]
    [Trait(UiTest.TC, "TC-AUTO-45-05")]
    public Task PowerShell_module_lists_the_open_documents() => UiTestContext.RunAsync(async ctx =>
    {
        string seq = ctx.TestData("TD-SEQ-1M");
        string zero = ctx.TestData("TD-ZERO-1M");
        string pipe = NewPipeName();
        AppSession app = await StartAsync(ctx, pipe, enabled: true, seq, zero);
        await app.WaitUntilAsync(async () => (await RpcAsync(app))["pipeExists"]!.GetValue<bool>(), Wait, "the pipe");
        await app.WaitUntilAsync(async () => (await app.TabNamesAsync()).Count == 2, Wait, "two tabs");

        // 両方のタブを一度表示して開き終える (表示していないタブは読み込みを後回しにしている)。
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 1 });
        await app.SendAsync("selectTab", new JsonObject { ["index"] = 0 });
        await app.IdleAsync();

        // 配布物の HexEditor.Automation モジュール (ビルドの出力の Automation\HexEditor.Automation)。
        string module = Path.Combine(Path.GetDirectoryName(AppLocator.ExePath)!, "Automation", "HexEditor.Automation", "HexEditor.Automation.psd1");
        Assert.True(File.Exists(module), module);
        string script = $"$ProgressPreference = 'SilentlyContinue'; Import-Module '{module}'; Get-HexEditorDocument | ForEach-Object {{ '{{0}}|{{1}}' -f $_.path, $_.length }}";
        string encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

        var shells = new List<string> { Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") };
        if (Executable("pwsh", "-NoProfile -Command \"(Get-Process -Id $PID).Path\"") is { } pwsh)
        {
            shells.Add(pwsh);
        }
        else
        {
            Console.WriteLine("PowerShell 7 is not installed: only Windows PowerShell 5.1 is checked here (CI checks both).");
        }

        foreach (string shell in shells)
        {
            // 前提: PowerShell の実行ファイルを許可した記録を置いている。
            await RpcAsync(app, "trust", new JsonObject { ["path"] = shell });
            (int exit, string output) = await RunAsync(shell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, pipe, TimeSpan.FromSeconds(120));
            Assert.True(exit == 0, Path.GetFileName(shell) + ": " + output);
            string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.True(lines.Length == 2, Path.GetFileName(shell) + ": " + output);
            Assert.True(lines.Contains($"{seq}|1048576", StringComparer.OrdinalIgnoreCase), output);
            Assert.Contains($"{zero}|1048576", lines, StringComparer.OrdinalIgnoreCase);
        }
    });

    [CiPrivilegedFact]
    [Trait(UiTest.TC, "TC-AUTO-45-03")]
    public Task Another_user_cannot_connect() => UiTestContext.RunAsync(async ctx =>
    {
        string? python = Python() ?? throw new InvalidOperationException("Python 3 is required on the privileged runner.");
        string pipe = NewPipeName();
        AppSession app = await StartAsync(ctx, pipe, enabled: true);
        await app.WaitUntilAsync(async () => (await RpcAsync(app))["pipeExists"]!.GetValue<bool>(), Wait, "the pipe");

        // テスト用のローカルの利用者を作り、その資格情報で接続を試す (CI の使い捨てのランナーだけ)。
        const string User = "hexrpc-other";
        string password = "Hx!" + Guid.NewGuid().ToString("N")[..12] + "a1";
        await RunAsync("net", $"user {User} {password} /add", pipe, TimeSpan.FromSeconds(60));
        try
        {
            string client = ctx.TestData("TD-AUTO-RPC-PY");
            string output = Path.Combine(Path.GetTempPath(), "hexrpc-other-" + Guid.NewGuid().ToString("N") + ".txt");
            string command = $"/c \"\"{python}\" \"{client}\" connect --pipe \\\\.\\pipe\\{pipe} > \"{output}\" 2>&1\"";
            int exit = OtherUser.Run(User, password, Path.Combine(Environment.SystemDirectory, "cmd.exe"), command, TimeSpan.FromSeconds(120));
            string text = File.Exists(output) ? await File.ReadAllTextAsync(output) : string.Empty;
            Assert.NotEqual(0, exit);
            Assert.Contains("winerror 5", text);
            Assert.Null(app.Find("RpcTrustDialog"));
        }
        finally
        {
            await RunAsync("net", $"user {User} /delete", pipe, TimeSpan.FromSeconds(60));
        }
    });

    /// <summary>別の利用者としてプロセスを起動する (<c>CreateProcessWithLogonW</c>)。終了コードを返す。</summary>
    private static class OtherUser
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessWithLogonW(string user, string domain, string password, int logonFlags, string? application,
            string commandLine, int creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startup, out ProcessInformation info);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out int exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        public static int Run(string user, string password, string application, string arguments, TimeSpan timeout)
        {
            const int LogonWithProfile = 1, CreateNoWindow = 0x08000000;
            var startup = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcessWithLogonW(user, ".", password, LogonWithProfile, application, $"\"{application}\" {arguments}", CreateNoWindow, IntPtr.Zero,
                Path.GetTempPath(), ref startup, out ProcessInformation info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                WaitForSingleObject(info.hProcess, (uint)timeout.TotalMilliseconds);
                return GetExitCodeProcess(info.hProcess, out int code) ? code : -1;
            }
            finally
            {
                CloseHandle(info.hProcess);
                CloseHandle(info.hThread);
            }
        }
    }
}
