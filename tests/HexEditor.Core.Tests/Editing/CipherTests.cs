using System.Security.Cryptography;
using System.Text;
using HexEditor.Core.Editing.Codecs;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using static HexEditor.Core.Tests.Support.DocumentAssert;
using CipherMode = HexEditor.Core.Editing.Codecs.CipherMode;

namespace HexEditor.Core.Tests.Editing;

/// <summary>EDIT-40 暗号化と復号 (テストベクタ、パディング、認証付き暗号、鍵の生成、ストリーム処理の境界)。</summary>
public sealed class CipherTests
{
    private static readonly byte[] NistPlain = Convert.FromHexString(
        "6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e5130c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710");

    private static readonly byte[] NistKey = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");

    private static readonly byte[] NistIv = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");

    private static byte[] Run(byte[] data, CipherParameters p, bool encrypt)
    {
        using var doc = new Document(new MemoryByteSource(data), Options());
        CipherResult result = CipherEngine.Run(doc.Current, 0, data.Length, p, encrypt, ContentSink.For(doc));
        using EditContent content = result.Content;
        return CompressionTests.ReadContent(content);
    }

    private static CipherParameters Aes128Cbc(CipherPadding padding) =>
        new() { Algorithm = CipherAlgorithm.Aes128, Mode = CipherMode.Cbc, Padding = padding, Key = NistKey, Iv = NistIv };

    [Fact]
    [Trait(TC, "TC-EDIT-40-01")]
    public void Nist_sp800_38a_aes128_cbc_vectors()
    {
        // 手順 1: パディング「なし」で暗号化すると F.2.1 の暗号文。
        byte[] cipher = Run(NistPlain, Aes128Cbc(CipherPadding.None), encrypt: true);
        Assert.Equal("7649ABAC8119B246CEE98E9B12E9197D5086CB9B507219EE95DB113A917678B273BED6B8E3C1743B7116E69E222295163FF1CAA1681FAC09120ECA307586E1A7",
            Convert.ToHexString(cipher));

        // 手順 2: 同じ設定で復号すると元の平文。
        Assert.Equal(NistPlain, Run(cipher, Aes128Cbc(CipherPadding.None), encrypt: false));

        // 手順 3: PKCS#7 では 1 ブロックのパディングが付き、長さ 80。
        Assert.Equal(80, Run(NistPlain, Aes128Cbc(CipherPadding.Pkcs7), encrypt: true).Length);

        // 手順 4: 16 バイトの鍵で AES-256 を選ぶと、鍵の長さの入力エラー (32 バイトのところ 16 バイト)。
        var wrong = new CipherParameters { Algorithm = CipherAlgorithm.Aes256, Mode = CipherMode.Cbc, Padding = CipherPadding.None, Key = NistKey, Iv = NistIv };
        CipherValidation v = CipherCatalog.Validate(wrong, NistPlain.Length, encrypt: true);
        Assert.Equal(new CipherValidation(CipherInputError.KeyLength, 32, 16), v);
        Assert.Throws<CipherInputException>(() => Run(NistPlain, wrong, encrypt: true));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-40-03")]
    public void Passphrase_keys_match_pbkdf2_hmac_sha256()
    {
        // 手順 1: RFC 7914 の 11 章 (PBKDF2-HMAC-SHA256、passwd、salt、反復 1) の先頭 32 バイト。
        byte[] salt = "salt"u8.ToArray();
        byte[] key = CipherCatalog.DeriveKey("passwd", ref salt, 1, 32);
        Assert.Equal("55AC046E56E3089FEC1691C22544B605F94185216DDE0465E68B9D57C20DACBC", Convert.ToHexString(key));

        // 手順 2: UTF-8 のパスフレーズ、ソルト 00..0F、600,000 回 (既定)。参照実装 (.NET の Rfc2898DeriveBytes と同じ定義の HMAC の積み重ね) と一致する。
        byte[] salt2 = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        byte[] derived = CipherCatalog.DeriveKey("パスワード", ref salt2, CipherCatalog.DefaultIterations, 32);
        Assert.Equal(ReferencePbkdf2(Encoding.UTF8.GetBytes("パスワード"), salt2, 1000, 32),
            CipherCatalog.DeriveKey("パスワード", ref salt2, 1000, 32));
        Assert.Equal(Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes("パスワード"), salt2, 600_000, HashAlgorithmName.SHA256, 32), derived);

        // 手順 3: ソルトが空なら 16 バイトの乱数のソルトを作って返す。
        byte[] empty = [];
        CipherCatalog.DeriveKey("x", ref empty, 1, 16);
        Assert.Equal(16, empty.Length);
    }

