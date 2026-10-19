using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HexEditor.Core.Tests.Forensics.Images;

/// <summary>
/// libewf のツール (<c>ewfverify</c>、<c>ewfinfo</c>) による基準の検証 (TC-FOR-20-03、TC-FOR-20-04、TC-FOR-21-04 の「libewf のツールをインストールした
/// ランナー」)。ツールは本アプリに同梱せず、CI のジョブ ewf-crosscheck (Linux のランナーに ewf-tools を入れたもの) だけで使う。
/// ツールが PATH にない環境 (開発者の PC、Windows のランナー) では <see cref="Available"/> が偽で、ツールによる確認を省く
/// (<see cref="EwfToolsFactAttribute"/> のテストはスキップされる)。
/// </summary>
internal static partial class EwfTools
{
    /// <summary>環境変数 HEXEDITOR_EWF_TOOLS=1 で、ツールがないことを失敗にする (CI のジョブで省略されていないことを保証する)。</summary>
    public const string Variable = "HEXEDITOR_EWF_TOOLS";

    public static bool Required => Environment.GetEnvironmentVariable(Variable) == "1";

    public static bool Available => Find("ewfverify") is not null && Find("ewfinfo") is not null;

    private static string? Find(string tool)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in new[] { tool, tool + ".exe" })
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static (int Code, string Output) Run(string tool, params string[] args)
    {
        var start = new ProcessStartInfo(Find(tool)!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in args)
        {
            start.ArgumentList.Add(a);
        }

        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
        {
            process.Kill();
            throw new TimeoutException(tool + " did not finish.");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    private static bool Skip()
    {
        if (Available)
        {
            return false;
        }

        Assert.False(Required, "libewf tools (ewfverify, ewfinfo) are required in this job but were not found on PATH.");
        return true;
    }

    /// <summary>ewfverify で検証が成功し (格納された MD5 と計算した MD5 が一致)、ewfinfo のケース番号・証拠番号が一致する。</summary>
    public static void VerifyIfAvailable(string e01, byte[] md5, string caseNumber, string evidenceNumber)
    {
        if (Skip())
        {
            return;
        }

        (int code, string verify) = Run("ewfverify", "-q", e01);
        Assert.True(code == 0, verify);
        Assert.Contains(Convert.ToHexStringLower(md5), verify.ToLowerInvariant());
        Assert.Matches("(?i)ewfverify: SUCCESS", verify);

        (int infoCode, string info) = Run("ewfinfo", e01);
        Assert.True(infoCode == 0, info);
        Assert.Matches(@"(?im)^\s*Case number:\s*" + Regex.Escape(caseNumber) + @"\s*$", info);
        Assert.Matches(@"(?im)^\s*Evidence number:\s*" + Regex.Escape(evidenceNumber) + @"\s*$", info);
    }

    /// <summary>ewfinfo -v の error2 (取得時の読み込みエラー) の範囲。</summary>
    public static void CheckErrorsIfAvailable(string e01, long firstSector, long count)
    {
        if (Skip())
        {
            return;
        }

        (int code, string info) = Run("ewfinfo", "-v", e01);
        Assert.True(code == 0, info);
        Assert.Matches($@"(?im)at sector\(s\):\s*{firstSector}\s*-\s*{firstSector + count - 1}\s+number:\s*{count}", info);
    }

    /// <summary>ewfverify が、壊したチャンクを検証の失敗として報告する。</summary>
    public static void ReportsCorruptionIfAvailable(string e01)
    {
        if (Skip())
        {
            return;
        }

        (int code, string verify) = Run("ewfverify", "-q", e01);
        Assert.True(code != 0, verify);
    }
}

/// <summary>libewf のツールがある環境だけで実行するテスト。</summary>
internal sealed class EwfToolsFactAttribute : FactAttribute
{
    public EwfToolsFactAttribute()
    {
        if (!EwfTools.Available && !EwfTools.Required)
        {
            Skip = "libewf のツール (ewfverify、ewfinfo) をインストールしたランナー (CI のジョブ ewf-crosscheck) でだけ実行する。";
        }
    }
}
