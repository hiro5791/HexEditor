using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using HexEditor.Core.Engine;
using HexEditor.Core.Hashing;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Statistics;

/// <summary>ブロックの分類 (ANA-16 の仕様 2)。</summary>
public enum DataClass : byte
{
    /// <summary>まだ分類していない (キャンセルした範囲)。</summary>
    None,

    /// <summary>定数 (最頻値が 0x00 のもの)。</summary>
    Zero,

    Constant,
    Text,
    Encrypted,
    Compressed,
    Binary,
    Unreadable,
}

/// <summary>判定の確度 (ANA-16 の仕様 3)。</summary>
public enum Confidence : byte
{
    Low,
    Medium,
    High,
}

/// <summary>判定のしきい値 (ANA-16 の仕様 2、6)。</summary>
public sealed record ClassThresholds
{
    /// <summary>定数: 最頻値の割合の下限。</summary>
    public double ConstantShare { get; init; } = 0.99;

    /// <summary>テキスト: 印字可能 ASCII と空白類 (または UTF-8 / UTF-16 として妥当な) 割合の下限。</summary>
    public double TextShare { get; init; } = 0.95;

    /// <summary>暗号化・乱数: エントロピーの下限と p 値の範囲。</summary>
    public double EncryptedEntropy { get; init; } = 7.9;

    public double PValueLow { get; init; } = 0.01;

    public double PValueHigh { get; init; } = 0.99;

    /// <summary>圧縮: エントロピーの下限。</summary>
    public double CompressedEntropy { get; init; } = 7.2;
}

/// <summary>分類の指定。</summary>
public sealed record ClassifyRequest
{
    public const int MinBlockSize = 512;
    public const int MaxBlockSize = 1024 * 1024;

    public IReadOnlyList<HashRange> Ranges { get; init; } = [];

    /// <summary>ブロックの大きさ (512 バイト〜1 MB の 2 の累乗。既定 4 KB)。</summary>
    public int BlockSize { get; init; } = 4096;

    public ClassThresholds Thresholds { get; init; } = new();
}

/// <summary>同じ分類のブロックが続く区間 (ANA-16 の仕様 3)。オフセットはドキュメント上の位置。</summary>
public sealed record ClassRegion(long Offset, long Length, DataClass Class, double MeanEntropy, Confidence Confidence, long LogicalStart);

/// <summary>見つかった圧縮・暗号化形式のシグネチャ (ANA-16 の仕様 4)。</summary>
public sealed record SignatureHit(long Offset, string Format, bool IsCompression, Confidence Confidence, long? StreamLength = null);

/// <summary>分類の結果。</summary>
public sealed class ClassificationResult
{
    public required LogicalRanges Ranges { get; init; }

    public required int BlockSize { get; init; }

    /// <summary>ブロックごとの分類 (1 ブロック 1 バイト)。</summary>
    public required DataClass[] Classes { get; init; }

    public required IReadOnlyList<ClassRegion> Regions { get; init; }

    public required IReadOnlyList<SignatureHit> Signatures { get; init; }

    public bool Completed { get; init; }

    public double Fraction { get; init; }

    public required UnreadableSummary Unreadable { get; init; }

    /// <summary>分類ごとのバイト数 (帯の割合)。</summary>
    public long BytesOf(DataClass c) => Regions.Where(r => r.Class == c).Sum(r => r.Length);
}

/// <summary>
/// 暗号化・圧縮データの検出 (ANA-16)。対象をブロックごとに調べて分類し、同じ分類が続く部分を区間にまとめる。同じ読み込みで既知の
/// 圧縮・暗号化形式のシグネチャを探す。zlib・gzip は展開できることを確かめ、展開できたストリームの範囲の高エントロピーのブロックは
/// 「圧縮」とする (統計だけでは圧縮と暗号化を区別しにくいため)。メモリはブロック数に比例する (1 ブロック 4 バイト)。
/// </summary>
public static class DataClassifier
{
    /// <summary>展開してストリームの終わりを確かめる長さの上限 (圧縮されたデータの長さ)。</summary>
    public const long MaxVerifyLength = 256L * 1024 * 1024;

    /// <summary>ストリームの終わりを確かめる単位 (この精度で終わりが分かる)。</summary>
    private const int VerifyStep = 512;

    private static readonly ConditionalWeakTable<Document, ClassificationResult> Latest = new();