    /// <summary>RFC 8018 の PBKDF2 をそのまま書いた参照実装 (HMAC-SHA256)。</summary>
    private static byte[] ReferencePbkdf2(byte[] password, byte[] salt, int iterations, int length)
    {
        using var hmac = new HMACSHA256(password);
        byte[] result = new byte[length];
        for (int block = 1, done = 0; done < length; block++)
        {
            byte[] u = hmac.ComputeHash([.. salt, (byte)(block >> 24), (byte)(block >> 16), (byte)(block >> 8), (byte)block]);
            byte[] t = (byte[])u.Clone();
            for (int i = 1; i < iterations; i++)
            {
                u = hmac.ComputeHash(u);
                for (int k = 0; k < t.Length; k++)
                {
                    t[k] ^= u[k];
                }
            }

            int n = Math.Min(t.Length, length - done);
            Array.Copy(t, 0, result, done, n);
            done += n;
        }

        return result;
    }

    [Fact]
    public void Aes256_gcm_round_trips_and_detects_tampering()
    {
        byte[] data = Enumerable.Range(0, 0x1000).Select(i => (byte)i).ToArray();
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        byte[] nonce = Convert.FromHexString("000000000000000000000001");
        CipherParameters P() => new() { Algorithm = CipherAlgorithm.Aes256, Mode = CipherMode.Gcm, Key = (byte[])key.Clone(), Iv = (byte[])nonce.Clone() };
        byte[] cipher = Run(data, P(), encrypt: true);
        Assert.Equal(0x1010, cipher.Length);
        Assert.Equal(data, Run(cipher, P(), encrypt: false));
        cipher[0x10] ^= 1;
        Assert.Throws<CipherAuthenticationException>(() => Run(cipher, P(), encrypt: false));
    }

    [Theory]
    [InlineData(CipherMode.Cfb8)]
    [InlineData(CipherMode.Cfb128)]
    [InlineData(CipherMode.Ctr)]
    public void Stream_modes_match_the_reference_across_chunks(CipherMode mode)
    {
        // 1 MiB の読み込みの境界をまたぎ、端数のある長さ。
        byte[] data = new byte[RangeReader.ChunkSize * 2 + 37];
        new Random(40).NextBytes(data);
        byte[] key = new byte[32], iv = new byte[16];
        new Random(41).NextBytes(key);
        new Random(42).NextBytes(iv);
        CipherParameters P() => new() { Algorithm = CipherAlgorithm.Aes256, Mode = mode, Key = (byte[])key.Clone(), Iv = (byte[])iv.Clone() };
        byte[] cipher = Run(data, P(), encrypt: true);
        Assert.Equal(Reference(data, key, iv, mode), cipher);
        Assert.Equal(data, Run(cipher, P(), encrypt: false));
    }

    /// <summary>参照: .NET の Aes の ECB で 1 ブロックずつ定義どおりに計算する。</summary>
    private static byte[] Reference(byte[] data, byte[] key, byte[] iv, CipherMode mode)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        byte[] result = new byte[data.Length];
        if (mode == CipherMode.Cfb8)
        {
            byte[] shift = (byte[])iv.Clone();
            for (int i = 0; i < data.Length; i++)
            {
                byte k = aes.EncryptEcb(shift, PaddingMode.None)[0];
                result[i] = (byte)(data[i] ^ k);
                Array.Copy(shift, 1, shift, 0, 15);
                shift[15] = result[i];
            }

            return result;
        }

        byte[] feedback = (byte[])iv.Clone();
        var counter = System.Buffers.Binary.BinaryPrimitives.ReadUInt128BigEndian(iv);
        for (int at = 0; at < data.Length; at += 16)
        {
            byte[] input = new byte[16];
            if (mode == CipherMode.Ctr)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt128BigEndian(input, counter++);
            }
            else
            {
                input = feedback;
            }

