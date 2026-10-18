using System.Text;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Loading;
using HexEditor.Core.Templates.Visualization;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Templates.TemplateTestSupport;

namespace HexEditor.Core.Tests.Templates;

/// <summary>テンプレートのビジュアライザ (07 の TPL-16) の内容の作成。表示 (パネル) は UI テスト。</summary>
public sealed class TemplateVisualizerTests
{
    private static string Fixture(string relative) =>
        Path.Combine(Path.GetDirectoryName(Core.Tests.Engine.SourceTests.FindRepoFile("HexEditor.slnx"))!, "tests", "fixtures", "templates", relative);

    private static TemplateVisual Visualize(string template, byte[] data, string path)
    {
        TemplateResult r = RunOk(template, data);
        return TemplateVisualizer.Create(Node(r, path), TemplateDisplayContext.Invariant);
    }

    [Fact]
    [Trait(TC, "TC-TPL-16-01")]
    public void Bitmap_bgra8_of_a_top_down_32_bit_bmp_is_the_expected_image()
    {
        TemplateProgram program = TemplateLoader.LoadFile(Fixture(Path.Combine("TC-TPL-16-01", "template.hxt")));
        TemplateResult r = Run(program, TemplateDataSource.FromBytes(TestDataCatalog.TplBmp()));
        Assert.Equal(TemplateRunState.Completed, r.State);
        TemplateNode pixels = Node(r, "pixels");
        Assert.True(TemplateVisualizer.HasVisualizer(pixels));

        var bitmap = Assert.IsType<BitmapVisual>(TemplateVisualizer.Create(pixels));
        Assert.Equal(4, bitmap.Width);
        Assert.Equal(4, bitmap.Height);
        Assert.Equal("bgra8", bitmap.Format);
        Assert.False(bitmap.Truncated);

        // 16 画素の色 (上下の向きも正しい: 行 0 が上。BI_RGB の 4 バイト目は予約 (0) なので不透明として表示する)。
        Assert.Equal(TestDataCatalog.TplBmpExpected(), bitmap.Pixels);

        // 代替テキストの元になる説明に、幅・高さ・形式が入る (仕様 6)。
        Assert.Contains("4 × 4", bitmap.Description);
        Assert.Contains("bgra8", bitmap.Description);
    }

    [Theory]
    [InlineData("gray8", "80", 0xFF808080u)]
    [InlineData("rgb8", "102030", 0xFF102030u)]
    [InlineData("rgba8", "10203040", 0x40102030u)]
    [InlineData("rgb565", "00F8", 0xFFFF0000u)]
    public void Bitmap_formats_decode_one_pixel(string format, string hex, uint expected)
    {
        int size = hex.Length / 2;
        var v = Assert.IsType<BitmapVisual>(Visualize($"u8 px[{size}] [[visualize(\"bitmap\", 1, 1, \"{format}\")]];", Hex(hex), "px"));
        Assert.Equal(expected, v.Pixels[0]);
    }

    [Fact]
    public void Bitmap_indexed8_uses_a_palette_pattern()
    {
        // パレット (RGB 3 色) のパターンをパスで渡す。
        var v = Assert.IsType<BitmapVisual>(Visualize(
            "u8 pal[9]; u8 px[3] [[visualize(\"bitmap\", 3, 1, \"indexed8\", pal)]];",
            Hex("FF0000 00FF00 0000FF 02 01 00"), "px"));
        Assert.Equal([0xFF0000FFu, 0xFF00FF00u, 0xFFFF0000u], v.Pixels);
    }

    [Fact]
    public void Bitmap_with_bad_arguments_is_an_error_visual()
    {
        var v = Assert.IsType<ErrorVisual>(Visualize("u8 px[4] [[visualize(\"bitmap\", 0, 1, \"gray8\")]];", new byte[4], "px"));
        Assert.False(string.IsNullOrEmpty(v.Error));
        Assert.IsType<ErrorVisual>(Visualize("u8 px[4] [[visualize(\"bitmap\", 2, 2, \"yuv\")]];", new byte[4], "px"));
        Assert.IsType<ErrorVisual>(Visualize("u8 px[4] [[visualize(\"hologram\")]];", new byte[4], "px"));
    }

    [Fact]
    public void Bitmap_over_the_side_limit_is_truncated()
    {
        // 幅 20,000 は 16,384 に切る (仕様 3)。データは 1 行分だけ。
        var v = Assert.IsType<BitmapVisual>(Visualize("u8 px[20000] [[visualize(\"bitmap\", 20000, 1, \"gray8\")]];", new byte[20000], "px"));
        Assert.Equal(TemplateVisualizer.MaxImageSide, v.Width);
        Assert.True(v.Truncated);
    }

