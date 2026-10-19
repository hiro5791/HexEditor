namespace HexEditor.Core.FileSystems.Partitions;

/// <summary>
/// パーティションの種類 ID・種類 GUID の名前 (FOR-06 の仕様 1)。名前は表示の言語ごとに訳すため、リソースのキーの末尾 (<c>PartType_&lt;キー&gt;</c>)
/// で返す。知らない種類は null。
/// </summary>
public static class PartitionTypes
{
    public static readonly Guid EfiSystem = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    public static readonly Guid MicrosoftReserved = new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");
    public static readonly Guid WindowsBasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    public static readonly Guid WindowsRecovery = new("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC");
    public static readonly Guid LdmMetadata = new("5808C8AA-7E8F-42E0-85D2-E1E90434CFB3");
    public static readonly Guid LdmData = new("AF9B60A0-1431-4F62-BC68-3311714A69AD");
    public static readonly Guid LinuxFileSystem = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    public static readonly Guid LinuxLvm = new("E6D6D379-F507-44C2-A23C-238F2A3DF928");
    public static readonly Guid LinuxSwap = new("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F");
    public static readonly Guid LinuxRaid = new("A19D880F-05FC-4D3B-A006-743F0F84911E");
    public static readonly Guid LinuxHome = new("933AC7E1-2EB4-4F13-B844-0E14E2AEF915");
    public static readonly Guid AppleApfs = new("7C3457EF-0000-11AA-AA11-00306543ECAC");
    public static readonly Guid AppleHfsPlus = new("48465300-0000-11AA-AA11-00306543ECAC");
    public static readonly Guid AppleBoot = new("426F6F74-0000-11AA-AA11-00306543ECAC");
    public static readonly Guid BiosBoot = new("21686148-6449-6E6F-744E-656564454649");
    public static readonly Guid FreeBsdUfs = new("516E7CB6-6ECF-11D6-8FF8-00022D09712B");

    private static readonly Dictionary<Guid, string> GptNames = new()
    {
        [EfiSystem] = "EfiSystem",
        [MicrosoftReserved] = "MicrosoftReserved",
        [WindowsBasicData] = "WindowsBasicData",
        [WindowsRecovery] = "WindowsRecovery",
        [LdmMetadata] = "LdmMetadata",
        [LdmData] = "LdmData",
        [LinuxFileSystem] = "LinuxFileSystem",
        [LinuxLvm] = "LinuxLvm",
        [LinuxSwap] = "LinuxSwap",
        [LinuxRaid] = "LinuxRaid",
        [LinuxHome] = "LinuxHome",
        [AppleApfs] = "AppleApfs",
        [AppleHfsPlus] = "AppleHfsPlus",
        [AppleBoot] = "AppleBoot",
        [BiosBoot] = "BiosBoot",
        [FreeBsdUfs] = "FreeBsdUfs",
    };

    private static readonly Dictionary<byte, string> MbrNames = new()
    {
        [0x01] = "Fat12",
        [0x04] = "Fat16Small",
        [0x05] = "Extended",
        [0x06] = "Fat16",
        [0x07] = "NtfsExFat",
        [0x0B] = "Fat32Chs",
        [0x0C] = "Fat32Lba",
        [0x0E] = "Fat16Lba",
        [0x0F] = "ExtendedLba",
        [0x11] = "HiddenFat12",
        [0x12] = "Recovery",
        [0x14] = "HiddenFat16",
        [0x16] = "HiddenFat16",
        [0x17] = "HiddenNtfs",
        [0x1B] = "HiddenFat32",
        [0x1C] = "HiddenFat32",
        [0x1E] = "HiddenFat16",
        [0x27] = "WindowsRecovery",
        [0x42] = "Ldm",
        [0x82] = "LinuxSwap",
        [0x83] = "Linux",
        [0x85] = "LinuxExtended",
        [0x8E] = "LinuxLvm",
        [0xA5] = "FreeBsd",
        [0xA6] = "OpenBsd",
        [0xA8] = "AppleUfs",
        [0xA9] = "NetBsd",
        [0xAB] = "AppleBoot",
        [0xAF] = "AppleHfs",
        [0xEE] = "GptProtective",
        [0xEF] = "EfiSystem",
        [0xFD] = "LinuxRaid",
    };

    private static readonly string[] BsdFsTypes =
    [
        "unused", "swap", "Version 6", "Version 7", "System V", "4.1BSD", "Eighth Edition", "4.2BSD", "MSDOS", "4.4LFS", "unknown", "HPFS",
        "ISO9660", "boot", "ADOS", "HFS", "ADVfs", "ccd", "jfs2", "Linux Ext2", "vinum", "raid", "Linux LVM", "NTFS",
    ];

    public static string? GptKey(Guid type) => GptNames.GetValueOrDefault(type);

    public static string? MbrKey(byte id) => MbrNames.GetValueOrDefault(id);

    /// <summary>拡張パーティションの種類 ID (FOR-06 の仕様 1)。</summary>
    public static bool IsExtended(byte id) => id is 0x05 or 0x0F or 0x85;

    /// <summary>BSD のスライスの種類 ID (FreeBSD・OpenBSD・NetBSD)。</summary>
    public static bool IsBsdSlice(byte id) => id is 0xA5 or 0xA6 or 0xA9;

    /// <summary>BSD ディスクラベルのファイルシステムの種類の名前 (技術用語のため訳さない)。</summary>
    public static string BsdFsType(byte type) => type < BsdFsTypes.Length ? BsdFsTypes[type] : $"type {type}";
}
