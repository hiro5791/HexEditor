using System.Text.Json.Serialization;

namespace HexEditor.Core.Processes;

/// <summary>プロセスのアーキテクチャ (ENG-32 の仕様 1)。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProcessArchitecture>))]
public enum ProcessArchitecture
{
    Unknown,
    X86,
    X64,
    Arm64,
    Arm64EC,
    Arm,
}

/// <summary>管理者権限なしで開けるか (ENG-32 の仕様 1、ENG-28 の概要の表)。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProcessAccessLevel>))]
public enum ProcessAccessLevel
{
    /// <summary>同じユーザーの、昇格していないプロセス。UI のプロセスで直接開ける (アイコンなし)。</summary>
    Direct,

    /// <summary>昇格したプロセス・システムのプロセス・他のユーザーのプロセス。開くには管理者権限が要る (盾のアイコン)。</summary>
    NeedsElevation,

    /// <summary>保護されたプロセス (PPL など)。管理者権限があっても開けない (錠のアイコン)。</summary>
    Protected,
}

/// <summary>「プロセスを開く」の一覧の 1 行 (ENG-32 の仕様 1)。</summary>
public sealed record ProcessEntry
{
    public required int Pid { get; init; }

    public required string Name { get; init; }

    public ProcessArchitecture Architecture { get; init; }

    /// <summary>ユーザー名。取得できなければ null。</summary>
    public string? User { get; init; }

    public string? WindowTitle { get; init; }

    /// <summary>コミットサイズ (バイト)。取得できなければ null。</summary>
    public long? CommitBytes { get; init; }

    public ProcessAccessLevel Access { get; init; }

    /// <summary>このアプリと同じユーザーのプロセス (「自分のプロセスだけ表示」)。</summary>
    public bool IsCurrentUser { get; init; }

    /// <summary>実行ファイルのパス (アイコンの取得)。取得できなければ null。</summary>
    public string? ImagePath { get; init; }
}

/// <summary>メモリ領域の状態。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RegionState>))]
public enum RegionState
{
    Commit,
    Reserve,
    Free,
}

/// <summary>メモリ領域の種類。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RegionType>))]
public enum RegionType
{
    None,
    Image,
    Mapped,
    Private,
}

/// <summary>
/// プロセスのメモリ領域 1 つ (ENG-33 の仕様 1)。<see cref="Protect"/> は Win32 の <c>PAGE_*</c> の値。
/// </summary>
public sealed record MemoryRegion(long BaseAddress, long Size, RegionState State, uint Protect, RegionType Type, string? MappedName = null,
    long AllocationBase = 0)
{
    public long End => BaseAddress + Size;

    /// <summary>読める (コミット済みで、アクセス不可・ガードでない)。</summary>
    [JsonIgnore]
    public bool IsReadable => State == RegionState.Commit && PageProtection.IsReadable(Protect);

    [JsonIgnore]
    public bool IsWritable => State == RegionState.Commit && PageProtection.IsWritable(Protect);

    [JsonIgnore]
    public bool IsGuard => (Protect & PageProtection.Guard) != 0;
}

/// <summary>モジュール 1 つ (ENG-33 の仕様 1)。</summary>
public sealed record ProcessModule(string Name, long BaseAddress, long Size, string Path)
{
    public long End => BaseAddress + Size;
}

/// <summary>ページの保護属性 (Win32 の <c>PAGE_*</c>)。</summary>
public static class PageProtection
{
    public const uint NoAccess = 0x01;
    public const uint ReadOnly = 0x02;
    public const uint ReadWrite = 0x04;
    public const uint WriteCopy = 0x08;
    public const uint Execute = 0x10;
    public const uint ExecuteRead = 0x20;
    public const uint ExecuteReadWrite = 0x40;
    public const uint ExecuteWriteCopy = 0x80;
    public const uint Guard = 0x100;
    public const uint NoCache = 0x200;
    public const uint WriteCombine = 0x400;

    private const uint BaseMask = 0xFF;

    public static bool IsReadable(uint protect)
    {
        if ((protect & Guard) != 0)
        {
            return false;
        }

        return (protect & BaseMask) is ReadOnly or ReadWrite or WriteCopy or ExecuteRead or ExecuteReadWrite or ExecuteWriteCopy;
    }

    public static bool IsWritable(uint protect) =>
        (protect & Guard) == 0 && (protect & BaseMask) is ReadWrite or WriteCopy or ExecuteReadWrite or ExecuteWriteCopy;

    /// <summary>書き込める保護属性にしたもの (ENG-34 の仕様 3 の一時的な変更)。実行可能なら実行可能のまま。</summary>
    public static uint MakeWritable(uint protect) =>
        (protect & BaseMask) is Execute or ExecuteRead or ExecuteWriteCopy or ExecuteReadWrite ? ExecuteReadWrite : ReadWrite;

    /// <summary>
    /// 短い表記 (ENG-33 の仕様 1): R、RW、RX、RWX、WC (書き込み時コピー)、X、NA (アクセス不可)。ガードは末尾に <c>+G</c>。
    /// </summary>
    public static string ShortText(uint protect)
    {
        string text = (protect & BaseMask) switch
        {
            NoAccess => "NA",
            ReadOnly => "R",
            ReadWrite => "RW",
            WriteCopy => "WC",
            Execute => "X",
            ExecuteRead => "RX",
            ExecuteReadWrite => "RWX",
            ExecuteWriteCopy => "RWXC",
            0 => "-",
            _ => "?",
        };
        return (protect & Guard) != 0 ? text + "+G" : text;
    }
}
