using System.Buffers.Binary;
using System.Text;
using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Forensics.Time;

namespace HexEditor.Core.Forensics.Encryption;

/// <summary>
/// BitLocker のボリュームヘッダと FVE メタデータを読む (12 の FOR-29 の仕様 1)。復号はしない。読むのはボリュームの先頭と 3 つのメタデータブロックの
/// 数セクタだけ。内容は信頼できない入力として読み、エントリの数・大きさ・入れ子の深さに上限を設ける。
/// <code>
/// ボリュームヘッダ (Windows 7 以降): 3 "-FVE-FS-"、0xA0 BitLocker の識別子 GUID、0xB0 / 0xB8 / 0xC0 メタデータブロックの位置 (u64)
/// BitLocker To Go: 3 "MSWIN4.1"、0x1A8 識別子 GUID、0x1B8 / 0x1C0 / 0x1C8 メタデータブロックの位置
/// メタデータブロック: 0 "-FVE-FS-"、0x0A 版、0x0C 現在の状態、0x0E 次の状態、0x10 暗号化済みの大きさ、0x20〜 3 つの位置
/// メタデータの見出し (ブロックの 0x40): 0 大きさ、0x10 ボリュームの GUID、0x24 暗号化方式、0x28 作成日時 (FILETIME)、0x30〜 エントリ
/// エントリ: [大きさ u16][種類 u16][値の種類 u16][版 u16][値]
/// </code>
/// </summary>
public static class BitLockerParser
{
    public static readonly byte[] Signature = "-FVE-FS-"u8.ToArray();
    public static readonly byte[] ToGoSignature = "MSWIN4.1"u8.ToArray();

    /// <summary>ボリュームヘッダの BitLocker の識別子。</summary>
    public static readonly Guid Identifier = new("4967d63b-2e29-4ad8-8399-f6a339e3d001");

    public const int MetadataBlockHeaderSize = 0x40;
    public const int MetadataHeaderSize = 0x30;

    /// <summary>読むメタデータの上限 (実際は数 KiB。壊れた大きさで大量に読まない)。</summary>
    public const int MaxMetadataSize = 64 * 1024;

    /// <summary>エントリの数の上限。</summary>
    public const int MaxEntries = 256;

    /// <summary>入れ子の深さの上限。</summary>
    public const int MaxDepth = 4;

    public const ushort EntryTypeVmk = 0x0002;
    public const ushort EntryTypeDescription = 0x0007;
    public const ushort ValueTypeUnicode = 0x0002;
    public const ushort ValueTypeVmk = 0x0008;

    /// <summary>暗号化方式の名前 (FVE の方式の番号)。</summary>
    public static string MethodName(ushort method) => method switch
    {
        0x8000 => "AES-CBC 128 + Elephant diffuser",
        0x8001 => "AES-CBC 256 + Elephant diffuser",
        0x8002 => "AES-CBC 128",
        0x8003 => "AES-CBC 256",
        0x8004 => "AES-XTS 128",
        0x8005 => "AES-XTS 256",
        0 => "None",
        _ => $"Unknown (0x{method:X4})",
    };

    /// <summary>キー保護機能の種類の名前。</summary>
    public static string ProtectorName(ushort type) => type switch
    {
        0x0000 => "Clear key",
        0x0100 => "TPM",
        0x0200 => "Startup key",
        0x0500 => "TPM and PIN",
        0x0800 => "Recovery password",
        0x2000 => "Password",
        _ => $"Unknown (0x{type:X4})",
    };

    /// <summary>ボリュームの先頭のセクタが BitLocker か (To Go を含む)。</summary>
    public static bool IsBitLocker(ReadOnlySpan<byte> sector) => Kind(sector) is not null;

    /// <summary>BitLocker の種類 (通常 / To Go)。違えば null。</summary>
    public static bool? Kind(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 512)
        {
            return null;
        }

        if (sector.Slice(3, 8).SequenceEqual(Signature))
        {
            return false;
        }

