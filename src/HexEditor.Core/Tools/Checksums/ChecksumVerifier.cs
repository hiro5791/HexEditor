using System.Security.Cryptography;
using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Tools.Checksums;

/// <summary>検証の結果の状態 (TOOL-19 の仕様 3)。</summary>
public enum ChecksumStatus
{
    Match,
    Mismatch,
    Missing,
    ReadError,
}

/// <summary>結果一覧の 1 行。列: 状態、パス、期待値、計算値、サイズ。</summary>
public sealed record ChecksumResultRow(
    int LineNumber,
    string Path,
    string FullPath,
    ChecksumStatus Status,
    string Expected,
    string? Actual,
    long? Size,
    string AlgorithmId,
    string? Error = null);

/// <summary>件数の要約 (例: 「一致 120、不一致 1、見つからない 2」。TOOL-19 の仕様 4)。</summary>
public sealed record ChecksumSummary(int Match, int Mismatch, int Missing, int ReadError)
{
    public int Total => Match + Mismatch + Missing + ReadError;

    public static ChecksumSummary Of(IEnumerable<ChecksumResultRow> rows)
    {
        int[] counts = new int[4];
        foreach (ChecksumResultRow row in rows)
        {
            counts[(int)row.Status]++;
        }

        return new ChecksumSummary(counts[0], counts[1], counts[2], counts[3]);
    }
}

/// <summary>チェックサムファイルの検証 (TOOL-19)。ファイルを順に計算し、確定した行から <c>onRow</c> に渡す。</summary>
public static class ChecksumVerifier
{
    /// <summary>
    /// 検証する。<paramref name="baseDirectory"/> はチェックサムファイルのあるフォルダ。<paramref name="fallbackAlgorithmId"/> は
    /// アルゴリズムを判定できない行に使う (利用者が選んだもの)。
    /// </summary>
    public static IReadOnlyList<ChecksumResultRow> Verify(ChecksumDocument document, string baseDirectory, string? fallbackAlgorithmId = null,
        Action<ChecksumResultRow>? onRow = null, LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        long total = 0;
        foreach (ChecksumLineEntry entry in document.Entries)
        {
            string path = Resolve(baseDirectory, entry.Path);
            if (File.Exists(path))
            {
                try
                {
                    total += new FileInfo(path).Length;
                }
                catch (IOException)
                {
                }
            }
        }

        operation?.SetTotal(total);
        var rows = new List<ChecksumResultRow>(document.Entries.Count);
        long done = 0;
        foreach (ChecksumLineEntry entry in document.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation?.ReportDetail(entry.Path);
            string full = Resolve(baseDirectory, entry.Path);
            string algorithmId = entry.AlgorithmId ?? fallbackAlgorithmId ?? string.Empty;
            string expected = Convert.ToHexStringLower(entry.Value);
            ChecksumResultRow row;
            if (!File.Exists(full))
            {
                row = new ChecksumResultRow(entry.LineNumber, entry.Path, full, ChecksumStatus.Missing, expected, null, null, algorithmId);
            }
            else if (HashCatalog.Find(algorithmId) is not { } algorithm)
            {
                row = new ChecksumResultRow(entry.LineNumber, entry.Path, full, ChecksumStatus.ReadError, expected, null, null, algorithmId, "Unknown algorithm");
            }
            else
            {
                long size = 0;
                try
                {
                    size = new FileInfo(full).Length;
                    byte[] actual = FileHashing.Compute(full, algorithm, operation, done, cancellationToken);
                    done += size;
                    operation?.Report(done);
                    row = new ChecksumResultRow(entry.LineNumber, entry.Path, full,
                        CryptographicOperations.FixedTimeEquals(actual, entry.Value) ? ChecksumStatus.Match : ChecksumStatus.Mismatch,
                        expected, Convert.ToHexStringLower(actual), size, algorithm.Id);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    done += size;
                    row = new ChecksumResultRow(entry.LineNumber, entry.Path, full, ChecksumStatus.ReadError, expected, null, size, algorithm.Id, ex.Message);
                }
            }

            rows.Add(row);
            onRow?.Invoke(row);
        }

        return rows;
    }

    /// <summary>チェックサムファイルに書かれたパス (<c>/</c> でも <c>\</c> でもよい) を、フォルダからの完全なパスにする。</summary>
    public static string Resolve(string baseDirectory, string entryPath)
    {
        string normalized = entryPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(baseDirectory, normalized));
    }
}
