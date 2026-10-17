using HexEditor.Core.Formats;
using HexEditor.Core.View;

namespace HexEditor.Core.Tests.Formats;

/// <summary>コマンドライン <c>hexed dump</c> の形式 (AUTO-39)。xxd / hexdump -C との一致は hexed のテスト (tests/HexEditor.Cli.Tests) で参照ファイルと比べる。</summary>
public sealed class TextDumpWriterTests
{
    private static string Dump(byte[] data, TextDumpStyle style, int width = 16, int? group = null, bool squeeze = false, long displayBase = 0,
        int chunk = int.MaxValue)
    {
        var sw = new StringWriter { NewLine = "\n" };
        var writer = new TextDumpWriter(sw, style, width, group ?? TextDumpWriter.DefaultGroup(style), TextEncoding.Ascii, displayBase, squeeze);
        for (int i = 0; i < data.Length; i += chunk)
        {
            writer.Write(data.AsSpan(i, Math.Min(chunk, data.Length - i)));
        }

        writer.Finish();
        return sw.ToString();
    }

    [Fact]
    public void Default_style_has_offset_hex_with_a_double_space_after_eight_bytes_and_text_between_bars()
    {
        byte[] data = [.. "ABCDEFGHIJKLMNOP"u8.ToArray(), 0x00, 0xFF];
        string[] lines = Dump(data, TextDumpStyle.Default).Split('\n');
        Assert.Equal("00000000  41 42 43 44 45 46 47 48  49 4A 4B 4C 4D 4E 4F 50  |ABCDEFGHIJKLMNOP|", lines[0]);
        Assert.Equal("00000010  00 FF" + new string(' ', 45) + "|..|", lines[1]);
        Assert.Equal(string.Empty, lines[2]);
    }

    [Fact]
    public void Hexdump_c_style_is_lowercase_and_ends_with_the_final_offset()
    {
        string text = Dump([0x41, 0x7F, 0x00], TextDumpStyle.HexdumpC);
        Assert.Equal("00000000  41 7f 00" + new string(' ', 42) + "|A..|\n00000003\n", text);
    }

    [Fact]
    public void Squeeze_prints_one_star_for_a_run_of_identical_lines_and_always_prints_a_partial_last_line()
    {
        byte[] data = [.. new byte[16 * 5], 1, 2, 3];
        string[] lines = Dump(data, TextDumpStyle.HexdumpC, squeeze: true).TrimEnd('\n').Split('\n');
        Assert.Equal(["00000000", "*", "00000050", "00000053"], lines.Select(l => l.Length > 8 ? l[..8] : l));
        Assert.Contains(lines, l => l == "*");

        // 最後の行が同じ内容でも、* の後に終端のオフセットが続く。
        string[] same = Dump(new byte[16 * 3], TextDumpStyle.HexdumpC, squeeze: true).TrimEnd('\n').Split('\n');
        Assert.Equal(["00000000", "*", "00000030"], same.Select(l => l.Length > 8 ? l[..8] : l));
    }

    [Fact]
    public void Output_does_not_depend_on_how_the_data_is_split_into_writes()
    {
        byte[] data = new byte[1000];
        new Random(5).NextBytes(data);
        foreach (TextDumpStyle style in Enum.GetValues<TextDumpStyle>())
        {
            string whole = Dump(data, style, squeeze: true);
            Assert.Equal(whole, Dump(data, style, squeeze: true, chunk: 7));
            Assert.Equal(whole, Dump(data, style, squeeze: true, chunk: 16));
        }
    }

    [Fact]
    public void Width_group_and_base_address_apply()
    {
        string plain = Dump([1, 2, 3, 4, 5], TextDumpStyle.Plain, width: 2, group: 0);
        Assert.Equal("01 02\n03 04\n05\n", plain);

        string xxd = Dump([0x61, 0x62, 0x63, 0x64, 0x65], TextDumpStyle.Xxd, width: 4, group: 1, displayBase: 0x100);
        Assert.Equal("00000100: 61 62 63 64  abcd\n00000104: 65 __ __ __  e\n".Replace("__", "  "), xxd);
    }

    [Fact]
    public void Xxd_squeeze_skips_only_all_zero_lines()
    {
        byte[] data = [.. new byte[16 * 3], .. Enumerable.Repeat((byte)7, 16 * 3)];
        string[] lines = Dump(data, TextDumpStyle.Xxd, squeeze: true).TrimEnd('\n').Split('\n');
        Assert.Equal(5, lines.Length);
        Assert.Equal("*", lines[1]);
        Assert.StartsWith("00000030:", lines[2]);
    }

    [Fact]
    public void Offsets_grow_beyond_eight_digits_for_large_addresses()
    {
        string text = Dump([0], TextDumpStyle.HexdumpC, displayBase: 0x1_0000_0000);
        Assert.StartsWith("100000000  00", text);
    }

    [Fact]
    public void Width_out_of_range_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextDumpWriter(new StringWriter(), TextDumpStyle.Default, 0, 8, null, 0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextDumpWriter(new StringWriter(), TextDumpStyle.Default, 1025, 8, null, 0, false));
    }
}
