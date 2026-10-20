using HexEditor.Core.Forensics.Carving;
using HexEditor.Core.Forensics.Time;

namespace HexEditor.Core.Forensics.Encryption;

/// <summary>検出した暗号化の種類 (12 の FOR-29)。</summary>
public enum EncryptionKind
{
    /// <summary>BitLocker (仕様 1)。</summary>
    BitLocker,

    /// <summary>BitLocker To Go (取り外し可能なドライブの BitLocker。仕様 1)。</summary>
    BitLockerToGo,

    Luks1,

    Luks2,

    /// <summary>APFS の暗号化されたボリューム・FileVault 2 (APFS のボリュームのフラグ)。</summary>
    ApfsEncrypted,

    /// <summary>Core Storage (GPT の種類 GUID。暗号化 (FileVault 1 世代の論理ボリューム) の可能性)。</summary>
    CoreStorage,

    /// <summary>署名がなく、エントロピーが高い: 暗号化または圧縮されたデータの可能性 (VeraCrypt / TrueCrypt など)。</summary>
    PossiblyEncrypted,
}

/// <summary>BitLocker の暗号化の状態 (FVE メタデータの現在の状態)。</summary>
public enum BitLockerConversion
{
    Unknown,

    /// <summary>暗号化されていない (復号済み)。</summary>
    Decrypted,

    /// <summary>暗号化・復号の進行中。</summary>
    InProgress,

    /// <summary>完了 (暗号化済み)。</summary>
    Encrypted,

    /// <summary>一時停止。</summary>
    Paused,
}

/// <summary>キー保護機能 1 つ (VMK のエントリ)。</summary>
public sealed record BitLockerProtector(ushort Type, Guid Id, ForensicTimestamp Created)
{
    /// <summary>種類の英語名 (書き出し用。画面では翻訳した名前を出す)。</summary>
    public string TypeName => BitLockerParser.ProtectorName(Type);
}

/// <summary>BitLocker のメタデータ (仕様 1 の表示する項目)。読めなかった項目は null。</summary>
public sealed record BitLockerInfo
{
    public bool ToGo { get; init; }

    /// <summary>FVE メタデータの版 (1 = Vista、2 = Windows 7 以降)。</summary>
    public int Version { get; init; }

    public ushort EncryptionMethod { get; init; }

    /// <summary>暗号化方式の名前 (AES-XTS 128 など)。</summary>
    public string MethodName => BitLockerParser.MethodName(EncryptionMethod);

    public Guid VolumeId { get; init; }

    public ForensicTimestamp? Created { get; init; }

    /// <summary>説明 (コンピューター名とドライブと日付)。</summary>
    public string? Description { get; init; }

    public IReadOnlyList<BitLockerProtector> Protectors { get; init; } = [];

    public BitLockerConversion State { get; init; }

    /// <summary>暗号化済みの量 (バイト。進行中・一時停止のときの目安)。</summary>
    public long? EncryptedBytes { get; init; }

    /// <summary>3 つのメタデータブロックの位置 (ボリュームの先頭からのバイト)。</summary>
    public IReadOnlyList<long> MetadataOffsets { get; init; } = [];

    /// <summary>読めたメタデータブロックの数。</summary>
    public int ReadableBlocks { get; init; }

    /// <summary>3 つのメタデータブロックの内容が食い違う (仕様 1 の警告)。</summary>
    public bool MetadataMismatch { get; init; }

    /// <summary>読めない・壊れたメタデータブロックがある (「エラー」: 読めた項目だけを表示し、警告を出す)。</summary>
    public bool Damaged { get; init; }
}

/// <summary>LUKS のヘッダ (仕様 2: 暗号方式、UUID、キースロットの数)。</summary>
public sealed record LuksInfo(int Version, string Cipher, string Uuid, int KeySlots, string? Label = null, string? Hash = null);

/// <summary>APFS のボリューム 1 つの暗号化 (FOR-13 のフラグ <c>APFS_FS_UNENCRYPTED</c> がなければ暗号化)。</summary>
public sealed record ApfsVolumeEncryption(string Name, Guid Uuid, bool Encrypted, ulong Flags);

/// <summary>検出の結果。<see cref="Offset"/> と <see cref="Length"/> は調べた範囲 (データソースの先頭からのバイト)。</summary>
public sealed record EncryptionInfo
{
    public required EncryptionKind Kind { get; init; }

    public long Offset { get; init; }

    public long Length { get; init; }

    public BitLockerInfo? BitLocker { get; init; }

    public LuksInfo? Luks { get; init; }

    public IReadOnlyList<ApfsVolumeEncryption> ApfsVolumes { get; init; } = [];

    /// <summary>先頭 64 KiB のエントロピー (ビット / バイト。<see cref="EncryptionKind.PossiblyEncrypted"/> の根拠)。</summary>
    public double? Entropy { get; init; }

    /// <summary>警告 (メタデータの食い違い・破損)。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>復号しない限り中身のファイルシステムを読めない (ファイルシステムブラウザを開かない。仕様 4)。</summary>
    public bool BlocksFileSystem => Kind is not EncryptionKind.CoreStorage;
}

/// <summary>ディスク上で見つけた範囲 (パーティション、またはパーティション表がなければ全体)。</summary>
public sealed record EncryptionScanEntry(int Index, long Offset, long Length, Guid? TypeGuid, byte? MbrType, EncryptionInfo? Encryption);

/// <summary>読み込みの小さな手助け (信頼できない入力のための範囲の確認付き)。</summary>
internal static class EncryptionRead
{
    /// <summary>[offset, offset + count) を読む。末尾を越える・読めない場合は null。</summary>
    public static byte[]? Bytes(ICarveData data, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > data.Length - count)
        {
            return null;
        }

        byte[] buffer = new byte[count];
        Sources.ReadResult result = data.Read(offset, buffer);
        return result.BytesReturned == count && result.IsComplete ? buffer : null;
    }
}
