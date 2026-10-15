using System.IO.Compression;
using System.Text.Json.Nodes;

namespace HexEditor.TestData;

/// <summary>
/// テスト用のプラグイン (08 のテストデータ TD-AUTO-PLG-*、TD-AUTO-HEXPLUGIN*) を組み立てる。プラグインの DLL は tests/plugins/ のプロジェクトを
/// ビルドしたもので、テストのプロジェクトがテストの出力の <c>testplugins\&lt;プロジェクト&gt;\</c> に置く。ここではマニフェストを書き、
/// <c>&lt;plugins&gt;/&lt;id&gt;/&lt;version&gt;/</c> に置く。
/// </summary>
public static class TestPlugins
{
    /// <summary>ビルドしたテスト用のプラグインの置き場所 (テストの出力の testplugins)。</summary>
    public static string BinRoot => Path.Combine(AppContext.BaseDirectory, "testplugins");

    /// <summary>1 つのテスト用のプラグイン。<paramref name="Files"/> は <paramref name="Project"/> の出力から写すファイル。</summary>
    public sealed record Definition(string Id, string Name, string Version, string Sdk, string Project, string Entry, IReadOnlyList<string> Files,
        JsonObject? TestJson = null, string? NameJa = null, string Publisher = "HexEditor Tests", bool CopyBinaries = true);

    private static Definition Generic(string id, JsonObject test, string sdk = "1.0") =>
        new(id, id, "1.0.0", sdk, "Plugin.Generic", "Plugin.Generic.dll", ["Plugin.Generic.dll"], test);

    private static JsonObject Command(string id, string title, string output, bool fail = false) =>
        new() { ["id"] = id, ["title"] = title, ["output"] = output, ["throw"] = fail };

    /// <summary>TD-AUTO-PLG-SAMPLE (ID test.sample、名前 Sample Plugin)。</summary>
    public static Definition Sample { get; } = new("test.sample", "Sample Plugin", "1.0.0", "1.0", "Plugin.Sample", "Plugin.Sample.dll", ["Plugin.Sample.dll"],
        NameJa: "サンプルプラグイン");

    /// <summary>TD-AUTO-PLG-SDK12: 1 つ前のマイナーバージョンの SDK でビルドしたものとして使う (コマンド sdk12.ping)。</summary>
    public static Definition Sdk12 { get; } = Generic("sdk12", new JsonObject { ["commands"] = new JsonArray(Command("sdk12.ping", "sdk12: ping", "pong from sdk12")) });

    /// <summary>TD-AUTO-PLG-SDK13: 1 つ新しいマイナーバージョンの SDK (アセンブリのバージョン 1.1) でビルドしたもの。</summary>
    public static Definition Sdk13 { get; } = new("sdk13", "sdk13", "1.0.0", "1.1", "Plugin.Sdk13", "Plugin.Sdk13.dll", ["Plugin.Sdk13.dll"]);

    /// <summary>TD-AUTO-PLG-DEP-A (TestLib 1.0.0)。</summary>
    public static Definition DepA { get; } = new("dep-a", "dep-a", "1.0.0", "1.0", "Plugin.DepA", "Plugin.DepA.dll", ["Plugin.DepA.dll", "Plugin.DepA.deps.json", "TestLib.dll"]);

    /// <summary>TD-AUTO-PLG-DEP-B (TestLib 2.0.0)。</summary>
    public static Definition DepB { get; } = new("dep-b", "dep-b", "1.0.0", "1.0", "Plugin.DepB", "Plugin.DepB.dll", ["Plugin.DepB.dll", "Plugin.DepB.deps.json", "TestLib.dll"]);

    /// <summary>TD-AUTO-PLG-THROW: インスペクタの型の読み取りとコマンドの実行で毎回例外を投げる。</summary>
    public static Definition Throw { get; } = Generic("throw", new JsonObject
    {
        ["commands"] = new JsonArray(Command("throw.run", "throw: run", "", fail: true)),
        ["inspectorType"] = new JsonObject { ["id"] = "throw.type", ["name"] = "throw-type", ["throw"] = true },
    });

