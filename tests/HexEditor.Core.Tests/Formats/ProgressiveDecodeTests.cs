using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Formats.FormatTestSupport;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Formats;

/// <summary>ENG-38 の仕様 2: デコード中も、書き終えた先頭の部分を表示できる (TC-ENG-38-03 の機能の部分。1 GB の計測は性能の環境で行う)。</summary>
public sealed class ProgressiveDecodeTests
{
    [Fact]
    [Trait(TC, "TC-ENG-38-03")]
    public void Decoded_head_is_readable_before_the_decode_finishes()
    {
        // 6 MiB の乱数の Base64 (改行なし)。デコードの途中 (入力を半分以上読んだ時点) で、先頭の表示用のデータソースを取る。
        byte[] data = new byte[6 * 1024 * 1024];
        new Random(0xB64).NextBytes(data);
        Directory.CreateDirectory(TempDirectory);
        string path = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N") + ".b64");
        File.WriteAllText(path, Convert.ToBase64String(data));
        try
        {
            ImportProgress? progress = null;
            SparseImage? preview = null;
            long inputLength = new FileInfo(path).Length;
            progress = new ImportProgress(report: read =>
            {
                if (preview is null && read > inputLength / 2)
                {
                    preview = progress!.Preview("preview");
                }
            });

            using ImportResult result = Importer.DecodeFile(path, EncodedFile.OpenOptions(FormatIds.Base64) with { Format = FormatIds.Base64 },
                TempDirectory, progress);

            // 途中の表示: 先頭から一部 (全体より短い) で、内容が元のデータの先頭と一致する。読み取り専用 (書き込み先がない)。
            Assert.NotNull(preview);
            using (preview)
            {
                Assert.InRange(preview!.Length, 64, data.Length - 1);
                byte[] head = new byte[64];
                Assert.True(preview.Read(0, head).IsComplete);
                Assert.Equal(data.AsSpan(0, 64).ToArray(), head);
                Assert.False(preview.Capabilities.HasFlag(SourceCapabilities.CanWrite));
            }

            // 完了後の結果は全体で、SHA-256 が元のデータと一致する。
            using var doc = new Document(result.TakeImage());
            Assert.Equal(data.Length, doc.Length);
            byte[] all = new byte[data.Length];
            Assert.True(doc.Current.Read(0, all).IsComplete);
            Assert.Equal(SHA256.HashData(data), SHA256.HashData(all));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
