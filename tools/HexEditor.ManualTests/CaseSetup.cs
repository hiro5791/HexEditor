using System.Text;
using System.Text.Json.Nodes;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Files;
using HexEditor.Core.Settings;
using HexEditor.Core.Sources;
using HexEditor.TestData;

namespace HexEditor.ManualTests;

/// <summary>準備の結果。起動する HexEditor の引数と、行ったこと・できなかったことの一覧。</summary>
public sealed class SetupResult
{
    /// <summary>テスト用の設定フォルダ (--test-profile)。</summary>
    public required string Profile { get; init; }

    /// <summary>作ったテストデータ (ID → パス)。</summary>
    public Dictionary<string, string> Files { get; } = [];

    /// <summary>行ったこと (表示用)。</summary>
    public List<string> Done { get; } = [];

    /// <summary>作れなかったテストデータ (手で用意する)。</summary>
    public List<string> Missing { get; } = [];

    /// <summary>HexEditor に渡す引数 (--new-instance・--test-profile を含む)。</summary>
    public List<string> Arguments { get; } = [];

    /// <summary>--test-hooks の設定ファイル (なければ null。開発版でだけ効く)。</summary>
    public string? HooksPath { get; set; }
}

/// <summary>
/// 手動テストの準備 (テスト方針 8.2) のうち、UI に依存しない部分: テストデータの生成、テスト用の設定フォルダ (設定・ブックマーク・スクリプト)、
/// --test-hooks の設定、HexEditor の引数。TestDataCatalog にないもの (各ケースのファイルの表で定義した小さなテストデータ) もここで作る。
/// </summary>
public static class CaseSetup
{
    /// <summary>ファイルとして作るテストデータ (TestDataCatalog にないもの)。</summary>
    private static readonly Dictionary<string, (string FileName, Func<byte[]> Content)> ExtraFiles = new()
    {
        // 01: 種 0x40 の乱数 64 KiB (QEMU のゲストの 0x100000 に読み込ませる)。
        ["TD-ENG-QEMU-RAM"] = ("TD-ENG-QEMU-RAM.bin", () =>
        {
            byte[] b = new byte[64 * 1024];
            TestDataCatalog.Random(0x40, 0, b);
            return b;
        }),

        // 04: 0x10〜0x27 に UTF-8 の「日本語のテキスト」、ほかは 00 (64 バイト)。
        ["TD-FIND-JA-UTF8"] = ("TD-FIND-JA-UTF8.bin", () =>
        {
            byte[] b = new byte[64];
            Encoding.UTF8.GetBytes("日本語のテキスト").CopyTo(b, 0x10);
            return b;
        }),

        // 07: テンプレート。
        ["TD-TPL-COLOR3"] = ("color3.hxt", () => Encoding.UTF8.GetBytes("u8 a[16] [[color(\"accent3\")]];\nu8 b[16];\n")),
        ["TD-TPL-CSV-JA"] = ("csv-ja.hxt", () => Encoding.UTF8.GetBytes(
            "u8 a [[comment(\"日本語のコメント\")]];\nu8 b [[comment(\"件数, \\\"重要\\\"\")]];\nu8 c [[comment(\"1 行目\\n2 行目\")]];\nu8 d;\n")),

        // 08: スクリプト。
        ["TD-AUTO-JS-NARRATOR"] = ("narrator.js", () => Encoding.UTF8.GetBytes("// narrator test\nconst answer = 42;\nconsole.log(answer);\n")),
        ["TD-AUTO-JS-SYNTAX"] = ("syntax.js", () => Encoding.UTF8.GetBytes(
            "// comment\nconst answer = 0x10;\nconst label = \"text\";\nconst pattern = /ab+c/g;\n\nfunction describe(value) {\n  // the value as text\n"
            + "  return label + ': ' + value.toString(16);\n}\n\nconsole.log(describe(answer), pattern.test('abbc'));\n")),

        // 05: TD-SEQ-1M 用のブックマーク 5 件 (.hexbm.json)。
        ["TD-INSP-BM-SET"] = ("TD-INSP-BM-SET.hexbm.json", () =>
        {
            BookmarkCollection bm = BookmarkSet();
            return Encoding.UTF8.GetBytes(BookmarkExchange.ExportJson(bm, bm.All.ToList(), bm.GroupRecords()));
        }),
    };

    /// <summary>ファイルではなく、設定フォルダ・--test-hooks の設定として用意するテストデータ。</summary>
    private static readonly HashSet<string> ProfileData = ["TD-UI-SET-ZOOM-UI400", "TD-UI-PROFILE-A11Y", "TD-VIEW-HOOK-READERR", "TD-UI-HOOK-BADSECTOR"];

    /// <summary>準備ボタンで用意できるテストデータか。</summary>
    public static bool CanPrepare(string id) =>
        ExtraFiles.ContainsKey(id) || ProfileData.Contains(id) || id == "TD-READONLY" || TestDataCatalog.All.Any(i => i.Id == id);

