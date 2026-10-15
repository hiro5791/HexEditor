using System.Diagnostics;
using System.Text;
using HexEditor.Cli;

namespace HexEditor.Cli.Tests.Support;

/// <summary>コマンドラインの実行結果。</summary>
public sealed record CliResult(int ExitCode, byte[] OutBytes, byte[] ErrBytes)
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public string Out => Utf8.GetString(OutBytes);

    public string Err => Utf8.GetString(ErrBytes);

    /// <summary>標準出力の行 (改行は LF / CRLF のどちらも受け付ける)。</summary>
    public string[] OutLines => Out.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
}

/// <summary>作業用の一時フォルダ (テストごとに作って消す。システムのファイルは触らない)。</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HexEditorCliTests", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>テストデータを作業用のフォルダに別の名前でコピーする。</summary>
    public string Copy(string testDataId, string name)
    {
        string target = File(name);
        System.IO.File.Copy(HexEditor.TestData.TestDataCatalog.Get(testDataId), target, overwrite: true);
        return target;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 使用中のファイルは残る (一時フォルダなので無視する)。
        }
    }
}

/// <summary>hexed.exe をプロセスとして起動する。設定・状態の置き場所はテストごとの一時フォルダ (普段の設定を読まない・書かない)。</summary>
public static class Hexed
{
    public static string ExePath { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "hexed.exe");

    /// <summary>テスト用の設定の置き場所 (全テストで共有。空のフォルダ)。</summary>
    public static string Profile { get; } = CreateProfile();

    private static string CreateProfile()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HexEditorCliTests", "profile");
        Directory.CreateDirectory(path);
        return path;
    }

    public static ProcessStartInfo StartInfo(string workingDirectory, IEnumerable<string> args, bool redirect = true)
    {
        var info = new ProcessStartInfo(ExePath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            RedirectStandardInput = redirect,
            CreateNoWindow = true,
        };
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["HEXED_TEST_PROFILE"] = Profile;
        return info;
    }

    /// <summary>実行して終了を待つ。標準出力・標準エラー出力をバイト列で取る。</summary>
    public static CliResult Run(string workingDirectory, params string[] args) => Run(workingDirectory, null, TimeSpan.FromSeconds(120), args);

    public static CliResult Run(string workingDirectory, byte[]? input, TimeSpan timeout, params string[] args)
    {
        using var process = new Process { StartInfo = StartInfo(workingDirectory, args) };
        process.Start();
        Task<byte[]> output = ReadAll(process.StandardOutput.BaseStream);
        Task<byte[]> error = ReadAll(process.StandardError.BaseStream);
        if (input is not null)
        {
            process.StandardInput.BaseStream.Write(input);
        }

        process.StandardInput.Close();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"hexed {string.Join(' ', args)} did not finish within {timeout}.");
        }

        return new CliResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    private static async Task<byte[]> ReadAll(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms).ConfigureAwait(false);
        return ms.ToArray();
    }

    /// <summary>プロセスの外から実行する (サービスを差し替えるテスト用)。標準出力・標準エラー出力は文字列で取る。</summary>
    public static CliResult RunInProcess(string workingDirectory, CliServices services, string[] args, Func<Stream>? input = null,
        CancellationToken cancellation = default)
    {
        var utf8 = new UTF8Encoding(false);
        var outStream = new MemoryStream();
        var errStream = new MemoryStream();
        var outWriter = new StreamWriter(outStream, utf8) { NewLine = "\n" };
        var errWriter = new StreamWriter(errStream, utf8) { NewLine = "\n", AutoFlush = true };
        var host = new CliHost
        {
            Out = outWriter,
            Err = errWriter,
            OpenInput = input ?? (() => new MemoryStream()),
            CurrentDirectory = workingDirectory,
            Cancellation = cancellation,
            Services = services,
            TestProfile = Profile,
            WindowsLanguages = () => ["en-US"],
        };
        int code = CliApp.Run(args, host);
        outWriter.Flush();
        errWriter.Flush();
        return new CliResult(code, outStream.ToArray(), errStream.ToArray());
    }
}
