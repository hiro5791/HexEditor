using System.Globalization;
using System.Threading.Channels;
using HexEditor.Core.Forensics.Images.Ewf;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Forensics.Images;

/// <summary>イメージ作成の指定 (FOR-20 の仕様 2〜4、FOR-23)。</summary>
public sealed record AcquisitionOptions
{
    /// <summary>出力形式 (raw・分割 raw・E01)。</summary>
    public ImageFormat Format { get; init; } = ImageFormat.Raw;

    /// <summary>
    /// 出力先。raw はファイル (<c>m.dd</c>)、分割 raw は拡張子を除いた名前 (<c>split/m</c> → <c>m.001</c> …)、E01 は <c>m.E01</c>。
    /// </summary>
    public required string Output { get; init; }

    /// <summary>分割 raw の分割サイズ・E01 のセグメントサイズ (既定 2 GiB)。</summary>
    public long SplitSize { get; init; } = 2L * 1024 * 1024 * 1024;

    public IReadOnlyList<string> Hashes { get; init; } = ImageHashes.Default;

    /// <summary>読み込みエラー時の再試行回数 (既定 3)。</summary>
    public int Retries { get; init; } = 3;

    /// <summary>読めなかったセクタを埋める値 (既定 0)。</summary>
    public byte Fill { get; init; }

    /// <summary>読み込み単位 (既定 1 MiB。セクタサイズの倍数に切り上げる)。</summary>
    public int ReadUnit { get; init; } = 1024 * 1024;

    /// <summary>取得後に検証する (既定オン。FOR-22)。</summary>
    public bool Verify { get; init; } = true;

    public EwfCompression Compression { get; init; } = EwfCompression.Fast;

    public EwfCaseInfo Case { get; init; } = new();

    /// <summary>入力元の中の取得する範囲 (バイト。セクタ境界に揃えたもの)。長さ null は末尾まで。</summary>
    public long RangeStart { get; init; }

    public long? RangeLength { get; init; }

    /// <summary>取得する範囲の種類 (FOR-23)。</summary>
    public AcquisitionScope Scope { get; init; }

    /// <summary>スパース・スケルトンで含める範囲 (入力元の上のバイトの範囲)。<see cref="Scope"/> が Full なら使わない。</summary>
    public IReadOnlyList<(long Offset, long Length)>? Included { get; init; }

    /// <summary>raw をスパースファイルとして書く (含めない範囲を書かない。FOR-23 の仕様 2)。</summary>
    public bool SparseOutput { get; init; } = true;

    public string AppVersion { get; init; } = string.Empty;

    public AcquisitionLogText LogText { get; init; } = new();

    /// <summary>圧縮の並列数 (メモリは読み込み単位 × 並列数に限る)。</summary>
    public int Parallelism { get; init; } = Math.Clamp(Environment.ProcessorCount, 2, 8);

    /// <summary>処理センターの詳細に出す文 (読めなかったセクタ数 → 文)。</summary>
    public Func<long, string>? DetailText { get; init; }
}

/// <summary>取得の結果。</summary>
public sealed record AcquisitionResult
{
    public AcquisitionStatus Status { get; init; }

    public IReadOnlyList<string> Files { get; init; } = [];

    public string LogPath { get; init; } = string.Empty;

    public string? RangesPath { get; init; }

    public DateTimeOffset Started { get; init; }

    public DateTimeOffset Ended { get; init; }

    public long SectorsRead { get; init; }

    /// <summary>読めなかったセクタ (イメージの中の LBA)。</summary>
    public IReadOnlyList<SectorRange> BadSectors { get; init; } = [];

    /// <summary>イメージ全体のハッシュ (含めない範囲の 0 を含む)。</summary>
    public IReadOnlyDictionary<string, byte[]> Hashes { get; init; } = new Dictionary<string, byte[]>();

    /// <summary>含めた範囲だけのハッシュ (スパース・スケルトン)。</summary>
    public IReadOnlyDictionary<string, byte[]>? IncludedHashes { get; init; }

    public VerificationResult? Verification { get; init; }

    public string? Message { get; init; }

    /// <summary>キャンセルの後、出力済みのファイルを残したか。</summary>
    public bool KeptPartialFiles { get; init; }

    public long BadSectorCount => BadSectors.Sum(r => r.SectorCount);
}

