using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace HexEditor.App.Services;

/// <summary>
/// Windows のトースト通知 (UI-36 の仕様 8)。アプリがアクティブでないときに、10 秒以上かかった長時間処理が終わったら出す。
/// 設定 notifications.toast (既定 true、ポータブル版は既定 false: 通知の登録がレジストリに書くため。PKG-06)。
/// テスト用のビルドで異常を再現する仕組みが有効なときは、利用者の画面に出さずにログにだけ書く。
/// </summary>
public static class ToastNotifier
{
    public const string SettingKey = "notifications.toast";

    private static bool _registered;
    private static bool _failed;

    /// <summary>通知を押したとき (UI スレッドとは限らない)。</summary>
    public static event Action? Invoked;

    public static bool IsEnabled(IAppEnvironment env) =>
        App.Settings.GetBool(SettingKey, env.Distribution != Distribution.Portable);

    /// <summary>
    /// トースト通知の登録 (非パッケージのアプリでは HKCU の AppUserModelId と COM の登録) を消す (ポータブル版の「この PC から登録を解除」。
    /// 10 の PKG-09 の仕様 4)。失敗は例外のまま呼び出し元に返す (失敗した項目の一覧に出す)。
    /// </summary>
    public static void UnregisterAll()
    {
        AppNotificationManager.Default.UnregisterAll();
        _registered = false;
    }

    public static void Show(string title, string message)
    {
        if (TestHooks.Active)
        {
            AppLog.Info($"Toast (not shown in test builds): {title} / {message}");
            return;
        }

        if (_failed)
        {
            return;
        }

        try
        {
            if (!_registered)
            {
                // 通知を押したときの処理は、登録の前に結び付ける (Windows App SDK の決まり)。
                AppNotificationManager.Default.NotificationInvoked += (_, _) => Invoked?.Invoke();
                AppNotificationManager.Default.Register();
                _registered = true;
            }

            AppNotification notification = new AppNotificationBuilder().AddText(title).AddText(message).BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            // 通知を使えない環境 (通知が無効なポリシーなど) では、以後は試さない。
            _failed = true;
            AppLog.Warning($"Toast notifications unavailable: {ex.GetType().Name}");
        }
    }
}
