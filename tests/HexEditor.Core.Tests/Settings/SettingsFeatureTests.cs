using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HexEditor.Core.Commands;
using HexEditor.Core.Settings;
using HexEditor.Core.Tests.Commands;
using HexEditor.Core.Tests.Engine;
using HexEditor.Core.Tests.I18n;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Settings;

/// <summary>設定画面 (UI-22)、保存形式 (UI-23)、リセット (UI-24)、インポート / エクスポート (UI-25)。</summary>
public sealed class SettingsFeatureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-settings2").FullName;

    private static string AppFolder => Path.GetDirectoryName(SourceTests.FindRepoFile("src/HexEditor.App/HexEditor.App.csproj"))!;

    private static Dictionary<string, string> Strings(string language) =>
        ResourceChecker.LoadResw(Path.Combine(AppFolder, "Strings", language, "Resources.resw"));

    [Fact]
    public void Every_setting_has_names_and_option_labels_and_a_valid_default()
    {
        Dictionary<string, string> en = Strings("en"), ja = Strings("ja");
        var errors = new List<string>();
        foreach (SettingDefinition s in BuiltInSettings.All)
        {
            var keys = new List<string> { s.NameKey, s.DescriptionKey, "SetCategory_" + s.Category };
            keys.AddRange(s.Options.Where(o => s.Key != "ui.language" || o == "system").Select(s.OptionKey));
            errors.AddRange(keys.Where(k => !en.ContainsKey(k) || !ja.ContainsKey(k)).Select(k => $"{s.Key}: {k}"));
            if (!s.Validate(s.Default))
            {
                errors.Add($"{s.Key}: 既定値が正しくありません");
            }
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        Assert.Equal(SettingCategories.All.Length, SettingCategories.All.Distinct().Count());
    }

    [Fact]
    public void Validation_checks_kind_options_and_range()
    {
        var catalog = SettingsCatalog.CreateBuiltIn();
        Assert.True(catalog.Validate("ui.theme", "dark"));
        Assert.False(catalog.Validate("ui.theme", "purple"));
        Assert.False(catalog.Validate("ui.theme", 3));
        Assert.True(catalog.Validate("view.scroll.cursorMargin", 10));
        Assert.False(catalog.Validate("view.scroll.cursorMargin", 11));
        Assert.True(catalog.Validate("future.unknown", new JsonObject()));
        Assert.Throws<InvalidOperationException>(() => catalog.Register(new SettingDefinition("ui.theme", "appearance", SettingKind.Bool, false)));
    }

    [Fact]
    [Trait(TC, "TC-UI-22-03")]
    public void Search_finds_settings_by_localized_and_english_names_and_key()
    {
        // UI-08 のズームの適用範囲 (view.zoom.hexScope) と「テーマ」で確かめる。
        Dictionary<string, string> en = Strings("en"), ja = Strings("ja");
        var catalog = SettingsCatalog.CreateBuiltIn();
        SettingTexts Texts(SettingDefinition s) => new(ja[s.NameKey], ja[s.DescriptionKey], en[s.NameKey]);

        Assert.Contains(catalog.Search("zoom", Texts), r => r.Setting.Key == "view.zoom.hexScope");
        Assert.Contains(catalog.Search("ズーム", Texts), r => r.Setting.Key == "view.zoom.hexScope" && r.NamePositions.Count == 3);
        SettingSearchResult theme = Assert.Single(catalog.Search("theme", Texts), r => r.Setting.Key == "ui.theme");
        Assert.Empty(theme.NamePositions);
        Assert.Equal([0, 1, 2], Assert.Single(catalog.Search("テーマ", Texts)).NamePositions);
        Assert.Contains(catalog.Search("ui.statusBar", Texts), r => r.Setting.Key == "ui.statusBar.visible");
        Assert.Empty(catalog.Search("   ", Texts));

        // 変更の印は「settings.json にあるか」(既定値と同じ値は書かない)。
        using var store = new SettingsStore(_dir);
        store.Load();
        Assert.Empty(catalog.Modified(store));
        store.SetNode("ui.theme", "dark", "system");
        Assert.Equal(["ui.theme"], catalog.Modified(store).Select(s => s.Key));
        store.SetNode("ui.theme", "system", "system");
        Assert.Empty(catalog.Modified(store));
    }

    [Fact]
    [Trait(TC, "TC-UI-23-01")]
    public void First_file_has_only_schema_and_version_and_the_schema_is_up_to_date()
    {
        string schemaPath = Path.Combine(AppFolder, SettingsSchema.FileName);
        using (var store = new SettingsStore(_dir, new Uri(schemaPath).AbsoluteUri))
        {
            store.Load();
            store.SaveNow();
        }

        JsonObject json = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, SettingsStore.FileName)))!.AsObject();
        Assert.Equal(["$schema", "$schemaVersion"], json.Select(p => p.Key));
        Assert.Equal(1, json["$schemaVersion"]!.GetValue<int>());
        Assert.EndsWith("settings.schema.json", json["$schema"]!.GetValue<string>(), StringComparison.Ordinal);

        // 同梱のスキーマが設定項目の登録と一致している (違えば HEXEDITOR_UPDATE_SCHEMA=1 で作り直す)。
        Dictionary<string, string> en = Strings("en");
        string generated = SettingsSchema.Generate(SettingsCatalog.CreateBuiltIn(), s => en[s.NameKey]);
        if (Environment.GetEnvironmentVariable("HEXEDITOR_UPDATE_SCHEMA") == "1")
        {
            File.WriteAllText(schemaPath, generated);
        }

        Assert.True(File.ReadAllText(schemaPath).ReplaceLineEndings("\n") == generated,
            "src/HexEditor.App/settings.schema.json が古いです。HEXEDITOR_UPDATE_SCHEMA=1 を付けてこのテストを実行し、作り直してください。");
        JsonObject schema = JsonNode.Parse(generated)!.AsObject();
        Assert.Equal(["system", "light", "dark"], schema["properties"]!["ui.theme"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Theory]
    [InlineData(SettingsWritePoint.TempHalfWritten, false)]
    [InlineData(SettingsWritePoint.BeforeReplace, false)]
    [InlineData(SettingsWritePoint.AfterReplace, true)]
    [Trait(TC, "TC-UI-23-05")]
    public void Interrupted_write_leaves_a_readable_file(SettingsWritePoint point, bool changed)
    {
        // プロセスの強制終了の代わりに、その時点で書き込みを止める (UI テストでは実際に強制終了する: SettingsUiTests)。
        using (var store = new SettingsStore(_dir))
        {
            store.Load();
            store.SetInt("view.font.size", 11, 10);
            store.Flush();
        }

        try
        {
            SettingsStore.WriteHook = p =>
            {
                if (p == point)
                {
                    throw new OperationCanceledException("killed");
                }
            };
            using var store = new SettingsStore(_dir);
            store.Load();
            store.SetInt("view.font.size", 12, 10);
            Assert.Throws<OperationCanceledException>(store.Flush);
        }
        finally
        {
            SettingsStore.WriteHook = null;
        }

        JsonObject json = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, SettingsStore.FileName)))!.AsObject();
        Assert.Equal(1, json["$schemaVersion"]!.GetValue<int>());
        Assert.Equal(changed ? 12 : 11, json["view.font.size"]!.GetValue<int>());
    }

    [Fact]
    [Trait(TC, "TC-UI-24-02")]
    public void Full_reset_backs_up_first_and_keeps_document_data_by_default()
    {
        File.WriteAllText(Path.Combine(_dir, SettingsStore.FileName), """{ "$schemaVersion": 1, "ui.theme": "dark" }""");
        File.WriteAllText(Path.Combine(_dir, SettingsFiles.Keybindings), KeyMapTests.KeysCustom);
        Directory.CreateDirectory(Path.Combine(_dir, SettingsFiles.Documents));
        File.WriteAllText(Path.Combine(_dir, SettingsFiles.Documents, "doc.json"), """{ "bookmarks": [ { "name": "mark1", "offset": 256 } ] }""");

        Assert.Equal(SettingsParts.Settings | SettingsParts.KeyBindings, SettingsParts.ResetDefault);
        string backup = SettingsFiles.CreateBackup(_dir, SettingsFiles.PathsOf(SettingsParts.ResetDefault), new DateTime(2026, 10, 8, 12, 0, 0));
        Assert.Equal("backup-20261008-120000", Path.GetFileName(backup));
        Assert.Contains("\"dark\"", File.ReadAllText(Path.Combine(backup, SettingsStore.FileName)));
        Assert.True(File.Exists(Path.Combine(backup, SettingsFiles.Keybindings)));
        Assert.False(Directory.Exists(Path.Combine(backup, SettingsFiles.Documents)));

        using (var store = new SettingsStore(_dir))
        {
            store.Load();
            store.ReplaceAll([]);
            store.Flush();
        }

        Assert.DoesNotContain("ui.theme", File.ReadAllText(Path.Combine(_dir, SettingsStore.FileName)));
        Assert.True(File.Exists(Path.Combine(_dir, SettingsFiles.Documents, "doc.json")));

        // バックアップは直近 5 個を残す。
        for (int i = 1; i <= 6; i++)
        {
            SettingsFiles.CreateBackup(_dir, [SettingsStore.FileName], new DateTime(2026, 10, 8, 12, 0, i));
            Thread.Sleep(20);
        }

        Assert.Equal(SettingsFiles.BackupsKept, Directory.GetDirectories(_dir, "backup-*").Length);
    }

    [Fact]
    [Trait(TC, "TC-UI-25-01")]
    public void Exported_settings_imported_elsewhere_give_the_same_theme_and_shortcuts()
    {
        // インストーラ版の設定フォルダ A (TD-UI-SET-DARK と TD-UI-KEYS-CUSTOM) から、ポータブル版の B へ。
        var catalog = SettingsCatalog.CreateBuiltIn();
        var keysA = new KeyMap(KeyMapTests.Catalog());
        keysA.Load(KeyBindingsDocument.Parse(KeyMapTests.KeysCustom));
        var settingsA = new JsonObject { ["ui.theme"] = "dark", ["storage.tempDirectory"] = @"D:\scratch" };
        string exported = SettingsBundle.Create(SettingsParts.Settings | SettingsParts.KeyBindings | SettingsParts.Themes, "1.0.0", settingsA, catalog,
            keysA.ToDocument(), [], null).ToJson();

        SettingsBundle bundle = SettingsBundle.Parse(exported);
        var current = new JsonObject { ["storage.tempDirectory"] = @"E:\portable-temp" };
        SettingsImportPlan plan = bundle.PlanSettings(current, catalog, ImportMode.Replace);
        Assert.Equal("dark", plan.Result["ui.theme"]!.GetValue<string>());
        Assert.Equal(@"E:\portable-temp", plan.Result["storage.tempDirectory"]!.GetValue<string>());

        var keysB = new KeyMap(KeyMapTests.Catalog());
        keysB.ApplyImport(keysB.PreviewImport(bundle.Keybindings!, KeyImportMode.Replace));
        Assert.Equal("Ctrl+K Ctrl+F", keysB.BindingsFor("edit.fill").Single().Binding.Chord.ToString());
        Assert.Empty(keysB.BindingsFor("file.print"));
    }

    [Fact]
    [Trait(TC, "TC-UI-25-02")]
    public void Default_export_contains_no_file_paths()
    {
        var catalog = SettingsCatalog.CreateBuiltIn();
        var recent = JsonNode.Parse("""{ "items": [ { "path": "C:\\data\\seq-1m.bin" }, { "path": "C:\\data\\bytes-256.bin" } ] }""");
        var settings = new JsonObject { ["ui.theme"] = "dark", ["storage.tempDirectory"] = @"C:\big\temp" };
        const SettingsParts exportDefault = SettingsParts.Settings | SettingsParts.KeyBindings | SettingsParts.Themes;
        Assert.False(exportDefault.HasFlag(SettingsParts.RecentAndState));
        string json = SettingsBundle.Create(exportDefault, "1.0.0", settings, catalog, new KeyBindingsDocument(), [], recent).ToJson();
        Assert.DoesNotContain("seq-1m.bin", json);
        Assert.DoesNotContain("bytes-256.bin", json);
        Assert.DoesNotMatch(new Regex(@"[A-Za-z]:\\\\"), json);
        Assert.Contains("\"ui.theme\": \"dark\"", json);

        // 最近使ったファイルを選べば入る。
        Assert.Contains("seq-1m.bin", SettingsBundle.Create(exportDefault | SettingsParts.RecentAndState, "1.0.0", settings, catalog, null, [], recent).ToJson());
    }

    [Fact]
    [Trait(TC, "TC-UI-25-03")]
    public void Invalid_values_are_skipped_and_listed()
    {
        // TD-UI-SETEXP-BADVALUE。フォントの大きさ (UI-29) は組み込みの定義 (6〜72)、ズーム (UI-08) は 50〜400。
        var catalog = SettingsCatalog.CreateBuiltIn();
        SettingsBundle bundle = SettingsBundle.Parse("""{"$schemaVersion": 1, "app": "HexEditor", "version": "1.0.0", "settings": {"view.font.size": 200, "ui.theme": "purple", "view.zoom.hex": 150}}""");
        SettingsImportPlan plan = bundle.PlanSettings([], catalog, ImportMode.Merge);
        Assert.Equal(["ui.theme", "view.font.size"], plan.Skipped.Select(s => s.Key).Order());
        Assert.Contains(plan.Skipped, s => s.Key == "view.font.size" && s.Value == "200");
        Assert.Contains(plan.Skipped, s => s.Key == "ui.theme" && s.Value == "\"purple\"");
        Assert.Equal(150, plan.Result["view.zoom.hex"]!.GetValue<int>());
        Assert.Equal(1, plan.ChangedCount);
    }

    [Fact]
    public void Bundles_of_the_wrong_format_or_too_new_are_rejected()
    {
        var invalid = Assert.Throws<SettingsBundleException>(() => SettingsBundle.Parse("{\n not json"));
        Assert.Equal((SettingsBundleError.InvalidJson, 2), (invalid.Error, invalid.Line));
        Assert.Equal(SettingsBundleError.NotSettingsFile,
            Assert.Throws<SettingsBundleException>(() => SettingsBundle.Parse("""{ "$schemaVersion": 1, "app": "Other" }""")).Error);
        Assert.Equal(SettingsBundleError.MissingVersion,
            Assert.Throws<SettingsBundleException>(() => SettingsBundle.Parse("""{ "app": "HexEditor" }""")).Error);
        var tooNew = Assert.Throws<SettingsBundleException>(() => SettingsBundle.Parse("""{ "$schemaVersion": 2, "app": "HexEditor" }"""));
        Assert.True(tooNew.TooNew);
        Assert.Equal(2, tooNew.Version);
        Assert.True(Assert.Throws<SettingsBundleException>(() =>
            SettingsBundle.Parse("""{ "$schemaVersion": 1, "app": "HexEditor", "keybindings": { "$schemaVersion": 5, "bindings": [] } }""")).TooNew);

        // 表示の文はリソースから作るので、Core の例外の文は日本語を含まない (英語の記録用)。
        Assert.DoesNotMatch(@"[぀-ヿ]", invalid.Message);
        SettingsBundle ok = SettingsBundle.Parse("""{ "$schemaVersion": 1, "app": "HexEditor", "themes": [ { "name": "a.json", "content": {} }, { "name": "..\\x.json", "content": {} } ] }""");
        Assert.Equal(SettingsParts.Themes, ok.Parts);
        Assert.Single(ok.Themes!);
    }

    [Fact]
    public void Replace_all_reports_changed_keys_and_state_store_round_trips()
    {
        using var store = new SettingsStore(_dir);
        store.Load();
        store.SetNode("ui.theme", "dark", "system");
        IReadOnlyCollection<string>? changed = null;
        store.Changed += keys => changed = keys;
        store.ReplaceAll(new JsonObject { ["log.level"] = "debug" });
        Assert.Equal(["log.level", "ui.theme"], changed!.Order());
        Assert.Equal("system", store.GetString("ui.theme", "system"));
        store.SetNode("ui.toolbar.items", ToolbarItems.DefaultJson(), ToolbarItems.DefaultJson());
        Assert.False(store.Contains("ui.toolbar.items"));

        using (var state = new StateStore(_dir))
        {
            state.Load();
            state.Set("panels.lastLayout", new JsonObject { ["x"] = 1 });
        }

        using var again = new StateStore(_dir);
        again.Load();
        Assert.Equal(1, again.Get("panels.lastLayout")!["x"]!.GetValue<int>());
        again.Clear();
        Assert.Null(again.Get("panels.lastLayout"));
    }

    [Fact]
    public void Toolbar_items_report_missing_commands()
    {
        var catalog = CommandCatalog.CreateBuiltIn();
        (IReadOnlyList<string> shown, int missing) = ToolbarItems.Resolve(["file.open", "no.such.thing", "help.about"], catalog);
        Assert.Equal(["file.open", "help.about"], shown);
        Assert.Equal(1, missing);
        Assert.All(ToolbarItems.Default, id => Assert.True(catalog.Contains(id), id));
        Assert.Equal(ToolbarItems.Default, ToolbarItems.Parse(null));
        Assert.Equal("開く", ToolbarItems.FallbackIcon("開く"));
        Assert.Equal("Go", ToolbarItems.FallbackIcon("Go to offset"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
