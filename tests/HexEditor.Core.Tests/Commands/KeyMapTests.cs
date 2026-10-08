using HexEditor.Core.Commands;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Commands;

/// <summary>キー割り当て (UI-18〜UI-21)。</summary>
public sealed class KeyMapTests
{
    /// <summary>組み込みのコマンドに、まだ他の機能が登録していないコマンド (テストデータが使う edit.fill など) を足した登録。</summary>
    internal static CommandCatalog Catalog()
    {
        var catalog = CommandCatalog.CreateBuiltIn();
        foreach (string id in new[] { "edit.fill", "file.print" })
        {
            if (!catalog.Contains(id))
            {
                catalog.Register(new CommandDefinition(id, id.Split('.')[0]) { DefaultBindings = id == "file.print" ? [KeyBinding.Parse("Ctrl+P")] : [] });
            }
        }

        return catalog;
    }

    /// <summary>TD-UI-KEYS-CUSTOM (UI-18 の仕様 9 の例)。</summary>
    internal const string KeysCustom = """
        {
          "$schemaVersion": 1,
          "preset": "default",
          "bindings": [
            { "command": "edit.fill", "key": "Ctrl+K Ctrl+F", "when": "editor" },
            { "command": "-file.print", "key": "Ctrl+P" }
          ]
        }
        """;

    private static string Keys(KeyMap map, string command) => string.Join(", ", map.BindingsFor(command).Select(b => b.Binding.ToString()));

    [Fact]
    public void Parses_and_formats_key_chords()
    {
        Assert.Equal("Ctrl+Shift+P", KeyChord.Parse("shift+ctrl+p").ToString());
        Assert.Equal("Ctrl+K Ctrl+F", KeyChord.Parse("Ctrl+K, Ctrl+F").ToString());
        Assert.Equal("Ctrl+OemComma", KeyChord.Parse("Ctrl+,").ToString());
        Assert.Equal("Alt+Left", KeyChord.Parse("Alt+←").ToString());
        Assert.Equal("Ctrl+Alt+7", KeyChord.Parse("Ctrl+Alt+7").ToString());
        Assert.False(KeyChord.TryParse("Ctrl+K Ctrl+F Ctrl+G", out _));
        Assert.False(KeyChord.TryParse("Hyper+A", out _));
        Assert.Equal(KeyChord.Parse("Ctrl+G"), KeyChord.Parse("ctrl+g"));
    }

    [Fact]
    public void Resolves_narrow_scope_first_and_two_stroke_chords()
    {
        var map = new KeyMap(Catalog());
        map.Load(KeyBindingsDocument.Parse(KeysCustom));
        var ctrlK = KeyChord.Parse("Ctrl+K").First;
        var ctrlF = KeyChord.Parse("Ctrl+F").First;

        Assert.Equal(KeyMatchKind.Prefix, map.Resolve([ctrlK], [KeyScope.Editor, KeyScope.Global]).Kind);
        Assert.Equal("edit.fill", map.Resolve([ctrlK, ctrlF], [KeyScope.Editor, KeyScope.Global]).Binding!.Command);
        Assert.Equal("search.find", map.Resolve([ctrlF], [KeyScope.Editor, KeyScope.Global]).Binding!.Command);

        // エディタの有効範囲の割り当ては、エディタの外では使わない。
        Assert.Equal(KeyMatchKind.None, map.Resolve([ctrlK], [KeyScope.Global]).Kind);
        Assert.Equal(KeyMatchKind.None, map.Resolve([KeyChord.Parse("Ctrl+P").First], [KeyScope.Global]).Kind);
    }

    [Fact]
    [Trait(TC, "TC-UI-18-01")]
    public void Added_key_is_listed_with_the_default_and_resolves()
    {
        var map = new KeyMap(Catalog());
        Assert.True(map.Add("go.goTo", KeyBinding.Parse("Ctrl+J"), replaceConflicts: false));
        Assert.Equal("Ctrl+G, Ctrl+J", Keys(map, "go.goTo"));
        Assert.Equal("go.goTo", map.Resolve([KeyChord.Parse("Ctrl+J").First], [KeyScope.Editor, KeyScope.Global]).Binding!.Command);
        Assert.Equal(BindingOrigin.User, map.BindingsFor("go.goTo")[1].Origin);
        Assert.Equal(BindingOrigin.Default, map.BindingsFor("go.goTo")[0].Origin);
        KeyBindingEntry entry = Assert.Single(map.UserEntries);
        Assert.Equal(("go.goTo", "Ctrl+J", false), (entry.Command, entry.Chord.ToString(), entry.Remove));
    }

