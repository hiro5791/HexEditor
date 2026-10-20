using System.Buffers.Binary;
using System.Text;
using HexEditor.Core.Forensics.Carving;

namespace HexEditor.Core.Forensics.Encryption;

/// <summary>
/// APFS のコンテナのボリュームが暗号化されているか (FileVault 2) を調べる (12 の FOR-29 の仕様 2)。コンテナのスーパーブロック (NXSB) の
/// オブジェクトマップ (B 木) からボリュームのスーパーブロック (APSB) を引き、<c>apfs_fs_flags</c> の <c>APFS_FS_UNENCRYPTED</c> (bit 0) を見る。
/// ファイルシステムの解析 (FOR-13) とは別に、判定に必要な最小限だけを読む。B 木の深さ・キーの数・ボリュームの数に上限を設ける。
/// </summary>
public static class ApfsEncryptionProbe
{
    public const uint ContainerMagic = 0x4253584E; // "NXSB"
    public const uint VolumeMagic = 0x42535041; // "APSB"
    public const ulong FlagUnencrypted = 0x1;
    public const int MaxVolumes = 100;
    public const int MaxDepth = 8;

    private const int OmapTreeOidOffset = 48;
    private const int NodeHeaderSize = 56;
    private const int BtreeInfoSize = 40;
    private const ushort NodeRoot = 0x1;
    private const ushort NodeFixedKv = 0x4;

    /// <summary>先頭のブロックが APFS のコンテナか。</summary>
    public static bool IsContainer(ReadOnlySpan<byte> block) => block.Length >= 40 && BinaryPrimitives.ReadUInt32LittleEndian(block[32..]) == ContainerMagic;

    /// <summary>コンテナのボリュームを調べる。コンテナでなければ null。読めないボリュームは飛ばす。</summary>
    public static IReadOnlyList<ApfsVolumeEncryption>? Probe(ICarveData data, long offset)
    {
        byte[]? head = EncryptionRead.Bytes(data, offset, 4096);
        if (head is null || !IsContainer(head))
        {
            return null;
        }

        uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(36));
        if (blockSize is < 4096 or > 65536 || (blockSize & (blockSize - 1)) != 0)
        {
            return [];
        }

        byte[]? super = blockSize == 4096 ? head : EncryptionRead.Bytes(data, offset, (int)blockSize);
        if (super is null)
        {
            return [];
        }

