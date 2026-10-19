using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Carving;

/// <summary>
/// カービングの実行 (12 の FOR-19)。対象範囲をチャンク単位で走査し (境界には最長のヘッダー長 - 1 バイトを重ねる)、見つけた候補の長さを
/// 構造の検証 → サイズの取得 → フッター → 最大サイズ の順に決めて、結果の一覧に加える。書き出しは走査と並行して行い (有限のキューで走査に背圧をかける)、
/// マニフェスト <c>carving-manifest.csv</c> を 1 件ごとに追記する。メモリは固定量 (チャンク × 並列数 + 書き出しの buffer) で、ファイルの大きさに依存しない。
/// キャンセルしてもそれまでの結果とマニフェストは残す。
/// </summary>
public static class CarvingEngine
{
    public const string ManifestFileName = "carving-manifest.csv";

    public static readonly string[] ManifestHeader = ["Number", "Type", "Offset", "LBA", "Length", "LengthMethod", "MD5", "SHA256", "OutputPath"];

    private const int ExportBuffer = 1024 * 1024;

    private sealed class HeaderEntry(int signature, HexMaskPattern pattern, long offset)
    {
        public int Signature { get; } = signature;

        public HexMaskPattern Pattern { get; } = pattern;

        public long Offset { get; } = offset;
    }

    private readonly record struct Candidate(long FileStart, long HeaderPosition, int Signature, int HeaderLength);

    /// <summary>走査の準備 (定義をヘッダーの表にする)。</summary>
    private sealed class Plan
    {
        public readonly Dictionary<long, (List<HeaderEntry>[] ByFirst, List<HeaderEntry> Wild)> Aligned = [];
        public readonly List<HeaderEntry>[] UnalignedByAnchor = new List<HeaderEntry>[256];
        public System.Buffers.SearchValues<byte>? UnalignedFirstBytes;
        public int MaxHeaderLength;
        public readonly List<CarvingSignature> Signatures = [];
        public readonly HexMaskPattern?[] Footers;

        public Plan(IReadOnlyList<CarvingSignature> signatures)
        {
            Signatures.AddRange(signatures);
            Footers = new HexMaskPattern?[signatures.Count];
            var anchorBytes = new HashSet<byte>();
            for (int s = 0; s < signatures.Count; s++)
            {
                CarvingSignature sig = signatures[s];
                Footers[s] = sig.ParsedFooter();
                foreach (HexMaskPattern header in sig.ParsedHeaders())
                {
                    MaxHeaderLength = Math.Max(MaxHeaderLength, header.Length);
                    var entry = new HeaderEntry(s, header, sig.HeaderOffset);
                    if (sig.SectorAligned)
                    {
                        if (!Aligned.TryGetValue(sig.HeaderOffset, out var group))
                        {
                            group = (Enumerable.Range(0, 256).Select(_ => new List<HeaderEntry>()).ToArray(), []);
                            Aligned[sig.HeaderOffset] = group;
                        }

                        if (header.Mask[0] == 0xFF)
                        {
                            group.ByFirst[header.Value[0]].Add(entry);
                        }
                        else
                        {
                            group.Wild.Add(entry);
                        }
                    }
                    else
                    {
                        byte first = header.Value[header.AnchorOffset];
                        (UnalignedByAnchor[first] ??= []).Add(entry);
                        anchorBytes.Add(first);
                    }
                }
            }

            if (anchorBytes.Count > 0)
            {
                UnalignedFirstBytes = System.Buffers.SearchValues.Create([.. anchorBytes]);
            }
        }
    }

    private sealed class ScanChunk
    {
        public required long Start;
        public required long OwnedEnd;
        public required long RangeStart;
        public required long RangeEnd;
        public required long TotalBefore;
        public List<Candidate> Candidates = [];
        public List<UnreadableRange> Unreadable = [];
        public long OwnedLength => OwnedEnd - Start;
    }

