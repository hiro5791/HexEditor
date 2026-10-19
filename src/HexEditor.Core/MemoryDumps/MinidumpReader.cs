using System.Buffers.Binary;
using System.Text;
using HexEditor.Core.Processes;

namespace HexEditor.Core.MemoryDumps;

/// <summary>
/// ミニダンプ (MDMP) のメモリの範囲・モジュールの一覧を読み、プロセスの仮想アドレス空間のドキュメントにする (01 の ENG-36 の仕様 4)。
/// ENG-35 で保存したミニダンプと、他のツール (WinDbg、procdump、タスクマネージャーなど) のものを読む。読むのはストリームの一覧と
/// 範囲・モジュールの表だけで、メモリの中身は表示するときに読む (<see cref="SnapshotByteSource"/> と同じ形で開く)。
/// <code>
/// 見出し: 0 "MDMP"、8 ストリームの数、12 ストリームの一覧の位置、20 作成日時 (time_t)
/// ストリームの一覧: [種類 u32][大きさ u32][位置 u32]
/// 4 ModuleList、5 MemoryList、7 SystemInfo、9 Memory64List、15 MiscInfo、16 MemoryInfoList
/// </code>
/// 信頼できない入力として読む: 数・大きさ・位置をファイルの大きさと上限で確かめ、違えば <see cref="MemoryDumpFormatException"/>。
/// </summary>
public static class MinidumpReader
{
    public const uint Signature = 0x504D444D; // "MDMP"
    public const int MaxStreams = 4096;
    public const int MaxRanges = 1_000_000;
    public const int MaxModules = 65536;

    private const uint ModuleListStream = 4;
    private const uint MemoryListStream = 5;
    private const uint SystemInfoStream = 7;
    private const uint Memory64ListStream = 9;
    private const uint MiscInfoStream = 15;
    private const uint MemoryInfoListStream = 16;

