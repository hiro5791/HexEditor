using HexEditor.Core.Settings;

namespace HexEditor.App.Services;

/// <summary>ウィンドウの設定 (UI-14、UI-15)。</summary>
public static class WindowSettings
{
    /// <summary>外部から開いたファイルを開くウィンドウ (<c>lastActiveWindow</c> / <c>newWindow</c>。UI-15 の仕様 2)。</summary>
    public const string OpenExternalInKey = "window.openExternalIn";

    /// <summary>ファイルを指定しない 2 つ目の起動で新しいウィンドウを開く (既定 false。UI-15 の仕様 4)。</summary>
    public const string LaunchWithoutFileOpensNewWindowKey = "window.launchWithoutFileOpensNewWindow";

    public static bool OpenExternalInNewWindow(SettingsStore settings) =>
        settings.GetString(OpenExternalInKey, "lastActiveWindow") == "newWindow";

    public static bool LaunchWithoutFileOpensNewWindow(SettingsStore settings) => settings.GetBool(LaunchWithoutFileOpensNewWindowKey, false);
}
