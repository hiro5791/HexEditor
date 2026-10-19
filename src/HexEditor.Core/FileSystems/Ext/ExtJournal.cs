using System.Buffers.Binary;

namespace HexEditor.Core.FileSystems.Ext;

/// <summary>jbd2 のブロックの種類 (FOR-12 の仕様 6)。</summary>
public enum ExtJournalBlockType
{
    Descriptor = 1,
    Commit = 2,
    SuperblockV1 = 3,
    SuperblockV2 = 4,
    Revoke = 5,
}

/// <summary>ジャーナルのブロック 1 つ (ジャーナルの中のブロック番号、ボリューム上の位置、種類、トランザクションの番号)。</summary>
public sealed record ExtJournalBlock(long Index, long? VolumeOffset, ExtJournalBlockType Type, uint Sequence);

/// <summary>
/// ext3 / ext4 のジャーナル (jbd2) の一覧 (FOR-12 の仕様 6)。ジャーナルのスーパーブロックと、記述子・コミット・取り消しのブロックを並べるだけで、
/// 再生はしない。値はビッグエンディアン。
/// </summary>
public sealed class ExtJournal
{
    public const uint Magic = 0xC03B3998;

    /// <summary>一覧にするブロック数の上限。</summary>
    public const int MaxBlocks = 1_000_000;

    private readonly IVolumeReader _content;
    private readonly ExtentContentReader? _extents;

    private ExtJournal(IVolumeReader content, byte[] sb)
    {
        _content = content;
        _extents = content as ExtentContentReader;
        ReadOnlySpan<byte> b = sb;
        Type = (ExtJournalBlockType)BinaryPrimitives.ReadUInt32BigEndian(b[4..]);
        BlockSize = (int)BinaryPrimitives.ReadUInt32BigEndian(b[12..]);
        MaxLength = BinaryPrimitives.ReadUInt32BigEndian(b[16..]);
        First = BinaryPrimitives.ReadUInt32BigEndian(b[20..]);
        Sequence = BinaryPrimitives.ReadUInt32BigEndian(b[24..]);
        Start = BinaryPrimitives.ReadUInt32BigEndian(b[28..]);
        FeatureCompat = BinaryPrimitives.ReadUInt32BigEndian(b[36..]);
        FeatureIncompat = BinaryPrimitives.ReadUInt32BigEndian(b[40..]);
        Uuid = new Guid(b.Slice(48, 16), bigEndian: true);
    }

    public ExtJournalBlockType Type { get; }
    public int BlockSize { get; }
    public uint MaxLength { get; }
    public uint First { get; }
    public uint Sequence { get; }

    /// <summary>ログの先頭のブロック (0 はジャーナルが空 = 正常にアンマウントされた)。</summary>
    public uint Start { get; }

    public uint FeatureCompat { get; }
    public uint FeatureIncompat { get; }
    public Guid Uuid { get; }

    /// <summary>ジャーナルの inode の内容から読む。ジャーナルがない・スーパーブロックが壊れているなら null。</summary>
    public static ExtJournal? Read(ExtFileSystem fs)
    {
        ExtSuperblock sb = fs.Superblock;
        if (!sb.HasJournal || sb.JournalInode == 0 || fs.ReadInode(sb.JournalInode) is not { IsEmpty: false })
        {
            return null;
        }

        FsEntry? entry = fs.EntryForInode(sb.JournalInode);
        return entry is null ? null : Open(fs.OpenContent(entry));
    }

    /// <summary>ジャーナルの内容 (ブロック 0 がジャーナルのスーパーブロック) から読む。</summary>
    public static ExtJournal? Open(IVolumeReader content)
    {
        byte[] head = content.ReadBytes(0, 1024);
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(head);
        uint type = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4));
        uint blockSize = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(12));
        if (magic != Magic || type is not (3 or 4) || blockSize is < 1024 or > 65536 || (blockSize & (blockSize - 1)) != 0)
        {
            return null;
        }

        return new ExtJournal(content, head);
    }

    /// <summary>
    /// ジャーナルのブロックのうち、jbd2 のヘッダー (マジック) を持つものを順に返す。データのブロックでマジックと同じ値で始まるものは jbd2 が
    /// エスケープするので、ヘッダーのあるブロックはジャーナルの管理ブロックとみなせる。
    /// </summary>
    public IEnumerable<ExtJournalBlock> Blocks(CancellationToken cancellationToken = default)
    {
        long count = Math.Min(Math.Min(MaxLength, _content.Length / BlockSize), MaxBlocks);
        byte[] header = new byte[12];
        for (long i = 0; i < count; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            _content.Read(i * BlockSize, header);
            if (BinaryPrimitives.ReadUInt32BigEndian(header) != Magic)
            {
                continue;
            }

            uint type = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            if (type is < 1 or > 5)
            {
                continue;
            }

            yield return new ExtJournalBlock(i, _extents?.VolumeOffsetOf(i * BlockSize), (ExtJournalBlockType)type,
                BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)));
        }
    }
}
