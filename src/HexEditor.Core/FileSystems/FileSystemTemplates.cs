using System.Globalization;

namespace HexEditor.Core.FileSystems;

/// <summary>
/// ファイルシステムの構造を Hex ビューで色分けするテンプレート (07): 「MFT レコードを表示」「INDX を表示」(FOR-10 の仕様 3・6)、
/// 「ディレクトリエントリを表示」(FOR-11 の画面)。適用の開始位置 (<c>$</c>) に構造を 1 つ置く。テンプレートのファイルはアプリのデータの
/// フォルダに書き出してから適用する (適用の記録が開き直しても使えるように)。
/// </summary>
public static class FileSystemTemplates
{
    private const string Header = """
        #pragma author "HexEditor"
        #pragma version "1.0.0"
        #pragma license "MIT"
        #pragma endian little

        """;

    private const string NtfsCommon = """
        flags RecordFlags : u16 { InUse = 0x1, Directory = 0x2 };

        enum AttributeType : u32 {
            StandardInformation = 0x10, AttributeList = 0x20, FileName = 0x30, ObjectId = 0x40, SecurityDescriptor = 0x50, VolumeName = 0x60,
            VolumeInformation = 0x70, Data = 0x80, IndexRoot = 0x90, IndexAllocation = 0xA0, Bitmap = 0xB0, ReparsePoint = 0xC0,
            EaInformation = 0xD0, Ea = 0xE0, LoggedUtilityStream = 0x100, End = 0xFFFFFFFF,
        };

        struct StandardInformation {
            filetime created;
            filetime modified;
            filetime mft_modified;
            filetime accessed;
            u32      file_attributes [[format("hex")]];
            u32      max_versions;
            u32      version;
            u32      class_id;
        };

        struct FileNameAttribute {
            u64      parent_reference [[format("hex")]];
            filetime created;
            filetime modified;
            filetime mft_modified;
            filetime accessed;
            u64      allocated_size;
            u64      real_size;
            u32      file_attributes [[format("hex")]];
            u32      reparse_or_ea;
            u8       name_length;
            u8       name_space;
            char16   name[name_length] [[encoding("utf-16le")]];
        };

        """;

    /// <summary>MFT レコード 1 つ (「MFT レコードを表示」)。</summary>
    public static string NtfsRecord(int recordSize) => Header + """
        #pragma name "NTFS MFT record"
        #pragma name.ja "NTFS の MFT レコード"

        """ + NtfsCommon + """
        struct Attribute {
            AttributeType type;
            if (type != AttributeType::End) {
                u32 length;
                u8  non_resident;
                u8  name_length;
                u16 name_offset;
                u16 attribute_flags [[format("hex")]];
                u16 attribute_id;
                if (non_resident == 0) {
                    u32 value_length;
                    u16 value_offset;
                    u8  indexed;
                    u8  padding_byte;
                    if (type == AttributeType::StandardInformation && value_length >= 48) {
                        StandardInformation standard_information @ addressof(this) + value_offset;
                    } else if (type == AttributeType::FileName && value_length >= 66) {
                        FileNameAttribute file_name @ addressof(this) + value_offset;
                    }
                } else {
                    u64 start_vcn;
                    u64 last_vcn;
                    u16 runlist_offset;
                    u16 compression_unit;
                    u32 padding_dword;
                    u64 allocated_size;
                    u64 real_size;
                    u64 initialized_size;
                }
            }
        } [[size(type == AttributeType::End ? 4 : length)]];

        struct MftRecord(u64 record_size) {
            char signature[4];
            u16         update_sequence_offset;
            u16         update_sequence_count;
            u64         log_sequence_number;
            u16         sequence_number;
            u16         hard_link_count;
            u16         first_attribute_offset;
            RecordFlags record_flags;
            u32         bytes_in_use;
            u32         bytes_allocated;
            u64         base_record [[format("hex")]];
            u16         next_attribute_id;
            u16         padding_word;
            u32         record_number;
            if (signature == "FILE") {
                Attribute attributes[until(last.type == AttributeType::End || endof(last) >= addressof(this) + bytes_in_use)] @ addressof(this) + first_attribute_offset;
            }
        } [[size(record_size)]];

        """ + $"MftRecord({recordSize.ToString(CultureInfo.InvariantCulture)}) mft_record;\n";

    /// <summary>INDX レコード 1 つ (「INDX を表示」)。</summary>
    public static string NtfsIndex(int blockSize) => Header + """
        #pragma name "NTFS INDX record"
        #pragma name.ja "NTFS の INDX レコード"

        """ + NtfsCommon + """
        struct IndexEntry {
            u64 file_reference [[format("hex")]];
            u16 entry_length;
            u16 key_length;
            u16 entry_flags [[format("hex"), comment("1: 子のノードあり、2: 最後のエントリ")]];
            u16 padding_word;
            if (key_length >= 66) {
                FileNameAttribute key;
            }
        } [[size(entry_length < 16 ? 16 : entry_length)]];

        struct IndexBlock(u64 block_size) {
            char signature[4];
            u16  update_sequence_offset;
            u16  update_sequence_count;
            u64  log_sequence_number;
            u64  vcn;
            u32  entries_offset;
            u32  index_length;
            u32  allocated_size;
            u32  index_flags [[format("hex")]];
            if (signature == "INDX") {
                IndexEntry entries[until((last.entry_flags & 2) != 0 || last.entry_length < 16 || endof(last) >= addressof(this) + 0x18 + index_length)] @ addressof(this) + 0x18 + entries_offset;
            }
        } [[size(block_size)]];

        """ + $"IndexBlock({blockSize.ToString(CultureInfo.InvariantCulture)}) index_record;\n";