        // To Go: FAT32 の見た目 (MSWIN4.1) と、決まった位置の BitLocker の識別子。
        return sector.Slice(3, 8).SequenceEqual(ToGoSignature) && new Guid(sector.Slice(0x1A8, 16)) == Identifier ? true : null;
    }

    /// <summary>ボリュームヘッダからメタデータブロックの位置を読む。</summary>
    public static long[] MetadataOffsets(ReadOnlySpan<byte> sector, bool toGo)
    {
        int at = toGo ? 0x1B8 : 0xB0;
        if (sector.Length < at + 24)
        {
            return [];
        }

        return [BinaryPrimitives.ReadInt64LittleEndian(sector[at..]), BinaryPrimitives.ReadInt64LittleEndian(sector[(at + 8)..]),
            BinaryPrimitives.ReadInt64LittleEndian(sector[(at + 16)..])];
    }

    /// <summary>
    /// ボリューム (<paramref name="volumeOffset"/> から <paramref name="volumeLength"/> バイト) を BitLocker として読む。BitLocker でなければ null。
    /// メタデータが壊れていても、読めた項目で結果を返す。
    /// </summary>
    public static BitLockerInfo? Parse(ICarveData data, long volumeOffset, long volumeLength)
    {
        byte[]? sector = EncryptionRead.Bytes(data, volumeOffset, 512);
        if (sector is null || Kind(sector) is not { } toGo)
        {
            return null;
        }

        long[] offsets = MetadataOffsets(sector, toGo);
        var blocks = new List<byte[]>();
        foreach (long offset in offsets)
        {
            if (offset <= 0 || offset > volumeLength - MetadataBlockHeaderSize - MetadataHeaderSize)
            {
                continue;
            }

            if (ReadBlock(data, volumeOffset + offset) is { } block)
            {
                blocks.Add(block);
            }
        }

        var info = new BitLockerInfo { ToGo = toGo, MetadataOffsets = offsets, ReadableBlocks = blocks.Count, Damaged = blocks.Count < 3 };
        if (blocks.Count == 0)
        {
            return info;
        }

        byte[] first = blocks[0];
        bool mismatch = blocks.Skip(1).Any(b => !b.AsSpan(MetadataBlockHeaderSize).SequenceEqual(first.AsSpan(MetadataBlockHeaderSize)));
        return ParseBlock(first, info) with { MetadataMismatch = mismatch };
    }

    /// <summary>メタデータブロック 1 つ (ブロックの見出しとメタデータ全体) を読む。署名が違えば null。</summary>
    private static byte[]? ReadBlock(ICarveData data, long at)
    {
        byte[]? head = EncryptionRead.Bytes(data, at, MetadataBlockHeaderSize + MetadataHeaderSize);
        if (head is null || !head.AsSpan(0, 8).SequenceEqual(Signature))
        {
            return null;
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(MetadataBlockHeaderSize));
        if (size < MetadataHeaderSize || size > MaxMetadataSize)
        {
            return head;
        }

        return EncryptionRead.Bytes(data, at, MetadataBlockHeaderSize + (int)size) ?? head;
    }

    private static BitLockerInfo ParseBlock(byte[] block, BitLockerInfo info)
    {
        ReadOnlySpan<byte> b = block;
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(b[0x0A..]);
        ushort current = BinaryPrimitives.ReadUInt16LittleEndian(b[0x0C..]);
        ushort next = BinaryPrimitives.ReadUInt16LittleEndian(b[0x0E..]);
        long encrypted = BinaryPrimitives.ReadInt64LittleEndian(b[0x10..]);
        ReadOnlySpan<byte> meta = b[MetadataBlockHeaderSize..];
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(meta);
        var guid = new Guid(meta.Slice(0x10, 16));
        ushort method = BinaryPrimitives.ReadUInt16LittleEndian(meta[0x24..]);
        ulong created = BinaryPrimitives.ReadUInt64LittleEndian(meta[0x28..]);
        BitLockerConversion state = (current, next) switch
        {
            (1, _) => BitLockerConversion.Decrypted,
            (4, _) => BitLockerConversion.Encrypted,
            (5, _) => BitLockerConversion.Paused,
            (2 or 3, _) => BitLockerConversion.InProgress,
            _ => BitLockerConversion.Unknown,
        };

        bool damaged = info.Damaged;
        var protectors = new List<BitLockerProtector>();
        string? description = null;
        int end = (int)Math.Min(size, (uint)meta.Length);
        if (size > meta.Length)
        {
            damaged = true;
        }

        ParseEntries(meta[MetadataHeaderSize..Math.Max(MetadataHeaderSize, end)], 0, protectors, ref description, ref damaged);
        return info with
        {
            Version = version,
            EncryptionMethod = method,
            VolumeId = guid,
            Created = created == 0 ? null : ForensicTimestamp.FromFileTime(created),
            Description = description,
            Protectors = protectors,
            State = state,
            EncryptedBytes = state is BitLockerConversion.InProgress or BitLockerConversion.Paused && encrypted >= 0 ? encrypted : null,
            Damaged = damaged,
        };
    }

    private static void ParseEntries(ReadOnlySpan<byte> entries, int depth, List<BitLockerProtector> protectors, ref string? description, ref bool damaged)
    {
        int at = 0;
        int count = 0;
        while (at + 8 <= entries.Length && count++ < MaxEntries)
        {
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(entries[at..]);
            if (size == 0)
            {
                break;
            }

            if (size < 8 || at + size > entries.Length)
            {
                damaged = true;
                break;
            }

            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(entries[(at + 2)..]);
            ushort valueType = BinaryPrimitives.ReadUInt16LittleEndian(entries[(at + 4)..]);
            ReadOnlySpan<byte> value = entries.Slice(at + 8, size - 8);
            if (depth == 0 && type == EntryTypeVmk && valueType == ValueTypeVmk && value.Length >= 28)
            {
                protectors.Add(new BitLockerProtector(BinaryPrimitives.ReadUInt16LittleEndian(value[26..]), new Guid(value[..16]),
                    ForensicTimestamp.FromFileTime(BinaryPrimitives.ReadUInt64LittleEndian(value[16..]))));
            }
            else if (depth == 0 && type == EntryTypeDescription && valueType == ValueTypeUnicode && description is null)
            {
                description = Encoding.Unicode.GetString(value[..(value.Length & ~1)]).TrimEnd('\0');
            }

            at += size;
        }

        if (count > MaxEntries)
        {
            damaged = true;
        }
    }

    // ---- 作る (テストデータ。cases/12-forensics.md の TD-FOR-BITLOCKER-RAW) ----

    /// <summary>テストデータ用のキー保護機能。</summary>
    public sealed record ProtectorSpec(ushort Type, Guid Id, DateTime CreatedUtc);

    /// <summary>テストデータ用の BitLocker のボリュームの見出しとメタデータの書き込み先 (オフセット → バイト列)。</summary>
    public static IReadOnlyList<(long Offset, byte[] Data)> BuildVolume(long volumeSize, ushort method, Guid volumeId, DateTime createdUtc, string description,
        IReadOnlyList<ProtectorSpec> protectors, long[]? metadataOffsets = null, ushort state = 4)
    {
        long[] offsets = metadataOffsets ?? [0x0210_0000L, 0x0230_0000L, 0x0250_0000L];
        byte[] header = new byte[512];
        header[0] = 0xEB;
        header[1] = 0x58;
        header[2] = 0x90;
        Signature.CopyTo(header, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x0B), 512);
        header[0x0D] = 8;
        Identifier.TryWriteBytes(header.AsSpan(0xA0));
        for (int i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0xB0 + i * 8), offsets[i]);
        }

        header[510] = 0x55;
        header[511] = 0xAA;

        // メタデータのエントリ: 説明と、各キー保護機能 (VMK)。
        var entries = new List<byte>();
        void Entry(ushort type, ushort valueType, ReadOnlySpan<byte> value)
        {
            byte[] e = new byte[8 + value.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(e, (ushort)e.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(2), type);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(4), valueType);
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(6), 1);
            value.CopyTo(e.AsSpan(8));
            entries.AddRange(e);
        }

        foreach (ProtectorSpec p in protectors)
        {
            byte[] vmk = new byte[28 + 16];
            p.Id.TryWriteBytes(vmk);
            BinaryPrimitives.WriteInt64LittleEndian(vmk.AsSpan(16), p.CreatedUtc.ToFileTimeUtc());
            BinaryPrimitives.WriteUInt16LittleEndian(vmk.AsSpan(26), p.Type);
            Entry(EntryTypeVmk, ValueTypeVmk, vmk);
        }

        Entry(EntryTypeDescription, ValueTypeUnicode, Encoding.Unicode.GetBytes(description + "\0"));

        int metaSize = MetadataHeaderSize + entries.Count;
        byte[] block = new byte[MetadataBlockHeaderSize + metaSize];
        Signature.CopyTo(block, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0x08), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0x0A), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0x0C), state);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(0x0E), state);
        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(0x10), volumeSize);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0x1C), 16);
        for (int i = 0; i < 3; i++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(0x20 + i * 8), offsets[i]);
        }

        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(0x38), 0x0270_0000L);
        Span<byte> meta = block.AsSpan(MetadataBlockHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(meta, (uint)metaSize);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[8..], MetadataHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[12..], (uint)metaSize);
        volumeId.TryWriteBytes(meta[0x10..]);
        BinaryPrimitives.WriteUInt32LittleEndian(meta[0x20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(meta[0x24..], method);
        BinaryPrimitives.WriteInt64LittleEndian(meta[0x28..], createdUtc.ToFileTimeUtc());
        entries.ToArray().CopyTo(meta[MetadataHeaderSize..]);
        return [(0, header), (offsets[0], block), (offsets[1], (byte[])block.Clone()), (offsets[2], (byte[])block.Clone())];
    }
}
