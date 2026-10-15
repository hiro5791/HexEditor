using HexEditor.Cli.Tests.Support;
using HexEditor.TestData;

namespace HexEditor.Cli.Tests;

/// <summary>Hex ダンプの出力 (AUTO-39)。参照は <c>xxd</c> の出力と、<c>hexdump -C</c> の規則に従って作った出力 (tests/fixtures/dump)。</summary>
public sealed class DumpTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "dump", name)).Replace("\r\n", "\n");

    [Fact]
    [Trait("TC", "TC-AUTO-39-01")]
    public void Xxd_style_matches_xxd()
    {
        using var dir = new TempDir();
        string bytes = TestDataCatalog.Get("TD-BYTES-256");
        string squeeze = TestDataCatalog.Get("TD-AUTO-SQUEEZE");

        Assert.Equal(Fixture("bytes256.xxd"), Hexed.Run(dir.Path, "dump", bytes, "--style", "xxd").Out.Replace("\r\n", "\n"));
        Assert.Equal(Fixture("bytes256.c8.xxd"), Hexed.Run(dir.Path, "dump", bytes, "--style", "xxd", "--width", "8").Out.Replace("\r\n", "\n"));
        Assert.Equal(Fixture("squeeze.xxd"), Hexed.Run(dir.Path, "dump", squeeze, "--style", "xxd").Out.Replace("\r\n", "\n"));

        // 最後の行が途中で終わる場合、グループの大きさ、00 の行の省略 (xxd -a)。
        Assert.Equal(Fixture("bytes256.first37.xxd"), Hexed.Run(dir.Path, "dump", bytes, "--style", "xxd", "--range", "0:37").Out.Replace("\r\n", "\n"));
        Assert.Equal(Fixture("bytes256.g1.xxd"), Hexed.Run(dir.Path, "dump", bytes, "--style", "xxd", "--group", "1", "--range", "0:40").Out.Replace("\r\n", "\n"));
        Assert.Equal(Fixture("bytes256.g4c12.xxd"),
            Hexed.Run(dir.Path, "dump", bytes, "--style", "xxd", "--group", "4", "--width", "12", "--range", "0:40").Out.Replace("\r\n", "\n"));
        Assert.Equal(Fixture("squeeze.a.xxd"), Hexed.Run(dir.Path, "dump", squeeze, "--style", "xxd", "--squeeze").Out.Replace("\r\n", "\n"));
    }

    [Fact]
    [Trait("TC", "TC-AUTO-39-02")]
    public void Hexdump_c_style_with_squeeze_matches_hexdump_c()
    {
        using var dir = new TempDir();
        string squeeze = TestDataCatalog.Get("TD-AUTO-SQUEEZE");
        string bytes = TestDataCatalog.Get("TD-BYTES-256");

        CliResult s = Hexed.Run(dir.Path, "dump", squeeze, "--style", "hexdump-c", "--squeeze");
        Assert.Equal(Fixture("squeeze.hexdump-c"), s.Out.Replace("\r\n", "\n"));
        Assert.Contains("*", s.OutLines);
        Assert.Equal("00000450", s.OutLines[^1]);

        Assert.Equal(Fixture("bytes256.hexdump-c"), Hexed.Run(dir.Path, "dump", bytes, "--style", "hexdump-c", "--squeeze").Out.Replace("\r\n", "\n"));
        Assert.Equal(Fixture("bytes256.first37.hexdump-c"),
            Hexed.Run(dir.Path, "dump", bytes, "--style", "hexdump-c", "--squeeze", "--range", "0:37").Out.Replace("\r\n", "\n"));

        // --squeeze なしでは省略しない。
        CliResult all = Hexed.Run(dir.Path, "dump", squeeze, "--style", "hexdump-c");
        Assert.DoesNotContain("*", all.OutLines);
        Assert.Equal(1104 / 16 + 1, all.OutLines.Length);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-39-03")]
    public void Range_from_the_end_prints_only_the_last_bytes_and_a_range_outside_the_file_exits_with_2()
    {
        using var dir = new TempDir();
        string bytes = TestDataCatalog.Get("TD-BYTES-256");

        CliResult tail = Hexed.Run(dir.Path, "dump", bytes, "--range", "end-32:32");
        Assert.Equal(0, tail.ExitCode);
        Assert.Equal(2, tail.OutLines.Length);
        Assert.StartsWith("000000e0  E0 E1 E2 E3 E4 E5 E6 E7  E8 E9 EA EB EC ED EE EF  |", tail.OutLines[0]);
        Assert.StartsWith("000000f0  F0 F1 F2 F3 F4 F5 F6 F7  F8 F9 FA FB FC FD FE FF  |", tail.OutLines[1]);

        CliResult outside = Hexed.Run(dir.Path, "dump", bytes, "--range", "end-16:32", "--lang", "en");
        Assert.Equal(2, outside.ExitCode);
        Assert.Equal(string.Empty, outside.Out);
        Assert.Contains("outside the file", outside.Err);

        CliResult dotDot = Hexed.Run(dir.Path, "dump", bytes, "--range", "0x10..0x20");
        Assert.Single(dotDot.OutLines);
        Assert.StartsWith("00000010  10 11", dotDot.OutLines[0]);
    }

    [Fact]
    public void Default_style_prints_the_offset_hex_and_text_columns_and_base_address_and_encoding_apply()
    {
        using var dir = new TempDir();
        byte[] data = [.. "Hello, hex!"u8.ToArray(), 0x00, 0xE3, 0x81, 0x82];
        File.WriteAllBytes(dir.File("t.bin"), data);

        CliResult ascii = Hexed.Run(dir.Path, "dump", "t.bin");
        Assert.Equal("00000000  48 65 6C 6C 6F 2C 20 68  65 78 21 00 E3 81 82     |Hello, hex!....|", ascii.OutLines[0]);

        CliResult shifted = Hexed.Run(dir.Path, "dump", "t.bin", "--base-address", "0x1000", "--range", "5:4");
        Assert.StartsWith("00001005  2C 20 68 65", shifted.OutLines[0]);

        CliResult cp932 = Hexed.Run(dir.Path, "dump", "t.bin", "--encoding", "cp932", "--width", "4", "--group", "0", "--range", "11:4");
        Assert.Contains("|", cp932.OutLines[0]);

        CliResult unknown = Hexed.Run(dir.Path, "dump", "t.bin", "--encoding", "nope");
        Assert.Equal(2, unknown.ExitCode);
    }

    [Fact]
    public void Plain_and_source_code_styles_are_available()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("t.bin"), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17]);

        CliResult plain = Hexed.Run(dir.Path, "dump", "t.bin", "--style", "plain", "--width", "8");
        Assert.Equal(["01 02 03 04 05 06 07 08", "09 0A 0B 0C 0D 0E 0F 10", "11"], plain.OutLines);

        CliResult b64 = Hexed.Run(dir.Path, "dump", "t.bin", "--style", "base64");
        Assert.Equal(Convert.ToBase64String(File.ReadAllBytes(dir.File("t.bin"))), b64.OutLines[0].Trim());
        Assert.DoesNotContain('\r', b64.Out);

        CliResult c = Hexed.Run(dir.Path, "dump", "t.bin", "--style", "c", "--range", "0:4");
        Assert.Equal(0, c.ExitCode);
        Assert.Contains("0x01", c.Out);

        CliResult unknown = Hexed.Run(dir.Path, "dump", "t.bin", "--style", "klingon");
        Assert.Equal(2, unknown.ExitCode);
    }

    [Fact]
    [Trait("TC", "TC-AUTO-36-04")]
    public void Output_file_of_a_cancelled_dump_is_removed_and_the_exit_code_is_6()
    {
        using var dir = new TempDir();
        string sparse = TestDataCatalog.Get("TD-SPARSE-100G");
        string output = dir.File("dump.txt");

        using var process = System.Diagnostics.Process.Start(ConsoleProcess.StartInfo(dir.Path, ["dump", sparse, "-o", output]))!;
        _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        Assert.True(SpinWait.SpinUntil(() => File.Exists(output) && SafeLength(output) > 1_000_000, TimeSpan.FromSeconds(60)));
        ConsoleProcess.SendCtrlC(process);
        Assert.True(process.WaitForExit(60_000));

        Assert.Equal(6, process.ExitCode);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
