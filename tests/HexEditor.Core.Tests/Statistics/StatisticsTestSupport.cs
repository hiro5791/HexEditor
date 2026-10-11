using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Tests.Support;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Statistics;

/// <summary>統計のテストの共通の道具。</summary>
internal static class StatisticsTestSupport
{
    public const long KiB = 1024;
    public const long MiB = 1024 * KiB;
    public const long GiB = 1024 * MiB;

    public static Document Doc(byte[] data) => new(new MemoryByteSource(data), DocumentAssert.Options());

    public static Document Doc(FakeByteSource source) => new(source, DocumentAssert.Options());

    public static Document File(string id) => new(new MemoryByteSource(System.IO.File.ReadAllBytes(TestDataCatalog.Get(id))), DocumentAssert.Options());

    /// <summary>内容を計算するデータソース (巨大な長さでもメモリを使わない。読み込みを記録する)。</summary>
    public static FakeByteSource Virtual(long length, Action<long, Span<byte>> content) => new(length, content, SourceCapabilities.CanResize);

    /// <summary>TD-ANA-HALF-2G の内容: 前半 1 GiB は 00、後半 1 GiB は AES-CTR の鍵ストリーム。</summary>
    public static void HalfZeroHalfRandom(long offset, Span<byte> destination)
    {
        long half = GiB;
        int zeros = (int)Math.Clamp(half - offset, 0, destination.Length);
        destination[..zeros].Clear();
        if (zeros < destination.Length)
        {
            TestDataCatalog.AesCtr(TestDataCatalog.CryptKey, offset + zeros - half, destination[zeros..]);
        }
    }
}