        ulong omapOid = BinaryPrimitives.ReadUInt64LittleEndian(super.AsSpan(160));
        uint maxFs = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(super.AsSpan(180)), MaxVolumes);
        byte[]? omap = Block(data, offset, blockSize, omapOid);
        if (omap is null)
        {
            return [];
        }

        ulong tree = BinaryPrimitives.ReadUInt64LittleEndian(omap.AsSpan(OmapTreeOidOffset));
        var result = new List<ApfsVolumeEncryption>();
        for (int i = 0; i < maxFs; i++)
        {
            int at = 184 + i * 8;
            if (at + 8 > super.Length)
            {
                break;
            }

            ulong fsOid = BinaryPrimitives.ReadUInt64LittleEndian(super.AsSpan(at));
            if (fsOid == 0)
            {
                continue;
            }

            if (Lookup(data, offset, blockSize, tree, fsOid) is not { } paddr || Block(data, offset, blockSize, paddr) is not { } volume
                || BinaryPrimitives.ReadUInt32LittleEndian(volume.AsSpan(32)) != VolumeMagic || volume.Length < 960)
            {
                continue;
            }

            ulong flags = BinaryPrimitives.ReadUInt64LittleEndian(volume.AsSpan(264));
            string name = Name(volume.AsSpan(704, 256));
            result.Add(new ApfsVolumeEncryption(name, new Guid(volume.AsSpan(240, 16)), (flags & FlagUnencrypted) == 0, flags));
        }

        return result;
    }

    private static string Name(ReadOnlySpan<byte> s)
    {
        int end = s.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? s : s[..end]);
    }

    private static byte[]? Block(ICarveData data, long offset, uint blockSize, ulong paddr)
    {
        if (paddr == 0 || paddr > (ulong)(data.Length / blockSize))
        {
            return null;
        }

        return EncryptionRead.Bytes(data, offset + (long)paddr * blockSize, (int)blockSize);
    }

    /// <summary>オブジェクトマップの B 木で <paramref name="oid"/> の物理アドレスを引く (最新の版)。</summary>
    private static ulong? Lookup(ICarveData data, long offset, uint blockSize, ulong root, ulong oid)
    {
        ulong node = root;
        for (int depth = 0; depth < MaxDepth; depth++)
        {
            if (Block(data, offset, blockSize, node) is not { } b)
            {
                return null;
            }

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(32));
            ushort level = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(34));
            uint keys = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(36));
            ushort tocOff = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(40));
            ushort tocLen = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(42));
            if ((flags & NodeFixedKv) == 0 || keys > blockSize / 4)
            {
                return null;
            }

            int toc = NodeHeaderSize + tocOff;
            int keyStart = NodeHeaderSize + tocOff + tocLen;
            int valueEnd = (flags & NodeRoot) != 0 ? (int)blockSize - BtreeInfoSize : (int)blockSize;
            ulong? best = null;
            for (int i = 0; i < keys; i++)
            {
                int entry = toc + i * 4;
                if (entry + 4 > b.Length)
                {
                    return null;
                }

                int k = keyStart + BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(entry));
                int v = valueEnd - BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(entry + 2));
                if (k < 0 || k + 16 > b.Length || v < 0 || v + (level == 0 ? 16 : 8) > b.Length)
                {
                    return null;
                }

                ulong keyOid = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(k));
                if (keyOid > oid)
                {
                    break;
                }

                if (level == 0)
                {
                    if (keyOid == oid)
                    {
                        best = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(v + 8));
                    }
                }
                else
                {
                    best = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(v));
                }
            }

            if (level == 0 || best is null)
            {
                return best;
            }

            node = best.Value;
        }

        return null;
    }

    // ---- 作る (テスト用の最小のコンテナ) ----

    /// <summary>
    /// ボリュームを持つ最小の APFS のコンテナを作る (ブロック 4 KiB。0: NXSB、1: omap、2: omap の B 木 (根・葉)、3〜: ボリュームのスーパーブロック)。
    /// </summary>
    public static byte[] BuildContainer(IReadOnlyList<(string Name, bool Encrypted)> volumes)
    {
        const int bs = 4096;
        int blocks = 3 + volumes.Count;
        byte[] image = new byte[blocks * bs];
        Span<byte> nx = image.AsSpan(0, bs);
        BinaryPrimitives.WriteUInt32LittleEndian(nx[32..], ContainerMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(nx[36..], bs);
        BinaryPrimitives.WriteUInt64LittleEndian(nx[40..], (ulong)blocks);
        BinaryPrimitives.WriteUInt64LittleEndian(nx[160..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(nx[180..], (uint)volumes.Count);
        for (int i = 0; i < volumes.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(nx[(184 + i * 8)..], (ulong)(0x400 + i));
        }

        Span<byte> omap = image.AsSpan(bs, bs);
        BinaryPrimitives.WriteUInt64LittleEndian(omap[OmapTreeOidOffset..], 2);

        Span<byte> node = image.AsSpan(2 * bs, bs);
        BinaryPrimitives.WriteUInt16LittleEndian(node[32..], NodeRoot | 0x2 | NodeFixedKv);
        BinaryPrimitives.WriteUInt16LittleEndian(node[34..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(node[36..], (uint)volumes.Count);
        ushort tocLen = (ushort)(volumes.Count * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(node[40..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(node[42..], tocLen);
        int keyStart = NodeHeaderSize + tocLen;
        int valueEnd = bs - BtreeInfoSize;
        for (int i = 0; i < volumes.Count; i++)
        {
            int k = i * 16;
            int v = (i + 1) * 16;
            BinaryPrimitives.WriteUInt16LittleEndian(node[(NodeHeaderSize + i * 4)..], (ushort)k);
            BinaryPrimitives.WriteUInt16LittleEndian(node[(NodeHeaderSize + i * 4 + 2)..], (ushort)v);
            BinaryPrimitives.WriteUInt64LittleEndian(node[(keyStart + k)..], (ulong)(0x400 + i));
            BinaryPrimitives.WriteUInt64LittleEndian(node[(keyStart + k + 8)..], 1);
            BinaryPrimitives.WriteUInt64LittleEndian(node[(valueEnd - v + 8)..], (ulong)(3 + i));
        }

        for (int i = 0; i < volumes.Count; i++)
        {
            Span<byte> apsb = image.AsSpan((3 + i) * bs, bs);
            BinaryPrimitives.WriteUInt32LittleEndian(apsb[32..], VolumeMagic);
            Guid.Parse($"0000000{i}-1111-2222-3333-444455556666").TryWriteBytes(apsb[240..]);
            BinaryPrimitives.WriteUInt64LittleEndian(apsb[264..], volumes[i].Encrypted ? 0UL : FlagUnencrypted);
            Encoding.UTF8.GetBytes(volumes[i].Name, apsb[704..960]);
        }

        return image;
    }
}
