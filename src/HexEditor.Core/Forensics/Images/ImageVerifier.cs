using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Images;

/// <summary>検証の判定 (FOR-22 の仕様 3、「エラー」)。</summary>
public enum VerificationVerdict
{
    Match,
    Mismatch,
    NoReference,

    /// <summary>読み込みエラーのため検証できない (不一致とはしない)。</summary>
    ReadError,
}

/// <summary>比べる値の取得元 (FOR-22 の仕様 2)。</summary>
public enum ReferenceSource
{
    /// <summary>E01 に格納されたハッシュ。</summary>
    Stored,

    /// <summary>同じフォルダの取得ログ。</summary>
    AcquisitionLog,

    /// <summary>証拠一覧の値。</summary>
    Evidence,

    /// <summary>利用者が入力した値。</summary>
    User,

    /// <summary>取得の直後: 取得中に計算した値。</summary>
    Acquisition,

    /// <summary>入力元のディスクを読み直した値 (仕様 4)。</summary>
    SourceDisk,
}

/// <summary>比べる値 1 つ。</summary>
public sealed record ReferenceHash(string Algorithm, byte[] Value, ReferenceSource Source);

/// <summary>アルゴリズム 1 つの検証の結果。</summary>
public sealed record AlgorithmVerification(string Algorithm, byte[]? Computed, IReadOnlyList<(ReferenceHash Reference, bool Match)> Comparisons,
    VerificationVerdict Verdict);

/// <summary>検証の結果 (FOR-22)。</summary>
public sealed record VerificationResult(IReadOnlyList<AlgorithmVerification> Algorithms, IReadOnlyList<UnreadableRange> ReadErrors, DateTimeOffset At)
{
    /// <summary>全体の判定: どれかが不一致なら不一致、読み込みエラーがあれば読み込みエラー、すべて比べる値なしなら比べる値なし、それ以外は一致。</summary>
    public VerificationVerdict Overall =>
        ReadErrors.Count > 0 ? VerificationVerdict.ReadError
        : Algorithms.Any(a => a.Verdict == VerificationVerdict.Mismatch) ? VerificationVerdict.Mismatch
        : Algorithms.All(a => a.Verdict == VerificationVerdict.NoReference) ? VerificationVerdict.NoReference
        : VerificationVerdict.Match;

    public VerificationVerdict VerdictOf(string algorithm) =>
        Algorithms.FirstOrDefault(a => a.Algorithm == ImageHashes.Normalize(algorithm))?.Verdict ?? VerificationVerdict.NoReference;
}

/// <summary>イメージの検証 (FOR-22)。</summary>
public static class ImageVerifier
{
    /// <summary>
    /// イメージの比べる値を探す (仕様 2): E01 の格納値、同じフォルダの取得ログ (<c>&lt;名前&gt;.txt</c>)、証拠一覧の値、利用者が入力した値
    /// (16 進。長さでアルゴリズムを決める。<paramref name="userAlgorithm"/> で指定もできる)。
    /// </summary>
    public static IReadOnlyList<ReferenceHash> FindReferences(IImageSource image, string? userHex = null, string? userAlgorithm = null,
        IReadOnlyDictionary<string, byte[]>? evidence = null)
    {
        var refs = new List<ReferenceHash>();
        foreach ((string algorithm, byte[] value) in image.Info.StoredHashes)
        {
            refs.Add(new ReferenceHash(algorithm, value, ReferenceSource.Stored));
        }

        string first = image.Info.Files.Count > 0 ? image.Info.Files[0] : string.Empty;
        if (first.Length > 0 && AcquisitionLog.FindFor(first, image.Info.Format) is { } log)
        {
            foreach ((string algorithm, byte[] value) in AcquisitionLog.ReadHashes(log))
            {
                refs.Add(new ReferenceHash(algorithm, value, ReferenceSource.AcquisitionLog));
            }
        }

        if (evidence is not null)
        {
            foreach ((string algorithm, byte[] value) in evidence)
            {
                if (ImageHashes.TryNormalize(algorithm, out string a))
                {
                    refs.Add(new ReferenceHash(a, value, ReferenceSource.Evidence));
                }
            }
        }

        if (ParseUserHash(userHex, userAlgorithm) is { } user)
        {
            refs.Add(user);
        }

        return refs;
    }

