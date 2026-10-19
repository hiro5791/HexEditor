using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Cases;

/// <summary>
/// 証拠のハッシュ (FOR-03 の仕様 5)。ドキュメントの生のデータ (<see cref="DocumentSnapshot.Raw"/>。変換表示や編集の影響を受けない元のバイト列) を
/// 先頭から順に読み、メモリは固定 (チャンクの大きさ) でハッシュを求める。読めない範囲があれば <see cref="HashReadException"/>
/// (欠けたデータのハッシュを証拠のハッシュとして記録しない)。
/// </summary>
public static class EvidenceHasher
{
    /// <summary>既定の計算するアルゴリズム (仕様 5: SHA-256 と MD5)。</summary>
    public static readonly IReadOnlyList<string> DefaultAlgorithms = ["SHA-256", "MD5"];

    /// <summary>選べるアルゴリズム。</summary>
    public static readonly IReadOnlyList<string> Supported = ["SHA-256", "MD5", "SHA-1", "SHA-512"];

    public const int ChunkSize = 4 * 1024 * 1024;

    private static HashAlgorithmName NameOf(string algorithm) => algorithm.ToUpperInvariant() switch
    {
        "SHA-256" or "SHA256" => HashAlgorithmName.SHA256,
        "MD5" => HashAlgorithmName.MD5,
        "SHA-1" or "SHA1" => HashAlgorithmName.SHA1,
        "SHA-512" or "SHA512" => HashAlgorithmName.SHA512,
        _ => throw new ArgumentException($"Unsupported algorithm: {algorithm}", nameof(algorithm)),
    };

    /// <summary>スナップショットの生のデータ全体のハッシュ。<paramref name="progress"/> には読み終えたバイト数を渡す。</summary>
    public static IReadOnlyList<EvidenceHash> Compute(DocumentSnapshot snapshot, IEnumerable<string> algorithms, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        Compute(new SnapshotReader(snapshot.Raw), algorithms, progress, cancellationToken);

    /// <summary>範囲を読む口 (テスト用に差し替えられる)。</summary>
    public interface IRangeReader
    {
        long Length { get; }

        ReadResult Read(long offset, Span<byte> destination);
    }

    private sealed class SnapshotReader(DocumentSnapshot snapshot) : IRangeReader
    {
        public long Length => snapshot.Length;

        public ReadResult Read(long offset, Span<byte> destination) => snapshot.Read(offset, destination);
    }

    public static IReadOnlyList<EvidenceHash> Compute(IRangeReader reader, IEnumerable<string> algorithms, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string[] names = [.. algorithms];
        var hashers = new List<IncrementalHash>();
        try
        {
            foreach (string name in names)
            {
                hashers.Add(IncrementalHash.CreateHash(NameOf(name)));
            }

            byte[] buffer = new byte[ChunkSize];
            long position = 0, length = reader.Length;
            while (position < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = (int)Math.Min(buffer.Length, length - position);
                ReadResult result = reader.Read(position, buffer.AsSpan(0, count));
                if (result.Unreadable.Count > 0)
                {
                    throw new HashReadException(result.Unreadable[0]);
                }

                foreach (IncrementalHash hasher in hashers)
                {
                    hasher.AppendData(buffer, 0, result.BytesReturned);
                }

                if (result.BytesReturned <= 0)
                {
                    break;
                }

                position += result.BytesReturned;
                progress?.Report(position);
            }

            var list = new List<EvidenceHash>();
            for (int i = 0; i < names.Length; i++)
            {
                list.Add(new EvidenceHash(names[i], Convert.ToHexStringLower(hashers[i].GetHashAndReset())));
            }

            return list;
        }
        finally
        {
            foreach (IncrementalHash hasher in hashers)
            {
                hasher.Dispose();
            }
        }
    }
}