    /// <summary>実行する。キャンセルされても例外にはせず、<see cref="CarvingResult.Cancelled"/> を立てて返す。</summary>
    public static async Task<CarvingResult> RunAsync(ICarveData data, CarvingOptions options, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        Plan plan = new(options.Signatures);
        CarvedFileStore store = options.Store ?? new CarvedFileStore(options.Signatures, options.SectorSize);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var unreadable = new List<UnreadableRange>();
        SearchRange[] ranges = [.. options.Ranges.Where(r => r.Length > 0).Select(r => new SearchRange(r.Offset, Math.Min(r.End, data.Length) - r.Offset)).Where(r => r.Length > 0)];
        long total = ranges.Sum(r => r.Length);

        AuditScope? audit = options.Audit?.Begin(new AuditRecord(AuditOperations.Carve)
        {
            EvidenceId = options.EvidenceId,
            Path = options.SourcePath,
            Parameters = AuditRecord.Args(("signatures", options.Signatures.Count.ToString(CultureInfo.InvariantCulture)),
                ("ranges", ranges.Length.ToString(CultureInfo.InvariantCulture)), ("bytes", total.ToString(CultureInfo.InvariantCulture)),
                ("export", options.Export && options.OutputFolder is not null ? "yes" : "no"), ("output", options.OutputFolder ?? string.Empty),
                ("skipInside", options.SkipInside ? "yes" : "no")),
        });

        using var abort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = abort.Token;
        bool abortedByOutput = false;
        long scanned = 0, currentOffset = 0;
        string? manifestPath = null;
        string? manifestHash = null;
        string? finalFolder = options.OutputFolder;

        Exporter? exporter = null;
        Task? exportTask = null;
        Channel<CarvedFile>? channel = null;
        try
        {
            if (options.OutputFolder is { } folder)
            {
                Directory.CreateDirectory(folder);
                exporter = new Exporter(data, options, folder, store, unreadable, abort);
                if (options.Export)
                {
                    channel = Channel.CreateBounded<CarvedFile>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
                    exportTask = Task.Run(() => exporter.RunAsync(channel.Reader, token));
                }
            }

            var progressTimer = Stopwatch.StartNew();
            void Report(bool force)
            {
                if (options.Progress is null || (!force && progressTimer.ElapsedMilliseconds < 100))
                {
                    return;
                }

                progressTimer.Restart();
                lock (counts)
                {
                    options.Progress.Report(new CarvingProgress(scanned, total, store.Count, exporter?.ExportedCount ?? 0, new Dictionary<string, long>(counts), currentOffset));
                }
            }

            // 先読みして並列に走査し、結果は順番どおりに処理する。
            var pending = new Queue<Task<ScanChunk>>();
            long skipUntil = 0;
            long beforeChunk = 0;
            int window = Math.Max(1, options.Parallelism);

            async Task ConsumeAsync(Task<ScanChunk> task)
            {
                ScanChunk chunk = await task.ConfigureAwait(false);
                unreadable.AddRange(chunk.Unreadable);
                foreach (Candidate c in chunk.Candidates)
                {
                    token.ThrowIfCancellationRequested();
                    if (options.SkipInside && c.FileStart < skipUntil)
                    {
                        continue;
                    }

                    CarvingSignature sig = plan.Signatures[c.Signature];
                    if (!TryResolve(data, plan, options, c, chunk.RangeEnd, token, out long length, out LengthMethod method))
                    {
                        continue;
                    }

                    skipUntil = c.FileStart + length;
                    long number = store.Add(c.Signature, c.FileStart, length, method);
                    lock (counts)
                    {
                        counts[sig.Name] = counts.GetValueOrDefault(sig.Name) + 1;
                    }

                    if (channel is not null)
                    {
                        // 有限のキュー: 書き出しが追いつかないときは、ここで走査が待つ。
                        await channel.Writer.WriteAsync(store[number - 1], token).ConfigureAwait(false);
                    }
                    else if (exporter is not null)
                    {
                        exporter.WriteListOnly(store[number - 1]);
                    }
                }

                scanned += chunk.OwnedLength;
                currentOffset = chunk.OwnedEnd;
                Report(false);
            }

            try
            {
                foreach (SearchRange range in ranges)
                {
                    for (long position = range.Offset; position < range.End; position += options.ChunkSize)
                    {
                        token.ThrowIfCancellationRequested();
                        var chunk = new ScanChunk
                        {
                            Start = position,
                            OwnedEnd = Math.Min(position + options.ChunkSize, range.End),
                            RangeStart = range.Offset,
                            RangeEnd = range.End,
                            TotalBefore = beforeChunk,
                        };
                        beforeChunk += chunk.OwnedLength;
                        pending.Enqueue(Task.Run(() => ScanOne(data, plan, options, chunk, token), token));
                        while (pending.Count >= window)
                        {
                            await ConsumeAsync(pending.Dequeue()).ConfigureAwait(false);
                        }
                    }
                }

                while (pending.Count > 0)
                {
                    await ConsumeAsync(pending.Dequeue()).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }

            // 走査のタスクが残っていれば終わるのを待つ (キャンセル後に対象のデータを読み続けないため)。
            foreach (Task<ScanChunk> leftover in pending)
            {
                try
                {
                    await leftover.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            channel?.Writer.TryComplete();
            if (exportTask is not null)
            {
                try
                {
                    await exportTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            abortedByOutput = exporter?.Aborted == true;
            finalFolder = exporter?.Folder ?? options.OutputFolder;
            if (exporter is not null)
            {
                manifestPath = exporter.Finish(unreadable);
                manifestHash = manifestPath is null ? null : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifestPath)));
            }

            Report(true);
        }
        finally
        {
            exporter?.Dispose();
            // 走査のタスクが残っていれば止める。
            abort.Cancel();
        }

        bool cancelled = cancellationToken.IsCancellationRequested || abortedByOutput;
        lock (counts)
        {
            audit?.Complete(abortedByOutput ? AuditResult.Failure : cancelled ? AuditResult.Cancelled : AuditResult.Success,
                abortedByOutput ? "Stopped because the output folder could not be written." : string.Empty,
                AuditRecord.Args(("files", store.Count.ToString(CultureInfo.InvariantCulture)), ("scanned", scanned.ToString(CultureInfo.InvariantCulture)),
                    ("manifest", manifestPath ?? string.Empty), ("manifestSha256", manifestHash ?? string.Empty)));
            return new CarvingResult
            {
                Files = store,
                Unreadable = unreadable,
                Cancelled = cancelled,
                AbortedByOutputProblem = abortedByOutput,
                BytesScanned = scanned,
                TotalBytes = total,
                Elapsed = started.Elapsed,
                ManifestPath = manifestPath,
                ManifestSha256 = manifestHash,
                CountsByType = new Dictionary<string, long>(counts),
                OutputFolder = finalFolder,
            };
        }
    }

    // ---- 走査 ----

    private static ScanChunk ScanOne(ICarveData data, Plan plan, CarvingOptions options, ScanChunk chunk, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long readEnd = Math.Min(chunk.RangeEnd, chunk.OwnedEnd + Math.Max(0, plan.MaxHeaderLength - 1));
        int length = (int)(readEnd - chunk.Start);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        try
        {
            ReadResult read = data.Read(chunk.Start, buffer.AsSpan(0, length));
            foreach (UnreadableRange u in read.Unreadable)
            {
                chunk.Unreadable.Add(u with { Offset = u.Offset });
            }

            int got = Math.Min(read.BytesReturned, length);
            ReadOnlySpan<byte> span = buffer.AsSpan(0, got);
            long bufferStart = chunk.Start;
            int sector = Math.Max(1, options.SectorSize);

            // セクタ境界のみの定義: 各セクタの先頭 (ヘッダーの位置を引いた位置) だけを比べる。
            foreach ((long offset, (List<HeaderEntry>[] byFirst, List<HeaderEntry> wild)) in plan.Aligned)
            {
                long firstStart = ((chunk.Start - offset) + sector - 1) / sector * sector;
                if (firstStart < 0)
                {
                    firstStart = 0;
                }

                for (long fileStart = firstStart; fileStart + offset < chunk.OwnedEnd; fileStart += sector)
                {
                    long hp = fileStart + offset;
                    if (hp < chunk.Start || fileStart < chunk.RangeStart)
                    {
                        continue;
                    }

                    int index = (int)(hp - bufferStart);
                    if (index >= got)
                    {
                        break;
                    }

                    foreach (HeaderEntry entry in byFirst[span[index]])
                    {
                        TryAdd(chunk, span, bufferStart, entry, hp, fileStart);
                    }

                    foreach (HeaderEntry entry in wild)
                    {
                        TryAdd(chunk, span, bufferStart, entry, hp, fileStart);
                    }
                }
            }

            // セクタ境界を問わない定義: ヘッダーの固定部分の先頭バイトで足がかりを探し、全体を比べる。
            if (plan.UnalignedFirstBytes is { } firstBytes)
            {
                int i = 0;
                while (i < got)
                {
                    int j = span[i..].IndexOfAny(firstBytes);
                    if (j < 0)
                    {
                        break;
                    }

                    int at = i + j;
                    foreach (HeaderEntry entry in plan.UnalignedByAnchor[span[at]])
                    {
                        long hp = bufferStart + at - entry.Pattern.AnchorOffset;
                        if (hp < chunk.Start || hp >= chunk.OwnedEnd)
                        {
                            continue;
                        }

                        long fileStart = hp - entry.Offset;
                        if (fileStart < chunk.RangeStart)
                        {
                            continue;
                        }

                        TryAdd(chunk, span, bufferStart, entry, hp, fileStart);
                    }

                    i = at + 1;
                }
            }

            if (chunk.Unreadable.Count > 0)
            {
                // 読めなかったバイトを含むヘッダーは一致とみなさない。
                chunk.Candidates.RemoveAll(c => chunk.Unreadable.Any(u => c.HeaderPosition < u.End && c.HeaderPosition + c.HeaderLength > u.Offset));
            }

            chunk.Candidates.Sort((a, b) => a.FileStart != b.FileStart ? a.FileStart.CompareTo(b.FileStart) : a.Signature.CompareTo(b.Signature));
            return chunk;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void TryAdd(ScanChunk chunk, ReadOnlySpan<byte> span, long bufferStart, HeaderEntry entry, long headerPosition, long fileStart)
    {
        long index = headerPosition - bufferStart;
        if (index < 0 || index + entry.Pattern.Length > span.Length)
        {
            return;
        }

        if (entry.Pattern.MatchesAt(span[(int)index..]))
        {
            chunk.Candidates.Add(new Candidate(fileStart, headerPosition, entry.Signature, entry.Pattern.Length));
        }
    }

    // ---- 長さの決定 ----

    private static bool TryResolve(ICarveData data, Plan plan, CarvingOptions options, Candidate c, long rangeEnd, CancellationToken token, out long length, out LengthMethod method)
    {
        length = 0;
        method = LengthMethod.MaxSize;
        CarvingSignature sig = plan.Signatures[c.Signature];
        long maxEnd = c.FileStart > long.MaxValue - sig.MaxSize ? long.MaxValue : c.FileStart + sig.MaxSize;
        long limit = Math.Min(Math.Min(rangeEnd, maxEnd), data.Length);
        if (limit <= c.FileStart)
        {
            return false;
        }

        long budget = Math.Min(512L << 20, 64L << 20 | (limit - c.FileStart) * 16);
        var window = new ByteWindow(data, limit, budget, token);
        if (sig.Structure is { Length: > 0 } structure)
        {
            IStructureValidator? validator = options.Resolver.Resolve(structure);
            long? measured = validator?.Measure(window, c.FileStart);
            if (measured is not { } m)
            {
                return false;
            }

            length = m;
            method = LengthMethod.Structure;
        }
        else if (sig.SizeField is { } rule)
        {
            if (!window.TryUInt(c.FileStart + rule.Offset, rule.Bits / 8, rule.BigEndian, out ulong value) || value > (ulong)long.MaxValue / 2)
            {
                return false;
            }

            long size = (long)value + rule.Add;
            if (size <= 0 || size > sig.MaxSize)
            {
                return false;
            }

            length = Math.Min(size, limit - c.FileStart);
            method = LengthMethod.SizeField;
        }
        else if (plan.Footers[c.Signature] is { } footer)
        {
            long from = c.HeaderPosition + c.HeaderLength;
            long at = from < limit ? ByteSearch.Find(data, from, limit, footer, token) : -1;
            if (at >= 0)
            {
                length = Math.Min(at - c.FileStart + sig.FooterIncludeLength(footer), limit - c.FileStart);
                method = LengthMethod.Footer;
            }
            else
            {
                length = limit - c.FileStart;
                method = LengthMethod.MaxSize;
            }
        }
        else
        {
            length = limit - c.FileStart;
            method = LengthMethod.MaxSize;
        }

        return length >= Math.Max(Math.Max(sig.MinSize, 1), sig.HeaderOffset + c.HeaderLength);
    }

    // ---- 書き出しとマニフェスト ----

    private sealed class Exporter : IDisposable
    {
        private readonly ICarveData _data;
        private readonly CarvingOptions _options;
        private readonly CarvedFileStore _store;
        private readonly List<UnreadableRange> _unreadable;
        private readonly CancellationTokenSource _abort;
        private readonly StreamWriter? _manifest;
        private readonly string _manifestFolder;
        private long _exported;
        private int _sinceFlush;

        public Exporter(ICarveData data, CarvingOptions options, string folder, CarvedFileStore store, List<UnreadableRange> unreadable, CancellationTokenSource abort)
        {
            _data = data;
            _options = options;
            _store = store;
            _unreadable = unreadable;
            _abort = abort;
            Folder = folder;
            _manifestFolder = folder;
            string path = Path.Combine(folder, ManifestFileName);
            // 書き込み保護モード (FOR-01): 保護の対象 (開いているデータソース・証拠) には書かない。
            HexEditor.Core.Forensics.WriteBlock.WriteBlockGate.Current.EnsureFileWrite(path);
            _manifest = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            _manifest.Write(string.Join(',', ManifestHeader) + "\r\n");
            _manifest.Flush();
            ManifestPath = path;
        }

        public string Folder { get; private set; }

        public string ManifestPath { get; }

        public bool Aborted { get; private set; }

        public long ExportedCount => Interlocked.Read(ref _exported);

        public async Task RunAsync(ChannelReader<CarvedFile> reader, CancellationToken token)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(ExportBuffer);
            try
            {
                await foreach (CarvedFile file in reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    if (!await ExportOneAsync(file, buffer, token).ConfigureAwait(false))
                    {
                        break;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>1 件を書き出す。続けられなければ false。</summary>
        private async Task<bool> ExportOneAsync(CarvedFile file, byte[] buffer, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                OutputProblemInfo? problem = null;
                string target = Path.Combine(Folder, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    long? free = (_options.FreeSpace ?? DefaultFreeSpace)(Folder);
                    if (free is { } f && f < file.Length + _options.FreeSpaceReserve)
                    {
                        problem = new OutputProblemInfo(OutputProblem.NotEnoughSpace, Folder, file.Length, f, "Not enough free space.");
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        (string md5, string sha256) = CopyOut(file, target, buffer, token);
                        _store.MarkExported(file.Number);
                        Interlocked.Increment(ref _exported);
                        WriteRow(file, md5, sha256, target);
                        return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    TryDelete(target);
                    throw;
                }
                catch (IOException ex) when (IsDiskFull(ex))
                {
                    TryDelete(target);
                    problem = new OutputProblemInfo(OutputProblem.NotEnoughSpace, Folder, file.Length, 0, ex.Message);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    TryDelete(target);
                    problem = new OutputProblemInfo(OutputProblem.CannotWrite, Folder, file.Length, 0, ex.Message);
                }

                OutputDecision decision = _options.OnOutputProblem is null
                    ? new OutputDecision(OutputDecisionKind.Abort)
                    : await _options.OnOutputProblem(problem, token).ConfigureAwait(false);
                switch (decision.Kind)
                {
                    case OutputDecisionKind.Retry:
                        continue;
                    case OutputDecisionKind.ChangeFolder when !string.IsNullOrWhiteSpace(decision.NewFolder):
                        Folder = decision.NewFolder!;
                        Directory.CreateDirectory(Folder);
                        continue;
                    default:
                        Aborted = true;
                        _abort.Cancel();
                        return false;
                }
            }
        }

        private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 0x27 or 0x70;

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

        private static long? DefaultFreeSpace(string folder)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(folder));
                return string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ? null : new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                return null;
            }
        }

        private (string Md5, string Sha256) CopyOut(CarvedFile file, string target, byte[] buffer, CancellationToken token)
        {
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            HexEditor.Core.Forensics.WriteBlock.WriteBlockGate.Current.EnsureFileWrite(target);
            using var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read, 1, FileOptions.SequentialScan);
            long done = 0;
            while (done < file.Length)
            {
                token.ThrowIfCancellationRequested();
                int count = (int)Math.Min(buffer.Length, file.Length - done);
                ReadResult r = _data.Read(file.Offset + done, buffer.AsSpan(0, count));
                if (r.BytesReturned <= 0)
                {
                    break;
                }

                foreach (UnreadableRange u in r.Unreadable)
                {
                    lock (_unreadable)
                    {
                        _unreadable.Add(u);
                    }
                }

                stream.Write(buffer, 0, r.BytesReturned);
                md5.AppendData(buffer, 0, r.BytesReturned);
                sha.AppendData(buffer, 0, r.BytesReturned);
                done += r.BytesReturned;
            }

            stream.Flush();
            return (Convert.ToHexStringLower(md5.GetHashAndReset()), Convert.ToHexStringLower(sha.GetHashAndReset()));
        }

        private void WriteRow(CarvedFile file, string md5, string sha256, string target)
        {
            string output = Path.GetRelativePath(_manifestFolder, target).Replace('\\', '/');
            if (Path.IsPathRooted(output) || output.StartsWith("..", StringComparison.Ordinal))
            {
                output = target;
            }

            lock (_manifest!)
            {
                _manifest.Write(string.Join(',', new[]
                {
                    file.Number.ToString(CultureInfo.InvariantCulture), file.Type, file.Offset.ToString(CultureInfo.InvariantCulture), file.Lba.ToString(CultureInfo.InvariantCulture),
                    file.Length.ToString(CultureInfo.InvariantCulture), file.Method.ToString(), md5, sha256, output,
                }.Select(AuditExporter.CsvEscape)) + "\r\n");
                if (++_sinceFlush >= 16)
                {
                    _manifest.Flush();
                    _sinceFlush = 0;
                }
            }
        }

        /// <summary>「一覧だけ作る」場合の行 (ハッシュと書き出し先は空)。</summary>
        public void WriteListOnly(CarvedFile file)
        {
            lock (_manifest!)
            {
                _manifest.Write(string.Join(',', new[]
                {
                    file.Number.ToString(CultureInfo.InvariantCulture), file.Type, file.Offset.ToString(CultureInfo.InvariantCulture), file.Lba.ToString(CultureInfo.InvariantCulture),
                    file.Length.ToString(CultureInfo.InvariantCulture), file.Method.ToString(), string.Empty, string.Empty, string.Empty,
                }.Select(AuditExporter.CsvEscape)) + "\r\n");
            }
        }

        /// <summary>読めなかった範囲を末尾に記録して閉じる。マニフェストのパス。</summary>
        public string? Finish(IReadOnlyList<UnreadableRange> unreadable)
        {
            lock (_manifest!)
            {
                foreach (UnreadableRange u in unreadable.OrderBy(u => u.Offset))
                {
                    _manifest.Write(FormattableString.Invariant($"#unreadable,{u.Offset},{u.Length},{u.Reason}\r\n"));
                }

                _manifest.Flush();
                _manifest.Dispose();
            }

            return ManifestPath;
        }

        public void Dispose()
        {
            try
            {
                _manifest?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