/// <summary>取得中に利用者に尋ねること (UI が実装する)。</summary>
public interface IAcquisitionPrompts
{
    /// <summary>出力先の書き込みエラー・容量不足: 一時停止して「再試行」(true) か「中止」(false) を選ばせる。</summary>
    Task<bool> RetryAfterWriteErrorAsync(IOException error);

    /// <summary>キャンセルした: 出力済みのファイルを残す (true) か削除する (false) か。</summary>
    Task<bool> KeepPartialFilesAsync();
}

/// <summary>
/// ディスクイメージの作成 (FOR-20、FOR-23)。読み込み・ハッシュ計算・圧縮・書き込みを並列に行う (読み込みは 1 つ、圧縮は並列、書き込みは順に)。
/// メモリは読み込み単位 × 並列数に限る。ハッシュは読み込んだデータからその場で計算し、読み直さない。
/// </summary>
public static class ImageAcquisition
{
#if HEX_TEST_HOOKS
    /// <summary>テスト用: ブロックを書く直前に呼ぶ (イメージの中のオフセット)。例外で書き込みエラーを再現する。</summary>
    internal static Action<long>? BeforeWrite { get; set; }
#endif

    /// <summary>入力元の読み込み (オフセットは入力元の上の位置)。</summary>
    public delegate ReadResult ReadFunc(long offset, Span<byte> buffer);

    public static Task<AcquisitionResult> RunAsync(IByteSource source, AcquisitionSourceInfo info, AcquisitionOptions options,
        LongRunningOperation? operation = null, IAcquisitionPrompts? prompts = null, TimeProvider? time = null) =>
        RunAsync(source.Read, source.Length, info, options, operation, prompts, time);