    /// <summary>TD-AUTO-PLG-SLOW: インスペクタの型の表示文字列の作成に 200 ms かかる。</summary>
    public static Definition Slow { get; } = Generic("slow", new JsonObject
    {
        ["inspectorType"] = new JsonObject { ["id"] = "slow.slow-type", ["name"] = "slow-type", ["delayMs"] = 200 },
    });

    /// <summary>
    /// TD-INSP-PLUGIN-TYPES (05 のテストデータ): インスペクタの型 <c>Loop</c> (サイズ 4。変換から戻らない)、<c>Throw</c> (サイズ 4。
    /// <c>InvalidOperationException("test failure")</c>)、<c>Echo</c> (サイズ 4。4 バイトを空白区切りの Hex にする)。
    /// </summary>
    public static Definition InspectorTypes { get; } = Generic("insptypes", new JsonObject
    {
        ["inspectorTypes"] = new JsonArray(
            new JsonObject { ["id"] = "insptypes.loop", ["name"] = "Loop", ["size"] = 4, ["hang"] = true },
            new JsonObject { ["id"] = "insptypes.throw", ["name"] = "Throw", ["size"] = 4, ["throw"] = true, ["message"] = "test failure" },
            new JsonObject { ["id"] = "insptypes.echo", ["name"] = "Echo", ["size"] = 4, ["echo"] = true }),
    });

    /// <summary>TD-UI-HEXPLUGIN (09 のテストデータ): プラグイン ID <c>test</c>、コマンド <c>plugin.test.hello</c> (出力パネルに Hello) だけ。</summary>
    public static Definition HexPlugin { get; } = Generic("test", new JsonObject { ["commands"] = new JsonArray(Command("test.hello", "test: hello", "Hello")) });

    /// <summary>TD-AUTO-PLG-DUP1: コマンド dup1.cmd。</summary>
    public static Definition Dup1 { get; } = Generic("dup1", new JsonObject { ["commands"] = new JsonArray(Command("dup1.cmd", "dup: cmd", "from dup1")) });

    /// <summary>TD-AUTO-PLG-DUP2: 同じ ID dup1.cmd のコマンドを登録する。</summary>
    public static Definition Dup2 { get; } = Generic("dup2", new JsonObject { ["commands"] = new JsonArray(Command("dup1.cmd", "dup: cmd", "from dup2")) });

    /// <summary>TD-AUTO-PLG-BADSDK: マニフェストの sdk が 99.0。</summary>
    public static Definition BadSdk { get; } = Generic("badsdk", new JsonObject(), sdk: "99.0");

    /// <summary>TD-AUTO-PLG-NOENTRY: マニフェストの entry の DLL がない。</summary>
    public static Definition NoEntry { get; } = new("noentry", "noentry", "1.0.0", "1.0", "Plugin.Generic", "Missing.Entry.dll", [], CopyBinaries: false);

    /// <summary>TD-AUTO-PLG-10 の 1 つ: 初期化に 200 ms かかる (コマンド &lt;id&gt;.cmd)。</summary>
    public static Definition Init200(int index) => Generic($"init{index:00}", new JsonObject
    {
        ["initDelayMs"] = 200,
        ["commands"] = new JsonArray(Command($"init{index:00}.cmd", $"init{index:00}: cmd", $"from init{index:00}")),
    });

    /// <summary>マニフェストの JSON。</summary>
    public static JsonObject Manifest(Definition d)
    {
        var m = new JsonObject
        {
            ["id"] = d.Id,
            ["name"] = d.Name,
            ["version"] = d.Version,
            ["publisher"] = d.Publisher,
            ["sdk"] = d.Sdk,
            ["entry"] = d.Entry,
            ["permissions"] = new JsonArray("file-read"),
            ["description"] = "Test plugin " + d.Id,
        };
        if (d.NameJa is not null)
        {
            m["name.ja"] = d.NameJa;
        }

        return m;
    }