    [Fact]
    [Trait(TC, "TC-UI-18-02")]
    public void Conflicting_key_is_reported_and_replace_moves_it()
    {
        var map = new KeyMap(Catalog());
        var ctrlF = KeyBinding.Parse("Ctrl+F");
        var conflicts = map.FindConflicts("go.goTo", ctrlF);
        Assert.Equal(["search.find"], conflicts.Select(c => c.Command));
        Assert.Equal(KeyScope.Global, conflicts[0].Binding.Scope);

        // 置き換えずには追加できない (同じ有効範囲で 1 つのキーに 2 つのコマンドは割り当てない)。
        Assert.False(map.Add("go.goTo", ctrlF, replaceConflicts: false));
        Assert.True(map.Add("go.goTo", ctrlF, replaceConflicts: true));
        Assert.Equal(string.Empty, Keys(map, "search.find"));
        Assert.Equal("Ctrl+G, Ctrl+F", Keys(map, "go.goTo"));

        // 狭い有効範囲の割り当てはグローバルを隠す警告になる (重複ではない)。
        var editorF = KeyBinding.Parse("Ctrl+G", KeyScope.Editor);
        Assert.Empty(map.FindConflicts("edit.selectAll", editorF));
        Assert.Equal(["go.goTo"], map.FindShadowed("edit.selectAll", editorF).Select(c => c.Command));
    }

    [Fact]
    [Trait(TC, "TC-UI-18-03")]
    public void Unassignable_keys_are_rejected()
    {
        Assert.Equal(KeyRejection.TypingKey, KeyValidation.Check(KeyChord.Parse("A")));
        Assert.Equal(KeyRejection.TypingKey, KeyValidation.Check(KeyChord.Parse("Shift+5")));
        Assert.Equal(KeyRejection.AltF4, KeyValidation.Check(KeyChord.Parse("Alt+F4")));
        Assert.Equal(KeyRejection.WindowsKey, KeyValidation.Check(KeyChord.Parse("Win+E")));
        Assert.Equal(KeyRejection.CtrlAltDelete, KeyValidation.Check(KeyChord.Parse("Ctrl+Alt+Delete")));
        Assert.Equal(KeyRejection.ModifierOnly, KeyValidation.Check(KeyChord.Parse("Ctrl+Shift")));
        Assert.Equal(KeyRejection.None, KeyValidation.Check(KeyChord.Parse("F7")));
        Assert.Equal(KeyRejection.None, KeyValidation.Check(KeyChord.Parse("Delete")));
        Assert.Equal(KeyRejection.None, KeyValidation.Check(KeyChord.Parse("Ctrl+K S")));

        var map = new KeyMap(Catalog());
        Assert.False(map.Add("go.goTo", KeyBinding.Parse("A"), replaceConflicts: true));
        Assert.Equal("Ctrl+G", Keys(map, "go.goTo"));
    }

    [Fact]
    [Trait(TC, "TC-UI-18-04")]
    public void Reset_all_empties_the_user_bindings()
    {
        var map = new KeyMap(Catalog());
        map.Load(KeyBindingsDocument.Parse(KeysCustom));
        Assert.Equal(2, map.UserEntries.Count);
        Assert.Equal(string.Empty, Keys(map, "file.print"));

        map.ResetAll();
        KeyBindingsDocument doc = KeyBindingsDocument.Parse(map.ToDocument().ToJson());
        Assert.Empty(doc.Bindings);
        Assert.Equal("default", doc.Preset);
        Assert.Equal("Ctrl+P", Keys(map, "file.print"));
    }

    [Fact]
    public void Reset_command_and_round_trip_of_the_file()
    {
        var map = new KeyMap(Catalog());
        map.Remove("search.find", KeyBinding.Parse("Ctrl+F"));
        map.Add("search.find", KeyBinding.Parse("Ctrl+Shift+F7"), false);
        Assert.Equal("Ctrl+Shift+F7", Keys(map, "search.find"));

        // 利用者が読めるよう、キーの表記の「+」をエスケープしない。
        Assert.Contains("\"Ctrl+Shift+F7\"", map.ToDocument().ToJson());
        var copy = new KeyMap(Catalog());
        copy.Load(KeyBindingsDocument.Parse(map.ToDocument().ToJson()));
        Assert.Equal("Ctrl+Shift+F7", Keys(copy, "search.find"));

        map.ResetCommand("search.find");
        Assert.Equal("Ctrl+F", Keys(map, "search.find"));
        Assert.Empty(map.UserEntries);
    }

