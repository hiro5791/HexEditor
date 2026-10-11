using System.Globalization;
using System.Text.Json;
using HexEditor.Core.Hashing;

namespace HexEditor.Core.Tests.Hashing;

/// <summary>tests/fixtures/hash のフィクスチャと、ハッシュのテストの補助。</summary>
internal static class HashFixtures
{
    public static readonly byte[] Check9 = "123456789"u8.ToArray();

    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "hash", name);

    public static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(PathOf(name)));

    /// <summary>CRC カタログの 1 項目 (crc-catalogue.json)。</summary>
    public sealed record CatalogueEntry(string Name, int Width, ulong Poly, ulong Init, bool RefIn, bool RefOut, ulong XorOut, ulong Check,
        ulong Residue, IReadOnlyList<string> Aliases)
    {
        public CrcParameters Parameters => new(Width, Poly, Init, RefIn, RefOut, XorOut, Check);

        public override string ToString() => Name;
    }

    private static readonly Lazy<IReadOnlyList<CatalogueEntry>> CatalogueEntries = new(() =>
    {
        using JsonDocument doc = Load("crc-catalogue.json");
        // カタログには幅 64 を超える項目 (CRC-82/DARC) もあるが、対応する幅 (1〜64) の項目だけを使う。
        return [.. doc.RootElement.GetProperty("entries").EnumerateArray().Where(e => e.GetProperty("width").GetInt32() <= 64).Select(e => new CatalogueEntry(
            e.GetProperty("name").GetString()!,
            e.GetProperty("width").GetInt32(),
            Hex(e.GetProperty("poly")),
            Hex(e.GetProperty("init")),
            e.GetProperty("refin").GetBoolean(),
            e.GetProperty("refout").GetBoolean(),
            Hex(e.GetProperty("xorout")),
            Hex(e.GetProperty("check")),
            Hex(e.GetProperty("residue")),
            [.. e.GetProperty("aliases").EnumerateArray().Select(a => a.GetString()!)]))];
    });

    /// <summary>CRC カタログの幅 64 以下の全項目 (幅 3〜64)。</summary>
    public static IReadOnlyList<CatalogueEntry> Catalogue => CatalogueEntries.Value;

    public static ulong Hex(JsonElement e) => ulong.Parse(e.GetString()!.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    /// <summary>1 回で渡して計算する (長さが先に必要なアルゴリズムには長さを宣言する)。</summary>
    public static byte[] Compute(HashAlgorithmInfo algorithm, ReadOnlySpan<byte> data, HashParameters? parameters = null) =>
        HashEngine.ComputeBytes(algorithm, data, parameters);

    /// <summary>不規則な長さに分けて渡して計算する (0 バイトの Append も含む)。</summary>
    public static byte[] ComputeInPieces(HashAlgorithmInfo algorithm, byte[] data, int seed, HashParameters? parameters = null, int maxPiece = 70)
    {
        IHasher hasher = algorithm.CreateHasher(parameters);
        (hasher as ILengthPrefixedHasher)?.DeclareLength(data.Length);
        var random = new Random(seed);
        int at = 0;
        while (at < data.Length)
        {
            int n = Math.Min(data.Length - at, random.Next(0, maxPiece));
            hasher.Append(data.AsSpan(at, n));
            at += n;
        }

        return hasher.Finish();
    }

    /// <summary>xxHash の sanity_test_vectors.h の入力 (XSUM_fillTestBuffer)。</summary>
    public static byte[] XxHashSanityBuffer(int length)
    {
        byte[] buffer = new byte[length];
        ulong byteGen = 2654435761UL;
        for (int i = 0; i < length; i++)
        {
            buffer[i] = (byte)(byteGen >> 56);
            byteGen = unchecked(byteGen * 11400714785074694797UL);
        }

        return buffer;
    }
}
