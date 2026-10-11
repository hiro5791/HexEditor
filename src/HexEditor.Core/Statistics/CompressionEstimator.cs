using System.IO.Compression;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;

namespace HexEditor.Core.Statistics;

/// <summary>試し圧縮の結果 (ANA-12 の仕様 3)。</summary>
public sealed record CompressionEstimate(long SampleBytes, long TotalBytes, long CompressedBytes)
{
    /// <summary>圧縮後のサイズ / 元のサイズ (%)。</summary>
    public double Ratio => SampleBytes == 0 ? 0 : CompressedBytes * 100.0 / SampleBytes;

    /// <summary>対象の全体を圧縮した (標本でない)。</summary>
    public bool IsWhole => SampleBytes >= TotalBytes;
}

/// <summary>
/// 試し圧縮で圧縮率を推定する (ANA-12 の仕様 3)。対象から 64 KB のブロックを均等な間隔で最大 1,024 個 (合計 64 MB) 取り出し、
/// Deflate (圧縮レベル 6) で圧縮する。対象が 64 MB 以下なら全体を圧縮する。読むのは最大 64 MB。
/// </summary>
public static class CompressionEstimator
{
    public const int SampleBlock = 64 * 1024;
    public const int MaxBlocks = 1024;
    public const long MaxSample = (long)SampleBlock * MaxBlocks;

    public static CompressionEstimate Estimate(DocumentSnapshot snapshot, IReadOnlyList<HashRange> ranges, LongRunningOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        CancellationToken token = operation?.CancellationToken ?? cancellationToken;
        var logical = new LogicalRanges(HashEngine.Normalize(ranges, snapshot.Length));
        long total = logical.Length;
        var pieces = new List<(long From, long To)>();
        if (total <= MaxSample)
        {
            pieces.Add((0, total));
        }
        else
        {
            // 均等な間隔で 1,024 個 (最後のブロックは末尾で終わる)。
            for (int i = 0; i < MaxBlocks; i++)
            {
                long at = (long)((Int128)(total - SampleBlock) * i / (MaxBlocks - 1));
                pieces.Add((at, at + SampleBlock));
            }
        }

        operation?.SetTotal(pieces.Sum(p => p.To - p.From));
        var counter = new CountingStream();
        long sampled = 0;
        using (var deflate = new DeflateStream(counter, new ZLibCompressionOptions { CompressionLevel = 6 }, leaveOpen: true))
        {
            var scanner = new RangeScanner(snapshot, logical, RangeScanner.DefaultChunkSize, token);
            foreach ((long from, long to) in pieces)
            {
                foreach (ScanChunk chunk in scanner.Read(from, to))
                {
                    token.ThrowIfCancellationRequested();
                    foreach ((int s, int l) in chunk.GoodParts())
                    {
                        deflate.Write(chunk.Buffer, s, l);
                        sampled += l;
                    }

                    operation?.Report(scanner.BytesRead);
                }
            }
        }

        return new CompressionEstimate(sampled, total, counter.Written);
    }

    /// <summary>書き込まれたバイト数だけを数える。</summary>
    private sealed class CountingStream : Stream
    {
        public long Written { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => Written;

        public override long Position { get => Written; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Written += count;

        public override void Write(ReadOnlySpan<byte> buffer) => Written += buffer.Length;
    }
}
