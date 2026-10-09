using System.Diagnostics;
using System.Globalization;
using System.Text;
using HexEditor.Core.Clipboard;
using HexEditor.Core.Formats;
using HexEditor.Core.Tests.Clipboard;
using static HexEditor.Core.Tests.Formats.FormatTestSupport;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Formats;

/// <summary>
/// ソースコードの配列のエクスポートを各言語の処理系に通す (TC-TOOL-09-01)。TD-BYTES-256 を要素 1・2・4・8 バイトと 16 進 / 10 進の組み合わせで
/// 書き出し、要素数と最初・最後の要素を出力する小さなプログラムに組み込んでコンパイル・実行する。処理系が PATH にない場合はスキップする。
/// </summary>
[Trait("Category", "Integration")]
public sealed class ExportCompileTests
{
    private static readonly byte[] Bytes256 = [.. Enumerable.Range(0, 256).Select(i => (byte)i)];

    private static readonly (int Size, bool Decimal)[] Variants = [.. new[] { 1, 2, 4, 8 }.SelectMany(s => new[] { (s, false), (s, true) })];

    private static string Name((int Size, bool Decimal) v) => $"d{v.Size}{(v.Decimal ? "d" : "h")}";

    private static string Array(string format, (int Size, bool Decimal) v, bool header = false) =>
        Export(Bytes256, new ExportOptions
        {
            Format = format,
            NewLine = "\n",
            HeaderFile = header,
            LengthConstant = header,
            Copy = new CopyOptions { ElementSize = v.Size, ArrayDecimal = v.Decimal, VariableName = Name(v) },
        });

