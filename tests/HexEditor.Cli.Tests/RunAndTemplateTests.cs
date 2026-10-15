using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HexEditor.Cli.Services;
using HexEditor.Cli.Tests.Support;
using HexEditor.Core.Engine;
using HexEditor.Core.Scripting;
using HexEditor.Core.Templates;
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

    // ---- 本物のスクリプトのホスト (Core の ScriptHost + CoreScriptEnvironment) ----

    /// <summary>本物のスクリプトのホスト・テンプレートエンジン。信頼の一覧・保存領域はテストの一時フォルダ (普段の設定を読まない・書かない)。</summary>
    private static CliServices RealServices(TempDir dir) => new()
    {
        Scripts = new CoreScriptRunner(() => new ScriptFolders(dir.File("settings"), dir.File("data")), () => null),
        Templates = new CoreTemplateService(() => TemplateCatalog.CreateDefault(dir.File("templates"))),
    };

    /// <summary>TD-AUTO-JS-XOR: パラメータ key (Hex) で全体を XOR する。</summary>
    private const string XorScript = """
        // @name XOR
        // @param key string "Key (hex)"
        await doc.transform("all", "xor", script.args.key);
        """;

    [Fact]
    [Trait("TC", "TC-AUTO-41-01")]
    public void Real_script_host_xor_and_save()
    {
        using var dir = new TempDir();
        string a = dir.Copy("TD-SEQ-1M", "a.bin");
        File.WriteAllText(dir.File("xor.js"), XorScript);
        CliResult result = Hexed.RunInProcess(dir.Path, RealServices(dir), ["run", "xor.js", "a.bin", "--arg", "key=5A", "--save", "--trust"]);

        Assert.True(result.ExitCode == 0, result.Err);
        byte[] written = File.ReadAllBytes(a);
        Assert.Equal(1024 * 1024, written.Length);
        Assert.Equal(0x5A, written[0]);
        Assert.Equal(0x5B, written[1]);
        Assert.Equal((byte)(0x2A ^ 0x5A), written[0x2A]);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-41-02")]
    public void Real_script_host_dry_run_changes_nothing_and_prints_the_summary()
    {
        using var dir = new TempDir();
        string a = dir.Copy("TD-SEQ-1M", "a.bin");
        string before = Sha256(a);
        File.WriteAllText(dir.File("xor.js"), XorScript + "\nawait doc.save();\nconsole.log('saved');");
        CliResult result = Hexed.RunInProcess(dir.Path, RealServices(dir), ["run", "xor.js", "a.bin", "--arg", "key=FF", "--dry-run", "--trust", "--lang", "en"]);

        Assert.True(result.ExitCode == 0, result.Err);
        Assert.Equal(["saved"], result.OutLines);
        Assert.Contains("a.bin: 1048576 byte(s) changed", result.Err);
        Assert.Equal(before, Sha256(a));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-41-03")]
    public void Real_script_host_untrusted_script_exits_with_5()
    {
        using var dir = new TempDir();
        string a = dir.Copy("TD-SEQ-1M", "a.bin");
        string before = Sha256(a);
        File.WriteAllText(dir.File("xor.js"), XorScript);
        CliResult result = Hexed.RunInProcess(dir.Path, RealServices(dir), ["run", "xor.js", "a.bin", "--arg", "key=5A", "--save", "--lang", "en"]);

        Assert.Equal(5, result.ExitCode);
        Assert.Contains("--trust", result.Err);
        Assert.Equal(before, Sha256(a));
    }

    [Fact]
    public void Real_script_host_follows_the_policy_and_limits_of_the_gui_settings()
    {
        // GUI と同じ設定ファイルを読む (AUTO-36 の仕様 8): scripts.policy = disabled なら --trust があっても実行しない (AUTO-34 の仕様 9)。
        using var dir = new TempDir();
        string a = dir.Copy("TD-SEQ-1M", "a.bin");
        string before = Sha256(a);
        File.WriteAllText(dir.File("xor.js"), XorScript);
        Directory.CreateDirectory(dir.File("settings"));
        File.WriteAllText(Path.Combine(dir.File("settings"), "settings.json"), """{ "scripts.policy": "disabled" }""");
        CliResult denied = Hexed.RunInProcess(dir.Path, RealServices(dir), ["run", "xor.js", "a.bin", "--arg", "key=5A", "--save", "--trust", "--lang", "en"]);
        Assert.Equal(5, denied.ExitCode);
        Assert.Contains("scripts.policy", denied.Err);
        Assert.Equal(before, Sha256(a));

        // 実行時間の上限 (scripts.js.timeoutSeconds) も設定から読む: 1 秒で終了コード 7。
        File.WriteAllText(Path.Combine(dir.File("settings"), "settings.json"), """{ "scripts.js.timeoutSeconds": 1 }""");
        File.WriteAllText(dir.File("loop.js"), "while (true) {}\n");
        CliResult limited = Hexed.RunInProcess(dir.Path, RealServices(dir), ["run", "loop.js", "--trust", "--lang", "en"]);
        Assert.Equal(7, limited.ExitCode);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-41-04")]
    public void Real_script_host_errors_exit_with_4_and_the_line_number()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("throws.js"), "const x = 1;\n\nfunction boom() {\n  throw new Error('boom here');\n}\nboom();\n");
        CliResult result = Hexed.RunInProcess(dir.Path, RealServices(dir), ["run", "throws.js", "--trust", "--lang", "en"]);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("throws.js", result.Err);
        Assert.Contains("(4,", result.Err);
        Assert.Contains("boom here", result.Err);
        Assert.Equal("", result.Out.Trim());
    }

    [Fact]
    [Trait("TC", "TC-AUTO-41-05")]
    public void Real_script_host_app_exit_70()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("exit.js"), "console.log('before'); app.exit(70); console.log('after');");
        CliResult result = Hexed.RunInProcess(dir.Path, RealServices(dir), ["run", "exit.js", "--trust"]);

        Assert.Equal(70, result.ExitCode);
        Assert.Equal(["before"], result.OutLines);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-13-01")]
    public void Script_applies_the_standard_pe_template_and_reads_e_lfanew()
    {
        using var dir = new TempDir();
        dir.Copy("TD-PE-X64", "pe.exe");
        File.WriteAllText(dir.File("petpl.js"), """
            const r = await doc.applyTemplate("pe", { show: false });
            console.log(r.get("header.e_lfanew").value, doc.readInt(0x3C, "u32", "le"));
            """);
        CliResult result = Hexed.RunInProcess(dir.Path, RealServices(dir), ["run", "petpl.js", "pe.exe", "--trust"]);

        Assert.True(result.ExitCode == 0, result.Err);
        long expected = BitConverter.ToUInt32(TestDataCatalog.PeX64(), 0x3C);
        Assert.Equal([$"{expected} {expected}"], result.OutLines);
    }

    [Theory]
    [InlineData("TD-PE-X64", "pe.exe", "pe")]
    [InlineData("TD-PNG", "image.png", "png")]
    [Trait("TC", "TC-AUTO-13-02")]
    public void Script_toJSON_equals_the_json_of_hexed_template_except_schema(string data, string name, string template)
    {
        using var dir = new TempDir();
        dir.Copy(data, name);
        File.WriteAllText(dir.File("tojson.js"), $$"""
            const r = await doc.applyTemplate("{{template}}", { show: false });
            console.log(JSON.stringify(r.toJSON()));
            """);
        CliServices services = RealServices(dir);
        CliResult script = Hexed.RunInProcess(dir.Path, services, ["run", "tojson.js", name, "--trust"]);
        CliResult cli = Hexed.RunInProcess(dir.Path, services, ["template", name, "--template", template, "--format", "json"]);

        Assert.True(script.ExitCode == 0, script.Err);
        Assert.True(cli.ExitCode == 0, cli.Err);
        JsonObject fromCli = JsonNode.Parse(cli.Out)!.AsObject();
        Assert.Equal("hexeditor.template/1", (string?)fromCli["schema"]);
        fromCli.Remove("schema");
        JsonNode fromScript = JsonNode.Parse(script.Out)!;
        Assert.True(JsonNode.DeepEquals(fromCli, fromScript), $"script:\n{script.Out}\ncli:\n{cli.Out}");
    }

    [Fact]
    [Trait("TC", "TC-TPL-19-04")]
    public void Headless_script_applies_a_template_file_and_reads_a_path()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("magic.bin"), [0x4D, 0x41, 0x47, 0x43, 0x02, 0x00, 0x01, 0x00]);
        File.WriteAllText(dir.File("header.hxt"), "struct Header { u32 magic; u16 version; u16 count; };\nHeader hdr;\n");
        File.WriteAllText(dir.File("apply.js"), """
            const r = await doc.applyTemplate(script.args.template, { show: false });
            console.log(r.get("hdr.version").value);
            """);
        CliResult result = Hexed.RunInProcess(dir.Path, RealServices(dir), ["run", "apply.js", "magic.bin", "--trust", "--arg", "template=" + dir.File("header.hxt")]);

        Assert.True(result.ExitCode == 0, result.Err);
        Assert.Equal(["2"], result.OutLines);
    }

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

    /// <summary>JSON の葉 (子のないノード) のパス。</summary>
    private static List<string> LeafPaths(JsonNode node)
    {
        var paths = new List<string>();
        void Walk(JsonNode n)
        {
            if (n["children"] is JsonArray children && children.Count > 0)
            {
                foreach (JsonNode? c in children)
                {
                    Walk(c!);
                }
            }
            else if (n["path"] is { } path)
            {
                paths.Add((string)path!);
            }
        }

        Walk(node);
        return paths;
    }

    [Fact]
    [Trait("TC", "TC-AUTO-42-02")]
    public void Real_engine_fields_filter()
    {
        using var dir = new TempDir();
        string pe = TestDataCatalog.Get("TD-PE-X64");
        byte[] bytes = TestDataCatalog.PeX64();
        int sections = BitConverter.ToUInt16(bytes, (int)BitConverter.ToUInt32(bytes, 0x3C) + 6);
        string[] expected = ["header.e_lfanew", .. Enumerable.Range(0, sections).Select(i => $"sections[{i}].Name")];

        CliResult json = Hexed.RunInProcess(dir.Path, RealServices(dir), ["template", pe, "--template", "pe", "--format", "json", "--fields", "header.e_lfanew,sections[*].name"]);
        Assert.True(json.ExitCode == 0, json.Err);
        Assert.Equal(expected, LeafPaths(JsonNode.Parse(json.Out)!));

        CliResult csv = Hexed.RunInProcess(dir.Path, RealServices(dir), ["template", pe, "--template", "pe", "--format", "csv", "--fields", "header.e_lfanew,sections[*].name"]);
        Assert.True(csv.ExitCode == 0, csv.Err);
        Assert.Equal("path,type,offset,size,value", csv.OutLines[0]);
        string[][] rows = [.. csv.OutLines.Skip(1).Select(l => l.Split(','))];
        Assert.Equal(expected, rows.Select(r => r[0]));
        Assert.All(rows, r => Assert.Equal(5, r.Length));
        Assert.Equal(BitConverter.ToUInt32(bytes, 0x3C).ToString(System.Globalization.CultureInfo.InvariantCulture), rows[0][4]);
        Assert.Equal("60", rows[0][2]);
        Assert.Equal("4", rows[0][3]);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-42-03")]
    public void Real_engine_auto_selects_png()
    {
        using var dir = new TempDir();
        dir.Copy("TD-PNG", "image.png");
        dir.Copy("TD-PNG", "image.dat");
        dir.Copy("TD-RANDOM-16M", "random.bin");
        foreach (string name in new[] { "image.png", "image.dat" })
        {
            CliResult result = Hexed.RunInProcess(dir.Path, RealServices(dir), ["template", name, "--auto", "--format", "json"]);
            Assert.True(result.ExitCode == 0, result.Err);
            JsonNode root = JsonNode.Parse(result.Out)!;
            Assert.Equal("png", (string?)root["template"]);
            JsonArray chunks = root["children"]!.AsArray().First(c => (string?)c!["name"] == "chunks")!["children"]!.AsArray();
            Assert.Contains(chunks, c => c!["children"]!.AsArray().Any(f => (string?)f!["name"] == "type" && (string?)f["value"] == "IHDR"));
        }

        CliResult none = Hexed.RunInProcess(dir.Path, RealServices(dir), ["template", "random.bin", "--auto"]);
        Assert.Equal(1, none.ExitCode);
    }

    [Fact]
    public void Template_text_format_unknown_template_and_syntax_errors()
    {
        using var dir = new TempDir();
        string pe = TestDataCatalog.Get("TD-PE-X64");
        CliResult text = Hexed.RunInProcess(dir.Path, RealServices(dir), ["template", pe, "--template", "pe", "--format", "text", "--depth", "1"]);
        Assert.True(text.ExitCode == 0, text.Err);
        Assert.Contains(text.OutLines, l => l.StartsWith("header : ", StringComparison.Ordinal));
        Assert.DoesNotContain(text.OutLines, l => l.TrimStart().StartsWith("e_lfanew", StringComparison.Ordinal));

        CliResult unknown = Hexed.RunInProcess(dir.Path, RealServices(dir), ["template", pe, "--template", "no-such-template", "--lang", "en"]);
        Assert.Equal(2, unknown.ExitCode);
        Assert.Contains("no-such-template", unknown.Err);

        File.WriteAllText(dir.File("bad.hxt"), "struct { u8 x; ");
        CliResult bad = Hexed.RunInProcess(dir.Path, RealServices(dir), ["template", pe, "--template", dir.File("bad.hxt"), "--lang", "en"]);
        Assert.Equal(4, bad.ExitCode);
        Assert.Contains("bad.hxt(1,", bad.Err);
    }
}
