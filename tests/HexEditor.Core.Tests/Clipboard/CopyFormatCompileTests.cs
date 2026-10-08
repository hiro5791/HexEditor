using System.Diagnostics;
using HexEditor.Core.Clipboard;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Clipboard;

/// <summary>
/// 指定したコマンドが PATH にないときはスキップする Fact (TC-EDIT-25-02 の「環境」: CI ランナーに各言語の処理系を入れる)。
/// </summary>
public sealed class RequiresToolFactAttribute : FactAttribute
{
    public RequiresToolFactAttribute(string tool)
    {
        if (CopyFormatCompileTests.FindTool(tool) is null)
        {
            Skip = $"{tool} が PATH にありません (CI ランナーに入れて実行する)。";
        }
    }
}

/// <summary>各言語の配列の出力を処理系に通す (TC-EDIT-25-02)。0〜255 の 256 バイト (TD-BYTES-256) を対象にする。</summary>
[Trait("Category", "Integration")]
public sealed class CopyFormatCompileTests
{
    private static readonly byte[] Bytes256 = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

    private static readonly string Expected = string.Join('\n', Enumerable.Range(0, 256)) + "\n";

    internal static string? FindTool(string name)
    {
        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat", string.Empty] : [string.Empty];
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            foreach (string ext in extensions)
            {
                string path = Path.Combine(dir.Trim('"'), name + ext);
                // Windows ストアの python3 の入口 (WindowsApps) は実体がないことがあるため使わない。
                if (File.Exists(path) && !path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static string Output(CopyFormat format, CopyOptions? options = null) =>
        CopyFormatter.Format(format, Bytes256, 0, (options ?? new CopyOptions()) with { NewLine = "\n" }, out _);

    private static string Run(string tool, string arguments, string directory)
    {
        var info = new ProcessStartInfo(FindTool(tool)!, arguments)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        using Process process = Process.Start(info)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(3)), $"{tool} が終わりません。");
        Assert.True(process.ExitCode == 0, $"{tool} {arguments}: {stderr.Result}{stdout.Result}");
        return stdout.Result.ReplaceLineEndings("\n");
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "compile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [RequiresToolFact("python")]
    [Trait(TC, "TC-EDIT-25-02")]
    public void Python_both_notations()
    {
        string dir = TempDir();
        foreach (CopyOptions options in new[] { new CopyOptions(), new CopyOptions { PythonBytesLiteral = true } })
        {
            File.WriteAllText(Path.Combine(dir, "p.py"), Output(CopyFormat.ArrayPython, options) + "\nfor b in data:\n    print(b)\n");
            Assert.Equal(Expected, Run("python", "p.py", dir));
        }
    }

    [RequiresToolFact("dotnet")]
    [Trait(TC, "TC-EDIT-25-02")]
    public void CSharp_both_notations()
    {
        string dir = TempDir();
        string array = Output(CopyFormat.ArrayCSharp);
        string span = Output(CopyFormat.ArrayCSharp, new CopyOptions { CSharpSpan = true, VariableName = "Span" });
        string program = $$"""
            {{array}}
            foreach (byte b in data) System.Console.WriteLine(b);
            foreach (byte b in P.Span) System.Console.WriteLine(b);
            static class P
            {
                public static {{span}}
            }
            """;
        File.WriteAllText(Path.Combine(dir, "p.cs"), program);
        Assert.Equal(Expected + Expected, Run("dotnet", "run p.cs", dir));
    }

    [RequiresToolFact("gcc")]
    [Trait(TC, "TC-EDIT-25-02")]
    public void C()
    {
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "p.c"), "#include <stdio.h>\n#include <stdint.h>\n" + Output(CopyFormat.ArrayC)
            + "\nint main(void) { for (size_t i = 0; i < sizeof data; i++) printf(\"%u\\n\", data[i]); return 0; }\n");
        Run("gcc", "-Wall -Werror -o p p.c", dir);
        Assert.Equal(Expected, Run(Path.Combine(dir, "p"), string.Empty, dir));
    }

    [RequiresToolFact("javac")]
    [Trait(TC, "TC-EDIT-25-02")]
    public void Java()
    {
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "Main.java"), "public class Main {\nstatic " + Output(CopyFormat.ArrayJava)
            + "\npublic static void main(String[] a) { for (byte b : data) System.out.println(b & 0xFF); }\n}\n");
        Run("javac", "Main.java", dir);
        Assert.Equal(Expected, Run("java", "-cp . Main", dir));
    }

    [RequiresToolFact("rustc")]
    [Trait(TC, "TC-EDIT-25-02")]
    public void Rust()
    {
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "p.rs"), "fn main() {\n" + Output(CopyFormat.ArrayRust) + "\nfor b in data.iter() { println!(\"{}\", b); }\n}\n");
        Run("rustc", "p.rs", dir);
        Assert.Equal(Expected, Run(Path.Combine(dir, "p"), string.Empty, dir));
    }

    [RequiresToolFact("go")]
    [Trait(TC, "TC-EDIT-25-02")]
    public void Go()
    {
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "p.go"), "package main\nimport \"fmt\"\nfunc main() {\n" + Output(CopyFormat.ArrayGo)
            + "\nfor _, b := range data { fmt.Println(b) }\n}\n");
        Assert.Equal(Expected, Run("go", "run p.go", dir));
    }
}
