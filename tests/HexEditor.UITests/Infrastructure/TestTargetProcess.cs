using System.Diagnostics;
using System.Globalization;

namespace HexEditor.UITests.Infrastructure;

/// <summary>
/// テスト用のプロセス tools/TestTarget (テスト方針 7.1) を起動する。既知の値 (<see cref="Value"/>) を持つページ・UTF-16 の文字列・指定した数の
/// 領域を確保させ、アドレスを受け取る。終わったら、自分で起動したこのプロセスだけを終了する。先に
/// <c>dotnet build tools/TestTarget -c Release</c> でビルドしておく。
/// </summary>
public sealed class TestTargetProcess : IDisposable
{
    /// <summary>TestTarget の既定の値 (<c>HEXEDIT!</c>)。</summary>
    public static readonly byte[] Value = [0x48, 0x45, 0x58, 0x45, 0x44, 0x49, 0x54, 0x21];

    private TestTargetProcess(Process process, IReadOnlyDictionary<string, string> lines)
    {
        Process = process;
        ValueAddress = ParseAddress(lines["VALUE"]);
        TextAddress = lines.TryGetValue("TEXT", out string? text) ? ParseAddress(text) : null;
    }

    public Process Process { get; }

    public int Pid => Process.Id;

    /// <summary>既知の値を置いたアドレス。</summary>
    public long ValueAddress { get; }

    /// <summary>--text の文字列を置いたアドレス。</summary>
    public long? TextAddress { get; }

    public static string ExePath =>
        Directory.Exists(Path.Combine(AppLocator.RepositoryRoot, "tools", "TestTarget", "bin"))
        && Directory.EnumerateFiles(Path.Combine(AppLocator.RepositoryRoot, "tools", "TestTarget", "bin"), "TestTarget.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() is { } exe
            ? exe
            : throw new FileNotFoundException("TestTarget が見つかりません。先に `dotnet build tools/TestTarget -c Release` でビルドしてください。");

    /// <summary>
    /// 起動して、準備ができる (READY) まで待つ。<paramref name="restricted"/> なら一般ユーザーの権限で起動する (CI の特権のテストだけ)。
    /// テストのプロセスと同じ権限で起動する場合、CI のランナーでは管理者として動く (TC-ENG-32-02 の「管理者の TestTarget」)。
    /// </summary>
    public static async Task<TestTargetProcess> StartAsync(UiTestContext ctx, bool restricted = false, string? text = null, int regions = 0)
    {
        string output = Path.Combine(ctx.Root, $"testtarget-{Guid.NewGuid():N}.txt");
        var args = new List<string> { "--out", output };
        if (text is not null)
        {
            args.AddRange(["--text", text]);
        }

        if (regions > 0)
        {
            args.AddRange(["--regions", regions.ToString(CultureInfo.InvariantCulture)]);
        }

        Process process;
        if (restricted)
        {
            process = CiPrivileged.StartRestricted(ExePath, args, ctx.Root, noWindow: true);
        }
        else
        {
            var info = new ProcessStartInfo(ExePath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
            foreach (string a in args)
            {
                info.ArgumentList.Add(a);
            }

            process = Process.Start(info) ?? throw new InvalidOperationException("TestTarget を起動できません。");
            process.OutputDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
        }

        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < UiTest.Scaled(TimeSpan.FromSeconds(60)))
        {
            if (File.Exists(output))
            {
                string[] lines = File.ReadAllLines(output);
                if (lines.Contains("READY"))
                {
                    var map = lines.Where(l => l.Contains(' ')).ToDictionary(l => l[..l.IndexOf(' ')], l => l[(l.IndexOf(' ') + 1)..]);
                    return new TestTargetProcess(process, map);
                }
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException($"TestTarget exited with code {process.ExitCode}.");
            }

            await Task.Delay(100);
        }

        process.Kill();
        throw new TimeoutException("TestTarget did not become ready.");
    }

    private static long ParseAddress(string text)
    {
        string first = text.Split(' ').Last();
        return long.Parse(first.StartsWith("0x", StringComparison.Ordinal) ? first[2..] : first, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill();
                Process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        Process.Dispose();
    }
}
