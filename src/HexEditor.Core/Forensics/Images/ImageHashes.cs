using System.Security.Cryptography;

namespace HexEditor.Core.Forensics.Images;

/// <summary>イメージの取得・検証のハッシュ (MD5、SHA-1、SHA-256。FOR-20 の仕様 3)。複数のアルゴリズムを 1 回の読み込みで計算する。</summary>
public sealed class ImageHashes : IDisposable
{
    public const string Md5 = "MD5";
    public const string Sha1 = "SHA-1";
    public const string Sha256 = "SHA-256";

    /// <summary>選べるアルゴリズム。</summary>
    public static IReadOnlyList<string> Algorithms { get; } = [Md5, Sha1, Sha256];

    /// <summary>既定 (MD5 と SHA-256)。</summary>
    public static IReadOnlyList<string> Default { get; } = [Md5, Sha256];

    private readonly List<(string Name, IncrementalHash Hash)> _hashes;

    public ImageHashes(IEnumerable<string> algorithms)
    {
        _hashes = [.. algorithms.Select(Normalize).Distinct(StringComparer.Ordinal).Select(a => (a, IncrementalHash.CreateHash(Of(a))))];
    }

    public IReadOnlyList<string> Names => [.. _hashes.Select(h => h.Name)];

    /// <summary>アルゴリズム名を正規化する ("sha256" → "SHA-256")。知らない名前は例外。</summary>
    public static string Normalize(string name) => name.Replace("-", string.Empty, StringComparison.Ordinal).ToUpperInvariant() switch
    {
        "MD5" => Md5,
        "SHA1" => Sha1,
        "SHA256" => Sha256,
        _ => throw new ArgumentException($"Unsupported hash algorithm {name}.", nameof(name)),
    };

    public static bool TryNormalize(string name, out string normalized)
    {
        try
        {
            normalized = Normalize(name);
            return true;
        }
        catch (ArgumentException)
        {
            normalized = string.Empty;
            return false;
        }
    }

    /// <summary>16 進の値の長さから推定するアルゴリズム (利用者が入力した値。32 桁 → MD5、40 → SHA-1、64 → SHA-256)。</summary>
    public static string? AlgorithmForLength(int hexDigits) => hexDigits switch
    {
        32 => Md5,
        40 => Sha1,
        64 => Sha256,
        _ => null,
    };

    private static HashAlgorithmName Of(string name) => name switch
    {
        Md5 => HashAlgorithmName.MD5,
        Sha1 => HashAlgorithmName.SHA1,
        _ => HashAlgorithmName.SHA256,
    };

    public void Append(ReadOnlySpan<byte> data)
    {
        foreach ((_, IncrementalHash hash) in _hashes)
        {
            hash.AppendData(data);
        }
    }

    /// <summary>0 を <paramref name="count"/> バイト足す (スパース・スケルトンの含めない範囲)。</summary>
    public void AppendZeros(long count)
    {
        byte[] zeros = Zeros;
        while (count > 0)
        {
            int n = (int)Math.Min(count, zeros.Length);
            Append(zeros.AsSpan(0, n));
            count -= n;
        }
    }

    private static readonly byte[] Zeros = new byte[1 << 20];

    /// <summary>計算した値 (アルゴリズム名 → 値)。</summary>
    public IReadOnlyDictionary<string, byte[]> Finish()
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach ((string name, IncrementalHash hash) in _hashes)
        {
            result[name] = hash.GetHashAndReset();
        }

        return result;
    }

    public void Dispose()
    {
        foreach ((_, IncrementalHash hash) in _hashes)
        {
            hash.Dispose();
        }
    }
}
