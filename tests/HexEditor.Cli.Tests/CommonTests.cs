using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using HexEditor.Cli.Tests.Support;
using HexEditor.TestData;

namespace HexEditor.Cli.Tests;

/// <summary>コマンドラインの共通仕様 (AUTO-36)。</summary>
public sealed partial class CommonTests
{
    private static readonly string[] CommandNames = ["open", "hash", "dump", "compare", "run", "template", "patch", "convert", "find"];

    [Fact]
    [Trait("TC", "TC-AUTO-36-01")]
    public void Help_is_printed_in_the_display_language_with_exit_code_0()
    {
        using var dir = new TempDir();
        CliResult en = Hexed.Run(dir.Path, "--help", "--lang", "en");
        CliResult ja = Hexed.Run(dir.Path, "--help", "--lang", "ja");
        CliResult hashJa = Hexed.Run(dir.Path, "hash", "-h", "--lang", "ja");

        Assert.Equal(0, en.ExitCode);
        Assert.Equal(0, ja.ExitCode);
        Assert.Equal(0, hashJa.ExitCode);
        Assert.Equal(Messages.English("Cli_Help_Main").Trim(), en.Out.Replace("\r\n", "\n").Trim());
        foreach (string name in CommandNames)
        {
            Assert.Contains(name, en.Out);
            Assert.Contains(name, ja.Out);
        }

        Assert.Equal(new Messages("ja").Get("Cli_Help_Main").Trim(), ja.Out.Replace("\r\n", "\n").Trim());
        Assert.Contains("使い方", ja.Out);
        Assert.DoesNotContain("Usage:", ja.Out);
        Assert.Equal(new Messages("ja").Get("Cli_Help_hash").Trim(), hashJa.Out.Replace("\r\n", "\n").Trim());
        Assert.Contains("PowerShell", hashJa.Out);
        Assert.Contains("'$1F00'", hashJa.Out);
        Assert.Equal(string.Empty, hashJa.Err);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-36-02")]
    public void Unknown_options_expression_errors_and_unknown_commands_exit_with_2()
    {
        using var dir = new TempDir();
        string seq = TestDataCatalog.Get("TD-SEQ-1M");
        CliResult unknownOption = Hexed.Run(dir.Path, "hash", seq, "--no-such-option", "--lang", "en");
        CliResult badExpression = Hexed.Run(dir.Path, "dump", seq, "--range", "0x10+", "--lang", "en");
        CliResult unknownCommand = Hexed.Run(dir.Path, "nosuchcommand", "--lang", "en");

        foreach (CliResult r in new[] { unknownOption, badExpression, unknownCommand })
        {
            Assert.Equal(2, r.ExitCode);
            Assert.Equal(string.Empty, r.Out);
            Assert.Contains("--help", r.Err);
        }

        Assert.Contains("--no-such-option", unknownOption.Err);
        Assert.Contains("0x10+", badExpression.Err);
        Assert.Matches(@"position \d+", badExpression.Err);
        Assert.Contains("nosuchcommand", unknownCommand.Err);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-36-03")]
    public void Progress_is_not_mixed_into_redirected_output()
    {
        using var dir = new TempDir();
        string path = TestDataCatalog.Get("TD-MARKERS-1G");
        string outFile = dir.File("out.txt");
        string errFile = dir.File("err.txt");

        // 標準出力と標準エラー出力をファイルにリダイレクトして実行する (cmd と同じ)。
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = dir.Path,
            Arguments = $"/c \"\"{Hexed.ExePath}\" hash \"{path}\" -a sha256 > \"{outFile}\" 2> \"{errFile}\"\"",
            Environment = { ["HEXED_TEST_PROFILE"] = Hexed.Profile },
        })!;
        Assert.True(process.WaitForExit(120_000));

        string output = File.ReadAllText(outFile);
        string[] lines = output.TrimEnd('\r', '\n').Split('\n');
        Assert.Single(lines);
        Assert.StartsWith("sha256  ", lines[0]);
        Assert.EndsWith("  " + path, lines[0].TrimEnd('\r'));
        Assert.DoesNotContain('\r', output.Replace("\r\n", "\n"));
        Assert.DoesNotContain("%", output);
        Assert.DoesNotContain('\u001b', output);
        string error = File.ReadAllText(errFile);
        Assert.DoesNotContain('\r', error);
        Assert.DoesNotContain("%", error);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-36-03")]
    public void Progress_is_shown_on_one_line_only_when_the_error_output_is_a_terminal()
    {
        using var dir = new TempDir();
        string path = TestDataCatalog.Get("TD-MARKERS-1G");
        CliResult terminal = RunWithTerminalFlag(dir.Path, errIsTerminal: true, "hash", path, "-a", "sha256");
        CliResult redirected = RunWithTerminalFlag(dir.Path, errIsTerminal: false, "hash", path, "-a", "sha256");

        Assert.Equal(0, terminal.ExitCode);
        Assert.Contains('\r', terminal.Err);
        Assert.Contains("%", terminal.Err);
        Assert.DoesNotContain('\n', terminal.Err);
        Assert.Equal(string.Empty, redirected.Err);
        Assert.Equal(terminal.Out, redirected.Out);
        Assert.DoesNotContain('\r', terminal.Out);
    }

    private static CliResult RunWithTerminalFlag(string directory, bool errIsTerminal, params string[] args)
    {
        var utf8 = new System.Text.UTF8Encoding(false);
        var outStream = new MemoryStream();
        var errStream = new MemoryStream();
        var outWriter = new StreamWriter(outStream, utf8) { NewLine = "\n" };
        var errWriter = new StreamWriter(errStream, utf8) { NewLine = "\n", AutoFlush = true };
        var host = new CliHost
        {
            Out = outWriter,
            Err = errWriter,
            OpenInput = () => new MemoryStream(),
            CurrentDirectory = directory,
            ErrIsTerminal = errIsTerminal,
            TestProfile = Hexed.Profile,
            WindowsLanguages = () => ["en-US"],
        };
        int code = CliApp.Run([.. args, "--lang", "en"], host);
        outWriter.Flush();
        return new CliResult(code, outStream.ToArray(), errStream.ToArray());
    }

    [Fact]
    [Trait("TC", "TC-AUTO-36-05")]
    public void Json_output_contains_schema_and_is_valid_json_with_untranslated_keys()
    {
        using var dir = new TempDir();
        string seq = TestDataCatalog.Get("TD-SEQ-1M");
        string diff1 = TestDataCatalog.Get("TD-AUTO-SEQ-1M-DIFF1");
        string find = TestDataCatalog.Get("TD-AUTO-FIND");
        (string Schema, string[] Args)[] cases =
        [
            ("hexeditor.hash/1", ["hash", seq, "--format", "json"]),
            ("hexeditor.compare/1", ["compare", seq, diff1, "--format", "json"]),
            ("hexeditor.find/1", ["find", find, "--hex", "DE AD", "--format", "json"]),
        ];
        foreach ((string schema, string[] args) in cases)
        {
            CliResult en = Hexed.Run(dir.Path, [.. args, "--lang", "en"]);
            CliResult ja = Hexed.Run(dir.Path, [.. args, "--lang", "ja"]);
            using JsonDocument enJson = JsonDocument.Parse(en.OutBytes, new JsonDocumentOptions { AllowTrailingCommas = false });
            using JsonDocument jaJson = JsonDocument.Parse(ja.OutBytes);
            Assert.Equal(schema, enJson.RootElement.GetProperty("schema").GetString());
            Assert.Equal(KeysOf(enJson.RootElement), KeysOf(jaJson.RootElement));
            Assert.Equal(enJson.RootElement.GetRawText(), jaJson.RootElement.GetRawText());
        }
    }

    private static string KeysOf(JsonElement element)
    {
        var keys = new List<string>();
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    keys.Add(p.Name);
                    Walk(p.Value);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in e.EnumerateArray())
                {
                    Walk(item);
                }
            }
        }

        Walk(element);
        return string.Join(",", keys);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-36-06")]
    public void Headless_commands_do_not_load_winui_or_start_the_gui()
    {
        using var dir = new TempDir();
        string path = TestDataCatalog.Get("TD-MARKERS-1G");
        string folder = Path.GetDirectoryName(Hexed.ExePath)!;
        Assert.False(File.Exists(Path.Combine(folder, "HexEditor.exe")));
        Assert.False(File.Exists(Path.Combine(folder, "Microsoft.UI.Xaml.dll")));

        // 実行中のプロセスのモジュールの一覧に WinUI がないこと (100 GiB のハッシュの計算中に、.NET のモジュールが読み込まれるのを待って取る)。
        string huge = TestDataCatalog.Get("TD-SPARSE-100G");
        using var process = Process.Start(Hexed.StartInfo(dir.Path, ["hash", huge, "-a", "sha256"]))!;
        _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Assert.True(SpinWait.SpinUntil(() =>
            {
                try
                {
                    process.Refresh();
                    foreach (ProcessModule module in process.Modules)
                    {
                        loaded.Add(module.ModuleName);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 起動直後は読めない。
                }

                // .NET の実行環境が読み込まれた後、しばらく計算が続いた状態で調べる。
                return loaded.Contains("coreclr.dll") && loaded.Contains("clrjit.dll");
            }, TimeSpan.FromSeconds(60)));
            Thread.Sleep(1500);
            process.Refresh();
            foreach (ProcessModule module in process.Modules)
            {
                loaded.Add(module.ModuleName);
            }
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        Assert.NotEmpty(loaded);
        Assert.DoesNotContain(loaded, name => name.StartsWith("Microsoft.UI.Xaml", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(loaded, name => name.StartsWith("Microsoft.WindowsAppRuntime", StringComparison.OrdinalIgnoreCase));

        CliResult dump = Hexed.Run(dir.Path, "dump", TestDataCatalog.Get("TD-SEQ-1M"), "--range", "0:16");
        Assert.Equal(0, dump.ExitCode);
        Assert.DoesNotContain(Process.GetProcessesByName("HexEditor"),
            p => string.Equals(SafePath(p), Path.Combine(folder, "HexEditor.exe"), StringComparison.OrdinalIgnoreCase));
    }

    private static string? SafePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [Fact]
    public void Version_prints_the_build_version_and_exit_code_0()
    {
        using var dir = new TempDir();
        CliResult result = Hexed.Run(dir.Path, "--version");
        Assert.Equal(0, result.ExitCode);
        string expected = System.Diagnostics.FileVersionInfo.GetVersionInfo(Hexed.ExePath).ProductVersion!;
        Assert.Equal($"hexed {expected}", result.Out.Trim());
    }

    [Fact]
    public void Output_file_receives_the_result_and_nothing_is_written_for_a_usage_error()
    {
        using var dir = new TempDir();
        string seq = TestDataCatalog.Get("TD-SEQ-1M");
        string output = dir.File("hash.txt");
        File.WriteAllText(output, "existing");

        CliResult usage = Hexed.Run(dir.Path, "hash", seq, "--nope", "-o", output);
        Assert.Equal(2, usage.ExitCode);
        Assert.Equal("existing", File.ReadAllText(output));

        CliResult ok = Hexed.Run(dir.Path, "hash", seq, "-o", output);
        Assert.Equal(0, ok.ExitCode);
        Assert.Equal(string.Empty, ok.Out);
        Assert.StartsWith("sha256  ", File.ReadAllText(output));

        CliResult overInput = Hexed.Run(dir.Path, "hash", output, "-o", output, "--lang", "en");
        Assert.Equal(2, overInput.ExitCode);
        Assert.StartsWith("sha256  ", File.ReadAllText(output));
    }

    [Fact]
    public void Missing_files_exit_with_3_and_name_the_path()
    {
        using var dir = new TempDir();
        string missing = dir.File("none.bin");
        foreach (string command in new[] { "hash", "dump", "find" })
        {
            string[] args = command == "find" ? ["find", missing, "--hex", "00", "--lang", "en"] : [command, missing, "--lang", "en"];
            CliResult result = Hexed.Run(dir.Path, args);
            Assert.Equal(3, result.ExitCode);
            Assert.Contains(missing, result.Err);
        }
    }

    [GeneratedRegex(@"""Cli_[A-Za-z0-9_]+")]
    private static partial Regex KeyPattern();

    [Fact]
    public void Every_message_key_used_in_the_source_exists_in_english_and_japanese_with_the_same_keys()
    {
        string root = RepoRoot();
        var used = new HashSet<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src", "HexEditor.Cli"), "*.cs", SearchOption.AllDirectories))
        {
            foreach (Match m in KeyPattern().Matches(File.ReadAllText(file)))
            {
                used.Add(m.Value.TrimStart('"'));
            }
        }

        var en = ReadKeys(Path.Combine(root, "src", "HexEditor.Cli", "Strings", "en", "Cli.resw"));
        var ja = ReadKeys(Path.Combine(root, "src", "HexEditor.Cli", "Strings", "ja", "Cli.resw"));
        Assert.Equal(en.OrderBy(k => k), ja.OrderBy(k => k));
        foreach (string key in used)
        {
            // 末尾が _ の接頭辞 (Cli_Help_ + 名前など) は、実際のキーを別に調べる。
            if (key.EndsWith('_'))
            {
                continue;
            }

            Assert.Contains(key, en);
        }

        foreach (string name in CommandNames)
        {
            Assert.Contains("Cli_Help_" + name, en);
        }

        foreach (string kind in new[] { "changed", "inserted", "deleted", "unreadable" })
        {
            Assert.Contains("Cli_Compare_Kind_" + kind, en);
        }

        foreach (HexEditor.Core.Expressions.ExpressionError error in Enum.GetValues<HexEditor.Core.Expressions.ExpressionError>())
        {
            Assert.Contains("Cli_Expression_" + error, en);
        }
    }

    private static HashSet<string> ReadKeys(string path) =>
        [.. System.Xml.Linq.XDocument.Load(path).Root!.Elements("data").Select(d => d.Attribute("name")!.Value)];

    internal static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "HexEditor.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