    public static async Task<AcquisitionResult> RunAsync(ReadFunc read, long sourceLength, AcquisitionSourceInfo info, AcquisitionOptions options,
        LongRunningOperation? operation = null, IAcquisitionPrompts? prompts = null, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        int sector = Math.Max(1, info.SectorSize);
        long start = options.RangeStart;
        long length = options.RangeLength ?? sourceLength - start;
        if (start < 0 || length < 0 || start + length > sourceLength || start % sector != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The range is outside the source or not sector aligned.");
        }

        if (options.Format is not (ImageFormat.Raw or ImageFormat.SplitRaw or ImageFormat.E01))
        {
            throw new ArgumentException("Images can be created only in raw, split raw or E01 format.", nameof(options));
        }

        // E01 はセクタ単位なので、端数のセクタは 0 で補う。
        long mediaSize = options.Format == ImageFormat.E01 ? (length + sector - 1) / sector * sector : length;
        int unit = Math.Max(sector, (options.ReadUnit + sector - 1) / sector * sector);
        if (options.Format == ImageFormat.E01)
        {
            int chunk = EwfFormat.SectorsPerChunk * sector;
            unit = Math.Max(chunk, unit / chunk * chunk);
        }

        IReadOnlyList<(long Offset, long Length)> included = options.Scope == AcquisitionScope.Full || options.Included is null
            ? [(start, length)]
            : Normalize(options.Included, start, length, sector);

        DateTimeOffset started = time.GetUtcNow();
        string logPath = AcquisitionLog.PathFor(options.Format == ImageFormat.SplitRaw ? SplitRawNaming.SegmentPath(options.Output, 1) : options.Output, options.Format);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
        Audit(info, options, ImageAuditResult.Started, null, null);

        operation?.SetTotal(options.Verify ? mediaSize * 2 : mediaSize);
        using var whole = new ImageHashes(HashesFor(options));
        using var inside = options.Scope == AcquisitionScope.Full ? null : new ImageHashes(HashesFor(options));
        var bad = new List<SectorRange>();
        long sectorsRead = 0;
        AcquisitionStatus status = AcquisitionStatus.Completed;
        string? message = null;
        bool kept = true;
        ISink sink = CreateSink(options, mediaSize, sector, info);
        CancellationToken token = operation?.CancellationToken ?? CancellationToken.None;
        try
        {
            var reads = Channel.CreateBounded<Block>(new BoundedChannelOptions(Math.Max(2, options.Parallelism)) { SingleReader = true, SingleWriter = true });
            var writes = Channel.CreateBounded<Task<Block>>(new BoundedChannelOptions(Math.Max(2, options.Parallelism)) { SingleReader = true, SingleWriter = true });
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);

            // 1. 読み込み (入力元は 1 つのスレッドから順に読む)。
            Task reader = Task.Run(async () =>
            {
                try
                {
                    for (long at = 0; at < mediaSize; at += unit)
                    {
                        stop.Token.ThrowIfCancellationRequested();
                        int n = (int)Math.Min(unit, mediaSize - at);
                        Block block = ReadBlock(read, start, length, at, n, sector, included, options, stop.Token);
                        await reads.Writer.WriteAsync(block, stop.Token).ConfigureAwait(false);
                        if (block.SourceLost)
                        {
                            break;
                        }
                    }

                    reads.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    reads.Writer.TryComplete(ex);
                }
            }, stop.Token);

            // 3. 書き込み (順に)。書き込みエラーは利用者に再試行か中止かを尋ねる。
            Task writer = Task.Run(async () =>
            {
                try
                {
                    await foreach (Task<Block> pending in writes.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                    {
                        Block block = await pending.ConfigureAwait(false);
                        while (true)
                        {
                            stop.Token.ThrowIfCancellationRequested();
                            try
                            {
#if HEX_TEST_HOOKS
                                BeforeWrite?.Invoke(block.Offset);
#endif
                                sink.Write(block);
                                break;
                            }
                            catch (IOException ex) when (prompts is not null && ex is not ImageFormatException)
                            {
                                if (!await prompts.RetryAfterWriteErrorAsync(ex).ConfigureAwait(false))
                                {
                                    throw new AcquisitionAbortedException(ex.Message, ex);
                                }
                            }
                        }

                        operation?.Report(block.Offset + block.Length);
                    }
                }
                catch
                {
                    await stop.CancelAsync().ConfigureAwait(false);
                    throw;
                }
            }, stop.Token);

            // 2. ハッシュ (順に) と圧縮 (並列)。
            try
            {
                await foreach (Block block in reads.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                {
                    if (block.SourceLost)
                    {
                        status = AcquisitionStatus.SourceLost;
                        break;
                    }

                    whole.Append(block.Data.AsSpan(0, block.Length));
                    if (inside is not null)
                    {
                        foreach ((int from, int count) in block.IncludedParts)
                        {
                            inside.Append(block.Data.AsSpan(from, count));
                        }
                    }

                    bad.AddRange(block.Bad);
                    sectorsRead += block.IncludedParts.Sum(p => (long)p.Count) / sector;
                    if (block.Bad.Count > 0 && options.DetailText is { } detail)
                    {
                        operation?.ReportDetail(detail(bad.Sum(b => b.SectorCount)));
                    }

                    Task<Block> encoded = options.Format == ImageFormat.E01
                        ? Task.Run(() => Encode(block, options, sector), stop.Token)
                        : Task.FromResult(block);
                    await writes.Writer.WriteAsync(encoded, stop.Token).ConfigureAwait(false);
                }

                writes.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                writes.Writer.TryComplete(ex);
                await stop.CancelAsync().ConfigureAwait(false);
            }

            // 段階のどれかが失敗したら、取り消し (OperationCanceledException) ではない本当の原因を優先して伝える。
            var failures = new List<Exception>();
            foreach (Task t in new[] { reader, writer })
            {
                try
                {
                    await t.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failures.Add(ex is ChannelClosedException { InnerException: { } inner } ? inner : ex);
                }
            }

            token.ThrowIfCancellationRequested();
            if (failures.FirstOrDefault(f => f is not OperationCanceledException) is { } failure)
            {
                throw failure;
            }

            if (failures.Count > 0 && status != AcquisitionStatus.SourceLost)
            {
                throw failures[0];
            }

            if (status == AcquisitionStatus.Completed)
            {
                IReadOnlyDictionary<string, byte[]> partial = whole.Finish();
                sink.Finish(Md5Of(partial), partial.GetValueOrDefault(ImageHashes.Sha1), Merge(bad));
                return await CompleteAsync(info, options, sink, partial, inside?.Finish(), Merge(bad), sectorsRead, started, logPath, sector, start,
                    length, operation, mediaSize, time).ConfigureAwait(false);
            }

            message = options.LogText.StatusSourceLost;
            sink.Abort();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            status = AcquisitionStatus.Cancelled;
            sink.Abort();
            kept = prompts is null || await prompts.KeepPartialFilesAsync().ConfigureAwait(false);
            if (!kept)
            {
                sink.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AcquisitionAbortedException)
        {
            status = AcquisitionStatus.Failed;
            message = ex.Message;
            sink.Abort();
        }

        // 中断・失敗: 取得ログに「中断」などを書く (FOR-20 の「巨大ファイル」「エラー」)。
        var result = new AcquisitionResult
        {
            Status = status,
            Files = sink.Files,
            LogPath = logPath,
            Started = started,
            Ended = time.GetUtcNow(),
            SectorsRead = sectorsRead,
            BadSectors = Merge(bad),
            Message = message,
            KeptPartialFiles = kept,
        };
        WriteLog(result, info, options, sector, start, length, null);
        Audit(info, options, status == AcquisitionStatus.Cancelled ? ImageAuditResult.Cancelled : ImageAuditResult.Failure, result, message);
        return result;
    }

    private static IEnumerable<string> HashesFor(AcquisitionOptions options) =>
        options.Format == ImageFormat.E01 ? options.Hashes.Select(ImageHashes.Normalize).Concat([ImageHashes.Md5, ImageHashes.Sha1]).Distinct() : options.Hashes;

    private static byte[] Md5Of(IReadOnlyDictionary<string, byte[]> hashes) => hashes.GetValueOrDefault(ImageHashes.Md5) ?? new byte[16];

    private static async Task<AcquisitionResult> CompleteAsync(AcquisitionSourceInfo info, AcquisitionOptions options, ISink sink,
        IReadOnlyDictionary<string, byte[]> all, IReadOnlyDictionary<string, byte[]>? included, IReadOnlyList<SectorRange> bad, long sectorsRead,
        DateTimeOffset started, string logPath, int sector, long start, long length, LongRunningOperation? operation, long mediaSize, TimeProvider time)
    {
        // 利用者が選んだアルゴリズムだけを結果と取得ログに出す (E01 のために計算した MD5・SHA-1 は E01 に書く)。
        HashSet<string> chosen = [.. options.Hashes.Select(ImageHashes.Normalize)];
        var hashes = all.Where(kv => chosen.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var inside = included?.Where(kv => chosen.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        string? rangesPath = null;
        if (options.Scope != AcquisitionScope.Full && options.Included is not null)
        {
            rangesPath = RangesCsv.PathFor(logPath);
            RangesCsv.Write(rangesPath, Normalize(options.Included, start, length, sector).Select(r => new SectorRange((r.Offset - start) / sector, r.Length / sector)));
        }

        VerificationResult? verification = null;
        if (options.Verify)
        {
            await Task.Yield();
            using IImageSource image = ImageOpener.Open(sink.Files[0], options.Format, new ImageOpenOptions { SectorSize = sector });
            var references = hashes.Select(kv => new ReferenceHash(kv.Key, kv.Value, ReferenceSource.Acquisition))
                .Concat(image.Info.StoredHashes.Select(kv => new ReferenceHash(kv.Key, kv.Value, ReferenceSource.Stored))).ToList();
            var progress = operation is null ? null : new OffsetProgress(operation, mediaSize);
            verification = Verify(image, chosen, references, progress, operation?.CancellationToken ?? CancellationToken.None, time);
        }

        var result = new AcquisitionResult
        {
            Status = AcquisitionStatus.Completed,
            Files = sink.Files,
            LogPath = logPath,
            RangesPath = rangesPath,
            Started = started,
            Ended = time.GetUtcNow(),
            SectorsRead = sectorsRead,
            BadSectors = bad,
            Hashes = hashes,
            IncludedHashes = inside,
            Verification = verification,
        };
        WriteLog(result, info, options, sector, start, length, verification?.Overall);
        Audit(info, options, ImageAuditResult.Success, result, null);
        ImageForensics.Hooks.RegisterAcquiredImage(sink.Files[0], info.Path, hashes);
        if (verification is not null)
        {
            ImageVerification.Record(sink.Files[0], null, verification);
        }
        return result;
    }

    /// <summary>取得後の検証 (進捗は取得の続きとして報告する)。</summary>
    private static VerificationResult Verify(IImageSource image, IEnumerable<string> algorithms, IReadOnlyList<ReferenceHash> references,
        OffsetProgress? progress, CancellationToken token, TimeProvider time)
    {
        using var hashes = new ImageHashes(algorithms);
        var errors = new List<UnreadableRange>();
        byte[] buffer = new byte[1 << 20];
        for (long pos = 0; pos < image.Length; pos += buffer.Length)
        {
            token.ThrowIfCancellationRequested();
            int n = (int)Math.Min(buffer.Length, image.Length - pos);
            ReadResult r = image.Read(pos, buffer.AsSpan(0, n));
            errors.AddRange(r.Unreadable);
            hashes.Append(buffer.AsSpan(0, n));
            progress?.Report(pos + n);
        }

        IReadOnlyDictionary<string, byte[]> computed = hashes.Finish();
        var rows = new List<AlgorithmVerification>();
        foreach ((string name, byte[] value) in computed)
        {
            var comparisons = references.Where(r => r.Algorithm == name).Select(r => (r, r.Value.AsSpan().SequenceEqual(value))).ToList();
            VerificationVerdict verdict = errors.Count > 0 ? VerificationVerdict.ReadError
                : comparisons.Count == 0 ? VerificationVerdict.NoReference
                : comparisons.All(c => c.Item2) ? VerificationVerdict.Match : VerificationVerdict.Mismatch;
            rows.Add(new AlgorithmVerification(name, value, comparisons, verdict));
        }

        return new VerificationResult(rows, errors, time.GetUtcNow());
    }

    private sealed class OffsetProgress(LongRunningOperation operation, long offset)
    {
        public void Report(long done) => operation.Report(offset + done);
    }

    private static void WriteLog(AcquisitionResult result, AcquisitionSourceInfo info, AcquisitionOptions options, int sector, long start, long length,
        VerificationVerdict? verdict)
    {
        var data = new AcquisitionLogData
        {
            Status = result.Status,
            Source = info,
            RangeStart = start,
            RangeLength = length,
            FormatName = FormatName(options),
            Files = result.Files,
            Started = result.Started,
            Ended = result.Ended,
            SectorsRead = result.SectorsRead,
            Unreadable = result.BadSectors,
            Hashes = result.Hashes,
            IncludedHashes = result.IncludedHashes,
            Verification = verdict,
            AppVersion = options.AppVersion,
            Case = options.Case,
            CaseFolder = ImageForensics.Hooks.Case,
            Scope = options.Scope,
            RangesFile = result.RangesPath,
            Message = result.Message,
        };
        try
        {
            AcquisitionLog.Write(result.LogPath, data, options.LogText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 取得ログを書けない: 結果の文に含める (監査ログには記録する)。
        }

        _ = sector;
    }

    private static string FormatName(AcquisitionOptions options) => options.Format switch
    {
        ImageFormat.E01 => "E01 (" + options.Compression.ToString().ToLowerInvariant() + ", " + options.SplitSize.ToString(CultureInfo.InvariantCulture) + " bytes/segment)",
        ImageFormat.SplitRaw => "split raw (" + options.SplitSize.ToString(CultureInfo.InvariantCulture) + " bytes/segment)",
        _ => "raw",
    };

    private static void Audit(AcquisitionSourceInfo info, AcquisitionOptions options, ImageAuditResult result, AcquisitionResult? r, string? message)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["format"] = options.Format.ToString(),
            ["output"] = options.Output,
            ["scope"] = options.Scope.ToString(),
            ["rangeStart"] = options.RangeStart.ToString(CultureInfo.InvariantCulture),
        };
        if (r is not null)
        {
            foreach ((string algorithm, byte[] value) in r.Hashes)
            {
                parameters["hash." + algorithm] = AcquisitionLog.Hex(value);
            }

            parameters["badSectors"] = r.BadSectorCount.ToString(CultureInfo.InvariantCulture);
            parameters["files"] = string.Join(";", r.Files);
            if (r.Verification is { } v)
            {
                parameters["verification"] = v.Overall.ToString();
            }
        }

        ImageForensics.Hooks.Audit(new ImageAuditRecord
        {
            Operation = "image.create",
            TargetPath = info.Path,
            TargetIdentity = info.Identity,
            Parameters = parameters,
            Result = result,
            Message = message,
        });
    }

    /// <summary>含める範囲を、取得する範囲の中に切り詰め、セクタ境界に広げて並べる。</summary>
    internal static IReadOnlyList<(long Offset, long Length)> Normalize(IEnumerable<(long Offset, long Length)> ranges, long start, long length, int sector)
    {
        var list = new List<(long Offset, long Length)>();
        foreach ((long o, long l) in ranges.Where(r => r.Length > 0).OrderBy(r => r.Offset))
        {
            long from = Math.Max(start, o / sector * sector);
            long to = Math.Min(start + length, (o + l + sector - 1) / sector * sector);
            if (to <= from)
            {
                continue;
            }

            if (list.Count > 0 && from <= list[^1].Offset + list[^1].Length)
            {
                (long po, long pl) = list[^1];
                list[^1] = (po, Math.Max(po + pl, to) - po);
            }
            else
            {
                list.Add((from, to - from));
            }
        }

        return list;
    }

    internal static IReadOnlyList<SectorRange> Merge(List<SectorRange> ranges)
    {
        var merged = new List<SectorRange>();
        foreach (SectorRange r in ranges.OrderBy(r => r.StartSector))
        {
            if (merged.Count > 0 && r.StartSector <= merged[^1].EndSector)
            {
                SectorRange last = merged[^1];
                merged[^1] = new SectorRange(last.StartSector, Math.Max(last.EndSector, r.EndSector) - last.StartSector);
            }
            else
            {
                merged.Add(r);
            }
        }

        return merged;
    }

    /// <summary>読み込んだ 1 単位。<see cref="IncludedParts"/> はブロックの中の含めた部分 (位置と長さ)。</summary>
    private sealed class Block
    {
        public long Offset { get; init; }

        public int Length { get; init; }

        public required byte[] Data { get; init; }

        public List<(int From, int Count)> IncludedParts { get; } = [];

        public List<SectorRange> Bad { get; } = [];

        public bool SourceLost { get; set; }

        /// <summary>E01: 書き込む形にしたチャンク。</summary>
        public EwfEncodedChunk[]? Chunks { get; set; }
    }

    private static Block ReadBlock(ReadFunc read, long start, long length, long at, int n, int sector, IReadOnlyList<(long Offset, long Length)> included,
        AcquisitionOptions options, CancellationToken token)
    {
        var block = new Block { Offset = at, Length = n, Data = new byte[n] };
        long blockStart = start + at;
        long blockEnd = blockStart + n;
        foreach ((long o, long l) in included)
        {
            long from = Math.Max(o, blockStart);
            long to = Math.Min(o + l, Math.Min(blockEnd, start + length));
            if (to > from)
            {
                block.IncludedParts.Add(((int)(from - blockStart), (int)(to - from)));
            }
        }

        foreach ((int from, int count) in block.IncludedParts)
        {
            token.ThrowIfCancellationRequested();
            Span<byte> part = block.Data.AsSpan(from, count);
            ReadResult r = read(blockStart + from, part);
            if (r.IsComplete)
            {
                continue;
            }

            if (r.Unreadable.Any(u => u.Reason == UnreadableReason.Disconnected))
            {
                block.SourceLost = true;
                return block;
            }

            // 再試行 (既定 3 回)。
            for (int attempt = 0; attempt < options.Retries && !r.IsComplete; attempt++)
            {
                token.ThrowIfCancellationRequested();
                r = read(blockStart + from, part);
            }

            if (r.IsComplete)
            {
                continue;
            }

            // 読み込み単位を 1 セクタまで小さくして、読める部分を取る。読めないセクタは指定の値で埋める。
            byte[] one = new byte[sector];
            foreach (UnreadableRange u in r.Unreadable.ToList())
            {
                long first = Math.Max(blockStart + from, u.Offset) / sector * sector;
                long last = Math.Min(blockStart + from + count, u.End);
                for (long s = first; s < last; s += sector)
                {
                    token.ThrowIfCancellationRequested();
                    int len = (int)Math.Min(sector, blockStart + from + count - s);
                    ReadResult single = read(s, one.AsSpan(0, len));
                    Span<byte> target = block.Data.AsSpan((int)(s - blockStart), len);
                    if (single.Unreadable.Any(x => x.Reason == UnreadableReason.Disconnected))
                    {
                        block.SourceLost = true;
                        return block;
                    }

                    if (single.IsComplete)
                    {
                        one.AsSpan(0, len).CopyTo(target);
                    }
                    else
                    {
                        target.Fill(options.Fill);
                        block.Bad.Add(new SectorRange((s - start) / sector, 1));
                    }
                }
            }
        }

        return block;
    }

    private static Block Encode(Block block, AcquisitionOptions options, int sector)
    {
        int chunkBytes = EwfFormat.SectorsPerChunk * sector;
        int count = (block.Length + chunkBytes - 1) / chunkBytes;
        var chunks = new EwfEncodedChunk[count];
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism) }, i =>
        {
            int from = i * chunkBytes;
            int n = Math.Min(chunkBytes, block.Length - from);
            // 含めない範囲だけのチャンクは 0 として圧縮する (FOR-23 の仕様 2)。
            bool excluded = options.Scope != AcquisitionScope.Full && !block.IncludedParts.Any(p => p.From < from + n && from < p.From + p.Count);
            EwfCompression compression = excluded && options.Compression == EwfCompression.None ? EwfCompression.Fast : options.Compression;
            chunks[i] = EwfWriter.Encode(block.Data.AsSpan(from, n), compression);
        });
        block.Chunks = chunks;
        return block;
    }

