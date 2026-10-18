using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;

namespace HexEditor.Core.Tools.Files;

/// <summary>デインターリーブで入力の長さが「N × 単位」の倍数でないときの扱い (TOOL-15 の仕様 1)。</summary>
public enum RemainderMode
{
    /// <summary>最後の不足分を指定値で埋め、すべての出力を同じ長さにする (既定)。</summary>
    Pad,

    /// <summary>端数 (入力の末尾の、N × 単位に満たない部分) を最後の出力の末尾にそのまま入れる。</summary>
    KeepInLast,
}

/// <summary>デインターリーブの条件 (TOOL-15 の仕様 1)。</summary>
public sealed record DeinterleaveOptions
{
    /// <summary>分ける数 (2〜16)。</summary>
    public int Count { get; init; } = 2;

    /// <summary>単位のバイト数 (1 以上。1 / 2 / 4 / 8 または任意)。</summary>
    public int Unit { get; init; } = 1;

    public RemainderMode Remainder { get; init; } = RemainderMode.Pad;

    public byte PadValue { get; init; } = 0xFF;

    public string NameFormat { get; init; } = FileNameTemplate.DeinterleaveDefault;

    public string? OutputDirectory { get; init; }
}

public sealed record DeinterleaveOutput(int Index, string FileName, long Length);

public sealed record DeinterleavePlan(IReadOnlyList<DeinterleaveOutput> Outputs, string OutputDirectory, long InputLength)
{
    public long TotalBytes => Outputs.Sum(o => o.Length);

    public string PathOf(DeinterleaveOutput output) => Path.Combine(OutputDirectory, output.FileName);

    public IReadOnlyList<string> ExistingFiles => [.. Outputs.Select(PathOf).Where(File.Exists)];
}

/// <summary>インターリーブで入力の長さが違うときの扱い (TOOL-15 の仕様 2)。</summary>
public enum LengthMismatchMode
{
    /// <summary>短いものを指定値で埋める (既定)。</summary>
    Pad,

    /// <summary>エラーにする。</summary>
    Error,
}

public sealed record InterleaveOptions
{
    public int Unit { get; init; } = 1;

    public LengthMismatchMode Mismatch { get; init; } = LengthMismatchMode.Pad;

    public byte PadValue { get; init; } = 0xFF;
}

/// <summary>インターリーブとデインターリーブ (TOOL-15)。ストリームで処理する (入力・出力ごとに 1 MiB のバッファ)。</summary>
public static class Interleaver
{
    public const int MaxCount = 16;

    /// <summary>入力・出力ごとのバッファの大きさ。</summary>
    public const int BufferPerStream = 1024 * 1024;

    /// <summary>単位の上限 (N × 単位がバッファに収まる範囲)。</summary>
    public const int MaxUnit = 1024 * 1024;

    // ---- デインターリーブ ----

    public static DeinterleavePlan PlanDeinterleave(long length, string sourceName, string defaultDirectory, DeinterleaveOptions options)
    {
        if (options.Count is < 2 or > MaxCount)
        {
            throw new FileToolException(FileToolError.InvalidArgument, "分ける数は 2〜16 にしてください。");
        }

        if (options.Unit is < 1 or > MaxUnit)
        {
            throw new FileToolException(FileToolError.InvalidArgument, "単位は 1 バイト以上 1 MiB 以下にしてください。");
        }

        if (length <= 0)
        {
            throw new FileToolException(FileToolError.NothingToDo, "分けるデータがありません。");
        }

        IReadOnlyList<string>? names = FileNameTemplate.ExpandAll(options.NameFormat, sourceName, Enumerable.Range(0, options.Count).Select(i => (long)i).ToList());
        if (names is null)
        {
            throw new FileToolException(FileToolError.InvalidNames, "ファイル名の形式から、重ならない名前を作れません。");
        }

        long stride = (long)options.Count * options.Unit;
        long full = length / stride;
        long rest = length % stride;
        var outputs = new List<DeinterleaveOutput>(options.Count);
        for (int k = 0; k < options.Count; k++)
        {
            long size = full * options.Unit;
            if (options.Remainder == RemainderMode.Pad)
            {
                size += rest > 0 ? options.Unit : 0;
            }
            else if (k == options.Count - 1)
            {
                size += rest;
            }

            outputs.Add(new DeinterleaveOutput(k + 1, names[k], size));
        }

        return new DeinterleavePlan(outputs, Path.GetFullPath(options.OutputDirectory is { Length: > 0 } d ? d : defaultDirectory), length);
    }

