using System.Buffers.Binary;
using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Editing.Codecs;

/// <summary>暗号化・復号の結果。<see cref="Tag"/> は暗号化で「別の欄に表示するだけ」を選んだときの認証タグ。</summary>
public sealed record CipherResult(EditContent Content, byte[]? Tag);

/// <summary>復号でパディングが正しくない (EDIT-40 の「エラー」。「パディングなしで復号する」を付ける)。</summary>
public sealed class CipherPaddingException(Exception inner) : CryptographicException("パディングが正しくありません。", inner);

/// <summary>認証タグが一致しない (EDIT-40 の「エラー」。データは変えない)。</summary>
public sealed class CipherAuthenticationException(Exception inner) : CryptographicException("認証に失敗しました。", inner);

/// <summary>設定が正しくない (<see cref="CipherCatalog.Validate"/> の結果)。</summary>
public sealed class CipherInputException(CipherValidation validation) : ArgumentException(validation.Error.ToString())
{
    public CipherValidation Validation { get; } = validation;
}

/// <summary>
/// 暗号化・復号 (EDIT-40)。.NET の System.Security.Cryptography (Windows では CNG) を使い、自前の暗号の部品は持たない。CTR・CFB128 の端数は
/// ECB の暗号化で鍵ストリームを作る (仕様 1「CTR は ECB を使って実装する」)。ECB・CBC・CFB・CTR・RC4 は 1 MiB 単位のストリーム処理で、
/// 認証付き暗号は 1 GiB までを一括で処理する。結果は <see cref="ContentSink"/> に作り、完了後に呼び出し側が 1 回で反映する。
/// </summary>
public static class CipherEngine
{
    public static CipherResult Run(DocumentSnapshot snapshot, long offset, long length, CipherParameters p, bool encrypt, ContentSink sink,
        LongRunningOperation? operation = null)
    {
        CipherValidation validation = CipherCatalog.Validate(p, length, encrypt);
        if (!validation.IsValid)
        {
            sink.Dispose();
            throw new CipherInputException(validation);
        }

        try
        {
            var reader = new RangeReader(snapshot, offset, length, operation);
            var output = new CodecOutput(sink);
            byte[]? tag = null;
            if (CipherCatalog.IsAuthenticated(p.Algorithm, p.Mode))
            {
                tag = RunAead(reader, p, encrypt, output);
            }
            else if (p.Algorithm == CipherAlgorithm.Rc4)
            {
                RunRc4(reader, p, output);
            }
            else if (p.Mode == CipherMode.Ctr)
            {
                RunCtr(reader, p, output);
            }
            else if (p.Mode == CipherMode.Cfb128)
            {
                RunCfb128(reader, p, encrypt, output);
            }
            else
            {
                RunBlockMode(reader, p, encrypt, output);
            }

            return new CipherResult(sink.Complete(), tag);
        }
        catch
        {
            sink.Dispose();
            throw;
        }
    }

    private static SymmetricAlgorithm Create(CipherParameters p)
    {
        SymmetricAlgorithm algorithm = p.Algorithm switch
        {
            CipherAlgorithm.TripleDes => TripleDES.Create(),
            CipherAlgorithm.Des => DES.Create(),
            _ => Aes.Create(),
        };
        algorithm.Key = p.Key;
        return algorithm;
    }

    private static PaddingMode DotNetPadding(CipherPadding padding) => padding switch
    {
        CipherPadding.Zeros => PaddingMode.Zeros,
        CipherPadding.AnsiX923 => PaddingMode.ANSIX923,
        CipherPadding.Iso10126 => PaddingMode.ISO10126,
        CipherPadding.None => PaddingMode.None,
        _ => PaddingMode.PKCS7,
    };

    /// <summary>ECB・CBC・CFB8 (.NET の変換をそのまま 1 MiB ずつ流す)。</summary>
    private static void RunBlockMode(RangeReader reader, CipherParameters p, bool encrypt, CodecOutput output)
    {
        using SymmetricAlgorithm algorithm = Create(p);
        algorithm.Mode = p.Mode switch
        {
            CipherMode.Ecb => System.Security.Cryptography.CipherMode.ECB,
            CipherMode.Cbc => System.Security.Cryptography.CipherMode.CBC,
            _ => System.Security.Cryptography.CipherMode.CFB,
        };
        if (p.Mode == CipherMode.Cfb8)
        {
            algorithm.FeedbackSize = 8;
            algorithm.Padding = PaddingMode.None;
        }
        else
        {
            algorithm.Padding = DotNetPadding(p.Padding);
        }

        if (p.Mode != CipherMode.Ecb)
        {
            algorithm.IV = p.Iv;
        }

        using ICryptoTransform transform = encrypt ? algorithm.CreateEncryptor() : algorithm.CreateDecryptor();
        using var stream = new CryptoStream(output.AsStream(), transform, CryptoStreamMode.Write, leaveOpen: true);
        byte[] buffer = new byte[RangeReader.ChunkSize];
        int n;
        while ((n = reader.Read(buffer)) > 0)
        {
            stream.Write(buffer, 0, n);
        }

        try
        {
            stream.FlushFinalBlock();
        }
        catch (CryptographicException e) when (!encrypt)
        {
            throw new CipherPaddingException(e);
        }
    }