    // ---- 出力 ----

    private interface ISink
    {
        IReadOnlyList<string> Files { get; }

        void Write(Block block);

        void Finish(byte[] md5, byte[]? sha1, IReadOnlyList<SectorRange> bad);

        void Abort();

        void Delete();
    }

    private static ISink CreateSink(AcquisitionOptions options, long mediaSize, int sector, AcquisitionSourceInfo info) => options.Format switch
    {
        ImageFormat.E01 => new EwfSink(options, mediaSize, sector, info),
        ImageFormat.SplitRaw => new SplitSink(options.Output, options.SplitSize, mediaSize, options.Scope != AcquisitionScope.Full && options.SparseOutput),
        _ => new RawSink(options.Output, mediaSize, options.Scope != AcquisitionScope.Full && options.SparseOutput),
    };

    /// <summary>1 ファイルの raw。スパースなら含めない範囲を書かない (スパースにできない出力先では 0 を書く)。</summary>
    private sealed class RawSink : ISink
    {
        private readonly string _path;
        private readonly long _length;
        private SafeFileHandle? _handle;
        private readonly bool _sparse;

        public RawSink(string path, long length, bool sparse)
        {
            _path = Path.GetFullPath(path);
            _length = length;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            _handle = File.OpenHandle(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            _sparse = sparse && SparseFiles.TryMakeSparse(_handle);
        }

        public bool IsSparse => _sparse;

        public IReadOnlyList<string> Files => [_path];

        public void Write(Block block) => WriteTo(_handle!, block, 0, _sparse);

        public void Finish(byte[] md5, byte[]? sha1, IReadOnlyList<SectorRange> bad)
        {
            RandomAccess.SetLength(_handle!, _length);
            RandomAccess.FlushToDisk(_handle!);
            _handle!.Dispose();
            _handle = null;
        }

        public void Abort()
        {
            _handle?.Dispose();
            _handle = null;
        }

        public void Delete() => TryDelete(_path);
    }

