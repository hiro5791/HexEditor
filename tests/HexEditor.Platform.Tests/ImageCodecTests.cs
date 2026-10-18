using HexEditor.Platform.Imaging;
using HexEditor.TestData;
using static HexEditor.Platform.Tests.Support.TestSupport;

namespace HexEditor.Platform.Tests;

/// <summary>
/// テンプレートのビジュアライザの画像のデコード (07 の TPL-16 の仕様 4)。壊れた画像でも例外を外に出さず、理由を返す。
/// パネルの表示は UI テスト (TC-TPL-16-04)。
/// </summary>
public sealed class ImageCodecTests
{
    [Fact]
    [Trait(TC, "TC-TPL-16-04")]
    public async Task Valid_png_decodes()
    {
        (DecodedImage? image, string? error) = await ImageCodec.DecodeAsync(TestDataCatalog.Png16());
        Assert.Null(error);
        Assert.NotNull(image);
        Assert.Equal(16, image.Width);
        Assert.Equal(16, image.Height);
        uint[] argb = ImageCodec.ToArgb(image);
        // TD-PNG の (1, 2) の画素は (16, 32, 24)。
        Assert.Equal(0xFF102018u, argb[2 * 16 + 1]);

        (image, error) = await ImageCodec.DecodeAsync(TestDataCatalog.TplBmp());
        Assert.Null(error);
        Assert.Equal(4, image!.Width);
    }

    [Fact]
    [Trait(TC, "TC-TPL-16-04")]
    public async Task Broken_and_truncated_pngs_never_throw()
    {
        (DecodedImage? broken, string? error) = await ImageCodec.DecodeAsync(TestDataCatalog.TplPngBroken());
        Assert.True(broken is null ? !string.IsNullOrEmpty(error) : broken.Width == 16);

        byte[] png = TestDataCatalog.Png16();
        var random = new Random(16_04);
        int decoded = 0, failed = 0;

        // 先頭 0〜2,000 バイトの長さで切り詰めたもの 50 個。
        for (int i = 0; i < 50; i++)
        {
            int length = Math.Min(png.Length, random.Next(0, 2001));
            (DecodedImage? image, string? reason) = await ImageCodec.DecodeAsync(png[..length]);
            if (image is null)
            {
                Assert.False(string.IsNullOrEmpty(reason));
                failed++;
            }
            else
            {
                decoded++;
            }
        }

        // 無作為な 1 バイトを変えたもの 200 個。
        for (int i = 0; i < 200; i++)
        {
            byte[] copy = [.. png];
            copy[random.Next(copy.Length)] ^= (byte)random.Next(1, 256);
            (DecodedImage? image, string? reason) = await ImageCodec.DecodeAsync(copy);
            if (image is null)
            {
                Assert.False(string.IsNullOrEmpty(reason));
                failed++;
            }
            else
            {
                decoded++;
            }
        }

        Assert.Equal(250, decoded + failed);
        Assert.True(failed > 0);
    }
}
