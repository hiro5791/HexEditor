namespace HexEditor.Platform.Network;

/// <summary>アプリが自分から行う通信の種類 (00-overview 11.4、09 の UI-58 の仕様 1)。</summary>
public enum NetworkFeature
{
    /// <summary>更新の確認とダウンロード (10 の PKG-17〜PKG-21)。</summary>
    Updates,

    /// <summary>テンプレート・プラグインなどのダウンロード (オンラインリポジトリ)。</summary>
    Downloads,

    /// <summary>追加コンポーネント (C# スクリプト用のコンパイラ) のダウンロード。</summary>
    Components,

    /// <summary>翻訳の誤り報告・ドキュメントのページを開く (既定のブラウザで開くだけ)。</summary>
    BrowserPages,
}

/// <summary>
/// ネットワークを使う機能の管理 (09 の UI-58)。アプリが自分から行う通信は、すべてここで許可を確かめる。
/// 設定は <c>network.offline</c>、<c>network.downloads.enabled</c>、<c>network.translationReport.enabled</c> と、
/// 更新の確認の <c>update.checkAutomatically</c> (10 の PKG-17)。
/// 利用者が接続先を指定する機能 (リモートのデータソース、スクリプトの通信) はこの管理の対象外 (仕様 2)。
/// </summary>
public sealed class NetworkPolicy(ISettingsAccess settings)
{
    public const string OfflineKey = "network.offline";
    public const string DownloadsEnabledKey = "network.downloads.enabled";
    public const string TranslationReportEnabledKey = "network.translationReport.enabled";

    /// <summary>オフラインモード (仕様 3)。更新・ダウンロードをすべて止める。</summary>
    public bool Offline => settings.GetBool(OfflineKey, false);

    /// <summary>テンプレート・プラグイン・追加コンポーネントのダウンロードをしてよいか。</summary>
    public bool DownloadsAllowed => !Offline && settings.GetBool(DownloadsEnabledKey, true);

    /// <summary>
    /// 更新の確認をしてよいか (10 の PKG-17 の仕様 3)。オフラインなら手動・自動とも不可。
    /// 自動の確認は <c>update.checkAutomatically</c> が true のときだけ。
    /// </summary>
    public bool UpdateCheckAllowed(bool manual) =>
        !Offline && (manual || settings.GetBool(Updates.UpdatePreferences.CheckAutomaticallyKey, true));

    /// <summary>
    /// ブラウザで開く前に URL を見せて、開くかどうかを選ばせるか (仕様 3、UI-41 の仕様 6)。
    /// オフラインモードのときと、翻訳の報告を無効にしたときの翻訳の報告で true。
    /// </summary>
    public bool ConfirmBeforeOpening(bool translationReport) =>
        Offline || (translationReport && !settings.GetBool(TranslationReportEnabledKey, true));

    /// <summary>機能が使えるか (メニューの有効・無効と、通信の直前の確認)。</summary>
    public bool Allows(NetworkFeature feature) => feature switch
    {
        NetworkFeature.Updates => UpdateCheckAllowed(manual: true),
        NetworkFeature.Downloads or NetworkFeature.Components => DownloadsAllowed,
        _ => true,
    };

    /// <summary>設定画面「プライバシー」に出す一覧 (仕様 1): 機能、接続先の説明のリソースキー、設定のキー、既定値。</summary>
    public static IReadOnlyList<(NetworkFeature Feature, string SettingKey, bool Default)> Features { get; } =
    [
        (NetworkFeature.Updates, Updates.UpdatePreferences.CheckAutomaticallyKey, true),
        (NetworkFeature.Downloads, DownloadsEnabledKey, true),
        (NetworkFeature.Components, DownloadsEnabledKey, true),
        (NetworkFeature.BrowserPages, TranslationReportEnabledKey, true),
    ];
}