    /// <summary>ブロックを <paramref name="handle"/> の (ブロックの位置 − <paramref name="fileStart"/>) に書く。スパースなら含めた部分だけ。</summary>
    private static void WriteTo(SafeFileHandle handle, Block block, long fileStart, bool sparse, long segmentLength = long.MaxValue)
    {
        if (!sparse)
        {
            Clip(block.Offset, block.Length, fileStart, segmentLength, out int from, out int count);
            if (count > 0)
            {
                RandomAccess.Write(handle, block.Data.AsSpan(from, count), block.Offset + from - fileStart);
            }

            return;
        }

        foreach ((int partFrom, int partCount) in block.IncludedParts)
        {
            Clip(block.Offset + partFrom, partCount, fileStart, segmentLength, out int from, out int count);
            if (count > 0)
            {
                RandomAccess.Write(handle, block.Data.AsSpan(partFrom + from, count), block.Offset + partFrom + from - fileStart);
            }
        }
    }

    /// <summary>[offset, offset + length) と、ファイルの範囲 [fileStart, fileStart + segmentLength) の重なり (offset からの位置と長さ)。</summary>
    private static void Clip(long offset, int length, long fileStart, long segmentLength, out int from, out int count)
    {
        long s = Math.Max(offset, fileStart);
        long e = Math.Min(offset + length, fileStart + segmentLength);
        from = (int)(s - offset);
        count = (int)Math.Max(0, e - s);
    }

