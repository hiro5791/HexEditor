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
        IReadOnlyList<MemoryRegion> regions = [.. source.MemoryRegions.Where(r => Include(r, scope, selectedRegionBases)).OrderBy(r => r.BaseAddress)];
        long total = regions.Sum(r => r.Size);
        operation?.SetTotal(total);

        var written = new List<SnapshotRegion>();
        var gaps = new List<SnapshotGap>();
        byte[] buffer = new byte[ProcessMemoryByteSource.MaxTransfer];
        long done = 0;
        bool denied = false;

        try
        {
            return Write();
        }
        catch
        {
            // 途中で失敗した (キャンセル・権限不足・書き込みの失敗): 作りかけのファイルを残さない。
            TryDelete(path);
            throw;
        }

        SnapshotMetadata Write()
        {
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            // メタデータの長さは後で書くため、まず仮のヘッダ分を空ける。領域のデータは 4 KiB 境界から始める。
            stream.Write(Magic);
            Span<byte> placeholder = stackalloc byte[4];
            stream.Write(placeholder);
            long headerEnd = stream.Position;

            // データ部の開始を 4 KiB に揃える。メタデータの大きさが分からないため、十分な見積もりで空ける。
            long metadataReserve = AlignUp(2048 + (regions.Count * 160L) + (source.Modules.Count * 256L), Alignment);
            long dataStart = AlignUp(headerEnd + metadataReserve, Alignment);
            stream.SetLength(dataStart);
            stream.Position = dataStart;

            foreach (MemoryRegion region in regions)
            {
                operation?.CancellationToken.ThrowIfCancellationRequested();
                long fileOffset = AlignUp(stream.Position, Alignment);
                stream.Position = fileOffset;
                long regionBaseOffset = region.BaseAddress - source.BaseAddress;
                bool anyWritten = false;
                for (long pos = 0; pos < region.Size; pos += buffer.Length)
                {
                    int n = (int)Math.Min(buffer.Length, region.Size - pos);
                    ReadResult read = source.Read(regionBaseOffset + pos, buffer.AsSpan(0, n));
                    if (read.IsComplete)
                    {
                        stream.Write(buffer, 0, n);
                        anyWritten = true;
                    }
                    else
                    {
                        // 読めなかったページはデータに含めず、メタデータに記録する。
                        gaps.Add(new SnapshotGap(region.BaseAddress + pos, n));
                        denied |= read.Unreadable.Any(u => u.Reason == UnreadableReason.AccessDenied || u.ErrorCode == Devices.Win32Errors.AccessDenied);
                        stream.Position += n;
                    }

                    done += n;
                    operation?.Report(done);
                }

                if (anyWritten)
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

            var metadata = new SnapshotMetadata
            {
                ProcessName = source.ProcessName,
                Pid = source.Pid,
                Architecture = source.Memory.Architecture,
                CapturedUtc = DateTime.UtcNow,
                AddressLimit = source.Memory.AddressLimit,
                Regions = written,
                Modules = source.Modules,
                UnreadablePages = gaps,
            };
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(metadata, Json);
            if (headerEnd + json.Length > dataStart)
            {
                throw new IOException("The snapshot metadata did not fit in the reserved header.");
            }

            stream.Position = Magic.Length;
            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(size, json.Length);
            stream.Write(size);
            stream.Write(json);
            return metadata;
        }
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

    private static bool Include(MemoryRegion region, SnapshotScope scope, IReadOnlyCollection<long>? selected) =>
        region.State == RegionState.Commit && region.IsReadable && scope switch
        {
            SnapshotScope.WritableOnly => region.IsWritable,
            SnapshotScope.Selected => selected is not null && selected.Contains(region.BaseAddress),
            _ => true,
        };

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
