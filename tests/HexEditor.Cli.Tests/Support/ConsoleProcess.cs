using System.Diagnostics;

namespace HexEditor.Cli.Tests.Support;

/// <summary>
/// Ctrl+C のテスト用: hexed を窓のない専用のコンソールで起動し、別のプロセス (hexed のテスト用の機能 <c>--test-send-ctrl-c</c>) が
/// そのコンソールに Ctrl+C を送る。テストのプロセス自身のコンソールは変えない。
/// </summary>
internal static class ConsoleProcess
{
    public static ProcessStartInfo StartInfo(string workingDirectory, IEnumerable<string> args) => Hexed.StartInfo(workingDirectory, args);

    public static void SendCtrlC(Process target)
    {
        using var sender = Process.Start(Hexed.StartInfo(Path.GetTempPath(), ["--test-send-ctrl-c", target.Id.ToString()]))!;
        _ = sender.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        _ = sender.StandardError.BaseStream.CopyToAsync(Stream.Null);
        if (!sender.WaitForExit(30_000))
        {
            sender.Kill();
            throw new TimeoutException("The Ctrl+C sender did not finish.");
        }

        Assert.Equal(0, sender.ExitCode);
    }
}
