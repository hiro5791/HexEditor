using System.Security.Cryptography;
using HexEditor.Cli.Tests.Support;
using HexEditor.Core.Engine;
using HexEditor.TestData;

namespace HexEditor.Cli.Tests;

/// <summary>
/// スクリプトのヘッドレス実行 (AUTO-41) とテンプレートの適用 (AUTO-42) のコマンドラインの側。スクリプトのホスト・テンプレートエンジンは別の領域が作るため、
/// ここではその代わりを差し込み、引数・ファイルの読み込み・保存の扱い・終了コードの受け渡しを確かめる。本物のエンジンで確かめるテストは、
/// 該当の領域がマージされるまで Skip にしてある (理由に「needs ... merge」)。
/// </summary>
public sealed class RunAndTemplateTests
{
    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Script(TempDir dir, string name = "script.js")
    {
        File.WriteAllText(dir.File(name), "// fake script\n");
        return dir.File(name);
    }

    /// <summary>XOR するだけのスクリプト (AUTO-41 のテストの TD-AUTO-JS-XOR の代わり): 全体を (オフセット mod 256) の代わりに key で XOR する。</summary>
    private static int Xor(ScriptRunRequest request, int length = int.MaxValue)
    {
        byte key = Convert.ToByte(request.Arguments.First(a => a.Key == "key").Value, 16);
        Document doc = request.Documents[0];
        int n = (int)Math.Min(doc.Length, length);
        byte[] data = new byte[n];
        doc.Current.Read(0, data);
        for (int i = 0; i < n; i++)
        {
            data[i] ^= key;
        }

        doc.Overwrite(0, data);
        return 0;
    }