    /// <summary>期待する出力: 各組み合わせの「要素数 最初 最後」(符号なしの 10 進)。</summary>
    private static string Expected()
    {
        var sb = new StringBuilder();
        foreach ((int size, _) in Variants)
        {
            ulong Value(int index)
            {
                ulong v = 0;
                for (int k = 0; k < size; k++)
                {
                    v |= (ulong)Bytes256[index * size + k] << (k * 8);
                }

                return v;
            }

            int count = 256 / size;
            sb.Append(CultureInfo.InvariantCulture, $"{count} {Value(0)} {Value(count - 1)}\n");
        }

        return sb.ToString();
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "export-compile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Run(string tool, string arguments, string directory)
    {
        var info = new ProcessStartInfo(CopyFormatCompileTests.FindTool(tool) ?? tool, arguments)
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
        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(5)), $"{tool} が終わりません。");
        Assert.True(process.ExitCode == 0, $"{tool} {arguments}: {stderr.Result}{stdout.Result}");
        return stdout.Result.ReplaceLineEndings("\n");
    }

    [RequiresToolFact("gcc")]
    [Trait(TC, "TC-TOOL-09-01")]
    public void C_and_c_header()
    {
        string dir = TempDir();
        var sb = new StringBuilder("#include <stdio.h>\n#include <stdint.h>\n");
        var main = new StringBuilder("int main(void) {\n");
        foreach (var v in Variants)
        {
            string file = Name(v) + ".h";
            File.WriteAllText(Path.Combine(dir, file), Array(FormatIds.C, v, header: true));
            sb.Append($"#include \"{file}\"\n");
            main.Append($"printf(\"%u %llu %llu\\n\", (unsigned)(sizeof {Name(v)} / sizeof {Name(v)}[0]), (unsigned long long){Name(v)}[0], "
                + $"(unsigned long long){Name(v)}[sizeof {Name(v)} / sizeof {Name(v)}[0] - 1]);\n");
        }

        File.WriteAllText(Path.Combine(dir, "p.c"), sb + main.ToString() + "return 0;\n}\n");
        Run("gcc", "-Wall -Wextra -Werror -o p p.c", dir);
        Assert.Equal(Expected(), Run(Path.Combine(dir, "p"), string.Empty, dir));
    }

    [RequiresToolFact("g++")]
    [Trait(TC, "TC-TOOL-09-01")]
    public void Cpp_header()
    {
        string dir = TempDir();
        var sb = new StringBuilder("#include <cstdio>\n");
        var main = new StringBuilder("int main() {\n");
        foreach (var v in Variants)
        {
            string file = Name(v) + ".hpp";
            File.WriteAllText(Path.Combine(dir, file), Array(FormatIds.Cpp, v, header: true));
            sb.Append($"#include \"{file}\"\n");
            main.Append($"std::printf(\"%zu %llu %llu\\n\", {Name(v)}.size(), (unsigned long long){Name(v)}.front(), (unsigned long long){Name(v)}.back());\n");
        }

        File.WriteAllText(Path.Combine(dir, "p.cpp"), sb + main.ToString() + "return 0;\n}\n");
        Run("g++", "-std=c++17 -Wall -Wextra -Werror -o p p.cpp", dir);
        Assert.Equal(Expected(), Run(Path.Combine(dir, "p"), string.Empty, dir));
    }

    [RequiresToolFact("python")]
    [Trait(TC, "TC-TOOL-09-01")]
    public void Python()
    {
        string dir = TempDir();
        var sb = new StringBuilder();
        foreach (var v in Variants)
        {
            sb.Append(Array(FormatIds.Python, v)).Append($"print(len({Name(v)}), {Name(v)}[0], {Name(v)}[-1])\n");
        }

        File.WriteAllText(Path.Combine(dir, "p.py"), sb.ToString());
        Assert.Equal(Expected(), Run("python", "p.py", dir));
    }

    [RequiresToolFact("node")]
    [Trait(TC, "TC-TOOL-09-01")]
    public void JavaScript()
    {
        string dir = TempDir();
        var sb = new StringBuilder();
        foreach (var v in Variants)
        {
            sb.Append(Array(FormatIds.JavaScript, v)).Append($"console.log(`${{{Name(v)}.length}} ${{{Name(v)}[0]}} ${{{Name(v)}[{Name(v)}.length - 1]}}`);\n");
        }

        File.WriteAllText(Path.Combine(dir, "p.js"), sb.ToString());
        Assert.Equal(Expected(), Run("node", "p.js", dir));
    }

    [RequiresToolFact("dotnet")]
    [Trait(TC, "TC-TOOL-09-01")]
    public void CSharp()
    {
        string dir = TempDir();
        var sb = new StringBuilder();
        foreach (var v in Variants)
        {
            sb.Append(Array(FormatIds.CSharp, v)).Append($"System.Console.WriteLine($\"{{{Name(v)}.Length}} {{{Name(v)}[0]}} {{{Name(v)}[^1]}}\");\n");
        }

        File.WriteAllText(Path.Combine(dir, "p.cs"), sb.ToString());
        Assert.Equal(Expected(), Run("dotnet", "run p.cs", dir));
    }

    [RequiresToolFact("javac")]
    [Trait(TC, "TC-TOOL-09-01")]
    public void Java()
    {
        string dir = TempDir();
        var fields = new StringBuilder();
        var main = new StringBuilder();
        foreach (var v in Variants)
        {
            fields.Append("static ").Append(Array(FormatIds.Java, v));
            string mask(string e) => v.Size switch
            {
                1 => $"({e} & 0xFF)",
                2 => $"({e} & 0xFFFF)",
                4 => $"({e} & 0xFFFFFFFFL)",
                _ => $"Long.toUnsignedString({e})",
            };
            main.Append($"System.out.println({Name(v)}.length + \" \" + {mask(Name(v) + "[0]")} + \" \" + {mask($"{Name(v)}[{Name(v)}.length - 1]")});\n");
        }

        File.WriteAllText(Path.Combine(dir, "Main.java"), $"public class Main {{\n{fields}\npublic static void main(String[] a) {{\n{main}}}\n}}\n");
        Run("javac", "-Werror Main.java", dir);
        Assert.Equal(Expected(), Run("java", "-cp . Main", dir));
    }

    [RequiresToolFact("rustc")]
    [Trait(TC, "TC-TOOL-09-01")]
    public void Rust()
    {
        string dir = TempDir();
        var sb = new StringBuilder("fn main() {\n");
        foreach (var v in Variants)
        {
            sb.Append(Array(FormatIds.Rust, v)).Append($"println!(\"{{}} {{}} {{}}\", {Name(v)}.len(), {Name(v)}[0], {Name(v)}[{Name(v)}.len() - 1]);\n");
        }

        File.WriteAllText(Path.Combine(dir, "p.rs"), sb + "}\n");
        Run("rustc", "-D warnings p.rs", dir);
        Assert.Equal(Expected(), Run(Path.Combine(dir, "p"), string.Empty, dir));
    }

    [RequiresToolFact("go")]
    [Trait(TC, "TC-TOOL-09-01")]
    public void Go()
    {
        string dir = TempDir();
        var sb = new StringBuilder("package main\nimport \"fmt\"\nfunc main() {\n");
        foreach (var v in Variants)
        {
            sb.Append(Array(FormatIds.Go, v)).Append($"fmt.Println(len({Name(v)}), {Name(v)}[0], {Name(v)}[len({Name(v)})-1])\n");
        }

        File.WriteAllText(Path.Combine(dir, "p.go"), sb + "}\n");
        Assert.Equal(Expected(), Run("go", "run p.go", dir));
    }

    [RequiresToolFact("fpc")]
    [Trait(TC, "TC-TOOL-09-01")]
    public void Pascal()
    {
        string dir = TempDir();
        var consts = new StringBuilder();
        var main = new StringBuilder();
        foreach (var v in Variants)
        {
            consts.Append(Array(FormatIds.Pascal, v));
            main.Append($"WriteLn(Length({Name(v)}), ' ', {Name(v)}[0], ' ', {Name(v)}[High({Name(v)})]);\n");
        }

        File.WriteAllText(Path.Combine(dir, "p.pas"), $"program p;\n{consts}\nbegin\n{main}end.\n");
        Run("fpc", "-Sew p.pas", dir);
        Assert.Equal(Expected(), Run(Path.Combine(dir, "p"), string.Empty, dir));
    }
}
