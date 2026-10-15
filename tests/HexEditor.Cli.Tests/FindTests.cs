using System.Text.Json;
using HexEditor.Cli.Tests.Support;
using HexEditor.TestData;

namespace HexEditor.Cli.Tests;

/// <summary>検索 (AUTO-44 の 2)。</summary>
public sealed class FindTests
{
    [Fact]
    [Trait("TC", "TC-AUTO-44-03")]
    public void Wildcard_hex_pattern_prints_the_matching_positions()
    {
        using var dir = new TempDir();
        string find = TestDataCatalog.Get("TD-AUTO-FIND");
        CliResult found = Hexed.Run(dir.Path, "find", find, "--hex", "DE AD ?? EF");
        Assert.Equal(0, found.ExitCode);
        Assert.Equal([$"{find}  0x100  4", $"{find}  0x2000  4", $"{find}  0xFFFFC  4"], found.OutLines);

        CliResult none = Hexed.Run(dir.Path, "find", TestDataCatalog.Get("TD-ZERO-1M"), "--hex", "DE AD ?? EF");
        Assert.Equal(1, none.ExitCode);
        Assert.Equal(string.Empty, none.Out);
    }

    [Fact]
    public void Text_regex_range_max_and_json_options_work()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("t.txt"), "alpha Beta alpha beta ALPHA"u8.ToArray());

        CliResult text = Hexed.Run(dir.Path, "find", "t.txt", "--text", "alpha");
        Assert.Equal(["t.txt  0x0  5", "t.txt  0xB  5", "t.txt  0x16  5"], text.OutLines);

        CliResult sensitive = Hexed.Run(dir.Path, "find", "t.txt", "--text", "alpha", "--case-sensitive");
        Assert.Equal(["t.txt  0x0  5", "t.txt  0xB  5"], sensitive.OutLines);

        CliResult regex = Hexed.Run(dir.Path, "find", "t.txt", "--regex", "[bB]eta");
        Assert.Equal(["t.txt  0x6  4", "t.txt  0x11  4"], regex.OutLines);

        CliResult range = Hexed.Run(dir.Path, "find", "t.txt", "--text", "alpha", "--range", "1:20");
        Assert.Equal(["t.txt  0xB  5"], range.OutLines);

        CliResult max = Hexed.Run(dir.Path, "find", "t.txt", "--text", "alpha", "--max", "1");
        Assert.Single(max.OutLines);

        CliResult json = Hexed.Run(dir.Path, "find", "t.txt", "--text", "alpha", "--format", "json");
        using JsonDocument document = JsonDocument.Parse(json.OutBytes);
        Assert.Equal(3, document.RootElement.GetProperty("count").GetInt64());
        Assert.Equal(11, document.RootElement.GetProperty("matches")[1].GetProperty("offset").GetInt64());

        CliResult utf16 = Hexed.Run(dir.Path, "find", "t.txt", "--text", "x", "--encoding", "nope");
        Assert.Equal(2, utf16.ExitCode);
    }

    [Fact]
    public void Several_files_wildcards_and_errors_are_reported_per_file()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("a.bin"), [0xCA, 0xFE, 0x00]);
        File.WriteAllBytes(dir.File("b.bin"), [0x00, 0xCA, 0xFE]);
        File.WriteAllBytes(dir.File("c.dat"), [0x01]);

        CliResult both = Hexed.Run(dir.Path, "find", "*.bin", "--hex", "CA FE");
        Assert.Equal(["a.bin  0x0  2", "b.bin  0x1  2"], both.OutLines);

        CliResult withMissing = Hexed.Run(dir.Path, "find", "a.bin", "none.bin", "--hex", "CA FE", "--lang", "en");
        Assert.Equal(3, withMissing.ExitCode);
        Assert.Equal(["a.bin  0x0  2"], withMissing.OutLines);

        Assert.Equal(2, Hexed.Run(dir.Path, "find", "a.bin").ExitCode);
        Assert.Equal(2, Hexed.Run(dir.Path, "find", "a.bin", "--hex", "00", "--text", "x").ExitCode);
        Assert.Equal(2, Hexed.Run(dir.Path, "find", "a.bin", "--hex", "ZZ").ExitCode);
    }
}