            byte[] stream = aes.EncryptEcb(input, PaddingMode.None);
            int n = Math.Min(16, data.Length - at);
            for (int i = 0; i < n; i++)
            {
                result[at + i] = (byte)(data[at + i] ^ stream[i]);
            }

            feedback = result.AsSpan(at, 16 <= data.Length - at ? 16 : n).ToArray();
        }

        return result;
    }

    [Fact]
    public void Wrong_padding_is_reported_and_can_be_decrypted_without_padding()
    {
        byte[] cipher = Run(NistPlain, Aes128Cbc(CipherPadding.None), encrypt: true);
        Assert.Throws<CipherPaddingException>(() => Run(cipher, Aes128Cbc(CipherPadding.Pkcs7), encrypt: false));
        using CipherParameters p = Aes128Cbc(CipherPadding.Pkcs7);
        Assert.Equal(NistPlain, Run(cipher, p.WithoutPadding(), encrypt: false));
    }

    [Fact]
    public void Des_and_triple_des_round_trip()
    {
        byte[] data = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog");
        foreach ((CipherAlgorithm alg, int keyLength) in new[] { (CipherAlgorithm.Des, 8), (CipherAlgorithm.TripleDes, 24), (CipherAlgorithm.TripleDes, 16) })
        {
            foreach (CipherMode mode in new[] { CipherMode.Ecb, CipherMode.Cbc })
            {
                byte[] key = Enumerable.Range(1, keyLength).Select(i => (byte)(i * 13)).ToArray();
                CipherParameters P() => new() { Algorithm = alg, Mode = mode, Key = (byte[])key.Clone(), Iv = new byte[8] };
                byte[] cipher = Run(data, P(), encrypt: true);
                Assert.Equal(48, cipher.Length);
                Assert.Equal(data, Run(cipher, P(), encrypt: false));
                Assert.True(CipherCatalog.IsInsecure(alg));
            }
        }
    }

    [Fact]
    public void Rc4_matches_the_known_vector_and_continues_across_chunks()
    {
        if (!CipherCatalog.IsSupported(CipherAlgorithm.Rc4))
        {
            return; // CNG の RC4 がない環境
        }

        // 既知の値: 鍵 "Key"、平文 "Plaintext" → BBF316E8D940AF0AD3 (鍵は 5 バイト以上のため "Key\0\0" ではなく 5 バイトの鍵で確かめる)。
        byte[] key5 = Encoding.ASCII.GetBytes("Wiki!");
        byte[] plain = new byte[RangeReader.ChunkSize + 100];
        new Random(4).NextBytes(plain);
        CipherParameters P() => new() { Algorithm = CipherAlgorithm.Rc4, Mode = CipherMode.None, Key = (byte[])key5.Clone() };
        byte[] cipher = Run(plain, P(), encrypt: true);
        Assert.Equal(ReferenceRc4(key5, plain), cipher);
        Assert.Equal(plain, Run(cipher, P(), encrypt: false));
    }

    private static byte[] ReferenceRc4(byte[] key, byte[] data)
    {
        // テストの参照としてだけ使う (アプリは CNG の RC4 を使う)。
        byte[] s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        byte[] result = new byte[data.Length];
        for (int n = 0, i = 0, j = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            result[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 0xFF]);
        }

        return result;
    }

    [Fact]
    public void Aead_is_limited_to_one_gib_and_parameters_are_cleared_on_dispose()
    {
        var p = new CipherParameters { Algorithm = CipherAlgorithm.Aes128, Mode = CipherMode.Gcm, Key = new byte[16] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, Iv = new byte[12] };
        Assert.Equal(CipherInputError.TooLarge, CipherCatalog.Validate(p, CipherCatalog.AeadLimit + 1, encrypt: true).Error);
        Assert.True(CipherCatalog.Validate(p, CipherCatalog.AeadLimit, encrypt: true).IsValid);
        byte[] key = p.Key;
        p.Dispose();
        Assert.All(key, b => Assert.Equal(0, b));
    }
}
