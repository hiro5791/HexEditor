using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace HexEditor.Platform.Imaging;

/// <summary>デコードした画像 (BGRA8、不透明度を乗算していない値、上の行から)。</summary>
public sealed record DecodedImage(int Width, int Height, byte[] Bgra, bool Truncated, string Format);

/// <summary>
/// Windows の画像コーデック (WIC) による画像のファイル形式のデコード (07 の TPL-16 の <c>image</c>。PNG、JPEG、GIF、BMP、ICO、TIFF、WebP)。
/// デコードの失敗は例外にせず理由を返す (仕様 4: アプリを落とさない)。幅・高さは 16,384 画素までにし、超える場合は左上を切り出す (仕様 3)。
/// </summary>
public static class ImageCodec
{
    public const int MaxSide = 16_384;

    /// <summary>デコードする。失敗したら <c>(null, 理由)</c>。</summary>
    public static async Task<(DecodedImage? Image, string? Error)> DecodeAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (data.Length == 0)
        {
            return (null, "no data");
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(data.AsBuffer()).AsTask(cancellationToken).ConfigureAwait(false);
            stream.Seek(0);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
            uint width = decoder.PixelWidth;
            uint height = decoder.PixelHeight;
            if (width == 0 || height == 0)
            {
                return (null, "the image has no pixels");
            }

            uint shownWidth = Math.Min(width, MaxSide);
            uint shownHeight = Math.Min(height, MaxSide);
            var transform = new BitmapTransform();
            if (shownWidth < width || shownHeight < height)
            {
                transform.Bounds = new BitmapBounds { X = 0, Y = 0, Width = shownWidth, Height = shownHeight };
            }

            PixelDataProvider pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);
            byte[] bgra = pixels.DetachPixelData();
            if (bgra.Length < (long)shownWidth * shownHeight * 4)
            {
                return (null, "the decoder returned too few pixels");
            }

            string format = decoder.DecoderInformation?.FriendlyName ?? string.Empty;
            return (new DecodedImage((int)shownWidth, (int)shownHeight, bgra, shownWidth < width || shownHeight < height, format), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 壊れたデータ・対応しない形式 (WINCODEC_ERR_*) など。
            string reason = ex.Message.Trim();
            return (null, reason.Length > 0 ? reason : $"0x{ex.HResult:X8}");
        }
    }

    /// <summary>BGRA8 の画素を 0xAARRGGBB にする (PNG の保存に使う)。</summary>
    public static uint[] ToArgb(DecodedImage image)
    {
        uint[] argb = new uint[image.Width * image.Height];
        byte[] b = image.Bgra;
        for (int i = 0; i < argb.Length; i++)
        {
            int o = i * 4;
            argb[i] = ((uint)b[o + 3] << 24) | ((uint)b[o + 2] << 16) | ((uint)b[o + 1] << 8) | b[o];
        }

        return argb;
    }
}