    /// <summary>利用者が入力した 16 進の値 (空白・区切りを許す)。形が違えば null。</summary>
    public static ReferenceHash? ParseUserHash(string? hex, string? algorithm = null)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        string digits = new([.. hex.Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-')]);
        if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            digits = digits[2..];
        }

        string? a = algorithm is not null && ImageHashes.TryNormalize(algorithm, out string n) ? n : ImageHashes.AlgorithmForLength(digits.Length);
        if (a is null || digits.Length != (a == ImageHashes.Md5 ? 32 : a == ImageHashes.Sha1 ? 40 : 64) || !digits.All(char.IsAsciiHexDigit))
        {
            return null;
        }

        return new ReferenceHash(a, Convert.FromHexString(digits), ReferenceSource.User);
    }

    /// <summary>
    /// イメージの内容のハッシュを計算し直し、比べる値と比べる (仕様 1〜4)。複数のアルゴリズムは 1 回の読み込みで計算する。
    /// <paramref name="sourceDisk"/> を渡すと、入力元のディスクのハッシュも計算して比べる値に加える (3 者の比較。仕様 4)。
    /// </summary>
    public static VerificationResult Verify(IByteSource image, IEnumerable<string> algorithms, IReadOnlyList<ReferenceHash> references,
        LongRunningOperation? operation = null, IByteSource? sourceDisk = null, TimeProvider? time = null)
    {
        List<string> names = [.. algorithms.Select(ImageHashes.Normalize).Concat(references.Select(r => r.Algorithm)).Distinct()];
        long total = image.Length + (sourceDisk?.Length ?? 0);
        operation?.SetTotal(total);
        (IReadOnlyDictionary<string, byte[]> computed, List<UnreadableRange> errors) = Compute(image, names, operation, 0);
        var refs = new List<ReferenceHash>(references);
        if (sourceDisk is not null)
        {
            (IReadOnlyDictionary<string, byte[]> disk, List<UnreadableRange> diskErrors) = Compute(sourceDisk, names, operation, image.Length);
            if (diskErrors.Count == 0)
            {
                refs.AddRange(disk.Select(kv => new ReferenceHash(kv.Key, kv.Value, ReferenceSource.SourceDisk)));
            }
        }

        var rows = new List<AlgorithmVerification>();
        foreach (string name in names)
        {
            var comparisons = refs.Where(r => r.Algorithm == name).Select(r => (r, r.Value.AsSpan().SequenceEqual(computed[name]))).ToList();
            VerificationVerdict verdict = errors.Count > 0 ? VerificationVerdict.ReadError
                : comparisons.Count == 0 ? VerificationVerdict.NoReference
                : comparisons.All(c => c.Item2) ? VerificationVerdict.Match
                : VerificationVerdict.Mismatch;
            rows.Add(new AlgorithmVerification(name, computed[name], comparisons, verdict));
        }

        return new VerificationResult(rows, errors, (time ?? TimeProvider.System).GetUtcNow());
    }

    /// <summary>データソース全体のハッシュ (読めなかった範囲は 0 として計算し、範囲を返す)。</summary>
    public static (IReadOnlyDictionary<string, byte[]> Hashes, List<UnreadableRange> Errors) Compute(IByteSource source, IEnumerable<string> algorithms,
        LongRunningOperation? operation = null, long progressBase = 0)
    {
        using var hashes = new ImageHashes(algorithms);
        var errors = new List<UnreadableRange>();
        byte[] buffer = new byte[1 << 20];
        for (long pos = 0; pos < source.Length; pos += buffer.Length)
        {
            operation?.CancellationToken.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buffer.Length, source.Length - pos);
            ReadResult r = source.Read(pos, buffer.AsSpan(0, n));
            errors.AddRange(r.Unreadable);
            hashes.Append(buffer.AsSpan(0, n));
            operation?.Report(progressBase + pos + n);
        }

        return (hashes.Finish(), errors);
    }
}