    /// <summary>
    /// テストケースの準備をする。<paramref name="profile"/> は作り直す (前のテストの設定を持ち越さない)。
    /// </summary>
    public static SetupResult Run(TestCase testCase, CasePrep prep, string dataFolder, string profile)
    {
        if (Directory.Exists(profile))
        {
            Directory.Delete(profile, recursive: true);
        }

        Directory.CreateDirectory(profile);
        var result = new SetupResult { Profile = profile };
        result.Done.Add($"テスト用の設定フォルダを新しく作った: {profile}");

        List<string> ids = testCase.TestData.Concat(prep.ArgumentData).Distinct().ToList();
        foreach (string id in ids)
        {
            if (ProfileData.Contains(id))
            {
                continue;
            }

            try
            {
                string path = Generate(id, dataFolder);
                result.Files[id] = path;
                result.Done.Add($"テストデータ {id} を作った: {path}");
            }
            catch (ArgumentException)
            {
                result.Missing.Add(id);
            }
        }

        WriteSettings(prep, ids, profile, result);
        WriteBookmarks(ids, profile, dataFolder, result);
        if (result.Files.TryGetValue("TD-AUTO-JS-NARRATOR", out string? narrator))
        {
            string scripts = Path.Combine(profile, "scripts");
            Directory.CreateDirectory(scripts);
            File.Copy(narrator, Path.Combine(scripts, "narrator.js"), overwrite: true);
            result.Done.Add("TD-AUTO-JS-NARRATOR を設定フォルダの scripts/narrator.js に置いた");
        }

        WriteHooks(prep, ids, profile, dataFolder, result);

        if (prep.CopyTo == "デスクトップ" && result.Files.Values.FirstOrDefault() is { } first)
        {
            string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Path.GetFileName(first));
            File.Copy(first, desktop, overwrite: true);
            File.SetAttributes(desktop, FileAttributes.Normal);
            result.Done.Add($"テストデータをデスクトップに置いた: {desktop}");
        }

        result.Arguments.Add("--new-instance");
        result.Arguments.Add("--test-profile");
        result.Arguments.Add(profile);
        if (result.HooksPath is { } hooks)
        {
            result.Arguments.Add("--test-hooks");
            result.Arguments.Add(hooks);
        }

        if (prep.Arguments is null)
        {
            // 既定: 最初のファイルのテストデータを開く (テンプレート・ブックマークのファイルは開かない)。
            if (testCase.TestData.FirstOrDefault(id => result.Files.ContainsKey(id) && id != "TD-INSP-BM-SET") is { } open)
            {
                result.Arguments.Add(result.Files[open]);
            }
        }
        else if (!prep.OpensNothing)
        {
            foreach (string token in SplitArguments(prep.Arguments))
            {
                result.Arguments.Add(Expand(token, result));
            }
        }

