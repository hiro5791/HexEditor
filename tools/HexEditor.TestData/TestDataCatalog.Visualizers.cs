using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HexEditor.Core.Security;

namespace HexEditor.TestData;

// cases/07-templates.md の表のうち、ビジュアライザ (TPL-16) とオンラインリポジトリ (TPL-36) のテストデータ。
public static partial class TestDataCatalog
{
    private static IEnumerable<TestDataItem> VisualizerItems() =>
    [
        new("TD-TPL-BMP", 118, "4×4 の 32 bit の BMP (高さ −4、BI_RGB)", path => WriteAll(path, TplBmp()), dir => Path.Combine(dir, "TD-TPL-BMP.bmp")),
        new("TD-TPL-WAV", 88_244, "1 秒、44,100 Hz、モノラル、PCM s16le の 440 Hz の正弦波", path => WriteAll(path, TplWav()), dir => Path.Combine(dir, "TD-TPL-WAV.wav")),
        new("TD-TPL-WAV-HXT", Encoding.UTF8.GetByteCount(TplWavHxt), "テンプレート wav-audio.hxt", path => File.WriteAllText(path, TplWavHxt, new UTF8Encoding(false)),
            dir => Path.Combine(dir, "TD-TPL-WAV-HXT", "wav-audio.hxt")),
        new("TD-TPL-PNG-BROKEN", Png16().Length, "TD-PNG の IDAT のデータの先頭から 32 バイトを FF にしたもの (CRC は直さない)", path => WriteAll(path, TplPngBroken()),
            dir => Path.Combine(dir, "TD-TPL-PNG-BROKEN.png")),
        new("TD-TPL-SECRET", 4096, "HXSECRET7F3A を 1 回含む 4 KiB (ファイル名も HXSECRET7F3A-report.bin)", path => WriteAll(path, TplSecret()),
            dir => Path.Combine(dir, "TD-TPL-SECRET", TplSecretMarker + "-report.bin")),
        new("TD-TPL-REPO", 0, "テスト用のオンラインリポジトリ (フォルダ。index.json など)", path => TplRepo.WriteAll(Path.GetDirectoryName(path)!),
            dir => Path.Combine(dir, "TD-TPL-REPO", "index.json")) { ComputeLength = () => TplRepo.Index("1.0.0").Length },
    ];

    /// <summary>TD-TPL-SECRET の目印 (TC-TPL-36-05)。</summary>
    public const string TplSecretMarker = "HXSECRET7F3A";

    /// <summary>TD-TPL-BMP の画素 (行 0〜3、左から。0xAARRGGBB)。</summary>
    public static uint[] TplBmpExpected()
    {
        uint[] row0 = [0xFFFF0000, 0xFF00FF00, 0xFF0000FF, 0xFFFFFFFF];
        uint[] row1 = [0xFF000000, 0xFF808080, 0xFFFFFF00, 0xFF00FFFF];
        return [.. row0, .. row1, .. row0.Reverse(), .. row1.Reverse()];
    }

