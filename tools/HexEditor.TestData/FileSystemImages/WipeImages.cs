using System.Buffers.Binary;
using System.Text;

namespace HexEditor.TestData.FileSystemImages;

/// <summary>
/// 消去 (FOR-30・FOR-31) のテスト用のディスクの内容。TD-FOR-VHDX-PATTERN と TD-FOR-VHDX-DELETED の、VHDX に入れる前の生のディスクを
/// メモリ上に作る (CI の管理者のテストは同じ内容を Windows で作った VHDX を使う。単体テストはこのイメージをディスクとして開く)。
/// </summary>
public static class WipeImages
{
    public const long MiB = 1024 * 1024;

    /// <summary>TD-FOR-VHDX-PATTERN の繰り返しの単位。</summary>
    public static readonly byte[] PatternUnit = "ORIGDATA"u8.ToArray();

    /// <summary>TD-FOR-VHDX-DELETED の削除したファイルの内容の単位。</summary>
    public const string WipeMe = "WIPE-ME-0123456789";

    /// <summary>TD-FOR-VHDX-DELETED の slack.txt のスラックに残る文字列。</summary>
    public const string SlackRemnant = "SLACK-REMNANT";

    /// <summary>TD-FOR-VHDX-DELETED の NTFS のパーティションの開始 (LBA)。</summary>
    public const long DeletedPartitionStart = 2048;

    /// <summary>TD-FOR-VHDX-DELETED の NTFS のボリュームの大きさ。</summary>
    public const long DeletedVolumeBytes = 250 * MiB;

    /// <summary>TD-FOR-VHDX-PATTERN の内容 (<paramref name="length"/> バイトを <c>ORIGDATA</c> の繰り返しで埋めたもの)。</summary>
    public static void FillPattern(Span<byte> buffer, long position)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = PatternUnit[(int)((position + i) % PatternUnit.Length)];
        }
    }

    /// <summary>TD-FOR-VHDX-PATTERN の生のディスク (64 MiB) をファイルに書く (偽のディスクのイメージ)。</summary>
    public static void WritePatternDisk(string path, long length = 64 * MiB)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        byte[] block = new byte[MiB];
        FillPattern(block, 0);
        for (long done = 0; done < length; done += block.Length)
        {
            stream.Write(block, 0, (int)Math.Min(block.Length, length - done));
        }
    }

    /// <summary>
    /// TD-FOR-VHDX-DELETED の NTFS のボリューム: keep.txt (TD-TEXT-ASCII)、slack.txt (100 バイト。クラスタには SLACK-REMNANT の繰り返し 4 KiB)、
    /// tiny.txt (10 バイト、常駐)、wipe.bin (WIPE-ME-0123456789 の繰り返し 1 MiB。作ってから削除する)。<paramref name="dirty"/> なら $Volume の dirty フラグを立てる。
    /// </summary>
    public static (SparseImage Volume, NtfsImageBuilder Builder) DeletedVolume(bool dirty = false)
    {
        var b = new NtfsImageBuilder(DeletedVolumeBytes, "DELETED");
        DateTime time = FileSet.TimeOf(20);
        b.AddFile(b.Root, "keep.txt", TestDataCatalog.TextAscii4K(), time, resident: false);
        byte[] slackCluster = FileSet.Repeat(SlackRemnant, 4096);
        b.AddFile(b.Root, "slack.txt", slackCluster.AsSpan(0, 100).ToArray(), time.AddSeconds(1), resident: false, allocatedContent: slackCluster);
        b.AddFile(b.Root, "tiny.txt", "tiny-file!"u8.ToArray(), time.AddSeconds(2), resident: true);
        NtfsImageBuilder.Node wipe = b.AddFile(b.Root, "wipe.bin", FileSet.Repeat(WipeMe, (int)MiB), time.AddSeconds(3), resident: false);
        b.Delete(wipe);
        var image = new SparseImage(DeletedVolumeBytes);
        b.Build(new SparseImageTarget(image), editRecord: (number, record) =>
        {
            if (dirty && number == 3)
            {
                SetVolumeDirty(record);
            }
        });
        return (image, b);
    }

    /// <summary>TD-FOR-VHDX-DELETED の生のディスク (256 MiB、GPT で NTFS のパーティション 1 つ)。</summary>
    public static SparseImage DeletedDisk(bool dirty = false)
    {
        var disk = new SparseImage(256 * MiB);
        long sectors = DeletedVolumeBytes / 512;
        PartitionImages.WriteGpt(disk, 512, PartitionImages.Numbered(0xDE1), [
            new PartitionImages.GptPart(PartitionImages.BasicData, PartitionImages.Numbered(1), DeletedPartitionStart, DeletedPartitionStart + sectors - 1, "Data")]);
        disk.Place(DeletedPartitionStart * 512, DeletedVolume(dirty).Volume);
        return disk;
    }

    /// <summary>MFT レコードの $VOLUME_INFORMATION (0x70) のフラグに dirty (0x0001) を立てる。</summary>
    private static void SetVolumeDirty(byte[] record)
    {
        int at = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
        while (at + 16 <= record.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(at));
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(at + 4));
            if (type == 0xFFFFFFFF || length <= 0)
            {
                return;
            }

            if (type == 0x70)
            {
                int value = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(at + 20));
                record[at + value + 10] |= 0x01;
                return;
            }

            at += length;
        }
    }

    /// <summary>テストの検索に使う ASCII の文字列のバイト列。</summary>
    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
