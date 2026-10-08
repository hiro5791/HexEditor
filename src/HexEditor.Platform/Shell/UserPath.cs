using System.Runtime.InteropServices;

namespace HexEditor.Platform.Shell;

/// <summary>
/// ユーザーの PATH (<c>HKCU\Environment</c> の <c>Path</c>) に、インストーラ版のフォルダ (<c>%LocalAppData%\HexEditor\current\</c>) を
/// 足す・消す (PKG-08 の仕様 6。コマンドラインの <c>hexed.exe</c> を使えるようにする)。
/// <list type="bullet">
/// <item>追加は既存の値の末尾に 1 回だけ行い、同じフォルダが既にあれば追加しない。</item>
/// <item>削除は追加した項目だけを消し、他の項目はそのまま残す。</item>
/// <item>値の種類 (<c>REG_EXPAND_SZ</c>) と、他の項目の <c>%USERPROFILE%</c> などは展開せずに残す。</item>
/// <item>システムの PATH (HKLM) には書かない (<see cref="IUserRegistry"/> は HKCU だけを扱う)。</item>
/// </list>
/// </summary>
public static class UserPath
{
    /// <summary>利用者が設定で外したことを記録する ID (state.json の <c>shellRegistration.disabled</c>。PKG-08 の仕様 5・6)。</summary>
    public const string Id = "commandLine";

    public const string EnvironmentKey = "Environment";
    public const string ValueName = "Path";

    /// <summary>exe のフォルダ (末尾に <c>\</c> を付ける。仕様 6 の書き方)。</summary>
    public static string FolderFor(string exePath) =>
        Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(exePath) ?? exePath) + Path.DirectorySeparatorChar;

    /// <summary>PATH の項目 (空の項目を除く。展開しない)。</summary>
    public static IReadOnlyList<string> Entries(IUserRegistry registry) =>
        (registry.GetRawString(EnvironmentKey, ValueName)?.Value ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries);

    public static bool Contains(IUserRegistry registry, string folder) => Entries(registry).Any(e => Same(e, folder));

    /// <summary>末尾に 1 回だけ足す。書き換えたら true (既にあれば false)。</summary>
    public static bool Add(IUserRegistry registry, string folder)
    {
        (string Value, bool Expandable)? current = registry.GetRawString(EnvironmentKey, ValueName);
        string value = current?.Value ?? string.Empty;
        if (value.Split(';').Any(e => Same(e, folder)))
        {
            return false;
        }

        string trimmed = value.TrimEnd(';');
        // 新しく作る場合は Windows の既定と同じ REG_EXPAND_SZ にする。
        registry.SetString(EnvironmentKey, ValueName, trimmed.Length == 0 ? folder : trimmed + ";" + folder, current?.Expandable ?? true);
        return true;
    }

    /// <summary>そのフォルダの項目だけを消す。書き換えたら true。項目がなくなったら値を消す。</summary>
    public static bool Remove(IUserRegistry registry, string folder)
    {
        (string Value, bool Expandable)? current = registry.GetRawString(EnvironmentKey, ValueName);
        if (current is not { } path)
        {
            return false;
        }

        string[] parts = path.Value.Split(';');
        string[] kept = [.. parts.Where(e => !Same(e, folder))];
        if (kept.Length == parts.Length)
        {
            return false;
        }

        if (kept.All(string.IsNullOrWhiteSpace))
        {
            registry.DeleteValue(EnvironmentKey, ValueName);
        }
        else
        {
            registry.SetString(EnvironmentKey, ValueName, string.Join(';', kept), path.Expandable);
        }

        return true;
    }

    /// <summary>
    /// 環境変数が変わったことを開いているアプリ (Explorer など) に知らせる (<c>WM_SETTINGCHANGE</c> の <c>"Environment"</c>)。
    /// 応答しないウィンドウで止まらないよう、ウィンドウごとに 1 秒で打ち切る。
    /// </summary>
    public static void NotifyEnvironmentChanged()
    {
        const int HwndBroadcast = 0xffff;
        const uint WmSettingChange = 0x001A;
        const uint SmtoAbortIfHung = 0x0002;
        _ = SendMessageTimeoutW(HwndBroadcast, WmSettingChange, 0, "Environment", SmtoAbortIfHung, 1000, out _);
    }

    /// <summary>同じフォルダか (環境変数を展開し、引用符と末尾の <c>\</c> を除いて、大文字・小文字を区別せずに比べる)。</summary>
    private static bool Same(string entry, string folder)
    {
        static string Normalize(string s) => Environment.ExpandEnvironmentVariables(s.Trim().Trim('"')).TrimEnd('\\', '/');
        string e = Normalize(entry);
        return e.Length > 0 && e.Equals(Normalize(folder), StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(nint window, uint message, nint wParam, string lParam, uint flags, uint timeout, out nint result);
}
