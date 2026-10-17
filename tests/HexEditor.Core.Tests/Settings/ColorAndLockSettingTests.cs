using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HexEditor.Core.Engine;
using HexEditor.Core.Settings;

namespace HexEditor.Core.Tests.Settings;

/// <summary>色の設定 (アクセントカラー。09 の UI-27) と「ファイルのロック」(01 の ENG-15 の仕様 3) の設定項目。</summary>
public sealed class ColorAndLockSettingTests
{
    private static SettingDefinition Find(string key) => SettingsCatalog.CreateBuiltIn().Find(key) ?? throw new InvalidOperationException(key);

    [Fact]
    public void Accent_color_accepts_system_and_rrggbb_only()
    {
        SettingDefinition accent = Find("ui.accentColor");
        Assert.Equal(SettingKind.Color, accent.Kind);
        Assert.Equal("system", accent.Default!.GetValue<string>());
        Assert.True(accent.RequiresRestart);
        Assert.True(accent.Validate(JsonValue.Create("system")));
        Assert.True(accent.Validate(JsonValue.Create("#E81123")));
        Assert.True(accent.Validate(JsonValue.Create("#00ff7f")));
        Assert.False(accent.Validate(JsonValue.Create("red")));
        Assert.False(accent.Validate(JsonValue.Create("#E8112")));
        Assert.False(accent.Validate(JsonValue.Create("#GG1123")));
        Assert.False(accent.Validate(JsonValue.Create(5)));
    }

    [Fact]
    public void The_schema_describes_color_settings_with_a_pattern()
    {
        string schema = SettingsSchema.Generate(SettingsCatalog.CreateBuiltIn(), s => s.Key);
        JsonObject accent = JsonNode.Parse(schema)!["properties"]!["ui.accentColor"]!.AsObject();
        Assert.Equal("string", accent["type"]!.GetValue<string>());
        var pattern = new Regex(accent["pattern"]!.GetValue<string>());
        Assert.Matches(pattern, "system");
        Assert.Matches(pattern, "#E81123");
        Assert.DoesNotMatch(pattern, "blue");
    }

    [Fact]
    public void File_lock_setting_offers_the_three_policies()
    {
        SettingDefinition lockSetting = Find(FileLockSettings.Key);
        Assert.Equal(SettingCategories.Files, lockSetting.Category);
        Assert.Equal(["whileModified", "always", "none"], lockSetting.Options);
        Assert.Equal(FileLockSettings.WhileModified, lockSetting.Default!.GetValue<string>());
        Assert.Equal(FileLockPolicy.WhileModified, FileLockSettings.Parse("whileModified"));
        Assert.Equal(FileLockPolicy.Always, FileLockSettings.Parse("always"));
        Assert.Equal(FileLockPolicy.None, FileLockSettings.Parse("none"));
        Assert.Equal(FileLockPolicy.WhileModified, FileLockSettings.Parse("bogus"));
        Assert.Equal(FileLockPolicy.WhileModified, FileLockSettings.Parse(null));
    }
}
