using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;

namespace HexEditor.Core.Hashing.Crypto;

/// <summary>
/// Keccak のスポンジ (FIPS 202 の SHA3-224〜512、SHAKE128 / SHAKE256 と、パディングが元の Keccak の Keccak-256)。
/// OS の SHA-3 が使えない環境の代わりの実装も兼ねる (ANA-19 の仕様 5)。
/// </summary>
public sealed class KeccakHasher : BlockHasher
{
    /// <summary>SHA-3 のドメイン区切り (01 + パディングの先頭の 1)。</summary>
    public const byte Sha3Suffix = 0x06;

    /// <summary>SHAKE のドメイン区切り (1111 + パディングの先頭の 1)。</summary>
    public const byte ShakeSuffix = 0x1F;

    /// <summary>元の Keccak (Ethereum などの Keccak-256) のパディング。</summary>
    public const byte KeccakSuffix = 0x01;

    private static ReadOnlySpan<ulong> RoundConstants =>
    [
        0x0000000000000001, 0x0000000000008082, 0x800000000000808A, 0x8000000080008000,
        0x000000000000808B, 0x0000000080000001, 0x8000000080008081, 0x8000000000008009,
        0x000000000000008A, 0x0000000000000088, 0x0000000080008009, 0x000000008000000A,
        0x000000008000808B, 0x800000000000008B, 0x8000000000008089, 0x8000000000008003,
        0x8000000000008002, 0x8000000000000080, 0x000000000000800A, 0x800000008000000A,
        0x8000000080008081, 0x8000000000008080, 0x0000000080000001, 0x8000000080008008,
    ];

    private static ReadOnlySpan<byte> Rotations => [1, 3, 6, 10, 15, 21, 28, 36, 45, 55, 2, 14, 27, 41, 56, 8, 25, 43, 62, 18, 39, 61, 20, 44];

    private static ReadOnlySpan<byte> PiLanes => [10, 7, 11, 17, 18, 3, 5, 16, 8, 21, 24, 4, 15, 23, 19, 13, 12, 2, 20, 14, 22, 9, 6, 1];

    private readonly ulong[] _state = new ulong[25];
    private readonly byte _suffix;
    private readonly int _outputBytes;

    /// <param name="capacityBits">容量 (出力長の 2 倍。SHAKE128 は 256、SHAKE256 は 512)。</param>
    /// <param name="outputBits">出力のビット数 (8 の倍数)。</param>
    public KeccakHasher(int capacityBits, int outputBits, byte suffix)
        : base(200 - capacityBits / 8)
    {
        _suffix = suffix;
        _outputBytes = outputBits / 8;
    }

    /// <summary>SHA3-224 / 256 / 384 / 512。</summary>
    public static KeccakHasher Sha3(int bits) => new(2 * bits, bits, Sha3Suffix);

    /// <summary>SHAKE128 (variant 128) / SHAKE256 (variant 256)。</summary>
    public static KeccakHasher Shake(int variant, int outputBits) => new(2 * variant, outputBits, ShakeSuffix);

    /// <summary>元の Keccak (Keccak-256 など)。</summary>
    public static KeccakHasher Keccak(int bits) => new(2 * bits, bits, KeccakSuffix);

    protected override void ProcessBlock(ReadOnlySpan<byte> block)
    {
        for (int i = 0; i < block.Length / 8; i++)
        {
            _state[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(block[(8 * i)..]);
        }

        Permute(_state);
    }

    protected override byte[] FinishCore()
    {
        Span<byte> last = stackalloc byte[BlockSize];
        last.Clear();
        Pending.CopyTo(last);
        last[Pending.Length] ^= _suffix;
        last[^1] ^= 0x80;
        ProcessBlock(last);

        // 絞り出し (出力がレートより長ければ置換を繰り返す)。
        byte[] output = new byte[_outputBytes];
        int at = 0;
        Span<byte> lane = stackalloc byte[8];
        while (true)
        {
            for (int i = 0; i < BlockSize / 8 && at < output.Length; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(lane, _state[i]);
                int n = Math.Min(8, output.Length - at);
                lane[..n].CopyTo(output.AsSpan(at));
                at += n;
            }

            if (at >= output.Length)
            {
                return output;
            }

            Permute(_state);
        }
    }

    /// <summary>Keccak-f[1600]。</summary>
    private static void Permute(ulong[] st)
    {
        Span<ulong> bc = stackalloc ulong[5];
        ReadOnlySpan<ulong> rc = RoundConstants;
        ReadOnlySpan<byte> rot = Rotations;
        ReadOnlySpan<byte> pi = PiLanes;
        for (int round = 0; round < 24; round++)
        {
            // θ
            for (int i = 0; i < 5; i++)
            {
                bc[i] = st[i] ^ st[i + 5] ^ st[i + 10] ^ st[i + 15] ^ st[i + 20];
            }

            for (int i = 0; i < 5; i++)
            {
                ulong t = bc[(i + 4) % 5] ^ BitOperations.RotateLeft(bc[(i + 1) % 5], 1);
                for (int j = 0; j < 25; j += 5)
                {
                    st[j + i] ^= t;
                }
            }

            // ρ と π
            ulong carry = st[1];
            for (int i = 0; i < 24; i++)
            {
                int j = pi[i];
                ulong next = st[j];
                st[j] = BitOperations.RotateLeft(carry, rot[i]);
                carry = next;
            }

            // χ
            for (int j = 0; j < 25; j += 5)
            {
                ulong b0 = st[j], b1 = st[j + 1], b2 = st[j + 2], b3 = st[j + 3], b4 = st[j + 4];
                st[j] = b0 ^ (~b1 & b2);
                st[j + 1] = b1 ^ (~b2 & b3);
                st[j + 2] = b2 ^ (~b3 & b4);
                st[j + 3] = b3 ^ (~b4 & b0);
                st[j + 4] = b4 ^ (~b0 & b1);
            }

            // ι
            st[0] ^= rc[round];
        }
    }
}

/// <summary>SHA-3 の実装の種類。</summary>
public enum Sha3ImplementationKind
{
    /// <summary>.NET 標準 (OS の暗号ライブラリ)。</summary>
    Platform,