    [Fact]
    [Trait(TC, "TC-TPL-16-02")]
    public void Audio_of_the_wav_data_chunk_decodes_to_the_same_samples()
    {
        byte[] wav = TestDataCatalog.TplWav();
        TemplateResult r = RunOk(TestDataCatalog.TplWavHxt, wav);
        TemplateNode data = Node(r, "chunks[1].data");
        var audio = Assert.IsType<AudioVisual>(TemplateVisualizer.Create(data));
        Assert.Equal(44_100, audio.SampleRate);
        Assert.Equal(1, audio.Channels);
        Assert.Equal(TimeSpan.FromSeconds(1), audio.Duration);
        Assert.Contains("44100 Hz", audio.Description);

        // 再生に渡すサンプル列 (16 bit) が data チャンクのサンプル列と一致する。
        short[] samples = PcmAudio.ToInt16(audio.Samples);
        Assert.Equal(44_100, samples.Length);
        for (int n = 0; n < samples.Length; n++)
        {
            Assert.Equal(TestDataCatalog.TplWavSample(n), samples[n]);
        }

        // WAV として保存すると元のファイルと同じ内容になる (仕様 5)。
        Assert.Equal(wav, audio.ToWav());
        (float Min, float Max)[] wave = audio.Waveform(100);
        Assert.Equal(100, wave.Length);
        Assert.All(wave, w => Assert.InRange(w.Max, 0.45f, 0.51f));
    }

    [Theory]
    [InlineData("pcm_u8", "80FF00", new[] { 0f, 127f / 128, -1f })]
    [InlineData("pcm_s16be", "7FFF8000", new[] { 32767f / 32768, -1f })]
    [InlineData("pcm_s24le", "FFFF7F000080", new[] { 8388607f / 8388608, -1f })]
    [InlineData("pcm_f32le", "0000003F", new[] { 0.5f })]
    public void Audio_formats_decode(string format, string hex, float[] expected)
    {
        byte[] data = Hex(hex);
        var audio = Assert.IsType<AudioVisual>(Visualize($"u8 s[{data.Length}] [[visualize(\"audio\", 8000, 1, \"{format}\")]];", data, "s"));
        Assert.Equal(expected, audio.Samples);
        byte[] wav = audio.ToWav();
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
    }

    [Fact]
    [Trait(TC, "TC-TPL-16-04")]
    public void Image_keeps_the_bytes_and_sniffs_the_format()
    {
        // 壊れた PNG もデータとして渡す (デコードは Windows のコーデック。Platform のテストで落ちないことを確かめる)。
        byte[] broken = TestDataCatalog.TplPngBroken();
        var v = Assert.IsType<EncodedImageVisual>(Visualize($"u8 img[{broken.Length}] [[visualize(\"image\")]];", broken, "img"));
        Assert.Equal(broken, v.Data);
        Assert.Equal("PNG", v.FormatName);
        Assert.Equal("BMP", EncodedImageVisual.Sniff(TestDataCatalog.TplBmp()));
        Assert.Null(EncodedImageVisual.Sniff(new byte[8]));
    }

    [Fact]
    [Trait(TC, "TC-TPL-16-05")]
    public void Table_of_a_struct_array_has_member_columns_and_csv()
    {
        // TD-ANA-REC64 の先頭 16 レコード。
        byte[] data = TestDataCatalog.Rec64()[..(64 * 16)];
        var table = Assert.IsType<TableVisual>(Visualize(
            "struct Rec { u8 tag[16]; le u32 no; u8 rest[44]; }; Rec recs[16] [[visualize(\"table\")]];", data, "recs"));
        Assert.Equal(["tag", "no", "rest"], table.Columns);
        Assert.Equal(16, table.RowCount);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(i.ToString(System.Globalization.CultureInfo.InvariantCulture), table.Row(i)[1]);
        }

        string[] lines = table.ToCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(17, lines.Length);
        Assert.Equal("tag,no,rest", lines[0]);
        Assert.Equal(string.Join(",", table.Row(3).Select(TableVisual.Csv)), lines[4]);
    }

    [Fact]
    public void Chart_color_text_and_disasm()
    {
        var chart = Assert.IsType<ChartVisual>(Visualize("le u16 v[4] [[visualize(\"chart\", \"bar\")]];", Hex("0100 0200 0300 0A00"), "v"));
        Assert.Equal(TemplateChartKind.Bar, chart.Chart);
        Assert.Equal([1.0, 2, 3, 10], chart.Values);
        Assert.Equal(10, chart.Maximum);

        var bytes = Assert.IsType<ChartVisual>(Visualize("u8 v[3] [[visualize(\"chart\", \"line\")]];", Hex("05 06 07"), "v"));
        Assert.Equal([5.0, 6, 7], bytes.Values);

        var color = Assert.IsType<ColorVisual>(Visualize("rgb565 c [[visualize(\"color\")]];", Hex("00F8"), "c"));
        Assert.Equal(0xFFFF0000u, color.Argb);
        Assert.Equal("#FF0000", color.Hex);

        var text = Assert.IsType<TextVisual>(Visualize("u8 t[4] [[visualize(\"text\", \"utf-8\")]];", Encoding.UTF8.GetBytes("abcd"), "t"));
        Assert.Equal("abcd", text.Text);
        var sjis = Assert.IsType<TextVisual>(Visualize("u8 t[4] [[visualize(\"text\", \"shift_jis\")]];", Hex("82A082A2"), "t"));
        Assert.Equal("あい", sjis.Text);

        // x86-64: nop; ret
        var disasm = Assert.IsType<DisassemblyVisual>(Visualize("u8 code[2] [[visualize(\"disasm\", \"x86-64\", 0x401000)]];", Hex("90 C3"), "code"));
        Assert.Equal(2, disasm.Instructions.Count);
        Assert.Equal("nop", disasm.Instructions[0].Mnemonic);
        Assert.Equal(0x401000UL, disasm.Instructions[0].Address);
        Assert.StartsWith("0000000000401001", disasm.Lines[1]);
        Assert.Contains("ret", disasm.Lines[1]);
    }
}