    /// <summary>分割 raw (<c>名前.001</c> …)。</summary>
    private sealed class SplitSink(string basePath, long splitSize, long length, bool sparse) : ISink
    {
        private readonly string _base = Path.GetFullPath(basePath);
        private readonly Dictionary<int, SafeFileHandle> _open = [];
        private readonly List<string> _files = [];
        private readonly HashSet<int> _created = [];

        public IReadOnlyList<string> Files => _files;

        public void Write(Block block)
        {
            int first = (int)(block.Offset / splitSize);
            int last = (int)((block.Offset + block.Length - 1) / splitSize);
            for (int i = first; i <= last; i++)
            {
                WriteTo(Segment(i), block, (long)i * splitSize, sparse, splitSize);
            }

            // 書き終えたセグメントを閉じる (同時に開くのは 2 つまで)。
            foreach (int done in _open.Keys.Where(k => k < first).ToList())
            {
                CloseSegment(done);
            }
        }

        private SafeFileHandle Segment(int index)
        {
            if (_open.TryGetValue(index, out SafeFileHandle? handle))
            {
                return handle;
            }

            string path = SplitRawNaming.SegmentPath(_base, index + 1);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 初めて開くときは作り直し (古いファイルを切り詰める)、閉じた後に開き直すときは内容を残す。
            bool created = _created.Add(index);
            handle = File.OpenHandle(path, created ? FileMode.Create : FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (sparse && created)
            {
                SparseFiles.TryMakeSparse(handle);
            }

            _open[index] = handle;
            if (!_files.Contains(path))
            {
                _files.Add(path);
            }

            return handle;
        }

        private void CloseSegment(int index)
        {
            SafeFileHandle handle = _open[index];
            RandomAccess.SetLength(handle, Math.Min(splitSize, length - (long)index * splitSize));
            RandomAccess.FlushToDisk(handle);
            handle.Dispose();
            _open.Remove(index);
        }

        public void Finish(byte[] md5, byte[]? sha1, IReadOnlyList<SectorRange> bad)
        {
            // 含めない範囲だけのセグメント (スパース) も作る。
            int count = length == 0 ? 1 : (int)((length + splitSize - 1) / splitSize);
            for (int i = 0; i < count; i++)
            {
                if (!_created.Contains(i))
                {
                    Segment(i);
                }
            }

            foreach (int i in _open.Keys.ToList())
            {
                CloseSegment(i);
            }

            _files.Sort(StringComparer.OrdinalIgnoreCase);
        }

        public void Abort()
        {
            foreach (SafeFileHandle h in _open.Values)
            {
                h.Dispose();
            }

            _open.Clear();
        }

        public void Delete()
        {
            foreach (string f in _files)
            {
                TryDelete(f);
            }
        }
    }