    [Fact]
    [Trait("TC", "TC-AUTO-41-01")]
    public void Save_writes_the_changed_documents_after_the_script_succeeds()
    {
        using var dir = new TempDir();
        string a = dir.Copy("TD-SEQ-1M", "a.bin");
        var scripts = new FakeScripts(r => Xor(r));
        CliResult result = Hexed.RunInProcess(dir.Path, new CliServices { Scripts = scripts }, ["run", Script(dir, "xor.js"), "a.bin", "--arg", "key=5A", "--save", "--trust"]);

        Assert.True(result.ExitCode == 0, result.Err);
        Assert.True(scripts.Last!.Trust);
        Assert.Equal([new KeyValuePair<string, string>("key", "5A")], scripts.Last.Arguments);
        byte[] written = File.ReadAllBytes(a);
        Assert.Equal(1024 * 1024, written.Length);
        Assert.Equal(0x5A, written[0]);
        Assert.Equal(0x5B, written[1]);
        Assert.Equal((byte)(0x2A ^ 0x5A), written[0x2A]);
        Assert.Empty(Directory.GetFiles(dir.Path, ".*"));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-41-02")]
    public void Dry_run_saves_nothing_returns_success_to_save_and_prints_a_summary_of_changed_bytes()
    {
        using var dir = new TempDir();
        string a = dir.Copy("TD-SEQ-1M", "a.bin");
        string before = Sha256(a);
        var scripts = new FakeScripts(r =>
        {
            Xor(r, 0x100);
            bool saved = r.SaveDocument(r.Documents[0]);
            r.Output.WriteLine(saved ? "true" : "false");
            return 0;
        });
        CliResult result = Hexed.RunInProcess(dir.Path, new CliServices { Scripts = scripts }, ["run", Script(dir, "xorsave.js"), "a.bin", "--dry-run", "--trust", "--lang", "en", "--arg", "key=FF"]);

        Assert.True(result.ExitCode == 0, result.Err);
        Assert.Equal(["true"], result.OutLines);
        Assert.Contains("a.bin: 256 byte(s) changed", result.Err);
        Assert.Equal(before, Sha256(a));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-41-03")]
    public void A_failing_script_saves_nothing_and_passes_its_exit_code_through()
    {
        using var dir = new TempDir();
        string a = dir.Copy("TD-SEQ-1M", "a.bin");
        string before = Sha256(a);
        var scripts = new FakeScripts(r =>
        {
            Xor(r);
            if (!r.Trust)
            {
                r.Error.WriteLine("The script is not trusted. Use --trust to run it.");
                return ExitCodes.Denied;
            }

            return 0;
        });
        CliResult result = Hexed.RunInProcess(dir.Path, new CliServices { Scripts = scripts }, ["run", Script(dir, "xor.js"), "a.bin", "--arg", "key=5A", "--save"]);

        Assert.Equal(5, result.ExitCode);
        Assert.Contains("--trust", result.Err);
        Assert.Equal(before, Sha256(a));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(70)]
    [Trait("TC", "TC-AUTO-41-05")]
    public void The_exit_code_of_the_script_is_the_exit_code_of_hexed(int code)
    {
        using var dir = new TempDir();
        CliResult result = Hexed.RunInProcess(dir.Path, new CliServices { Scripts = new FakeScripts(_ => code) }, ["run", Script(dir)]);
        Assert.Equal(code, result.ExitCode);
    }

    [Fact]
    public void Save_as_writes_the_first_document_to_a_new_path_and_unsaved_changes_are_warned_about()
    {
        using var dir = new TempDir();
        string a = dir.Copy("TD-BYTES-256", "a.bin");
        string before = Sha256(a);
        var scripts = new FakeScripts(r => Xor(r));

        CliResult saveAs = Hexed.RunInProcess(dir.Path, new CliServices { Scripts = scripts }, ["run", Script(dir), "a.bin", "--arg", "key=FF", "--save-as", "copy.bin"]);
        Assert.Equal(0, saveAs.ExitCode);
        Assert.Equal(before, Sha256(a));
        Assert.Equal(0xFF, File.ReadAllBytes(dir.File("copy.bin"))[0]);

        CliResult unsaved = Hexed.RunInProcess(dir.Path, new CliServices { Scripts = scripts }, ["run", Script(dir), "a.bin", "--arg", "key=FF", "--lang", "en"]);
        Assert.Equal(0, unsaved.ExitCode);
        Assert.Contains("unsaved changes", unsaved.Err);
        Assert.Equal(before, Sha256(a));

        CliResult selfSave = Hexed.RunInProcess(dir.Path, new CliServices
        {
            Scripts = new FakeScripts(r =>
            {
                Xor(r);
                r.Output.WriteLine(r.SaveDocument(r.Documents[0]) ? "saved" : "failed");
                return 0;
            }),
        }, ["run", Script(dir), "a.bin", "--arg", "key=FF"]);
        Assert.Equal(["saved"], selfSave.OutLines);
        Assert.NotEqual(before, Sha256(a));
    }

    [Fact]
    public void Arguments_files_readonly_allow_and_timeout_are_passed_to_the_script_host()
    {
        using var dir = new TempDir();
        dir.Copy("TD-BYTES-256", "a.bin");
        dir.Copy("TD-ZERO-1M", "b.bin");
        var scripts = new FakeScripts(_ => 0);
        CliResult result = Hexed.RunInProcess(dir.Path, new CliServices { Scripts = scripts },
            ["run", Script(dir), "a.bin", "b.bin", "--arg", "k=v=w", "--arg", "x=", "--allow", "file-read,network", "--allow", "file-write", "--timeout", "2.5", "--readonly"]);

        Assert.Equal(0, result.ExitCode);
        ScriptRunRequest request = scripts.Last!;
        Assert.Equal(2, request.Documents.Count);
        Assert.Equal(256, request.Documents[0].Length);
        Assert.True(request.ReadOnly);
        Assert.True(request.Documents[0].IsReadOnly);
        Assert.Equal([new("k", "v=w"), new("x", "")], request.Arguments);
        Assert.Equal(["file-read", "network", "file-write"], request.Allow);
        Assert.Equal(TimeSpan.FromSeconds(2.5), request.Timeout);
    }

    [Fact]
    public void Run_arguments_are_checked_and_without_a_script_host_the_command_is_not_available()
    {
        using var dir = new TempDir();
        dir.Copy("TD-BYTES-256", "a.bin");
        string script = Script(dir);
        var host = new CliServices { Scripts = new FakeScripts(_ => 0) };
        Assert.Equal(2, Hexed.RunInProcess(dir.Path, host, ["run"]).ExitCode);
        Assert.Equal(2, Hexed.RunInProcess(dir.Path, host, ["run", script, "--arg", "novalue"]).ExitCode);
        Assert.Equal(2, Hexed.RunInProcess(dir.Path, host, ["run", script, "--save", "--dry-run"]).ExitCode);
        Assert.Equal(2, Hexed.RunInProcess(dir.Path, host, ["run", script, "--timeout", "0"]).ExitCode);
        Assert.Equal(2, Hexed.RunInProcess(dir.Path, host, ["run", script, "--save-as", "x.bin"]).ExitCode);
        Assert.Equal(2, Hexed.RunInProcess(dir.Path, host, ["run", script, "a.bin", "--save-as", "a.bin"]).ExitCode);
        Assert.Equal(3, Hexed.RunInProcess(dir.Path, host, ["run", "missing.js"]).ExitCode);
        Assert.Equal(3, Hexed.RunInProcess(dir.Path, host, ["run", script, "missing.bin"]).ExitCode);

        CliResult unavailable = Hexed.RunInProcess(dir.Path, new CliServices(), ["run", script, "--lang", "en"]);
        Assert.Equal(2, unavailable.ExitCode);
        Assert.Contains("not available in this version", unavailable.Err);
    }

    [Fact(Skip = "needs scripting-core merge: TD-AUTO-JS-XOR run by the real script host (TC-AUTO-41-01).")]
    [Trait("TC", "TC-AUTO-41-01")]
    public void Real_script_host_xor_and_save() { }

    [Fact(Skip = "needs scripting-core merge: the real script host must report the trust error with exit code 5 (TC-AUTO-41-03).")]
    [Trait("TC", "TC-AUTO-41-03")]
    public void Real_script_host_untrusted_script_exits_with_5() { }

    [Fact(Skip = "needs scripting-core merge: file name, line number and message of script errors, exit code 4 (TC-AUTO-41-04).")]
    [Trait("TC", "TC-AUTO-41-04")]
    public void Real_script_host_errors_exit_with_4_and_the_line_number() { }

    [Fact(Skip = "needs scripting-core merge: app.exit(70) (TC-AUTO-41-05).")]
    [Trait("TC", "TC-AUTO-41-05")]
    public void Real_script_host_app_exit_70() { }

    // ---- テンプレート (AUTO-42) ----

    [Fact]
    public void Template_options_are_passed_to_the_template_engine_and_its_exit_code_is_returned()
    {
        using var dir = new TempDir();
        string pe = TestDataCatalog.Get("TD-PE-X64");
        var templates = new FakeTemplates(r =>
        {
            r.Output.WriteLine("{\"schema\": \"hexeditor.template/1\"}");
            return 0;
        });
        var services = new CliServices { Templates = templates };

        CliResult json = Hexed.RunInProcess(dir.Path, services,
            ["template", pe, "--template", "pe", "--format", "json", "--offset", "0x40", "--depth", "2", "--fields", "header.e_lfanew,sections[*].name,a[1,2].b"]);
        Assert.Equal(0, json.ExitCode);
        TemplateRequest request = templates.Last!;
        Assert.Equal("pe", request.Template);
        Assert.False(request.Auto);
        Assert.Equal(0x40, request.Offset);
        Assert.Equal("json", request.Format);
        Assert.Equal(2, request.Depth);
        Assert.Equal(["header.e_lfanew", "sections[*].name", "a[1,2].b"], request.Fields);
        Assert.Equal(["{\"schema\": \"hexeditor.template/1\"}"], json.OutLines);

        CliResult auto = Hexed.RunInProcess(dir.Path, new CliServices { Templates = new FakeTemplates(_ => 1) }, ["template", pe, "--auto"]);
        Assert.Equal(1, auto.ExitCode);
        CliResult error = Hexed.RunInProcess(dir.Path, new CliServices { Templates = new FakeTemplates(_ => 4) }, ["template", pe, "--template", "x"]);
        Assert.Equal(4, error.ExitCode);
    }

    [Fact]
    public void Template_arguments_are_checked_and_without_an_engine_the_command_is_not_available()
    {
        using var dir = new TempDir();
        string pe = TestDataCatalog.Get("TD-PE-X64");
        var services = new CliServices { Templates = new FakeTemplates(_ => 0) };
        Assert.Equal(2, Hexed.RunInProcess(dir.Path, services, ["template", pe]).ExitCode);
        Assert.Equal(2, Hexed.RunInProcess(dir.Path, services, ["template", pe, "--auto", "--template", "pe"]).ExitCode);
        Assert.Equal(2, Hexed.RunInProcess(dir.Path, services, ["template", pe, "--auto", "--format", "xml"]).ExitCode);
        Assert.Equal(2, Hexed.RunInProcess(dir.Path, services, ["template", pe, "--auto", "--offset", "99999999"]).ExitCode);
        Assert.Equal(3, Hexed.RunInProcess(dir.Path, services, ["template", dir.File("none.bin"), "--auto"]).ExitCode);
        Assert.Equal(2, Hexed.RunInProcess(dir.Path, new CliServices(), ["template", pe, "--auto"]).ExitCode);
    }

    [Fact(Skip = "needs templates-core merge: the JSON of 'hexed template' equals the template panel of the GUI (TC-AUTO-42-01, UI test).")]
    [Trait("TC", "TC-AUTO-42-01")]
    public void Real_engine_json_equals_the_template_panel() { }

    [Fact(Skip = "needs templates-core merge: --fields filters the real template result (TC-AUTO-42-02).")]
    [Trait("TC", "TC-AUTO-42-02")]
    public void Real_engine_fields_filter() { }

    [Fact(Skip = "needs templates-core merge: --auto picks the PNG template (TC-AUTO-42-03).")]
    [Trait("TC", "TC-AUTO-42-03")]
    public void Real_engine_auto_selects_png() { }
}
