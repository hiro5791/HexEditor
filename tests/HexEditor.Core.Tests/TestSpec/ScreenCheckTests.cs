using HexEditor.ManualTests;

namespace HexEditor.Core.Tests.TestSpec;

/// <summary>コントラストテーマの UI テストの画面の判定。</summary>
public sealed class ScreenCheckTests
{
    private const int Width = 400;
    private const int Height = 200;

    private static uint[] Image(uint background, uint glyph)
    {
        uint[] pixels = new uint[Width * Height];
        Array.Fill(pixels, background);
        for (int button = 0; button < 3; button++)
        {
            int center = Width - 23 - (46 * button);
            for (int x = center - 5; x <= center + 5; x++)
            {
                pixels[(16 * Width) + x] = glyph;
            }
        }

        return pixels;
    }

    [Fact]
    public void Readable_buttons_on_the_theme_background_pass() =>
        Assert.Empty(ScreenCheck.Evaluate(Image(0xFFFAEF, 0x202020), Width, Height, 1, [0xFFFAEF, 0x202020]));

    [Fact]
    public void Glyphs_with_too_little_contrast_are_reported() =>
        Assert.Equal(3, ScreenCheck.Evaluate(Image(0x202020, 0x303030), Width, Height, 1, null).Count);

    [Fact]
    public void Dimmed_glyphs_of_an_inactive_window_pass_only_with_the_inactive_limit()
    {
        uint[] image = Image(0xF3F3F3, 0x999999);
        Assert.Empty(ScreenCheck.Evaluate(image, Width, Height, 1, null, ScreenCheck.MinInactiveContrast));
        Assert.Equal(3, ScreenCheck.Evaluate(image, Width, Height, 1, null).Count);
    }

    [Fact]
    public void Unreadable_glyphs_fail_even_with_the_inactive_limit() =>
        Assert.Equal(3, ScreenCheck.Evaluate(Image(0x202020, 0x303030), Width, Height, 1, null, ScreenCheck.MinInactiveContrast).Count);

    [Fact]
    public void Black_window_in_a_light_contrast_theme_is_reported()
    {
        IReadOnlyList<string> problems = ScreenCheck.Evaluate(Image(0x000000, 0xFFFFFF), Width, Height, 1, [0xFFFAEF, 0xFFFFFF]);
        Assert.Contains(problems, p => p.Contains("#FFFAEF", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("テーマにない色 #000000", StringComparison.Ordinal));
    }

    [Fact]
    public void Scattered_smoothed_text_colors_are_not_foreign()
    {
        uint[] pixels = Image(0xFFFAEF, 0x202020);
        for (int i = 0; i < 300; i++)
        {
            pixels[(100 * Width) + i] = 0x808000 + (uint)i;
        }

        Assert.Empty(ScreenCheck.Evaluate(pixels, Width, Height, 1, [0xFFFAEF, 0x202020]));
    }

    [Fact]
    public void Large_areas_of_smoothed_text_colors_are_not_foreign()
    {
        uint[] pixels = Image(0x000000, 0xFFFFFF);
        Array.Fill(pixels, 0x808080u, 100 * Width, 20 * Width);
        Assert.Empty(ScreenCheck.Evaluate(pixels, Width, Height, 1, [0x000000, 0xFFFFFF]));
    }

    private static uint[] Stripes(int blur)
    {
        uint[] pixels = new uint[Width * 4];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int phase = x % 40;
                double level = phase < 30 ? 0 : phase >= 30 + blur ? 1 : (phase - 29.0) / (blur + 1);
                uint gray = (uint)(255 * (1 - level));
                pixels[(y * Width) + x] = (gray << 16) | (gray << 8) | gray;
            }
        }

        return pixels;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    public void Edge_width_grows_with_the_blur(int blur, int expected) =>
        Assert.Equal(expected, ScreenCheck.EdgeWidth(Stripes(blur), Width, 0, 0, Width, 4));

    [Fact]
    public void Contrast_follows_wcag() => Assert.Equal(21, ScreenCheck.Contrast(0xFFFFFF, 0x000000), 1);
}