    /// <summary>Core の実装 (<see cref="KeccakHasher"/>)。</summary>
    Managed,
}

/// <summary>
/// SHA-3 と SHAKE の作成。.NET 標準の実装が使えればそれを使い、使えない環境 (Windows 10 など) では同じ結果を出す
/// <see cref="KeccakHasher"/> に切り替える (ANA-19 の仕様 5)。どちらの場合もアルゴリズムは使える。
/// </summary>
public static class Sha3Provider
{
    private static readonly AsyncLocal<bool> s_forceManaged = new();
    private static int s_logged;

    /// <summary>テスト用の切り替え: .NET 標準の SHA-3 を「使えない」として扱う (TC-ANA-19-08。この非同期の流れの中だけ)。</summary>
    internal static bool ForceManaged
    {
        get => s_forceManaged.Value;
        set => s_forceManaged.Value = value;
    }

    /// <summary>直前に作った SHA-3 / SHAKE の実装の種類 (ログの代わりに確認できるようにする。この非同期の流れの中だけ)。</summary>
    public static Sha3ImplementationKind LastUsed => s_lastUsed.Value;

    private static readonly AsyncLocal<Sha3ImplementationKind> s_lastUsed = new();

    /// <summary>.NET 標準の SHA3-256 / 384 / 512 が使えるか (SHA3-224 は .NET 標準にない)。</summary>
    public static bool PlatformSupports(int bits)
    {
        if (ForceManaged)
        {
            return false;
        }

        try
        {
            return bits switch
            {
                256 => SHA3_256.IsSupported,
                384 => SHA3_384.IsSupported,
                512 => SHA3_512.IsSupported,
                _ => false,
            };
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool PlatformSupportsShake(int variant)
    {
        if (ForceManaged)
        {
            return false;
        }

        try
        {
            return variant == 128 ? Shake128.IsSupported : Shake256.IsSupported;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>SHA3-224 / 256 / 384 / 512 の計算を始める。</summary>
    public static IHasher CreateSha3(int bits)
    {
        if (PlatformSupports(bits))
        {
            Record(Sha3ImplementationKind.Platform);
            return new IncrementalHasher(bits switch
            {
                256 => HashAlgorithmName.SHA3_256,
                384 => HashAlgorithmName.SHA3_384,
                _ => HashAlgorithmName.SHA3_512,
            });
        }

        Record(Sha3ImplementationKind.Managed);
        return KeccakHasher.Sha3(bits);
    }

    /// <summary>SHAKE128 / SHAKE256 の計算を始める。</summary>
    public static IHasher CreateShake(int variant, int outputBits)
    {
        if (PlatformSupportsShake(variant))
        {
            Record(Sha3ImplementationKind.Platform);
            return new PlatformShake(variant, outputBits / 8);
        }

        Record(Sha3ImplementationKind.Managed);
        return KeccakHasher.Shake(variant, outputBits);
    }

    private static void Record(Sha3ImplementationKind kind)
    {
        s_lastUsed.Value = kind;
        if (kind == Sha3ImplementationKind.Managed && Interlocked.Exchange(ref s_logged, 1) == 0)
        {
            Trace.WriteLine("SHA-3: .NET 標準の実装が使えないため Core の実装 (KeccakHasher) を使います。");
        }
    }

    /// <summary>.NET 標準の SHAKE。</summary>
    private sealed class PlatformShake : IHasher
    {
        private readonly Shake128? _shake128;
        private readonly Shake256? _shake256;
        private readonly int _outputBytes;

        public PlatformShake(int variant, int outputBytes)
        {
            _outputBytes = outputBytes;
            if (variant == 128)
            {
                _shake128 = new Shake128();
            }
            else
            {
                _shake256 = new Shake256();
            }
        }

        public void Append(ReadOnlySpan<byte> data)
        {
            _shake128?.AppendData(data);
            _shake256?.AppendData(data);
        }

        public byte[] Finish()
        {
            byte[] value = _shake128 is { } s128 ? s128.GetHashAndReset(_outputBytes) : _shake256!.GetHashAndReset(_outputBytes);
            _shake128?.Dispose();
            _shake256?.Dispose();
            return value;
        }
    }
}
