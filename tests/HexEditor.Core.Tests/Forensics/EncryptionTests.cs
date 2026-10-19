using System.Buffers.Binary;
using HexEditor.Core.Engine;
using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Forensics.Encryption;
using HexEditor.Core.Sources;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Forensics;

/// <summary>
/// FOR-29 暗号化ボリュームの検出 (Core)。テスト用のイメージは生成ツールが形式の定義から組み立てたもの (BitLocker・cryptsetup の機能は使わない)。
/// </summary>
public sealed class EncryptionTests
{
    private static ICarveData OpenImage(string id, out Document doc)
    {
        doc = new Document(FileByteSource.Open(TestDataCatalog.Get(id)));
        return new SnapshotCarveData(doc.DataSnapshot);
    }

    [Fact]
    [Trait("TC", "TC-FOR-29-01")]
    public void BitLocker_metadata_shows_the_method_and_both_key_protectors_and_blocks_the_file_system_browser()
    {
        // 1. TD-FOR-BITLOCKER-RAW をディスクイメージとして開く。2. 暗号化の情報。
        ICarveData data = OpenImage("TD-FOR-BITLOCKER-RAW", out Document doc);
        using (doc)
        {
            IReadOnlyList<EncryptionScanEntry> scan = EncryptionDetector.Scan(data);
            EncryptionScanEntry partition = Assert.Single(scan);
            Assert.Equal(TestDataCatalog.BitLockerPartitionOffset, partition.Offset);
            EncryptionInfo info = Assert.IsType<EncryptionInfo>(partition.Encryption);
            Assert.Equal(EncryptionKind.BitLocker, info.Kind);
            BitLockerInfo b = info.BitLocker!;
            Assert.Equal("AES-XTS 128", b.MethodName);
            Assert.Equal(2, b.Version);
            Assert.Equal(TestDataCatalog.BitLockerVolumeGuid, b.VolumeId);
            Assert.Equal(BitLockerConversion.Encrypted, b.State);
            Assert.Equal("HEXTEST-PC E: 2026/10/01", b.Description);
            Assert.Equal(new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc), b.Created!.Value.Utc);
            Assert.Equal(["Password", "Recovery password"], b.Protectors.Select(p => p.TypeName));
            Assert.Equal([TestDataCatalog.BitLockerPasswordProtector, TestDataCatalog.BitLockerRecoveryProtector], b.Protectors.Select(p => p.Id));
            Assert.Equal(3, b.ReadableBlocks);
            Assert.False(b.MetadataMismatch);
            Assert.False(b.Damaged);
            Assert.Empty(info.Warnings);

            // 3. ファイルシステムブラウザを開こうとすると「暗号化されています」(開かない)。
            Assert.NotNull(EncryptionDetector.FileSystemBlockReason(data, partition.Offset, partition.Length, partition.TypeGuid));
        }
    }

    [Fact]
    [Trait("TC", "TC-FOR-29-03")]
    public void Luks2_is_detected_with_its_uuid_and_one_key_slot()
    {
        ICarveData data = OpenImage("TD-FOR-LUKS2", out Document doc);
        using (doc)
        {
            EncryptionScanEntry entry = Assert.Single(EncryptionDetector.Scan(data));
            EncryptionInfo info = Assert.IsType<EncryptionInfo>(entry.Encryption);
            Assert.Equal(EncryptionKind.Luks2, info.Kind);
            Assert.Equal(2, info.Luks!.Version);
            Assert.Equal(TestDataCatalog.Luks2Uuid, info.Luks.Uuid);
            Assert.Equal(1, info.Luks.KeySlots);
            Assert.Equal("aes-xts-plain64", info.Luks.Cipher);
            Assert.True(info.BlocksFileSystem);
        }
    }

    [Fact]
    public void Mismatched_and_damaged_metadata_blocks_are_warned_and_readable_items_are_kept()
    {
        IReadOnlyList<(long Offset, byte[] Data)> parts = BitLockerParser.BuildVolume(64L * 1024 * 1024, 0x8005, Guid.NewGuid(), DateTime.UtcNow, "PC C: 2026/10/07",
            [new BitLockerParser.ProtectorSpec(0x0100, Guid.NewGuid(), DateTime.UtcNow)]);
        byte[] volume = Assemble(parts, 48 * 1024 * 1024);

        // 2 つ目のブロックの説明を変える (食い違い)、3 つ目の署名を壊す (破損)。
        long second = parts[2].Offset;
        volume[second + 0x40 + 0x30 + 60] ^= 0x01;
        volume[parts[3].Offset] = 0;
        EncryptionInfo info = EncryptionDetector.Detect(new MemoryCarveData(volume), 0, volume.Length)!;
        Assert.Equal(EncryptionKind.BitLocker, info.Kind);
        Assert.True(info.BitLocker!.MetadataMismatch);
        Assert.True(info.BitLocker.Damaged);
        Assert.Equal(2, info.BitLocker.ReadableBlocks);
        Assert.Equal("AES-XTS 256", info.BitLocker.MethodName);
        Assert.Equal("TPM", Assert.Single(info.BitLocker.Protectors).TypeName);
        Assert.Contains("metadataMismatch", info.Warnings);
        Assert.Contains("metadataDamaged", info.Warnings);

        // メタデータがすべて読めなくても BitLocker とは判定し、壊れていると示す。
        foreach ((long offset, _) in parts.Skip(1))
        {
            volume[offset] = 0;
        }

        info = EncryptionDetector.Detect(new MemoryCarveData(volume), 0, volume.Length)!;
        Assert.Equal(EncryptionKind.BitLocker, info.Kind);
        Assert.Equal(0, info.BitLocker!.ReadableBlocks);
        Assert.True(info.BitLocker.Damaged);
    }

    [Fact]
    public void In_progress_conversion_reports_the_encrypted_amount_and_to_go_is_recognised()
    {
        IReadOnlyList<(long Offset, byte[] Data)> parts = BitLockerParser.BuildVolume(48L * 1024 * 1024, 0x8004, Guid.NewGuid(), DateTime.UtcNow, "x", [], state: 2);
        byte[] volume = Assemble(parts, 48 * 1024 * 1024);
        BitLockerInfo b = EncryptionDetector.Detect(new MemoryCarveData(volume), 0, volume.Length)!.BitLocker!;
        Assert.Equal(BitLockerConversion.InProgress, b.State);
        Assert.Equal(48L * 1024 * 1024, b.EncryptedBytes);

        // To Go: MSWIN4.1 と決まった位置の識別子とメタデータの位置。
        byte[] togo = new byte[512];
        "MSWIN4.1"u8.CopyTo(togo.AsSpan(3));
        BitLockerParser.Identifier.TryWriteBytes(togo.AsSpan(0x1A8));
        Assert.True(BitLockerParser.Kind(togo));
        "NOTFVE01"u8.CopyTo(togo.AsSpan(3));
        Assert.Null(BitLockerParser.Kind(togo));
    }

    [Fact]
    public void Luks1_apfs_core_storage_and_high_entropy_partitions_are_named()
    {
        // LUKS1: 暗号方式とモード、UUID、有効なキースロットの数。
        byte[] luks1 = new byte[64 * 1024];
        LuksParser.Magic.CopyTo(luks1, 0);
        BinaryPrimitives.WriteUInt16BigEndian(luks1.AsSpan(6), 1);
        "aes"u8.CopyTo(luks1.AsSpan(8));
        "xts-plain64"u8.CopyTo(luks1.AsSpan(40));
        "sha256"u8.CopyTo(luks1.AsSpan(72));
        "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"u8.CopyTo(luks1.AsSpan(168));
        BinaryPrimitives.WriteUInt32BigEndian(luks1.AsSpan(208), LuksParser.KeySlotEnabled);
        BinaryPrimitives.WriteUInt32BigEndian(luks1.AsSpan(208 + 48), 0x0000DEAD);
        BinaryPrimitives.WriteUInt32BigEndian(luks1.AsSpan(208 + 96), LuksParser.KeySlotEnabled);
        EncryptionInfo l1 = EncryptionDetector.Detect(new MemoryCarveData(luks1), 0, luks1.Length)!;
        Assert.Equal(EncryptionKind.Luks1, l1.Kind);
        Assert.Equal(("aes-xts-plain64", "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", 2), (l1.Luks!.Cipher, l1.Luks.Uuid, l1.Luks.KeySlots));

        // APFS: 暗号化されたボリューム (FileVault) があれば検出、なければ暗号化ではない。
        byte[] apfs = ApfsEncryptionProbe.BuildContainer([("Macintosh HD", true), ("Preboot", false)]);
        EncryptionInfo a = EncryptionDetector.Detect(new MemoryCarveData(apfs), 0, apfs.Length)!;
        Assert.Equal(EncryptionKind.ApfsEncrypted, a.Kind);
        Assert.Equal([("Macintosh HD", true), ("Preboot", false)], a.ApfsVolumes.Select(v => (v.Name, v.Encrypted)));
        byte[] plain = ApfsEncryptionProbe.BuildContainer([("Data", false)]);
        Assert.Null(EncryptionDetector.Detect(new MemoryCarveData(plain), 0, plain.Length));

        // Core Storage: GPT の種類 GUID だけで判定する (ファイルシステムブラウザは止めない)。
        EncryptionInfo cs = EncryptionDetector.Detect(new MemoryCarveData(new byte[4096]), 0, 4096, EncryptionDetector.CoreStorageType)!;
        Assert.Equal(EncryptionKind.CoreStorage, cs.Kind);
        Assert.False(cs.BlocksFileSystem);

        // 署名がなくエントロピーが 7.9 以上: 「暗号化または圧縮されたデータの可能性」。00 のデータや NTFS の署名があれば何も言わない。
        byte[] random = new byte[128 * 1024];
        TestDataCatalog.Random(0x29, 0, random);
        EncryptionInfo possible = EncryptionDetector.Detect(new MemoryCarveData(random), 0, random.Length)!;
        Assert.Equal(EncryptionKind.PossiblyEncrypted, possible.Kind);
        Assert.InRange(possible.Entropy!.Value, 7.9, 8.0);
        Assert.Null(EncryptionDetector.Detect(new MemoryCarveData(new byte[128 * 1024]), 0, 128 * 1024));
        "NTFS    "u8.CopyTo(random.AsSpan(3));
        Assert.Null(EncryptionDetector.Detect(new MemoryCarveData(random), 0, random.Length));
    }

    [Fact]
    public void Mbr_partitions_are_scanned_and_unpartitioned_images_are_one_range()
    {
        byte[] disk = new byte[4 * 1024 * 1024];
        disk[510] = 0x55;
        disk[511] = 0xAA;
        disk[446 + 4] = 0x07;
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(446 + 8), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(446 + 12), 4096);
        byte[] luks = LuksParser.BuildLuks2("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", 3);
        luks.CopyTo(disk, 2048 * 512);
        EncryptionScanEntry e = Assert.Single(EncryptionDetector.Scan(new MemoryCarveData(disk)));
        Assert.Equal((byte)0x07, e.MbrType);
        Assert.Equal(2048 * 512, e.Offset);
        Assert.Equal((EncryptionKind.Luks2, 3), (e.Encryption!.Kind, e.Encryption.Luks!.KeySlots));

        EncryptionScanEntry whole = Assert.Single(EncryptionDetector.Scan(new MemoryCarveData(luks)));
        Assert.Equal(0, whole.Index);
        Assert.Equal(EncryptionKind.Luks2, whole.Encryption!.Kind);
    }

    private static byte[] Assemble(IReadOnlyList<(long Offset, byte[] Data)> parts, int length)
    {
        byte[] volume = new byte[length];
        foreach ((long offset, byte[] data) in parts)
        {
            data.CopyTo(volume, offset);
        }

        return volume;
    }
}