    [Fact]
    public void Reset_command_does_not_create_duplicates_with_keys_moved_to_other_commands()
    {
        // 「検索」の Ctrl+F を「オフセットへ移動」に付け替えた (置き換える) 後で、「検索」を既定に戻す。
        var map = new KeyMap(Catalog());
        Assert.True(map.Add("go.goTo", KeyBinding.Parse("Ctrl+F"), replaceConflicts: true));
        Assert.Equal(string.Empty, Keys(map, "search.find"));

        IReadOnlyList<KeyConflict> skipped = map.ResetCommand("search.find");

        KeyConflict conflict = Assert.Single(skipped);
        Assert.Equal(("search.find", "go.goTo", "Ctrl+F"), (conflict.First.Command, conflict.Second.Command, conflict.First.Chord.ToString()));
        Assert.Equal(string.Empty, Keys(map, "search.find"));
        Assert.Equal("Ctrl+G, Ctrl+F", Keys(map, "go.goTo"));
        var duplicates = map.All.GroupBy(b => b.Binding).Where(g => g.Count() > 1).ToList();
        Assert.Empty(duplicates);

        // 重ならないキーは戻る。
        map.Remove("go.goTo", KeyBinding.Parse("Ctrl+F"));
        Assert.Empty(map.ResetCommand("search.find"));
        Assert.Equal("Ctrl+F", Keys(map, "search.find"));
    }

    [Fact]
    public void Line_errors_have_kinds_and_values_instead_of_text()
    {
        const string json = """
            {
              "bindings": [
                { "key": "Ctrl+J" },
                { "command": "go.goTo", "key": "Hyper+Q" },
                { "command": "go.goTo", "key": "Ctrl+J", "when": "nowhere" }
              ]
            }
            """;
        KeyBindingsDocument doc = KeyBindingsDocument.Parse(json);
        Assert.Equal(
            [(3, KeyBindingsErrorKind.MissingCommand, (string?)null), (4, KeyBindingsErrorKind.InvalidKey, "Hyper+Q"), (5, KeyBindingsErrorKind.InvalidScope, "nowhere")],
            doc.Errors.Select(e => (e.Line, e.Kind, e.Value)));
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData("\"3\"", 3)]
    [InlineData("1.5", 2)]
    [InlineData("\"x\"", 1)]
    [InlineData("true", 1)]
    public void Schema_version_is_read_leniently(string value, int expected) =>
        Assert.Equal(expected, KeyBindingsDocument.Parse($$"""{ "$schemaVersion": {{value}}, "bindings": [] }""").SchemaVersion);

    [Fact]
    public void Preset_010editor_follows_the_official_keys()
    {
        var map = new KeyMap(Catalog());
        map.SwitchPreset("010editor", preferUser: true);
        Assert.Equal("Ctrl+H, Ctrl+R", Keys(map, "search.replace"));
        Assert.Equal(string.Empty, Keys(map, "file.reload"));
        Assert.Equal("Ctrl+W, Ctrl+F4", Keys(map, "file.close"));
        Assert.Equal("Ctrl+Alt+W", Keys(map, "file.closeAll"));
        Assert.Equal("Ctrl+Y, Ctrl+Shift+Z", Keys(map, "edit.redo"));
        Assert.Equal("Ctrl+E (editor), Ctrl+Shift+A (editor)", Keys(map, "edit.selectRange"));
        Assert.Equal("Ctrl+B", Keys(map, "go.bookmark.edit"));
        Assert.Equal(string.Empty, Keys(map, "edit.pasteOverwrite"));
        Assert.Equal("Ctrl+I", Keys(map, "edit.insertFile"));
        Assert.Equal("Ctrl+K", Keys(map, "analysis.hash"));
    }

