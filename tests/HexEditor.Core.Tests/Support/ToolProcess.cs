using System.Diagnostics;
using System.Text;

namespace HexEditor.Core.Tests.Support;

/// <summary>
/// 外部の処理系 (gcc / javac / go / node / dotnet など) をテストから動かす。
/// <list type="bullet">
/// <item>標準入力は閉じる (入力を待たれて止まらないように)。</item>
/// <item>出力は行ごとに受け取って溜める。<c>dotnet build</c> / <c>dotnet run</c> が残す常駐のプロセス (MSBuild のノード、VBCSCompiler) は、
/// 親から受け継いだパイプの書き込み側を握ったまま残るため、<c>ReadToEnd</c> は終わらない。終了を待つのは本体のプロセスだけにし、
/// 出力の終わりは短い時間だけ待つ。</item>
/// <item>時間切れのときは子プロセスも含めて止め、それまでの出力を失敗メッセージに付ける。</item>
/// </list>
/// </summary>
internal static class ToolProcess
{
    /// <summary>
    /// 処理系 1 回あたりの上限。go の初回ビルドや javac の起動は、空いたランナーなら 1 分以内だが、
    /// 並列のテストで混んだ CI のランナーでは数倍かかる (実測: go run が 47 秒 → 3 分以上)。
    /// これらのテストは <see cref="SerialCollection"/> に置いて他のテストと並列にならないようにしてあり、この上限は本当に止まったときの安全網。
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>出力の終わり (パイプが閉じること) を、プロセスが終わった後に待つ時間。</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>処理系を動かして、成功 (終了コード 0) を確かめ、標準出力を返す。</summary>
    public static string Run(string tool, string arguments, string directory)
    {
        var info = new ProcessStartInfo(Clipboard.CopyFormatCompileTests.FindTool(tool) ?? tool, arguments) { WorkingDirectory = directory };
        (int code, string stdout, string stderr) = Execute(info, Timeout, $"{tool} {arguments}");
        Assert.True(code == 0, $"{tool} {arguments}: {stderr}{stdout}");
        return stdout.ReplaceLineEndings("\n");
    }

    /// <summary>プロセスを動かして、終了コードと標準出力・標準エラーを返す。時間切れは失敗にする。</summary>
    public static (int ExitCode, string Output, string Error) Execute(ProcessStartInfo info, TimeSpan timeout, string label)
    {
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;

        // 利用者の設定を読み書きしない・常駐のプロセスを残さない。
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        info.Environment["UseSharedCompilation"] = "false";
        info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutClosed = new TaskCompletionSource();
        var stderrClosed = new TaskCompletionSource();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => Collect(e, stdout, stdoutClosed);
        process.ErrorDataReceived += (_, e) => Collect(e, stderr, stderrClosed);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // すでに終わっている。
            }

            process.WaitForExit(TimeSpan.FromSeconds(10));
            Assert.Fail($"{label} が {timeout.TotalMinutes} 分で終わりません。標準出力: {Snapshot(stdout)} 標準エラー: {Snapshot(stderr)}");
        }

        Task.WaitAll([stdoutClosed.Task, stderrClosed.Task], DrainTimeout);
        return (process.ExitCode, Snapshot(stdout), Snapshot(stderr));
    }

    private static void Collect(DataReceivedEventArgs e, StringBuilder buffer, TaskCompletionSource closed)
    {
        if (e.Data is null)
        {
            closed.TrySetResult();
            return;
        }

        lock (buffer)
        {
            buffer.Append(e.Data).Append('\n');
        }
    }

    private static string Snapshot(StringBuilder buffer)
    {
        lock (buffer)
        {
            return buffer.ToString();
        }
    }
}
