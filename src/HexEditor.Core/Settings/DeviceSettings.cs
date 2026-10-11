namespace HexEditor.Core.Settings;

/// <summary>ディスク・プロセスの設定のキー (ENG-28、ENG-32、ENG-33)。</summary>
public static class DeviceSettings
{
    /// <summary>補助プロセスの 1 要求の待ち時間の上限 (秒。5〜300、既定 30。ENG-28 の仕様 5)。</summary>
    public const string HelperTimeoutKey = "devices.helperTimeoutSeconds";

    /// <summary>
    /// 開いているハンドルが 0 になってから補助プロセスが終了するまで (分。1〜60、既定 10。0 は「アプリの終了まで残す」。ENG-28 の仕様 6)。
    /// </summary>
    public const string HelperIdleKey = "devices.helperIdleMinutes";

    /// <summary>「プロセスを開く」の「読み取り専用で開く」の既定 (ENG-32 の仕様 3)。</summary>
    public const string ProcessReadOnlyKey = "process.openReadOnly";

    /// <summary>メモリマップの一覧の更新の間隔 (秒。1〜60、既定 5。0 はオフ。ENG-33 の仕様 6)。</summary>
    public const string MemoryMapRefreshKey = "memoryMap.refreshSeconds";

    /// <summary>.img などを通常の「開く」で開いたら、ディスクイメージとして開き直すかを提案する (ENG-31 の仕様 6)。</summary>
    public const string SuggestDiskImageKey = "diskImage.suggest";
}