    private sealed class EwfSink : ISink
    {
        private readonly EwfWriter _writer;

        public EwfSink(AcquisitionOptions options, long mediaSize, int sector, AcquisitionSourceInfo info)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Output))!);
            _writer = new EwfWriter(options.Output, new EwfWriterOptions
            {
                MediaSize = mediaSize,
                BytesPerSector = sector,
                Compression = options.Compression,
                SegmentSize = options.SplitSize,
                Case = options.Case,
                Version = options.AppVersion.Length > 0 ? options.AppVersion : "HexEditor",
                Model = info.Model,
                SerialNumber = info.SerialNumber,
                Physical = info.Physical,
            });
        }

        public IReadOnlyList<string> Files => _writer.Files;

        public void Write(Block block)
        {
            // 書き込みエラーの再試行のため、書いたチャンクの数を覚えておき、続きから書く。
            EwfEncodedChunk[] chunks = block.Chunks!;
            for (int i = _resume; i < chunks.Length; i++)
            {
                _writer.WriteChunk(chunks[i]);
                _resume = i + 1;
            }

            _resume = 0;
        }

        private int _resume;

        public void Finish(byte[] md5, byte[]? sha1, IReadOnlyList<SectorRange> bad)
        {
            _writer.Finish(md5, sha1, bad);
        }

        public void Abort() => _writer.Dispose();

        public void Delete()
        {
            foreach (string f in _writer.Files)
            {
                TryDelete(f);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>書き込みエラーの後に利用者が「中止」を選んだ。</summary>
public sealed class AcquisitionAbortedException(string message, Exception inner) : IOException(message, inner);