        return result;
    }

    /// <summary>テストデータを作り、パスを返す。作れなければ ArgumentException。</summary>
    public static string Generate(string id, string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        if (ExtraFiles.TryGetValue(id, out var extra))
        {
            string path = Path.Combine(dataFolder, id, extra.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, extra.Content());
            return path;
        }

        if (id == "TD-READONLY")
        {
            // TD-SEQ-1M の先頭 1 KiB に読み取り専用属性を付けたもの。
            string path = Path.Combine(dataFolder, "TD-READONLY.bin");
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            File.WriteAllBytes(path, Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray());
            File.SetAttributes(path, FileAttributes.ReadOnly);
            return path;
        }

        return TestDataCatalog.Generate(id, dataFolder);
    }

    private static void WriteSettings(CasePrep prep, List<string> ids, string profile, SetupResult result)
    {
        var settings = new JsonObject { ["$schemaVersion"] = SettingsStore.SchemaVersion };
        if (prep.Settings is { } json && JsonNode.Parse(json) is JsonObject extra)
        {
            foreach ((string key, JsonNode? value) in extra)
            {
                settings[key] = value?.DeepClone();
            }
        }

        if (ids.Contains("TD-UI-SET-ZOOM-UI400"))
        {
            settings["view.zoom.ui"] = 400;
        }

        File.WriteAllText(Path.Combine(profile, "settings.json"), settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        if (settings.Count > 1)
        {
            result.Done.Add("設定を書いた: " + string.Join("、", settings.Where(p => p.Key != "$schemaVersion").Select(p => $"{p.Key} = {p.Value?.ToJsonString()}")));
        }
    }

    private static void WriteBookmarks(List<string> ids, string profile, string dataFolder, SetupResult result)
    {
        var store = new DocumentDataStore(Path.Combine(profile, "documents"));
        if (ids.Contains("TD-INSP-BM-SET"))
        {
            string seq = result.Files.GetValueOrDefault("TD-SEQ-1M") ?? Generate("TD-SEQ-1M", dataFolder);
            result.Files.TryAdd("TD-SEQ-1M", seq);
            BookmarkStore.Save(store, seq, FileStamp.FromPath(seq), BookmarkSet());
            result.Done.Add("TD-SEQ-1M に TD-INSP-BM-SET のブックマーク 5 件を付けた");
        }

        if (ids.Contains("TD-UI-PROFILE-A11Y"))
        {
            string zero = result.Files.GetValueOrDefault("TD-ZERO-1M") ?? Generate("TD-ZERO-1M", dataFolder);
            result.Files.TryAdd("TD-ZERO-1M", zero);
            var bm = new BookmarkCollection();
            bm.Add(0x20, 1, "header");
            BookmarkStore.Save(store, zero, FileStamp.FromPath(zero), bm);
            result.Done.Add("設定フォルダ TD-UI-PROFILE-A11Y: TD-ZERO-1M にブックマーク header (0x20、長さ 1) を付けた");
        }
    }

    private static void WriteHooks(CasePrep prep, List<string> ids, string profile, string dataFolder, SetupResult result)
    {
        JsonObject hooks = prep.Hooks is { } json && JsonNode.Parse(json) is JsonObject h ? h : [];
        var sources = hooks["fileSources"] as JsonArray ?? [];
        if (ids.Contains("TD-VIEW-HOOK-READERR"))
        {
            // 開いたファイルの 0x1000〜0x1FFF を ERROR_IO_DEVICE にする。
            sources.Add(new JsonObject { ["match"] = "*", ["readErrors"] = new JsonArray(new JsonObject { ["offset"] = "0x1000", ["length"] = "0x1000" }) });
            result.Done.Add("TD-VIEW-HOOK-READERR: 開いたファイルの 0x1000〜0x1FFF を読み取れないようにした");
        }

        if (ids.Contains("TD-UI-HOOK-BADSECTOR"))
        {
            string zero = result.Files.GetValueOrDefault("TD-ZERO-1M") ?? Generate("TD-ZERO-1M", dataFolder);
            sources.Add(new JsonObject { ["match"] = zero, ["readErrors"] = new JsonArray(new JsonObject { ["offset"] = "0x200", ["length"] = "0x200" }) });
            result.Done.Add("TD-UI-HOOK-BADSECTOR: TD-ZERO-1M の 0x200〜0x3FF を読み取れないようにした");
        }

        if (sources.Count > 0)
        {
            hooks["fileSources"] = sources;
        }

        if (hooks.Count == 0)
        {
            return;
        }

        // 手動テストでは、ウィンドウを普通に前面に出す (フックの既定は前面に出さない)。
        hooks["noActivate"] = false;
        string path = Path.Combine(profile, "test-hooks.json");
        File.WriteAllText(path, hooks.ToJsonString());
        result.HooksPath = path;
    }

    /// <summary>TD-INSP-BM-SET の 5 件 (cases/05-inspector-and-annotations.md の表)。</summary>
    private static BookmarkCollection BookmarkSet()
    {
        var bm = new BookmarkCollection();
        Bookmark magic = bm.Add(0x10, 4, "magic");
        bm.SetColor(magic, BookmarkColor.Palette(1));
        bm.SetGroup(magic, "Header");
        bm.SetNumber(magic, 1);
        bm.SetComment(magic, "**magic** bytes");
        Bookmark size = bm.Add(0x20, 8, "size");
        bm.SetColor(size, BookmarkColor.Palette(2));
        bm.SetGroup(size, "Header");
        bm.SetComment(size, "size field");
        Bookmark table = bm.Add(0x100, 16, "table");
        bm.SetColor(table, BookmarkColor.Palette(3));
        bm.SetGroup(table, "Body/Tables");
        bm.SetNumber(table, 2);
        bm.SetComment(table, "# Table\nentries");
        Bookmark flag = bm.Add(0x400, 1, "flag");
        bm.SetColor(flag, BookmarkColor.Palette(4));
        Bookmark jp = bm.Add(0x800, 32, "日本語の名前");
        bm.SetColor(jp, BookmarkColor.Palette(5));
        bm.SetGroup(jp, "Body");
        bm.SetComment(jp, "コメント, カンマ入り");
        return bm;
    }

    private static string Expand(string token, SetupResult result)
    {
        string expanded = token.Replace("{設定フォルダ}", result.Profile, StringComparison.Ordinal);
        foreach ((string id, string path) in result.Files)
        {
            expanded = expanded.Replace("{" + id + "}", path, StringComparison.Ordinal);
        }

        return expanded;
    }

    /// <summary>引数を空白で分ける。"…" で囲んだものは 1 つ。</summary>
    public static List<string> SplitArguments(string text)
    {
        var list = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        bool any = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                {
                    list.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any)
        {
            list.Add(current.ToString());
        }

        return list;
    }

    /// <summary>1 つの文字列のコマンドラインにする (表示と、管理者として起動するときに使う)。</summary>
    public static string JoinArguments(IEnumerable<string> arguments) =>
        string.Join(" ", arguments.Select(a => a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c == '"') ? a : "\"" + a.Replace("\"", "\\\"") + "\""));
}