    public static SpaceShortage? CheckSpace(DeinterleavePlan plan, IVolumeInfoProvider? volumes = null)
    {
        Directory.CreateDirectory(plan.OutputDirectory);
        VolumeInfo? volume = (volumes ?? SystemVolumeInfoProvider.Instance).GetVolume(plan.OutputDirectory);
        long required = plan.TotalBytes - plan.ExistingFiles.Sum(f => new FileInfo(f).Length);
        return volume?.AvailableFreeSpace is long available && available < required ? new SpaceShortage(volume.Name, required, available) : null;
    }

    public static SplitResult Deinterleave(DeinterleavePlan plan, IContentReader input, DeinterleaveOptions options, LongRunningOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        int n = options.Count;
        int unit = options.Unit;
        long stride = (long)n * unit;
        long stripes = Math.Max(1, BufferPerStream / unit);
        int readSize = (int)(stripes * stride);

        var completed = new List<string>();
        var temps = new string[n];
        var streams = new FileStream[n];
        operation?.SetTotal(plan.InputLength);
        try
        {
            Directory.CreateDirectory(plan.OutputDirectory);
            for (int k = 0; k < n; k++)
            {
                temps[k] = plan.PathOf(plan.Outputs[k]) + ".~part";
                streams[k] = new FileStream(temps[k], FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.SequentialScan);
            }

            byte[] read = new byte[readSize];
            byte[][] parts = new byte[n][];
            for (int k = 0; k < n; k++)
            {
                parts[k] = new byte[stripes * unit];
            }

            long position = 0;
            while (position < plan.InputLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int got = input.Read(position, read);
                if (got <= 0)
                {
                    throw new IOException("入力を最後まで読めませんでした。");
                }

                int fullStripes = got / (int)stride;
                for (int s = 0; s < fullStripes; s++)
                {
                    for (int k = 0; k < n; k++)
                    {
                        read.AsSpan((int)(s * stride + (long)k * unit), unit).CopyTo(parts[k].AsSpan(s * unit));
                    }
                }

                int used = fullStripes * unit;
                int tail = got - fullStripes * (int)stride;
                for (int k = 0; k < n; k++)
                {
                    streams[k].Write(parts[k], 0, used);
                }

                if (tail > 0)
                {
                    // 入力の末尾の端数 (N × 単位に満たない部分)。
                    ReadOnlySpan<byte> rest = read.AsSpan(fullStripes * (int)stride, tail);
                    if (options.Remainder == RemainderMode.Pad)
                    {
                        for (int k = 0; k < n; k++)
                        {
                            byte[] chunk = new byte[unit];
                            Array.Fill(chunk, options.PadValue);
                            int from = k * unit;
                            if (from < tail)
                            {
                                rest.Slice(from, Math.Min(unit, tail - from)).CopyTo(chunk);
                            }

                            streams[k].Write(chunk, 0, unit);
                        }
                    }
                    else
                    {
                        streams[n - 1].Write(rest);
                    }
                }

                position += got;
                operation?.Report(position);
            }

            for (int k = 0; k < n; k++)
            {
                streams[k].Flush(flushToDisk: true);
                streams[k].Dispose();
                string target = plan.PathOf(plan.Outputs[k]);
                File.Move(temps[k], target, overwrite: true);
                temps[k] = string.Empty;
                completed.Add(target);
            }

            return new SplitResult(completed, false, null);
        }
        catch (OperationCanceledException)
        {
            Cleanup(streams, temps);
            return new SplitResult(completed, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Cleanup(streams, temps);
            return new SplitResult(completed, false, new FileToolException(FileToolError.WriteFailed, ex.Message, completed, ex));
        }
    }

    private static void Cleanup(FileStream?[] streams, string[] temps)
    {
        foreach (FileStream? s in streams)
        {
            s?.Dispose();
        }

        foreach (string t in temps)
        {
            if (!string.IsNullOrEmpty(t))
            {
                try
                {
                    File.Delete(t);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    // ---- インターリーブ ----

    /// <summary>インターリーブした結果の長さ。長さが違って「エラーにする」なら <see cref="FileToolException"/>。</summary>
    public static long InterleavedLength(IReadOnlyList<long> inputLengths, InterleaveOptions options)
    {
        if (inputLengths.Count is < 2 or > MaxCount)
        {
            throw new FileToolException(FileToolError.InvalidArgument, "入力は 2〜16 個にしてください。");
        }

        if (options.Unit is < 1 or > MaxUnit)
        {
            throw new FileToolException(FileToolError.InvalidArgument, "単位は 1 バイト以上 1 MiB 以下にしてください。");
        }

        long max = inputLengths.Max();
        if (options.Mismatch == LengthMismatchMode.Error && inputLengths.Any(l => l != max))
        {
            throw new FileToolException(FileToolError.LengthMismatch, "入力の長さが違います。");
        }

        long perInput = (max + options.Unit - 1) / options.Unit * options.Unit;
        return perInput * inputLengths.Count;
    }

    /// <summary>
    /// インターリーブして <paramref name="output"/> に順に書く。各入力から単位ずつ順に取り出す。短い入力は指定値で埋める。
    /// 出力は呼び出し側が開く (ファイル、またはドキュメントの一時ファイル)。
    /// </summary>
    public static void Interleave(IReadOnlyList<IContentReader> inputs, InterleaveOptions options, Stream output, LongRunningOperation? operation = null,
        CancellationToken cancellationToken = default)
    {
        long total = InterleavedLength([.. inputs.Select(i => i.Length)], options);
        operation?.SetTotal(total);
        int n = inputs.Count;
        int unit = options.Unit;
        long perInput = total / n;
        int unitsPerChunk = Math.Max(1, BufferPerStream / unit);
        byte[][] buffers = new byte[n][];
        for (int k = 0; k < n; k++)
        {
            buffers[k] = new byte[(long)unitsPerChunk * unit];
        }

        byte[] combined = new byte[(long)unitsPerChunk * unit * n];
        long written = 0;
        for (long start = 0; start < perInput; start += (long)unitsPerChunk * unit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chunk = (int)Math.Min((long)unitsPerChunk * unit, perInput - start);
            for (int k = 0; k < n; k++)
            {
                int got = inputs[k].Read(start, buffers[k].AsSpan(0, chunk));
                if (got < chunk)
                {
                    buffers[k].AsSpan(got, chunk - got).Fill(options.PadValue);
                }
            }

            int units = chunk / unit;
            for (int u = 0; u < units; u++)
            {
                for (int k = 0; k < n; k++)
                {
                    buffers[k].AsSpan(u * unit, unit).CopyTo(combined.AsSpan((u * n + k) * unit));
                }
            }

            output.Write(combined, 0, chunk * n);
            written += (long)chunk * n;
            operation?.Report(written);
        }
    }

    /// <summary>インターリーブの結果をファイルに保存する (一時ファイルに書いてから置き換える)。</summary>
    public static void InterleaveToFile(IReadOnlyList<IContentReader> inputs, InterleaveOptions options, string destination,
        LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        long total = InterleavedLength([.. inputs.Select(i => i.Length)], options);
        string folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(folder);
        VolumeInfo? volume = SystemVolumeInfoProvider.Instance.GetVolume(folder);
        long required = total + DocumentSaver.FreeSpaceMargin;
        if (volume?.AvailableFreeSpace is long available && available < required)
        {
            throw new FileToolException(FileToolError.InsufficientSpace, "空き容量が足りません。") { Space = new SpaceShortage(volume.Name, required, available) };
        }

        SafeFileWriter.Write(destination, output => Interleave(inputs, options, output, operation, cancellationToken));
    }

    /// <summary>インターリーブの結果を新しい (無題の) ドキュメントの内容にする。一定の大きさを超えたら一時ファイルに置く。</summary>
    public static EditContent InterleaveToContent(IReadOnlyList<IContentReader> inputs, InterleaveOptions options, Document target,
        LongRunningOperation? operation = null, CancellationToken cancellationToken = default)
    {
        long total = InterleavedLength([.. inputs.Select(i => i.Length)], options);
        using ContentSink sink = ContentSink.For(target);
        sink.ExpectedLength = total;
        using var adapter = new SinkStream(sink);
        Interleave(inputs, options, adapter, operation, cancellationToken);
        return sink.Complete();
    }

    private sealed class SinkStream(ContentSink sink) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => sink.Length;

        public override long Position
        {
            get => sink.Length;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => sink.Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => sink.Write(buffer);
    }
}
