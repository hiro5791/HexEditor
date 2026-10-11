using System.Text;
using System.Text.Json;
using HexEditor.Core.Hashing;
using HexEditor.Core.Hashing.Crypto;
using HexEditor.Core.Tests.Engine;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Hashing;

/// <summary>
/// フェーズ 2 の暗号学的ハッシュの公式のテストベクタ (ANA-19 の受け入れ基準 2、TC-ANA-19-04)。値は
/// tests/fixtures/hash/vectors/*.json に出典名とともに写してある。
/// </summary>
public sealed class CryptoHashVectorTests
{
    private const string FixtureFolder = "tests/fixtures/hash/vectors";

    /// <summary>フィクスチャの 1 件。</summary>
    public sealed record Vector(string File, string Source, string Algorithm, byte[] Input, HashParameters Parameters, string Expected, bool Base32)
    {
        public override string ToString() => $"{File}: {Algorithm} ({Input.Length} bytes{(Parameters == HashParameters.Default ? "" : ", " + Parameters)})";
    }

    private static string Folder => Path.GetDirectoryName(SourceTests.FindRepoFile($"{FixtureFolder}/sha3.json"))!;

    private static readonly Lazy<IReadOnlyList<Vector>> s_vectors = new(Load);

    private static List<Vector> Load()
    {
        var list = new List<Vector>();
        foreach (string path in Directory.GetFiles(Folder, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            string source = doc.RootElement.GetProperty("source").GetString()!;
            foreach (JsonElement v in doc.RootElement.GetProperty("vectors").EnumerateArray())
            {
                var parameters = new HashParameters
                {
                    OutputBits = v.TryGetProperty("outputBits", out JsonElement bits) ? bits.GetInt32() : 0,
                    KeyHex = v.TryGetProperty("keyHex", out JsonElement key) ? key.GetString() : null,
                    Ed2k = v.TryGetProperty("ed2k", out JsonElement ed2k) && ed2k.GetString() == "red" ? Ed2kMode.Red : Ed2kMode.Blue,
                };
                bool base32 = v.TryGetProperty("format", out JsonElement format) && format.GetString() == "base32";
                list.Add(new Vector(Path.GetFileName(path), source, v.GetProperty("algorithm").GetString()!, Input(v), parameters,
                    v.GetProperty("expected").GetString()!, base32));
            }
        }

        return list;
    }

    private static byte[] Input(JsonElement v)
    {
        if (v.TryGetProperty("ascii", out JsonElement ascii))
        {
            return Encoding.ASCII.GetBytes(ascii.GetString()!);
        }

        if (v.TryGetProperty("hex", out JsonElement hex))
        {
            return Convert.FromHexString(hex.GetString()!);
        }

        if (v.TryGetProperty("repeat", out JsonElement repeat))
        {
            var data = new byte[repeat.GetProperty("count").GetInt32()];
            Array.Fill(data, (byte)repeat.GetProperty("byte").GetInt32());
            return data;
        }

        if (v.TryGetProperty("sequence", out JsonElement sequence))
        {
            return [.. Enumerable.Range(0, sequence.GetInt32()).Select(i => (byte)i)];
        }

        if (v.TryGetProperty("seq251", out JsonElement seq251))
        {
            return [.. Enumerable.Range(0, seq251.GetInt32()).Select(i => (byte)(i % 251))];
        }

        throw new InvalidDataException("入力の指定がありません。");
    }

    public static TheoryData<int> VectorIndexes()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i < s_vectors.Value.Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    private static string Hex(byte[] value) => Convert.ToHexString(value);

    /// <summary>不規則な長さに分けて渡す (0 バイトの Append も混ぜる)。</summary>
    internal static byte[] HashInPieces(HashAlgorithmInfo algorithm, byte[] data, int seed, HashParameters? parameters = null, int maxPiece = 300)
    {
        IHasher hasher = algorithm.CreateHasher(parameters);
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

    [Theory]
    [Trait(TC, "TC-ANA-19-04")]
    [MemberData(nameof(VectorIndexes))]
    public void PhaseTwoCryptographicHashesMatchOfficialVectors(int index)
    {
        Vector v = s_vectors.Value[index];
        HashAlgorithmInfo algorithm = HashCatalog.Get(v.Algorithm);
        Assert.Equal(HashGroup.Cryptographic, algorithm.Group);
        Assert.True(algorithm.IsAvailable);
        Assert.Equal(HashParameterError.None, algorithm.Validate(v.Parameters));
        string expected = v.Base32 ? Hex(Base32Decode(v.Expected)) : v.Expected;

        byte[] oneShot = HashEngine.ComputeBytes(algorithm, v.Input, v.Parameters);
        Assert.Equal(expected, Hex(oneShot));
        Assert.Equal(algorithm.BitsFor(v.Parameters), oneShot.Length * 8);
        int maxPiece = v.Input.Length > 100_000 ? 1 << 20 : 300;
        Assert.Equal(expected, Hex(HashInPieces(algorithm, v.Input, index, v.Parameters, maxPiece)));

        // SHA-3 系は Core の実装 (OS の SHA-3 が使えない環境の代わり) でも同じ値になる。
        if (v.Algorithm.StartsWith("sha3", StringComparison.Ordinal) || v.Algorithm.StartsWith("shake", StringComparison.Ordinal))
        {
            Sha3Provider.ForceManaged = true;
            try
            {
                Assert.Equal(expected, Hex(HashEngine.ComputeBytes(algorithm, v.Input, v.Parameters)));
                Assert.Equal(Sha3ImplementationKind.Managed, Sha3Provider.LastUsed);
            }
            finally
            {
                Sha3Provider.ForceManaged = false;
            }
        }
    }

    [Fact]
    [Trait(TC, "TC-ANA-19-04")]
    public void EveryPhaseTwoCryptographicAlgorithmHasEmptyAndAbcVectors()
    {
        string[] ids =
        [
            "md2", "md4", "sha224", "sha512-224", "sha512-256", "sha3-224", "sha3-256", "sha3-384", "sha3-512", "shake128", "shake256",
            "keccak256", "ripemd128", "ripemd160", "ripemd256", "ripemd320", "tiger", "tiger2", "whirlpool", "blake2s", "blake2b",
            "blake3", "tth", "ed2k",
        ];
        foreach (string id in ids)
        {
            IEnumerable<Vector> plain = s_vectors.Value.Where(v => v.Algorithm == id && v.Parameters.KeyHex is null);
            Assert.True(plain.Any(v => v.Input.Length == 0), $"{id}: 空のデータのベクタがない");
            if (id is not ("tth" or "ed2k"))
            {
                Assert.True(plain.Any(v => v.Input.SequenceEqual("abc"u8.ToArray())), $"{id}: abc のベクタがない");
            }

            Assert.All(s_vectors.Value.Where(v => v.Algorithm == id), v => Assert.False(string.IsNullOrWhiteSpace(v.Source)));
        }

        // 鍵付き・出力長のベクタ (TC-ANA-19-04 の手順 2)。
        Assert.Contains(s_vectors.Value, v => v.Algorithm == "blake2b" && v.Parameters.KeyHex is not null);
        Assert.Contains(s_vectors.Value, v => v.Algorithm == "blake2s" && v.Parameters.KeyHex is not null);
        Assert.Contains(s_vectors.Value, v => v.Algorithm == "blake3" && v.Parameters.KeyHex is not null);
        Assert.Contains(s_vectors.Value, v => v.Algorithm == "blake3" && v.Parameters.OutputBits >= 1024);
        foreach (string shake in new[] { "shake128", "shake256" })
        {
            Assert.Contains(s_vectors.Value, v => v.Algorithm == shake && v.Parameters.OutputBits == 8);
            Assert.Contains(s_vectors.Value, v => v.Algorithm == shake && v.Parameters.OutputBits == 8192);
        }
    }

    /// <summary>RFC 4648 の Base32 (パディングなし) を読む。</summary>
    internal static byte[] Base32Decode(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (char c in text.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }

        return [.. output];
    }
}