    /// <summary>ミニダンプを読んで、スナップショットと同じ形のメタデータにする。</summary>
    public static SnapshotMetadata Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        return Read(stream, Path.GetFileName(path));
    }

    public static SnapshotMetadata Read(Stream stream, string fallbackName)
    {
        long fileLength = stream.Length;
        byte[] head = ReadAt(stream, 0, 32) ?? throw new MemoryDumpFormatException("The minidump header is truncated.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != Signature)
        {
            throw new MemoryDumpFormatException("This is not a minidump.");
        }

        uint streams = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
        uint directory = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
        uint time = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(20));
        if (streams > MaxStreams || (long)directory + streams * 12L > fileLength)
        {
            throw new MemoryDumpFormatException("The minidump stream directory is invalid.");
        }

        byte[] dir = ReadAt(stream, directory, (int)(streams * 12)) ?? throw new MemoryDumpFormatException("The minidump stream directory is truncated.");
        var found = new Dictionary<uint, (uint Size, uint Rva)>();
        for (int i = 0; i < streams; i++)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(i * 12));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(i * 12 + 4));
            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(i * 12 + 8));
            if (type != 0 && (long)rva + size <= fileLength)
            {
                found.TryAdd(type, (size, rva));
            }
        }

        ProcessArchitecture arch = ProcessArchitecture.Unknown;
        if (found.TryGetValue(SystemInfoStream, out var sys) && sys.Size >= 2 && ReadAt(stream, sys.Rva, 2) is { } s)
        {
            arch = BinaryPrimitives.ReadUInt16LittleEndian(s) switch
            {
                0 => ProcessArchitecture.X86,
                5 => ProcessArchitecture.Arm,
                9 => ProcessArchitecture.X64,
                12 => ProcessArchitecture.Arm64,
                _ => ProcessArchitecture.Unknown,
            };
        }

        int pid = 0;
        if (found.TryGetValue(MiscInfoStream, out var misc) && misc.Size >= 12 && ReadAt(stream, misc.Rva, 12) is { } m
            && (BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(4)) & 1) != 0)
        {
            pid = (int)BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(8));
        }

        List<ProcessModule> modules = found.TryGetValue(ModuleListStream, out var ml) ? ReadModules(stream, ml.Rva, ml.Size, fileLength) : [];
        Dictionary<long, (uint Protect, RegionType Type)> info = found.TryGetValue(MemoryInfoListStream, out var mi) ? ReadMemoryInfo(stream, mi.Rva, mi.Size) : [];
        List<(long Base, long Size, long FileOffset)> ranges;
        if (found.TryGetValue(Memory64ListStream, out var m64))
        {
            ranges = ReadMemory64(stream, m64.Rva, m64.Size, fileLength);
        }
        else if (found.TryGetValue(MemoryListStream, out var m32))
        {
            ranges = ReadMemory32(stream, m32.Rva, m32.Size, fileLength);
        }
        else
        {
            throw new MemoryDumpFormatException("The minidump has no memory list.");
        }

        ranges.Sort((a, b) => a.Base.CompareTo(b.Base));
        var regions = new List<SnapshotRegion>(ranges.Count);
        long previousEnd = 0;
        foreach ((long baseAddress, long size, long fileOffset) in ranges)
        {
            // 重なる範囲は後のものを除く (ドキュメントの領域は重ならない)。
            if (size <= 0 || baseAddress < previousEnd)
            {
                continue;
            }

            (uint protect, RegionType type) = info.TryGetValue(baseAddress, out var p) ? p : (PageProtection.ReadWrite, RegionType.Private);
            string? module = modules.FirstOrDefault(md => baseAddress >= md.BaseAddress && baseAddress < md.End)?.Name;
            regions.Add(new SnapshotRegion(baseAddress, size, protect, type, module, fileOffset));
            previousEnd = baseAddress + size;
        }

        long limit = arch switch
        {
            ProcessArchitecture.X86 or ProcessArchitecture.Arm => 0x1_0000_0000L,
            _ => 0x7FFF_FFFF_0000L,
        };
        limit = Math.Max(limit, previousEnd);
        string name = modules.Count > 0 ? modules[0].Name : fallbackName;
        return new SnapshotMetadata
        {
            ProcessName = name,
            Pid = pid,
            Architecture = arch,
            CapturedUtc = DateTimeOffset.FromUnixTimeSeconds(time).UtcDateTime,
            AddressLimit = limit,
            Regions = regions,
            Modules = modules,
        };
    }

    private static List<(long, long, long)> ReadMemory64(Stream stream, uint rva, uint size, long fileLength)
    {
        byte[] h = ReadAt(stream, rva, 16) ?? throw new MemoryDumpFormatException("The memory list is truncated.");
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(h);
        long baseRva = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(8));
        if (count > MaxRanges || 16 + count * 16 > size || baseRva < 0 || baseRva > fileLength)
        {
            throw new MemoryDumpFormatException("The memory list is invalid.");
        }

        byte[] table = ReadAt(stream, rva + 16, (int)(count * 16)) ?? throw new MemoryDumpFormatException("The memory list is truncated.");
        var list = new List<(long, long, long)>((int)count);
        long at = baseRva;
        for (int i = 0; i < (int)count; i++)
        {
            long start = BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(i * 16));
            long length = BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(i * 16 + 8));
            if (length < 0 || at > fileLength - length || start < 0 || start > long.MaxValue - length)
            {
                throw new MemoryDumpFormatException($"Memory range {i} is outside the file.");
            }

            list.Add((start, length, at));
            at += length;
        }

        return list;
    }

    private static List<(long, long, long)> ReadMemory32(Stream stream, uint rva, uint size, long fileLength)
    {
        byte[] h = ReadAt(stream, rva, 4) ?? throw new MemoryDumpFormatException("The memory list is truncated.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(h);
        if (count > MaxRanges || 4 + count * 16L > size)
        {
            throw new MemoryDumpFormatException("The memory list is invalid.");
        }

        byte[] table = ReadAt(stream, rva + 4, (int)(count * 16)) ?? throw new MemoryDumpFormatException("The memory list is truncated.");
        var list = new List<(long, long, long)>((int)count);
        for (int i = 0; i < count; i++)
        {
            long start = BinaryPrimitives.ReadInt64LittleEndian(table.AsSpan(i * 16));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 16 + 8));
            uint at = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 16 + 12));
            if ((long)at + length > fileLength || start < 0)
            {
                throw new MemoryDumpFormatException($"Memory range {i} is outside the file.");
            }

            list.Add((start, length, at));
        }

        return list;
    }

    private static List<ProcessModule> ReadModules(Stream stream, uint rva, uint size, long fileLength)
    {
        var modules = new List<ProcessModule>();
        if (ReadAt(stream, rva, 4) is not { } h)
        {
            return modules;
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(h);
        if (count > MaxModules || 4 + count * 108L > size)
        {
            return modules;
        }

        byte[]? table = ReadAt(stream, rva + 4, (int)(count * 108));
        if (table is null)
        {
            return modules;
        }

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = table.AsSpan(i * 108, 108);
            long baseAddress = BinaryPrimitives.ReadInt64LittleEndian(e);
            uint imageSize = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            uint nameRva = BinaryPrimitives.ReadUInt32LittleEndian(e[20..]);
            string path = ReadString(stream, nameRva, fileLength) ?? string.Empty;
            string name = Path.GetFileName(path.Replace('/', '\\').Split('\\')[^1]);
            modules.Add(new ProcessModule(name, baseAddress, imageSize, path));
        }

        return modules;
    }

    private static string? ReadString(Stream stream, uint rva, long fileLength)
    {
        if (rva == 0 || ReadAt(stream, rva, 4) is not { } h)
        {
            return null;
        }

        uint bytes = BinaryPrimitives.ReadUInt32LittleEndian(h);
        if (bytes > 32768 || rva + 4L + bytes > fileLength)
        {
            return null;
        }

        return ReadAt(stream, rva + 4, (int)(bytes & ~1u)) is { } s ? Encoding.Unicode.GetString(s) : null;
    }

    private static Dictionary<long, (uint, RegionType)> ReadMemoryInfo(Stream stream, uint rva, uint size)
    {
        var map = new Dictionary<long, (uint, RegionType)>();
        if (ReadAt(stream, rva, 16) is not { } h)
        {
            return map;
        }

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(h);
        uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(8));
        if (headerSize < 16 || entrySize < 48 || count > MaxRanges || headerSize + count * entrySize > size)
        {
            return map;
        }

        byte[]? table = ReadAt(stream, rva + headerSize, (int)(count * entrySize));
        if (table is null)
        {
            return map;
        }

        for (int i = 0; i < (int)count; i++)
        {
            ReadOnlySpan<byte> e = table.AsSpan(i * (int)entrySize, 48);
            long baseAddress = BinaryPrimitives.ReadInt64LittleEndian(e);
            uint protect = BinaryPrimitives.ReadUInt32LittleEndian(e[36..]);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(e[40..]);
            map.TryAdd(baseAddress, (protect, type switch
            {
                0x1000000 => RegionType.Image,
                0x40000 => RegionType.Mapped,
                0x20000 => RegionType.Private,
                _ => RegionType.None,
            }));
        }

        return map;
    }

    private static byte[]? ReadAt(Stream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > stream.Length - count)
        {
            return null;
        }

        byte[] buffer = new byte[count];
        stream.Position = offset;
        return stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false) == count ? buffer : null;
    }

    /// <summary>ミニダンプを開く (スナップショットと同じデータソース。メモリマップのパネルも使える)。</summary>
    public static SnapshotByteSource Open(string path)
    {
        SnapshotMetadata metadata = Read(path);
        return new SnapshotByteSource(path, metadata, $"{metadata.ProcessName} {metadata.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
    }
}