    [Fact]
    public void Invalid_lines_are_skipped_with_line_numbers_and_unknown_commands_are_kept()
    {
        const string json = """
            {
              "$schemaVersion": 1,
              "bindings": [
                { "command": "go.goTo", "key": "Ctrl+J" },
                { "command": "search.find", "key": "Hyper+Q" },
                { "command": "plugin.later.cmd", "key": "Ctrl+Shift+F8" }
              ]
            }
            """;
        KeyBindingsDocument doc = KeyBindingsDocument.Parse(json);
        Assert.Equal(5, Assert.Single(doc.Errors).Line);
        var map = new KeyMap(Catalog());
        map.Load(doc);
        Assert.Equal("Ctrl+G, Ctrl+J", Keys(map, "go.goTo"));

        // 後で登録されるコマンドの行は残し、登録された時点で効く。
        map.Add("go.goTo", KeyBinding.Parse("Ctrl+Shift+J"), false);
        Assert.Contains(map.UserEntries, e => e.Command == "plugin.later.cmd");
        map.Catalog.Register(new CommandDefinition("plugin.later.cmd", "tools"));
        Assert.Equal("Ctrl+Shift+F8", Keys(map, "plugin.later.cmd"));
    }

    [Fact]
    public void Former_ids_keep_old_bindings_working()
    {
        var catalog = Catalog();
        catalog.Register(new CommandDefinition("view.newName", "view") { FormerIds = ["view.oldName"] });
        var map = new KeyMap(catalog);
        map.Load(KeyBindingsDocument.Parse("""{ "bindings": [ { "command": "view.oldName", "key": "Ctrl+Shift+F9" } ] }"""));
        Assert.Equal("Ctrl+Shift+F9", Keys(map, "view.newName"));
    }

    [Fact]
    [Trait(TC, "TC-UI-19-01")]
    public void Presets_have_no_conflicts_and_refer_to_existing_commands()
    {
        foreach (string preset in KeyPresets.Ids)
        {
            IReadOnlyList<KeyBindingEntry> entries = KeyPresets.Load(preset, out KeyPresetError? error);
            Assert.Null(error);
            var catalog = CommandCatalog.CreateBuiltIn();
            Assert.All(entries, e => Assert.True(catalog.Contains(e.Command), $"{preset}: 知らないコマンド {e.Command}"));

            var map = new KeyMap(catalog);
            map.SwitchPreset(preset, preferUser: true);
            Assert.Equal(preset, map.Preset);
            var duplicates = map.All.GroupBy(b => (b.Binding.Scope, Key: b.Binding.Chord.First))
                .SelectMany(g => g.SelectMany(a => g.Where(b => b != a && (a.Binding.Chord == b.Binding.Chord || a.Binding.Chord.OverlapsPrefix(b.Binding.Chord)))
                    .Select(b => $"{preset}: {a.Command} と {b.Command} が {a.Binding} で重複"))).ToList();
            Assert.True(duplicates.Count == 0, string.Join(Environment.NewLine, duplicates));
        }

        Assert.NotEmpty(KeyPresets.Load("hxd", out _));
        Assert.NotEmpty(KeyPresets.Load("vscode", out _));
    }

    [Fact]
    [Trait(TC, "TC-UI-19-02")]
    public void Switching_preset_keeps_user_bindings()
    {
        var map = new KeyMap(Catalog());
        map.Load(KeyBindingsDocument.Parse(KeysCustom));
        var changed = 0;
        map.Changed += (_, _) => changed++;

        // コマンド X: hxd.json で default と割り当てが違うもの。
        KeyBindingEntry x = KeyPresets.Load("hxd", out _)[0];
        PresetSwitchPreview preview = map.PreviewPreset("hxd");
        Assert.Empty(preview.Conflicts);
        map.SwitchPreset("hxd", preferUser: true);

        Assert.True(changed > 0);
        Assert.Equal("hxd", map.ToDocument().Preset);
        Assert.Equal(x.Remove, !map.BindingsFor(x.Command).Any(b => b.Binding == x.Binding));
        Assert.Equal("Ctrl+K Ctrl+F (editor)", Keys(map, "edit.fill"));
        Assert.Equal(BindingOrigin.User, map.BindingsFor("edit.fill")[0].Origin);
    }

