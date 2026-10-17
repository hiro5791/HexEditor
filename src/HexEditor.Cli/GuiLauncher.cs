using System.Diagnostics;

namespace HexEditor.Cli;

/// <summary>
/// GUI の実行ファイル (HexEditor.exe) の起動 (AUTO-37)。<c>hexed.exe</c> と同じフォルダにある <c>HexEditor.exe</c> を使う
/// (3 つの配布形態とも同じフォルダに置く。MSIX 版は App Execution Alias が同じパッケージの中の exe を指す)。
/// 開発中のビルドでは環境変数 <c>HEXED_GUI_PATH</c> で場所を指定できる。
/// </summary>
internal sealed class GuiLauncher : IGuiLauncher
{
    public string? FindExecutable()
    {
        if (Environment.GetEnvironmentVariable("HEXED_GUI_PATH") is { Length: > 0 } custom)
        {
            return File.Exists(custom) ? custom : null;
        }

        string candidate = Path.Combine(AppContext.BaseDirectory, "HexEditor.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    public int Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        // ShellExecute で起動する: ハンドルを受け継がせない。受け継ぐと、hexed の出力をリダイレクトして読んでいる側が、GUI が終わるまで
        // 読み終わらない (GUI が hexed の標準出力のパイプを持ち続けるため)。
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info) ?? throw new IOException("The process could not be started.");
        return process.Id;
    }

    public int RunAndWait(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info) ?? throw new IOException("The process could not be started.");
        while (!process.WaitForExit(200))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return process.ExitCode;
    }
}