    /// <summary>FAT のディレクトリエントリ (32 バイト × <paramref name="count"/>。「ディレクトリエントリを表示」)。</summary>
    public static string FatDirectoryEntries(int count) => Header + """
        #pragma name "FAT directory entries"
        #pragma name.ja "FAT のディレクトリエントリ"

        struct DirectoryEntry {
            u8 first_byte [[format("hex"), comment("E5: 削除、00: 終わり")]];
            if (first_byte != 0) {
                u8 rest_of_name[10];
                u8 attributes [[format("hex"), comment("0F: 長い名前 (LFN)")]];
                if (attributes == 0x0F) {
                    u8  lfn_type;
                    u8  checksum [[format("hex")]];
                    u8  name2[12];
                    u16 first_cluster_zero;
                    u8  name3[4];
                } else {
                    u8  nt_reserved [[format("hex")]];
                    u8  create_time_tenth;
                    u16 create_time [[format("hex")]];
                    u16 create_date [[format("hex")]];
                    u16 access_date [[format("hex")]];
                    u16 first_cluster_high;
                    u16 write_time [[format("hex")]];
                    u16 write_date [[format("hex")]];
                    u16 first_cluster_low;
                    u32 file_size;
                }
            }
        } [[size(32)]];

        """ + $"DirectoryEntry entries[{count.ToString(CultureInfo.InvariantCulture)}];\n";

    /// <summary>exFAT のエントリセット (32 バイト × <paramref name="count"/>)。</summary>
    public static string ExFatEntries(int count) => Header + """
        #pragma name "exFAT directory entries"
        #pragma name.ja "exFAT のディレクトリエントリ"

        struct DirectoryEntry {
            u8 entry_type [[format("hex"), comment("85: File、C0: Stream Extension、C1: File Name。最上位ビットが 0 なら削除")]];
            if (entry_type == 0x85 || entry_type == 0x05) {
                u8  secondary_count;
                u16 set_checksum [[format("hex")]];
                u16 file_attributes [[format("hex")]];
                u16 reserved1;
                u32 create_timestamp [[format("hex")]];
                u32 last_modified_timestamp [[format("hex")]];
                u32 last_accessed_timestamp [[format("hex")]];
                u8  create_10ms;
                u8  last_modified_10ms;
                u8  create_utc_offset [[format("hex")]];
                u8  last_modified_utc_offset [[format("hex")]];
                u8  last_accessed_utc_offset [[format("hex")]];
            } else if (entry_type == 0xC0 || entry_type == 0x40) {
                u8  general_flags [[format("hex"), comment("2: NoFatChain")]];
                u8  reserved1;
                u8  name_length;
                u16 name_hash [[format("hex")]];
                u16 reserved2;
                u64 valid_data_length;
                u32 reserved3;
                u32 first_cluster;
                u64 data_length;
            } else if (entry_type == 0xC1 || entry_type == 0x41) {
                u8     general_flags;
                char16 file_name[15] [[encoding("utf-16le")]];
            }
        } [[size(32)]];

        """ + $"DirectoryEntry entries[{count.ToString(CultureInfo.InvariantCulture)}];\n";

    /// <summary>ext の inode (「inode を表示」。FOR-12)。<paramref name="inodeSize"/> は 128 以上 (拡張領域は 128 を超える場合)。</summary>
    public static string ExtInode(int inodeSize) => Header + """
        #pragma name "ext inode"
        #pragma name.ja "ext の inode"

        struct ExtentHeader {
            u16 magic [[format("hex"), comment("F30A")]];
            u16 entries;
            u16 max;
            u16 depth;
            u32 generation;
        };

        struct Inode {
            u16 mode [[format("oct")]];
            u16 uid_lo;
            u32 size_lo;
            u32 atime;
            u32 ctime;
            u32 mtime;
            u32 dtime [[comment("0 でなければ削除された")]];
            u16 gid_lo;
            u16 links_count;
            u32 blocks_lo;
            u32 i_flags [[format("hex"), comment("80000: extents、10000000: inline_data、800: encrypted、1000: htree")]];
            u32 osd1;
            if ((i_flags & 0x80000) != 0) {
                ExtentHeader extent_header;
                u8 extents[48];
            } else {
                u32 block[15];
            }
            u32 generation;
            u32 file_acl_lo;
            u32 size_high;
            u32 obso_faddr;
            u16 blocks_high;
            u16 file_acl_high;
            u16 uid_high;
            u16 gid_high;
            u16 checksum_lo [[format("hex")]];
            u16 reserved;

        """ + (inodeSize > 128 ? """
            u16 extra_isize;
            u16 checksum_hi [[format("hex")]];
            u32 ctime_extra;
            u32 mtime_extra;
            u32 atime_extra;
            u32 crtime;
            u32 crtime_extra;
            u32 version_hi;
            u32 projid;

        """ : string.Empty) + $"}} [[size({inodeSize.ToString(CultureInfo.InvariantCulture)})]];\n\nInode inode;\n";

    /// <summary>テンプレートを書き出す (同じ内容なら書き直さない)。パスを返す。</summary>
    public static string Write(string folder, string name, string text)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        if (!File.Exists(path) || File.ReadAllText(path) != text)
        {
            File.WriteAllText(path, text);
        }

        return path;
    }
}