    [Fact]
    public void Preset_conflicts_with_user_bindings_are_reported_and_resolved()
    {
        var map = new KeyMap(Catalog());
        Assert.True(map.Add("go.end", KeyBinding.Parse("Ctrl+K S"), false));
        PresetSwitchPreview preview = map.PreviewPreset("vscode");
        KeyConflict conflict = Assert.Single(preview.Conflicts);
        Assert.Equal("go.end", conflict.First.Command);
        Assert.Equal("file.saveAll", conflict.Second.Command);

        var preferUser = new KeyMap(Catalog());
        preferUser.Load(map.ToDocument());
        preferUser.SwitchPreset("vscode", preferUser: true);
        Assert.Equal("Ctrl+K S", Keys(preferUser, "go.end"));
        Assert.Equal(string.Empty, Keys(preferUser, "file.saveAll"));

        map.SwitchPreset("vscode", preferUser: false);
        Assert.Equal(string.Empty, Keys(map, "go.end"));
        Assert.Equal("Ctrl+K S", Keys(map, "file.saveAll"));
    }

    [Fact]
    public void Unknown_preset_falls_back_to_default()
    {
        Assert.Empty(KeyPresets.Load("nope", out KeyPresetError? error));
        Assert.NotNull(error);
        var map = new KeyMap(Catalog());
        map.Load(KeyBindingsDocument.Parse("""{ "preset": "nope", "bindings": [] }"""));
        Assert.Equal("default", map.Preset);
        Assert.NotNull(map.PresetError);
    }

    [Fact]
    [Trait(TC, "TC-UI-21-01")]
    public void Exported_file_imported_elsewhere_gives_the_same_bindings()
    {
        var a = new KeyMap(Catalog());
        a.Load(KeyBindingsDocument.Parse(KeysCustom));
        a.SwitchPreset("vscode", preferUser: true);
        string exported = a.ToDocument().ToJson();

        var b = new KeyMap(Catalog());
        KeyImportPreview preview = b.PreviewImport(KeyBindingsDocument.Parse(exported), KeyImportMode.Replace);
        Assert.Equal(0, preview.UnknownCommands);
        Assert.Contains("edit.fill", preview.ChangedCommands);
        b.ApplyImport(preview);

        Assert.Equal(a.All.Select(x => $"{x.Command} {x.Binding}").Order(), b.All.Select(x => $"{x.Command} {x.Binding}").Order());
        Assert.Equal("vscode", b.Preset);
    }

    [Fact]
    [Trait(TC, "TC-UI-21-02")]
    public void Importing_a_file_with_conflicts_lists_them_before_applying()
    {
        // TD-UI-KEYS-DUP: 「検索」に Ctrl+G (「オフセットへ移動」と重複) と、存在しないコマンド。
        const string dup = """
            {
              "$schemaVersion": 1,
              "preset": "default",
              "bindings": [
                { "command": "search.find", "key": "Ctrl+G", "when": "global" },
                { "command": "no.such.command", "key": "Ctrl+Shift+F9" }
              ]
            }
            """;
        var map = new KeyMap(Catalog());
        string before = string.Join("|", map.All.Select(x => $"{x.Command} {x.Binding}"));
        foreach (KeyImportMode mode in new[] { KeyImportMode.Replace, KeyImportMode.Merge })
        {
            KeyImportPreview preview = map.PreviewImport(KeyBindingsDocument.Parse(dup), mode);
            KeyConflict conflict = Assert.Single(preview.Conflicts);
            Assert.Equal(("search.find", "go.goTo", "Ctrl+G"), (conflict.First.Command, conflict.Second.Command, conflict.First.Chord.ToString()));
            Assert.Equal(1, preview.UnknownCommands);
        }

        // 確定しなければ (キャンセル) 割り当ては変わらない。
        Assert.Equal(before, string.Join("|", map.All.Select(x => $"{x.Command} {x.Binding}")));

        // 確定すると読み込んだ側が優先する。
        map.ApplyImport(map.PreviewImport(KeyBindingsDocument.Parse(dup), KeyImportMode.Merge));
        Assert.Equal("Ctrl+F, Ctrl+G", Keys(map, "search.find"));
        Assert.Equal(string.Empty, Keys(map, "go.goTo"));
    }

    [Fact]
    public void Too_new_files_are_detected()
    {
        Assert.True(KeyBindingsDocument.Parse("""{ "$schemaVersion": 2, "bindings": [] }""").IsTooNew);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => KeyBindingsDocument.Parse("{ not json"));
    }
}
