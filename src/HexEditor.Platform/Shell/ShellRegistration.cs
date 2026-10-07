using Microsoft.Win32;

namespace HexEditor.Platform.Shell;

/// <summary>HKCU のレジストリ (PKG-08 の仕様 3: 登録先はすべて HKCU)。テストでは差し替える。</summary>
public interface IUserRegistry
{
    /// <summary>キーがなければ作り、値を書く。<paramref name="name"/> が null なら既定の値。</summary>
    void SetValue(string key, string? name, string value);

    string? GetValue(string key, string? name);

    bool KeyExists(string key);

    /// <summary>キーをサブキーごと消す。なければ何もしない。</summary>
    void DeleteKeyTree(string key);
}

/// <summary>実際の HKCU。</summary>
public sealed class WindowsUserRegistry : IUserRegistry
{
    public void SetValue(string key, string? name, string value)
    {
        using RegistryKey k = Registry.CurrentUser.CreateSubKey(key, writable: true);
        k.SetValue(name ?? string.Empty, value, RegistryValueKind.String);
    }

    public string? GetValue(string key, string? name)
    {
        using RegistryKey? k = Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue(name ?? string.Empty) as string;
    }

    public bool KeyExists(string key)
    {
        using RegistryKey? k = Registry.CurrentUser.OpenSubKey(key);
        return k is not null;
    }

    public void DeleteKeyTree(string key) => Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
}

/// <summary>登録する項目 1 つ。<see cref="Values"/> は exe のパスから書く値 (名前 null は既定の値) を作る。</summary>
public sealed record ShellRegistrationEntry(string Id, string Key, Func<string, IReadOnlyList<(string? Name, string Value)>> Values);

/// <summary>
/// Explorer 連携のレジストリの登録の一覧 (PKG-08 の仕様 3)。登録と解除で同じ一覧を使う。HKCU だけに書き、HKLM には書かない。
/// フェーズ 0 は App Paths だけ。右クリックメニュー (09 の UI-54)、ファイル関連付け (UI-56)、PATH (PKG-08 の仕様 6) は
/// それぞれの機能を作るときにこの一覧に加える。
/// </summary>
public static class ShellRegistration
{
    public const string AppPathsId = "appPaths";

    /// <summary>App Paths: Win+R で <c>HexEditor</c> と入力すると起動する (PKG-07 の仕様 5)。</summary>
    public const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\HexEditor.exe";

    public static IReadOnlyList<ShellRegistrationEntry> Entries { get; } =
    [
        new(AppPathsId, AppPathsKey, exe => [(null, exe), ("Path", Path.GetDirectoryName(exe) ?? string.Empty)]),
    ];

    /// <summary>
    /// 一覧のうち <paramref name="disabled"/> (利用者が設定で解除した項目。state.json) 以外を登録する。冪等。
    /// 解除された項目は消す (更新後に復活させない。PKG-08 の仕様 2)。失敗した項目の Id と理由を返す。
    /// </summary>
    public static IReadOnlyList<string> Register(IUserRegistry registry, string exePath, IReadOnlySet<string> disabled, Func<bool>? timeUp = null)
    {
        var failures = new List<string>();
        foreach (ShellRegistrationEntry entry in Entries)
        {
            if (timeUp?.Invoke() == true)
            {
                failures.Add($"{entry.Id}: skipped (time limit)");
                continue;
            }

            try
            {
                if (disabled.Contains(entry.Id))
                {
                    registry.DeleteKeyTree(entry.Key);
                    continue;
                }

                foreach ((string? name, string value) in entry.Values(exePath))
                {
                    registry.SetValue(entry.Key, name, value);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                failures.Add($"{entry.Id}: {ex.GetType().Name}");
            }
        }

        return failures;
    }

    /// <summary>一覧のすべての項目を消す (アンインストール前のフック、<c>--unregister</c>)。失敗した項目の Id と理由を返す。</summary>
    public static IReadOnlyList<string> Unregister(IUserRegistry registry, Func<bool>? timeUp = null)
    {
        var failures = new List<string>();
        foreach (ShellRegistrationEntry entry in Entries)
        {
            if (timeUp?.Invoke() == true)
            {
                failures.Add($"{entry.Id}: skipped (time limit)");
                continue;
            }

            try
            {
                registry.DeleteKeyTree(entry.Key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                failures.Add($"{entry.Id}: {ex.GetType().Name}");
            }
        }

        return failures;
    }
}
