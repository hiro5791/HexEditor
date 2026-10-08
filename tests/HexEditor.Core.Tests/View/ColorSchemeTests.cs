using System.Text.Json;
using HexEditor.Core.View;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.View;

/// <summary>UI-28 Hex 表示の配色。</summary>
public sealed class ColorSchemeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hexeditor-schemes-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    [Trait(TC, "TC-UI-28-03")]
    public void Low_contrast_pairs_are_warned_but_can_be_saved()
    {
        ColorScheme scheme = ColorScheme.BuiltIns[0].Duplicate("low");
        SchemeColor.TryParse("#FFFFFF", out SchemeColor white);
        SchemeColor.TryParse("#949494", out SchemeColor gray);
        scheme.Set(SchemeElement.Background, dark: false, white);
        scheme.Set(SchemeElement.HexText, dark: false, gray);
        IReadOnlyList<ContrastWarning> warnings = scheme.ContrastWarnings();
        ContrastWarning warning = Assert.Single(warnings, w => w.Foreground == SchemeElement.HexText && !w.Dark);
        Assert.InRange(warning.Ratio, 2.9, 3.1);

        var store = new ColorSchemeStore(_dir);
        string path = store.Save(scheme);
        Assert.Equal(Path.Combine(_dir, "themes", "low.json"), path);
        Assert.Contains(store.All(), s => s.Name == "low");
    }

    [Fact]
    [Trait(TC, "TC-UI-28-02")]
    public void Exported_scheme_imports_to_the_same_colors()
    {
        ColorScheme scheme = ColorScheme.BuiltIns[0].Duplicate("test-scheme");
        SchemeColor.TryParse("#102030", out SchemeColor back);
        SchemeColor.TryParse("#F0E0D0", out SchemeColor fore);
        scheme.Set(SchemeElement.Background, false, back);
        scheme.Set(SchemeElement.HexText, false, fore);
        scheme.Set(SchemeElement.Background, true, back);
        scheme.Set(SchemeElement.HexText, true, fore);
        string export = Path.Combine(Path.GetTempPath(), $"scheme-{Guid.NewGuid():N}.json");
        try
        {
            ColorSchemeStore.Export(scheme, export);
            var other = new ColorSchemeStore(_dir);
            other.Import(export);
            ColorScheme imported = other.Find("test-scheme");
            foreach (SchemeElement e in Enum.GetValues<SchemeElement>())
            {
                Assert.Equal(scheme.Get(e, false), imported.Get(e, false));
                Assert.Equal(scheme.Get(e, true), imported.Get(e, true));
            }
        }
        finally
        {
            File.Delete(export);
        }
    }

    [Fact]
    public void Invalid_elements_fall_back_and_bad_json_is_rejected()
    {
        ColorScheme scheme = ColorScheme.Parse("""{"name": "x", "light": {"background": "#GGGGGG", "hexText": "#112233", "unknown": "#000000"}}""");
        Assert.Null(scheme.Get(SchemeElement.Background, false));
        Assert.Equal("#112233", scheme.Get(SchemeElement.HexText, true)!.Value.ToString());
        Assert.Equal(2, scheme.LoadWarnings.Count);
        Assert.ThrowsAny<JsonException>(() => ColorScheme.Parse("{\"name\": \"x\""));
    }

    [Fact]
    public void Built_ins_have_light_and_dark()
    {
        Assert.Equal(["default", "solarized", "contrast"], ColorScheme.BuiltIns.Select(s => s.Name));
        Assert.Equal("#FDF6E3", ColorScheme.BuiltIns[1].Get(SchemeElement.Background, false)!.Value.ToString());
        Assert.Equal("#002B36", ColorScheme.BuiltIns[1].Get(SchemeElement.Background, true)!.Value.ToString());
        Assert.Null(ColorScheme.BuiltIns[0].Get(SchemeElement.Background, false));
    }
}
