namespace HexEditor.Core.Search.Yara;

/// <summary>YARA スキャンの設定のキー (04-search.md の 10 章)。</summary>
public static class YaraSettings
{
    /// <summary>ブロックの大きさ (MiB。FIND-33 の仕様 6)。</summary>
    public const string BlockSizeKey = "search.yara.blockSizeMiB";

    public const int DefaultBlockSizeMiB = 64;

    public const int MinBlockSizeMiB = 16;

    public const int MaxBlockSizeMiB = 1024;

    /// <summary>時間の上限 (秒。FIND-33 の仕様 7)。</summary>
    public const string TimeLimitKey = "search.yara.timeLimitSeconds";

    public const int DefaultTimeLimitSeconds = 600;

    public const int MaxTimeLimitSeconds = 86_400;
}