    /// <summary>TD-TPL-BMP: BITMAPFILEHEADER 14 バイト、BITMAPINFOHEADER 40 バイト (高さ −4、32 bit、BI_RGB)、画素 64 バイト (B、G、R、0)。</summary>
    public static byte[] TplBmp()
    {
        byte[] bmp = new byte[118];
        Span<byte> b = bmp;
        "BM"u8.CopyTo(b);
        BinaryPrimitives.WriteInt32LittleEndian(b[2..], 118);
        BinaryPrimitives.WriteInt32LittleEndian(b[10..], 0x36);
        BinaryPrimitives.WriteInt32LittleEndian(b[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(b[18..], 4);
        BinaryPrimitives.WriteInt32LittleEndian(b[22..], -4);
        BinaryPrimitives.WriteInt16LittleEndian(b[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(b[28..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(b[30..], 0); // BI_RGB
        BinaryPrimitives.WriteInt32LittleEndian(b[34..], 64);
        uint[] pixels = TplBmpExpected();
        for (int i = 0; i < 16; i++)
        {
            int o = 0x36 + i * 4;
            bmp[o] = (byte)pixels[i];
            bmp[o + 1] = (byte)(pixels[i] >> 8);
            bmp[o + 2] = (byte)(pixels[i] >> 16);
            bmp[o + 3] = 0;
        }

        // 末尾の 10 バイトは 0 (ファイルの長さ 118 バイト)。
        return bmp;
    }

    /// <summary>TD-TPL-WAV のサンプル n の値 (round(16383 × sin(2π × 440 × n / 44100)))。</summary>
    public static short TplWavSample(int n) => (short)Math.Round(16383 * Math.Sin(2 * Math.PI * 440 * n / 44100.0), MidpointRounding.AwayFromZero);

    public static byte[] TplWav()
    {
        const int Samples = 44_100;
        byte[] wav = new byte[44 + Samples * 2];
        Span<byte> w = wav;
        "RIFF"u8.CopyTo(w);
        BinaryPrimitives.WriteInt32LittleEndian(w[4..], wav.Length - 8);
        "WAVE"u8.CopyTo(w[8..]);
        "fmt "u8.CopyTo(w[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(w[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(w[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(w[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(w[24..], 44_100);
        BinaryPrimitives.WriteInt32LittleEndian(w[28..], 88_200);
        BinaryPrimitives.WriteInt16LittleEndian(w[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(w[34..], 16);
        "data"u8.CopyTo(w[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(w[40..], Samples * 2);
        for (int n = 0; n < Samples; n++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(w[(44 + n * 2)..], TplWavSample(n));
        }

        return wav;
    }

    /// <summary>TD-TPL-WAV-HXT: RIFF・fmt のチャンクを読み、data のチャンクの中身に audio のビジュアライザを付けたテンプレート。</summary>
    public const string TplWavHxt = """
        #pragma name "WAV audio (test)"
        #pragma magic 0 "52 49 46 46"
        struct Chunk {
            char id[4];
            le u32 size;
            if (id == "fmt ") {
                le u16 format;
                le u16 channels;
                le u32 rate;
                le u32 byteRate;
                le u16 blockAlign;
                le u16 bits;
            } else if (id == "data") {
                u8 data[size] [[visualize("audio", 44100, 1, "pcm_s16le")]];
            } else {
                u8 body[size];
            }
        };
        char riff[4];
        le u32 riffSize;
        char wave[4];
        Chunk chunks[eof];
        """;

    /// <summary>TD-TPL-PNG-BROKEN: TD-PNG の IDAT のデータの先頭から 32 バイトを FF にしたもの (CRC は直さない)。</summary>
    public static byte[] TplPngBroken()
    {
        byte[] png = Png16();
        int at = 8;
        while (at + 8 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            if (png.AsSpan(at + 4, 4).SequenceEqual("IDAT"u8))
            {
                png.AsSpan(at + 8, Math.Min(32, length)).Fill(0xFF);
                break;
            }

            at += 12 + length;
        }

        return png;
    }

    public static byte[] TplSecret()
    {
        byte[] data = new byte[4096];
        Random(SeedOf("TD-TPL-SECRET"), 0, data);
        Encoding.ASCII.GetBytes(TplSecretMarker).CopyTo(data, 0x200);
        return data;
    }

    /// <summary>
    /// TD-TPL-REPO: テスト用のオンラインリポジトリ。テスト用の Ed25519 の鍵 (秘密鍵はこのテスト用のプロジェクトにだけある) で署名する。
    /// </summary>
    public static class TplRepo
    {
        /// <summary>テスト用の鍵の種 (秘密鍵)。</summary>
        private static readonly byte[] Seed = SHA256.HashData("TD-TPL-REPO test signing key"u8);

        private static readonly byte[] OtherSeed = SHA256.HashData("TD-TPL-REPO other key"u8);

        /// <summary>公式の鍵の代わりに使う公開鍵 (テスト用のビルドに渡す)。</summary>
        public static byte[] PublicKey => Ed25519.PublicKeyFromSeed(Seed);

        public static string PublicKeyBase64 => Convert.ToBase64String(PublicKey);

        public const string TemplateId = "hxtest";

        public const string DisplayName = "HXTest format";

        public const string License = "MIT";

        public static string Template(string version) => $"""
            #pragma name "{DisplayName}"
            #pragma version "{version}"
            #pragma magic 0 "48 58 54 53"
            char magic[4];
            u8 body[12];
            """;

        public static byte[] TemplateBytes(string version) => Encoding.UTF8.GetBytes(Template(version));

        /// <summary>索引 (<paramref name="versions"/> の版を含む)。</summary>
        public static byte[] Index(params string[] versions)
        {
            var templates = new JsonArray();
            foreach (string v in versions)
            {
                byte[] file = TemplateBytes(v);
                templates.Add(new JsonObject
                {
                    ["id"] = TemplateId,
                    ["name"] = new JsonObject { ["en"] = DisplayName, ["ja"] = DisplayName },
                    ["description"] = new JsonObject { ["en"] = "Test format for the online repository", ["ja"] = "オンラインリポジトリのテスト用の形式" },
                    ["author"] = "HexEditor tests",
                    ["version"] = v,
                    ["license"] = License,
                    ["categories"] = new JsonArray("test"),
                    ["extensions"] = new JsonArray("hxts"),
                    ["mime"] = new JsonArray(),
                    ["minAppVersion"] = "0.1.0",
                    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(file)),
                    ["size"] = file.Length,
                    ["kind"] = "hxt",
                    ["usesExtensions"] = false,
                    ["file"] = $"{TemplateId}-{v}.hxt",
                });
            }

            var root = new JsonObject { ["format"] = 1, ["name"] = "HexEditor test repository", ["templates"] = templates };
            return Encoding.UTF8.GetBytes(root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }

        public static byte[] Sign(byte[] data) => Ed25519.Sign(Seed, data);

        public static byte[] SignWithOtherKey(byte[] data) => Ed25519.Sign(OtherSeed, data);

        /// <summary>
        /// フォルダに書く: <c>index.json</c> (1.0.0) と <c>index.json.sig</c>、<c>index-v1.1.json</c> (1.0.0 と 1.1.0) と署名、テンプレート 2 つ、
        /// 改ざんした版 (<c>tampered/index-byte.json</c> (署名は元のまま)、<c>tampered/index-otherkey.json</c> (別の鍵の署名)、
        /// <c>tampered/hxtest-1.0.0.hxt</c> (1 バイト変えたもの))。
        /// </summary>
        public static void WriteAll(string folder)
        {
            Directory.CreateDirectory(folder);
            byte[] index = Index("1.0.0");
            byte[] index11 = Index("1.0.0", "1.1.0");
            File.WriteAllBytes(Path.Combine(folder, "index.json"), index);
            File.WriteAllText(Path.Combine(folder, "index.json.sig"), Convert.ToBase64String(Sign(index)));
            File.WriteAllBytes(Path.Combine(folder, "index-v1.1.json"), index11);
            File.WriteAllText(Path.Combine(folder, "index-v1.1.json.sig"), Convert.ToBase64String(Sign(index11)));
            File.WriteAllBytes(Path.Combine(folder, "hxtest-1.0.0.hxt"), TemplateBytes("1.0.0"));
            File.WriteAllBytes(Path.Combine(folder, "hxtest-1.1.0.hxt"), TemplateBytes("1.1.0"));
            string tampered = Path.Combine(folder, "tampered");
            Directory.CreateDirectory(tampered);
            File.WriteAllBytes(Path.Combine(tampered, "index-byte.json"), Tamper(index));
            File.WriteAllText(Path.Combine(tampered, "index-byte.json.sig"), Convert.ToBase64String(Sign(index)));
            File.WriteAllBytes(Path.Combine(tampered, "index-otherkey.json"), index);
            File.WriteAllText(Path.Combine(tampered, "index-otherkey.json.sig"), Convert.ToBase64String(SignWithOtherKey(index)));
            File.WriteAllBytes(Path.Combine(tampered, "hxtest-1.0.0.hxt"), Tamper(TemplateBytes("1.0.0")));
        }

        /// <summary>1 バイトを変える (中ほどの文字の下位ビットを反転)。</summary>
        public static byte[] Tamper(byte[] data)
        {
            byte[] copy = [.. data];
            copy[copy.Length / 2] ^= 0x01;
            return copy;
        }
    }
}
