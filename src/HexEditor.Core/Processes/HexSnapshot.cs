using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Processes;

/// <summary>スナップショットに含める対象 (ENG-35 の仕様 1)。</summary>
public enum SnapshotScope
{
    /// <summary>読めるすべての領域 (既定)。</summary>
    AllReadable,

    /// <summary>選んだ領域・モジュール。</summary>
    Selected,

    /// <summary>書き込み可能な領域だけ。</summary>
    WritableOnly,
}

/// <summary>スナップショットのメタデータ (ENG-35 の仕様 3)。</summary>
public sealed record SnapshotMetadata
{
    public required string ProcessName { get; init; }

    public int Pid { get; init; }

    public ProcessArchitecture Architecture { get; init; }

    public DateTime CapturedUtc { get; init; }

    public long AddressLimit { get; init; }

    public required IReadOnlyList<SnapshotRegion> Regions { get; init; }

    public IReadOnlyList<ProcessModule> Modules { get; init; } = [];

    /// <summary>読めなかったページ (データに含めない。ENG-35 の仕様 3)。</summary>
    public IReadOnlyList<SnapshotGap> UnreadablePages { get; init; } = [];
}

/// <summary>スナップショットの 1 領域 (アドレス・サイズ・保護属性・種類・モジュール名・ファイル内の位置)。</summary>
public sealed record SnapshotRegion(long BaseAddress, long Size, uint Protect, RegionType Type, string? ModuleName, long FileOffset);

/// <summary>読めなかった範囲。</summary>
public sealed record SnapshotGap(long BaseAddress, long Size);

/// <summary>スナップショットの保存の結果 (ENG-35)。</summary>
public sealed record SnapshotCaptureResult(SnapshotMetadata Metadata, ProcessCaptureSummary Summary);

/// <summary>
/// プロセスのスナップショットのファイル形式 (ENG-35 の仕様 3) の読み書き。
/// <code>[マジック "HXSNAP01" 8][メタデータの長さ u32][メタデータ (UTF-8 JSON)][データ (各領域は 4 KiB 境界から)]</code>
/// </summary>
public static class HexSnapshot
{
    public static readonly byte[] Magic = "HXSNAP01"u8.ToArray();

    public const string Extension = ".hexsnap";

    private const int Alignment = 4096;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// プロセスメモリのスナップショットを作り、<paramref name="path"/> に書く (ENG-35 の仕様 1〜3)。読めなかったページは
    /// メタデータに記録し、データには含めない。
    /// </summary>
    public static SnapshotMetadata Capture(ProcessMemoryByteSource source, string path, SnapshotScope scope = SnapshotScope.AllReadable,
        IReadOnlyCollection<long>? selectedRegionBases = null, LongRunningOperation? operation = null)
    {
        IReadOnlyList<(long, long)> selected = scope == SnapshotScope.Selected && selectedRegionBases is not null
            ? [.. source.MemoryRegions.Where(r => selectedRegionBases.Contains(r.BaseAddress)).Select(r => (r.BaseAddress, r.Size))]
            : [];
        return Write(source, path, new ProcessCaptureOptions { Scope = scope, SelectedRanges = selected, RefreshRegions = false }, operation).Metadata;
    }

    /// <summary>
    /// スナップショットを保存する (ENG-35 の仕様 1〜3、「巨大ファイル・長時間処理」「エラー」)。同じフォルダの一時ファイルに書き、完了したら
    /// 名前を変えて確定する。キャンセル・失敗・取得中のプロセスの終了では一時ファイルを削除し、<paramref name="path"/> の既存のファイルは
    /// 変えない。取得の前に空き容量を確かめる (<see cref="ProcessCaptureOptions.Volumes"/>)。
    /// </summary>
    public static SnapshotCaptureResult Write(ProcessMemoryByteSource source, string path, ProcessCaptureOptions options,
        LongRunningOperation? operation = null)
    {
        string temp = ProcessCapture.TempPathFor(path);
        try
        {
            SnapshotCaptureResult result = WriteCore(source, temp, options, operation);
            ProcessCapture.Commit(temp, path);
            return result;
        }
        catch
        {
            // 途中で失敗した (キャンセル・権限不足・書き込みの失敗・プロセスの終了): 作りかけのファイルを残さない。
            ProcessCapture.TryDelete(temp);
            throw;
        }
    }