    /// <summary>プラグインのフォルダの中身を書く (マニフェスト、test.json、DLL)。</summary>
    public static string WriteFolder(Definition d, string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), Manifest(d).ToJsonString());
        if (d.TestJson is not null)
        {
            File.WriteAllText(Path.Combine(folder, "test.json"), d.TestJson.ToJsonString());
        }

        if (d.CopyBinaries)
        {
            string bin = Path.Combine(BinRoot, d.Project);
            foreach (string file in d.Files)
            {
                File.Copy(Path.Combine(bin, file), Path.Combine(folder, file), overwrite: true);
            }
        }

        return folder;
    }

    /// <summary><c>&lt;pluginsRoot&gt;/&lt;id&gt;/&lt;version&gt;/</c> に置く。</summary>
    public static string Install(string pluginsRoot, Definition d) => WriteFolder(d, Path.Combine(pluginsRoot, d.Id, d.Version));

    /// <summary>フォルダを <c>.hexplugin</c> (zip) にする (TD-AUTO-HEXPLUGIN)。<paramref name="topFolder"/> を指定すると、その名前のフォルダの下に入れる。</summary>
    public static string Package(string folder, string packagePath, string? topFolder = null)
    {
        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }

        using (ZipArchive zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetRelativePath(folder, file).Replace('\\', '/');
                zip.CreateEntryFromFile(file, topFolder is null ? name : topFolder + "/" + name);
            }
        }

        return packagePath;
    }

    /// <summary>途中で切れた zip (TD-AUTO-HEXPLUGIN-BROKEN)。</summary>
    public static string Truncated(string packagePath, string brokenPath)
    {
        byte[] bytes = File.ReadAllBytes(packagePath);
        File.WriteAllBytes(brokenPath, bytes[..(bytes.Length / 2)]);
        return brokenPath;
    }

    /// <summary>
    /// 全ファイルのハッシュ (アプリの PluginManager.ContentHash と同じ計算。Core のテストが一致を確かめる)。UI テストが信頼の記録を置くのに使う。
    /// </summary>
    public static string ContentHash(string folder)
    {
        var sb = new System.Text.StringBuilder();
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Relative: Path.GetRelativePath(folder, f).Replace('\\', '/')))
            .OrderBy(f => f.Relative, StringComparer.OrdinalIgnoreCase).Select(f => f.Full))
        {
            string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (relative == ".motw")
            {
                continue;
            }

            using FileStream stream = File.OpenRead(file);
            sb.Append(relative.ToUpperInvariant()).Append('\0').Append(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream))).Append('\n');
        }

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>
    /// 設定フォルダの信頼の記録 (<c>scripts/trust.json</c>) に、プラグインを「有効にする」を選んだ記録を加える (UI テストの「信頼済み」の前提)。
    /// </summary>
    public static void Trust(string settingsFolder, string pluginFolder, string id)
    {
        string path = Path.Combine(settingsFolder, "scripts", "trust.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        JsonArray records = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsArray() : [];
        records.Add(new JsonObject
        {
            ["kind"] = "plugin",
            ["key"] = id,
            ["hash"] = ContentHash(pluginFolder),
            ["source"] = "userAllowed",
            ["permissions"] = new JsonArray(),
            ["time"] = DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        });
        File.WriteAllText(path, records.ToJsonString());
    }

    /// <summary>項目のパスを自由に書いた zip (zip-slip などの検証のテスト)。</summary>
    public static string RawPackage(string packagePath, IReadOnlyDictionary<string, byte[]> entries)
    {
        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }

        using ZipArchive zip = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        foreach ((string name, byte[] data) in entries)
        {
            using Stream s = zip.CreateEntry(name).Open();
            s.Write(data);
        }

        return packagePath;
    }
}