    /// <summary>CTR: IV を 128 ビットのビッグエンディアンのカウンタとして 1 ずつ増やし、ECB で暗号化した鍵ストリームと XOR する。</summary>
    private static void RunCtr(RangeReader reader, CipherParameters p, CodecOutput output)
    {
        using var aes = (Aes)Create(p);
        int block = CipherCatalog.BlockSize(p.Algorithm);
        UInt128 counter = BinaryPrimitives.ReadUInt128BigEndian(p.Iv);
        byte[] buffer = new byte[RangeReader.ChunkSize];
        byte[] counters = new byte[RangeReader.ChunkSize];
        byte[] stream = new byte[RangeReader.ChunkSize];
        int n;
        while ((n = reader.Read(buffer)) > 0)
        {
            int blocks = (n + block - 1) / block;
            for (int i = 0; i < blocks; i++)
            {
                BinaryPrimitives.WriteUInt128BigEndian(counters.AsSpan(i * block), counter);
                counter++;
            }

            aes.EncryptEcb(counters.AsSpan(0, blocks * block), stream, PaddingMode.None);
            for (int i = 0; i < n; i++)
            {
                buffer[i] ^= stream[i];
            }

            output.Write(buffer.AsSpan(0, n));
        }

        CryptographicOperations.ZeroMemory(stream);
    }

    /// <summary>CFB128: ブロック長の倍数は .NET の CFB で、最後の端数は前の暗号文ブロックを ECB で暗号化した鍵ストリームと XOR する。</summary>
    private static void RunCfb128(RangeReader reader, CipherParameters p, bool encrypt, CodecOutput output)
    {
        using var aes = (Aes)Create(p);
        aes.Mode = System.Security.Cryptography.CipherMode.CFB;
        aes.FeedbackSize = 128;
        aes.Padding = PaddingMode.None;
        aes.IV = p.Iv;
        using ICryptoTransform transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        byte[] feedback = (byte[])p.Iv.Clone();
        byte[] buffer = new byte[RangeReader.ChunkSize];
        byte[] result = new byte[RangeReader.ChunkSize];
        int n;
        while ((n = reader.Read(buffer)) > 0)
        {
            int full = n / 16 * 16;
            if (full > 0)
            {
                transform.TransformBlock(buffer, 0, full, result, 0);
                (encrypt ? result : buffer).AsSpan(full - 16, 16).CopyTo(feedback);
                output.Write(result.AsSpan(0, full));
            }

            if (full < n)
            {
                // 端数は範囲の最後だけ (読み込みは 1 MiB 単位で、16 の倍数)。
                byte[] stream = aes.EncryptEcb(feedback, PaddingMode.None);
                for (int i = full; i < n; i++)
                {
                    result[i] = (byte)(buffer[i] ^ stream[i - full]);
                }

                output.Write(result.AsSpan(full, n - full));
            }
        }
    }

    private static void RunRc4(RangeReader reader, CipherParameters p, CodecOutput output)
    {
        using var rc4 = new Rc4Cng(p.Key);
        byte[] buffer = new byte[RangeReader.ChunkSize];
        byte[] result = new byte[RangeReader.ChunkSize];
        int n;
        while ((n = reader.Read(buffer)) > 0)
        {
            rc4.Transform(buffer.AsSpan(0, n), result.AsSpan(0, n));
            output.Write(result.AsSpan(0, n));
        }
    }

    /// <summary>GCM・ChaCha20-Poly1305 (一括処理。1 GiB まで)。復号でタグが一致しなければ何も書かない。</summary>
    private static byte[]? RunAead(RangeReader reader, CipherParameters p, bool encrypt, CodecOutput output)
    {
        byte[] input = new byte[reader.Length];
        int done = 0, n;
        while ((n = reader.Read(input.AsSpan(done))) > 0)
        {
            done += n;
        }

        byte[] tag = new byte[CipherCatalog.TagLength];
        ReadOnlySpan<byte> data = input;
        if (!encrypt)
        {
            if (p.TagFromSelectionEnd)
            {
                data[^CipherCatalog.TagLength..].CopyTo(tag);
                data = data[..^CipherCatalog.TagLength];
            }
            else
            {
                p.Tag!.CopyTo(tag, 0);
            }
        }

        byte[] result = new byte[data.Length];
        try
        {
            if (p.Algorithm == CipherAlgorithm.ChaCha20Poly1305)
            {
                using var chacha = new ChaCha20Poly1305(p.Key);
                if (encrypt)
                {
                    chacha.Encrypt(p.Iv, data, result, tag, p.Aad);
                }
                else
                {
                    chacha.Decrypt(p.Iv, data, tag, result, p.Aad);
                }
            }
            else
            {
                using var gcm = new AesGcm(p.Key, CipherCatalog.TagLength);
                if (encrypt)
                {
                    gcm.Encrypt(p.Iv, data, result, tag, p.Aad);
                }
                else
                {
                    gcm.Decrypt(p.Iv, data, tag, result, p.Aad);
                }
            }
        }
        catch (AuthenticationTagMismatchException e)
        {
            CryptographicOperations.ZeroMemory(result);
            throw new CipherAuthenticationException(e);
        }

        output.Write(result);
        if (encrypt && p.AppendTag)
        {
            output.Write(tag);
            return null;
        }

        return encrypt ? tag : null;
    }
}