    private static SnapshotCaptureResult WriteCore(ProcessMemoryByteSource source, string path, ProcessCaptureOptions options,
        LongRunningOperation? operation)
    {
        using ProcessCapture.PauseScope pause = ProcessCapture.Pause(source.Memory, options.PauseTarget);
        if (options.RefreshRegions)
        {
            try
            {
                source.RefreshRegions();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // 取り直せない (終了した): 開いたときの一覧で続け、読み込みで終了を検出する。
            }
        }

        if (source.HasExited || source.Memory.HasExited)
        {
            throw new ProcessExitedDuringCaptureException();
        }

        IReadOnlyList<MemoryRegion> regions = ProcessCapture.SelectRegions(source.MemoryRegions, options);
        IReadOnlyList<ProcessModule> modules = source.Modules;
        long total = regions.Sum(r => r.Size);
        long metadataReserve = AlignUp(4096 + (regions.Count * 200L) + (modules.Count * 400L), Alignment);
        ProcessCapture.CheckSpace(path, metadataReserve + total + (regions.Count * (long)Alignment), options.Volumes);
        operation?.SetTotal(total);

        var written = new List<SnapshotRegion>();
        var gaps = new List<SnapshotGap>();
        byte[] buffer = new byte[ProcessCapture.ChunkSize];
        long done = 0;
        bool denied = false;
        long dataStart;
        long headerEnd = Magic.Length + 4;
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            // メタデータの長さとメタデータは後で書くため、見積もった分を空ける。領域のデータは 4 KiB 境界から始める。
            stream.Write(Magic);
            stream.Write(stackalloc byte[4]);
            dataStart = AlignUp(headerEnd + metadataReserve, Alignment);
            stream.SetLength(dataStart);
            stream.Position = dataStart;

            foreach (MemoryRegion region in regions)
            {
                operation?.CancellationToken.ThrowIfCancellationRequested();
                long fileOffset = AlignUp(stream.Position, Alignment);
                stream.Position = fileOffset;
                int gapsBefore = gaps.Count;
                long gapBytesBefore = gaps.Sum(g => g.Size);
                for (long pos = 0; pos < region.Size; pos += buffer.Length)
                {
                    operation?.CancellationToken.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(buffer.Length, region.Size - pos);
                    ProcessCapture.ReadChunk(source.Memory, region.BaseAddress + pos, buffer.AsSpan(0, n), gaps, ref denied);

                    // 読めなかったページは 0 のまま位置だけ進める (データには含めない。位置は領域の先頭からの位置で決まる)。
                    stream.Write(buffer, 0, n);
                    done += n;
                    operation?.Report(done);
                }

                bool allUnreadable = gaps.Sum(g => g.Size) - gapBytesBefore >= region.Size && gaps.Count > gapsBefore;
                if (!allUnreadable)
                {
                    written.Add(new SnapshotRegion(region.BaseAddress, region.Size, region.Protect, region.Type, region.MappedName, fileOffset));
                }
            }

            // どのページも読めず、権限不足で読めなかったページがある: プロセスを読む権限がない (ANA-09 の「エラー」。呼び出し側が昇格した
            // 補助プロセスでの再試行を提案する)。
            if (written.Count == 0 && denied)
            {
                throw new ProcessAccessException(ProcessOpenFailure.AccessDenied, Devices.Win32Errors.AccessDenied, "Access denied.");
            }
        }

        var metadata = new SnapshotMetadata
        {
            ProcessName = source.ProcessName,
            Pid = source.Pid,
            Architecture = source.Memory.Architecture,
            CapturedUtc = DateTime.UtcNow,
            AddressLimit = source.Memory.AddressLimit,
            Regions = written,
            Modules = modules,
            UnreadablePages = gaps,
        };
        metadata = WriteMetadata(path, metadata, dataStart, operation);
        (int unreadableRegions, long unreadableBytes) = ProcessCapture.Summarize(regions, gaps);
        return new SnapshotCaptureResult(metadata, new ProcessCaptureSummary
        {
            PauseFailed = pause.Failed,
            UnreadableRegions = unreadableRegions,
            UnreadableBytes = unreadableBytes,
            Regions = written.Count,
            CapturedBytes = total - unreadableBytes,
        });
    }

    /// <summary>
    /// メタデータを書く。見積もった領域に収まらない場合 (読めなかったページが非常に多い) は、データ部をずらした新しいファイルに写す
    /// (各領域の位置は 4 KiB の倍数だけずらすので、4 KiB 境界のまま)。
    /// </summary>
    private static SnapshotMetadata WriteMetadata(string path, SnapshotMetadata metadata, long dataStart, LongRunningOperation? operation)
    {
        long headerEnd = Magic.Length + 4;
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(metadata, Json);
        if (headerEnd + json.Length <= dataStart)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.Position = Magic.Length;
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(size, json.Length);
            stream.Write(size);
            stream.Write(json);
            return metadata;
        }

        // 位置の桁が増えても収まるよう、余裕を持ってずらす。
        long newStart = AlignUp(headerEnd + json.Length + 4096, Alignment);
        long delta = newStart - dataStart;
        SnapshotMetadata moved = metadata with { Regions = [.. metadata.Regions.Select(r => r with { FileOffset = r.FileOffset + delta })] };
        json = JsonSerializer.SerializeToUtf8Bytes(moved, Json);
        string relocated = path + ".move";
        try
        {
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var target = new FileStream(relocated, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                target.Write(Magic);
                Span<byte> size = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(size, json.Length);
                target.Write(size);
                target.Write(json);
                target.SetLength(newStart);
                target.Position = newStart;
                source.Position = dataStart;
                byte[] buffer = new byte[ProcessCapture.ChunkSize];
                int n;
                while ((n = source.Read(buffer)) > 0)
                {
                    operation?.CancellationToken.ThrowIfCancellationRequested();
                    target.Write(buffer, 0, n);
                }
            }

            File.Move(relocated, path, overwrite: true);
            return moved;
        }
        catch
        {
            ProcessCapture.TryDelete(relocated);
            throw;
        }
    }

    /// <summary>`.hexsnap` か (先頭のマジックで判断する。比較の「ファイルを選択...」でスナップショットを領域ごとに比べるため。ANA-09 の仕様 3)。</summary>
    public static bool IsSnapshotFile(string path)
    {
        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> magic = stackalloc byte[8];
            return stream.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) == magic.Length && magic.SequenceEqual(Magic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>スナップショットのメタデータを読む。</summary>
    public static SnapshotMetadata ReadMetadata(Stream stream)
    {
        Span<byte> magic = stackalloc byte[8];
        stream.ReadExactly(magic);
        if (!magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a .hexsnap file.");
        }

        Span<byte> size = stackalloc byte[4];
        stream.ReadExactly(size);
        byte[] json = new byte[BinaryPrimitives.ReadInt32LittleEndian(size)];
        stream.ReadExactly(json);
        return JsonSerializer.Deserialize<SnapshotMetadata>(json, Json) ?? throw new InvalidDataException("Empty snapshot metadata.");
    }

    private static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;
}