    /// <summary>
    /// ドキュメント全体の最新の分類の結果 (ミニマップの「分類」レイヤ (ANA-16 の仕様 7、VIEW-35) が読む)。なければ null。
    /// </summary>
    public static ClassificationResult? LatestFor(Document document) => Latest.TryGetValue(document, out ClassificationResult? r) ? r : null;

    /// <summary>最新の結果として記録する (統計パネルが計算したとき)。</summary>
    public static void Remember(Document document, ClassificationResult result) => Latest.AddOrUpdate(document, result);

    /// <summary>
    /// 分類する。キャンセルした場合は、処理済みの範囲の結果を持つ結果を返す (<see cref="ClassificationResult.Completed"/> が false)。
    /// </summary>
    public static ClassificationResult Classify(DocumentSnapshot snapshot, ClassifyRequest request, LongRunningOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        CancellationToken token = operation?.CancellationToken ?? cancellationToken;
        int blockSize = (int)StatMath.CeilPowerOfTwo(Math.Clamp(request.BlockSize, ClassifyRequest.MinBlockSize, ClassifyRequest.MaxBlockSize),
            ClassifyRequest.MinBlockSize);
        var ranges = new LogicalRanges(HashEngine.Normalize(request.Ranges, snapshot.Length));
        long length = ranges.Length;
        int count = (int)Math.Min(int.MaxValue - 64, (length + blockSize - 1) / blockSize);
        var classes = new DataClass[count];
        var entropy = new Half[count];
        var confidence = new Confidence[count];
        var signatures = new List<SignatureHit>();
        var scanner = new RangeScanner(snapshot, ranges, Math.Max(RangeScanner.DefaultChunkSize, blockSize), token);
        operation?.SetTotal(length);
        long processed = 0;
        bool completed = true;
        byte[] pending = new byte[blockSize];
        int pendingFill = 0;
        int pendingBad = 0;
        try
        {
            foreach (ScanChunk chunk in scanner.Read())
            {
                token.ThrowIfCancellationRequested();
                FindSignatures(snapshot, ranges, chunk, signatures, token);
                ClassifyChunk(chunk, blockSize, request.Thresholds, classes, entropy, confidence, pending, ref pendingFill, ref pendingBad);
                processed += chunk.Length;
                operation?.Report(processed);
            }

            if (pendingFill > 0)
            {
                int index = (int)((length - pendingFill) / blockSize);
                ClassifyBlock(pending.AsSpan(0, pendingFill), pendingBad, request.Thresholds, out classes[index], out entropy[index], out confidence[index]);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            completed = false;
        }

        // 展開できた zlib・gzip のストリームの範囲の高エントロピーのブロックは「圧縮」とする。
        foreach (SignatureHit hit in signatures.Where(h => h.IsCompression && h.StreamLength is > 0))
        {
            if (ranges.ToLogical(hit.Offset) is not long start)
            {
                continue;
            }

            long end = Math.Min(length, start + hit.StreamLength!.Value);
            for (int i = (int)(start / blockSize); i < count && (long)i * blockSize < end; i++)
            {
                long bs = (long)i * blockSize;
                long be = Math.Min(length, bs + blockSize);
                long covered = Math.Min(be, end) - Math.Max(bs, start);
                if (covered * 2 >= be - bs && classes[i] is DataClass.Encrypted or DataClass.Compressed or DataClass.Binary && (double)entropy[i] >= 6)
                {
                    classes[i] = DataClass.Compressed;
                    confidence[i] = Confidence.High;
                }
            }
        }

        Smooth(classes, length, blockSize);
        return new ClassificationResult
        {
            Ranges = ranges,
            BlockSize = blockSize,
            Classes = classes,
            Regions = MergeRegions(ranges, blockSize, classes, entropy, confidence),
            Signatures = signatures,
            Completed = completed,
            Fraction = length == 0 ? 1 : Math.Clamp((double)processed / length, 0, 1),
            Unreadable = scanner.Unreadable.Clone(),
        };
    }

    private static void ClassifyChunk(ScanChunk chunk, int blockSize, ClassThresholds t, DataClass[] classes, Half[] entropy, Confidence[] confidence,
        byte[] pending, ref int pendingFill, ref int pendingBad)
    {
        int at = 0;
        long logical = chunk.Logical;
        bool[]? badMask = chunk.HasBad ? BadMask(chunk) : null;

        // 前の読み込みの途中のブロック (連結の境目) を埋める。
        if (pendingFill > 0)
        {
            int take = Math.Min(blockSize - pendingFill, chunk.Length);
            chunk.Span[..take].CopyTo(pending.AsSpan(pendingFill));
            pendingBad += badMask is null ? 0 : badMask.AsSpan(0, take).Count(true);
            pendingFill += take;
            at = take;
            if (pendingFill == blockSize)
            {
                int index = (int)((logical + take - blockSize) / blockSize);
                ClassifyBlock(pending, pendingBad, t, out classes[index], out entropy[index], out confidence[index]);
                pendingFill = 0;
                pendingBad = 0;
            }
            else
            {
                return;
            }
        }

        // ここで at はブロックの境界 (連結の境目の端数は pending で埋めた)。
        int full = (chunk.Length - at) / blockSize;
        int startAt = at;
        Parallel.For(0, full, k =>
        {
            int offset = startAt + (k * blockSize);
            int index = (int)((logical + offset) / blockSize);
            int bad = badMask is null ? 0 : badMask.AsSpan(offset, blockSize).Count(true);
            ClassifyBlock(chunk.Buffer.AsSpan(offset, blockSize), bad, t, out classes[index], out entropy[index], out confidence[index]);
        });

        at += full * blockSize;
        if (at < chunk.Length)
        {
            int rest = chunk.Length - at;
            chunk.Span[at..].CopyTo(pending);
            pendingFill = rest;
            pendingBad = badMask is null ? 0 : badMask.AsSpan(at, rest).Count(true);
        }
    }

    private static bool[] BadMask(ScanChunk chunk)
    {
        bool[] mask = new bool[chunk.Length];
        foreach ((int s, int l) in chunk.Bad)
        {
            mask.AsSpan(s, l).Fill(true);
        }

        return mask;
    }

    /// <summary>1 ブロックの分類 (ANA-16 の仕様 2 の順に判定する)。</summary>
    internal static void ClassifyBlock(ReadOnlySpan<byte> block, int bad, ClassThresholds t, out DataClass result, out Half blockEntropy,
        out Confidence conf)
    {
        if (bad > 0)
        {
            result = DataClass.Unreadable;
            blockEntropy = Half.Zero;
            conf = Confidence.High;
            return;
        }

        Span<int> counts = stackalloc int[256];
        foreach (byte b in block)
        {
            counts[b]++;
        }

        int n = block.Length;
        double h = StatMath.Entropy(counts, n);
        blockEntropy = (Half)h;
        int modeValue = 0;
        int text = 0;
        for (int v = 0; v < 256; v++)
        {
            if (counts[v] > counts[modeValue])
            {
                modeValue = v;
            }

            if (StatMath.IsPrintable((byte)v) || StatMath.IsWhitespace((byte)v))
            {
                text += counts[v];
            }
        }

        double modeShare = (double)counts[modeValue] / n;
        if (modeShare >= t.ConstantShare)
        {
            result = modeValue == 0 ? DataClass.Zero : DataClass.Constant;
            conf = modeShare >= 0.999 ? Confidence.High : modeShare >= 0.995 ? Confidence.Medium : Confidence.Low;
            return;
        }

        double textShare = Math.Max((double)text / n, Math.Max(Utf8Share(block), Utf16Share(block)));
        if (textShare >= t.TextShare)
        {
            result = DataClass.Text;
            conf = textShare >= 0.99 ? Confidence.High : textShare >= 0.97 ? Confidence.Medium : Confidence.Low;
            return;
        }

        double p = StatMath.ChiSquarePValue(StatMath.ChiSquareUniform(counts, n), 255);
        if (h >= t.EncryptedEntropy && p >= t.PValueLow && p <= t.PValueHigh)
        {
            result = DataClass.Encrypted;
            bool clear = h >= t.EncryptedEntropy + 0.03 && p >= t.PValueLow * 5 && p <= 1 - ((1 - t.PValueHigh) * 5);
            bool near = h < t.EncryptedEntropy + 0.01 || p < t.PValueLow * 2 || p > 1 - ((1 - t.PValueHigh) * 2);
            conf = clear ? Confidence.High : near ? Confidence.Low : Confidence.Medium;
            return;
        }

        if (h >= t.CompressedEntropy)
        {
            result = DataClass.Compressed;
            conf = h >= t.CompressedEntropy + 0.4 ? Confidence.Medium : Confidence.Low;
            return;
        }

        result = DataClass.Binary;
        conf = h < t.CompressedEntropy - 0.7 && textShare < t.TextShare - 0.15 ? Confidence.Medium : Confidence.Low;
    }

    /// <summary>UTF-8 として妥当な、制御文字でない文字のバイトの割合 (ASCII だけのブロックも含む)。</summary>
    private static double Utf8Share(ReadOnlySpan<byte> block)
    {
        int good = 0;
        int i = 0;
        bool sawMultibyte = false;
        while (i < block.Length)
        {
            byte b = block[i];
            int len = b < 0x80 ? 1 : b is >= 0xC2 and <= 0xDF ? 2 : b is >= 0xE0 and <= 0xEF ? 3 : b is >= 0xF0 and <= 0xF4 ? 4 : 0;
            if (len == 0 || i + len > block.Length)
            {
                i++;
                continue;
            }

            bool ok = true;
            for (int k = 1; k < len; k++)
            {
                ok &= (block[i + k] & 0xC0) == 0x80;
            }

            if (ok && (len > 1 || StatMath.IsPrintable(b) || StatMath.IsWhitespace(b)))
            {
                good += len;
                sawMultibyte |= len > 1;
            }

            i += ok ? len : 1;
        }

        // ASCII だけなら上の印字可能の判定と同じなので、多バイト文字があるときだけ数える。
        return sawMultibyte ? (double)good / block.Length : 0;
    }

    /// <summary>
    /// UTF-16 (LE / BE の高い方) として、よく使う文字の範囲に入る単位の割合。2 バイトとも印字可能な ASCII の単位は数えない
    /// (ASCII のテキストが漢字の範囲に見えるため。ASCII のテキストは印字可能の割合で判定する)。そのような単位が 4 分の 3 を超える
    /// ブロックは UTF-16 とみなさない。
    /// </summary>
    private static double Utf16Share(ReadOnlySpan<byte> block)
    {
        int units = block.Length / 2;
        int le = 0;
        int be = 0;
        int neutral = 0;
        for (int i = 0; i + 1 < block.Length; i += 2)
        {
            if (StatMath.IsPrintable(block[i]) && StatMath.IsPrintable(block[i + 1]))
            {
                neutral++;
                continue;
            }

            le += IsCommonChar(block[i] | (block[i + 1] << 8)) ? 1 : 0;
            be += IsCommonChar((block[i] << 8) | block[i + 1]) ? 1 : 0;
        }

        int counted = units - neutral;
        return counted == 0 || counted * 4 < units ? 0 : (double)Math.Max(le, be) / counted;
    }

    private static bool IsCommonChar(int c) => c is 0x09 or 0x0A or 0x0D
        or (>= 0x20 and <= 0x7E) or (>= 0xA0 and <= 0x24F) or (>= 0x370 and <= 0x4FF) or (>= 0x590 and <= 0x6FF) or (>= 0xE00 and <= 0xE7F)
        or (>= 0x3000 and <= 0x30FF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xAC00 and <= 0xD7A3) or (>= 0xFF00 and <= 0xFFEF);

    /// <summary>
    /// 統計のゆらぎによる細切れをならす (ANA-16 の仕様 3 の補足): (1) 末尾のブロックの大きさの半分に満たないブロックは、前のブロックと
    /// 同じ分類にする。(2) 2 ブロック以下の「暗号化・乱数」「圧縮」の区間が、もう一方の高エントロピーの分類のより長い区間と隣り合う
    /// 場合は、その分類にまとめる (乱数のブロックの約 2% は p 値の条件を外れるため)。
    /// </summary>
    internal static void Smooth(DataClass[] classes, long length, int blockSize)
    {
        int n = classes.Length;
        if (n >= 2 && length - ((long)(n - 1) * blockSize) < blockSize / 2
            && classes[n - 1] is not DataClass.Unreadable and not DataClass.None && classes[n - 2] is not DataClass.Unreadable and not DataClass.None)
        {
            classes[n - 1] = classes[n - 2];
        }

        static bool High(DataClass c) => c is DataClass.Encrypted or DataClass.Compressed;
        var runs = new List<(int Start, int Length, DataClass Class)>();
        for (int i = 0; i < n;)
        {
            int j = i;
            while (j < n && classes[j] == classes[i])
            {
                j++;
            }

            runs.Add((i, j - i, classes[i]));
            i = j;
        }

        for (int r = 0; r < runs.Count; r++)
        {
            (int start, int len, DataClass c) = runs[r];
            if (!High(c) || len > 2)
            {
                continue;
            }

            (int Start, int Length, DataClass Class)? best = null;
            foreach (int k in new[] { r - 1, r + 1 })
            {
                if (k >= 0 && k < runs.Count && High(runs[k].Class) && runs[k].Class != c && runs[k].Length > len
                    && (best is null || runs[k].Length > best.Value.Length))
                {
                    best = runs[k];
                }
            }

            if (best is { } b)
            {
                classes.AsSpan(start, len).Fill(b.Class);
                runs[r] = (start, len, b.Class);
            }
        }
    }

    private static IReadOnlyList<ClassRegion> MergeRegions(LogicalRanges ranges, int blockSize, DataClass[] classes, Half[] entropy, Confidence[] confidence)
    {
        var regions = new List<ClassRegion>();
        long length = ranges.Length;
        int i = 0;
        while (i < classes.Length)
        {
            DataClass c = classes[i];
            int j = i;
            double sum = 0;
            int score = 0;
            while (j < classes.Length && classes[j] == c)
            {
                sum += (double)entropy[j];
                score += (int)confidence[j];
                j++;
            }

            if (c != DataClass.None)
            {
                long start = (long)i * blockSize;
                long end = Math.Min(length, (long)j * blockSize);
                int blocks = j - i;
                var conf = (Confidence)(int)Math.Round((double)score / blocks);
                regions.Add(new ClassRegion(ranges.ToDocument(start), end - start, c, sum / blocks, conf, start));
            }

            i = j;
        }

        return regions;
    }

    // ---- シグネチャ (ANA-16 の仕様 4) ----

    private static readonly SearchValues<byte> FirstBytes = SearchValues.Create([0x78, 0x1F, 0x28, 0xFD, 0x42, 0x04, 0x37, 0x50, 0x53, 0x2D, 0x84, 0x85, 0xC1]);

    private static void FindSignatures(DocumentSnapshot snapshot, LogicalRanges ranges, ScanChunk chunk, List<SignatureHit> hits, CancellationToken token)
    {
        ReadOnlySpan<byte> span = chunk.Span;
        int at = 0;
        Span<byte> head = stackalloc byte[32];
        while (at < span.Length)
        {
            int found = span[at..].IndexOfAny(FirstBytes);
            if (found < 0)
            {
                break;
            }

            int i = at + found;
            at = i + 1;
            long logical = chunk.Logical + i;
            scoped ReadOnlySpan<byte> view;
            if (i + 32 <= span.Length)
            {
                view = span.Slice(i, 32);
            }
            else
            {
                // 区切りの近く: 続きをドキュメントから読む。
                head.Clear();
                long doc = ranges.ToDocument(logical);
                ReadResult r = snapshot.Read(doc, head);
                view = head[..r.BytesReturned];
            }

            if (Match(view) is { } m)
            {
                long offset = chunk.Document + i;
                long? streamLength = null;
                if (m.Verify is { } kind)
                {
                    streamLength = VerifyStream(snapshot, offset, kind, token);
                    if (streamLength is null)
                    {
                        continue;
                    }
                }

                hits.Add(new SignatureHit(offset, m.Format, m.Compression, m.Confidence, streamLength));
            }
        }
    }

    private enum StreamKind
    {
        Zlib,
        Gzip,
    }

    private readonly record struct SignatureMatch(string Format, bool Compression, Confidence Confidence, StreamKind? Verify = null);

    /// <summary>位置の先頭のバイト列がシグネチャに一致するか。</summary>
    private static SignatureMatch? Match(ReadOnlySpan<byte> v)
    {
        if (v.Length < 2)
        {
            return null;
        }

        switch (v[0])
        {
            case 0x78 when v[1] is 0x01 or 0x5E or 0x9C or 0xDA && ((v[0] << 8) | v[1]) % 31 == 0:
                return new SignatureMatch("zlib", true, Confidence.High, StreamKind.Zlib);
            case 0x1F when v.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B, 0x08]) && v.Length > 3 && (v[3] & 0xE0) == 0:
                return new SignatureMatch("gzip", true, Confidence.High, StreamKind.Gzip);
            case 0x28 when v.StartsWith((ReadOnlySpan<byte>)[0x28, 0xB5, 0x2F, 0xFD]):
                return new SignatureMatch("zstd", true, Confidence.High);
            case 0xFD when v.StartsWith((ReadOnlySpan<byte>)[0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]):
                return new SignatureMatch("xz", true, Confidence.High);
            case 0x42 when v.Length >= 4 && v[1] == 0x5A && v[2] == 0x68 && v[3] is >= 0x31 and <= 0x39:
                return new SignatureMatch("bzip2", true, Confidence.High);
            case 0x04 when v.StartsWith((ReadOnlySpan<byte>)[0x04, 0x22, 0x4D, 0x18]):
                return new SignatureMatch("LZ4", true, Confidence.High);
            case 0x37 when v.StartsWith((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]):
                return new SignatureMatch("7z", true, Confidence.High);
            case 0x50 when v.StartsWith((ReadOnlySpan<byte>)[0x50, 0x4B, 0x03, 0x04]):
                return new SignatureMatch("ZIP", true, Confidence.High);
            case 0x53 when v.StartsWith("Salted__"u8):
                return new SignatureMatch("OpenSSL (Salted__)", false, Confidence.Medium);
            case 0x2D when v.StartsWith("-----BEGIN PGP "u8):
                return new SignatureMatch("PGP (ASCII armor)", false, Confidence.Medium);
            case 0x84 when v.Length >= 3 && v[2] == 0x03:
            case 0x85 when v.Length >= 4 && v[3] == 0x03 && v[1] == 0x01:
            case 0xC1 when v.Length >= 3 && v[2] == 0x03 && v[1] is >= 12 and < 192:
                // 公開鍵で暗号化したセッション鍵のパケット (タグ 1、版 3)。偶然の一致がありうるため確度は低い。
                return new SignatureMatch("PGP", false, Confidence.Low);
            default:
                return null;
        }
    }

    /// <summary>
    /// zlib・gzip のストリームを展開して確かめ、圧縮されたデータのおおよその長さ (512 バイト単位で切り上げ) を返す。展開できなければ
    /// null (偶然の一致)。<see cref="MaxVerifyLength"/> までに終わらなければ、確かめた長さを返す。
    /// </summary>
    private static long? VerifyStream(DocumentSnapshot snapshot, long offset, StreamKind kind, CancellationToken token)
    {
        try
        {
            var source = new SnapshotStream(snapshot, offset, VerifyStep, token);
            if (kind == StreamKind.Zlib)
            {
                source.Skip(2);
            }
            else if (SkipGzipHeader(source) is null)
            {
                return null;
            }

            using var inflate = new DeflateStream(source, CompressionMode.Decompress, leaveOpen: true);
            byte[] sink = new byte[64 * 1024];
            long produced = 0;
            while (source.Position - offset < MaxVerifyLength)
            {
                token.ThrowIfCancellationRequested();
                int n = inflate.Read(sink, 0, sink.Length);
                if (n == 0)
                {
                    // 終わり: 読んだ位置が終わりの 512 バイト以内。gzip は 8 バイト、zlib は 4 バイトの末尾を加える。
                    return produced < 16
                        ? null
                        : source.Position - offset + (kind == StreamKind.Gzip ? 8 : 4);
                }

                produced += n;
            }

            return source.Position - offset;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static long? SkipGzipHeader(SnapshotStream s)
    {
        Span<byte> h = stackalloc byte[10];
        if (s.ReadAtLeast(h, 10, throwOnEndOfStream: false) < 10)
        {
            return null;
        }

        int flags = h[3];
        long length = 10;
        if ((flags & 0x04) != 0)
        {
            Span<byte> x = stackalloc byte[2];
            if (s.ReadAtLeast(x, 2, throwOnEndOfStream: false) < 2)
            {
                return null;
            }

            int xlen = x[0] | (x[1] << 8);
            s.Skip(xlen);
            length += 2 + xlen;
        }

        foreach (int bit in new[] { 0x08, 0x10 })
        {
            if ((flags & bit) != 0)
            {
                // 0 で終わる文字列 (ファイル名、コメント)。
                int b;
                int guard = 0;
                do
                {
                    b = s.ReadByte();
                    length++;
                }
                while (b > 0 && ++guard < 65536);
            }
        }

        if ((flags & 0x02) != 0)
        {
            s.Skip(2);
            length += 2;
        }

        return length;
    }

    /// <summary>スナップショットを読む Stream (1 回の Read で最大 <c>step</c> バイトだけ返し、展開の終わりの位置を細かく知るため)。</summary>
    private sealed class SnapshotStream(DocumentSnapshot snapshot, long start, int step, CancellationToken token) : Stream
    {
        private long _position = start;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => snapshot.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public void Skip(long count) => _position += count;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested();
            int n = (int)Math.Min(Math.Min(buffer.Length, step), Math.Max(0, snapshot.Length - _position));
            if (n <= 0)
            {
                return 0;
            }

            ReadResult r = snapshot.Read(_position, buffer[..n]);
            if (!r.IsComplete)
            {
                throw new InvalidDataException("読み込めない範囲です。");
            }

            _position += r.BytesReturned;
            return r.BytesReturned;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
