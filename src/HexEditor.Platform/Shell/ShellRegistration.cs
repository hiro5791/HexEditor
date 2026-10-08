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

    /// <summary>値を 1 つ消す。キー・値がなければ何もしない。</summary>
    void DeleteValue(string key, string name);

    /// <summary>キーがあり、値もサブキーもないか。</summary>
    bool IsKeyEmpty(string key);

    /// <summary>
    /// 文字列の値を環境変数を展開せずに読む (<c>REG_EXPAND_SZ</c> の <c>%USERPROFILE%</c> などをそのまま)。
    /// <c>Expandable</c> は <c>REG_EXPAND_SZ</c> か。値がなければ null。
    /// </summary>
    (string Value, bool Expandable)? GetRawString(string key, string name);

    /// <summary>文字列の値を書く。<paramref name="expandable"/> なら <c>REG_EXPAND_SZ</c>、そうでなければ <c>REG_SZ</c>。</summary>
    void SetString(string key, string name, string value, bool expandable);
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

    public void DeleteValue(string key, string name)
    {
        using RegistryKey? k = Registry.CurrentUser.OpenSubKey(key, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public bool IsKeyEmpty(string key)
    {
        using RegistryKey? k = Registry.CurrentUser.OpenSubKey(key);
        return k is not null && k.ValueCount == 0 && k.SubKeyCount == 0;
    }

    public (string Value, bool Expandable)? GetRawString(string key, string name)
    {
        using RegistryKey? k = Registry.CurrentUser.OpenSubKey(key);
        if (k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value)
        {
            return null;
        }

        return (value, k.GetValueKind(name) == RegistryValueKind.ExpandString);
    }

    public void SetString(string key, string name, string value, bool expandable)
    {
        using RegistryKey k = Registry.CurrentUser.CreateSubKey(key, writable: true);
        k.SetValue(name, value, expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
    }
}

/// <summary>レジストリに書く値 1 つ (<see cref="Name"/> が null なら既定の値)。</summary>
public sealed record RegistryValue(string Key, string? Name, string Value);

/// <summary>Explorer に出す文字列 (登録時の表示言語。09 の UI-54 の仕様 1)。</summary>
public sealed record ShellLabels(string OpenWith, string ProjectType, string WorkspaceType, string BinaryType)
{
    /// <summary>リソースを読めないとき (フックの中など) の英語。</summary>
    public static ShellLabels English { get; } = new("Open with HexEditor", "HexEditor project", "HexEditor workspace", "Binary file");
}

/// <summary>登録の内容を決める値: 登録する exe、表示の文字列、「プログラムから開く」の候補にする拡張子 (09 の UI-56 の仕様 2)。</summary>
public sealed record ShellRegistrationContext(string ExePath, ShellLabels Labels, IReadOnlyList<string> OpenWithExtensions)
{
    public static ShellRegistrationContext For(string exePath) => new(exePath, ShellLabels.English, ShellRegistration.DefaultOpenWithExtensions);
}

/// <summary>
/// 登録する項目 1 つ。<see cref="OwnedKeys"/> は自分で作るキー (解除でサブキーごと消す)。<see cref="SharedValues"/> は他のアプリと
/// 共有するキーの中の自分の値 (解除では値だけを消し、空になったキーは消す。例: <c>.iso\OpenWithProgids</c> の <c>HexEditor.Binary</c>)。
/// </summary>
public sealed record ShellRegistrationEntry(
    string Id,
    string Key,
    IReadOnlyList<string> OwnedKeys,
    Func<ShellRegistrationContext, IReadOnlyList<RegistryValue>> Values,
    Func<IReadOnlyList<string>, IReadOnlyList<(string Key, string Name)>>? SharedValues = null);

/// <summary>登録の状態 1 項目 (設定画面「Explorer 連携」。09 の UI-54 の仕様 5〜7)。</summary>
public sealed record ShellRegistrationStatus(string Id, bool Registered, string? RegisteredExe)
{
    /// <summary>登録した exe が <paramref name="exePath"/> と同じか (大文字・小文字は区別しない)。</summary>
    public bool PointsTo(string exePath) =>
        RegisteredExe is { } r && Path.GetFullPath(r).Equals(Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Explorer 連携のレジストリの登録の一覧 (PKG-08 の仕様 3、09 の UI-54、UI-56)。登録と解除で同じ一覧を使う。HKCU だけに書き、HKLM には書かない。
/// インストーラ版はフックで登録し、ポータブル版は設定画面のボタンで登録する (既定は登録しない)。MSIX 版はマニフェストで宣言する (登録しない)。
/// ユーザーの PATH (PKG-08 の仕様 6) は既存の値の一部を書き換えるため、この一覧ではなく <see cref="UserPath"/> で扱う。
/// </summary>
public static class ShellRegistration
{
    public const string AppPathsId = "appPaths";
    public const string ContextMenuId = "contextMenu";
    public const string FileAssociationsId = "fileAssociations";

    /// <summary>App Paths: Win+R で <c>HexEditor</c> と入力すると起動する (PKG-07 の仕様 5)。</summary>
    public const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\HexEditor.exe";

    /// <summary>従来の右クリックメニュー「HexEditor で開く」(UI-54 の仕様 2・4: すべてのファイル。フォルダ・ドライブには出さない)。</summary>
    public const string ContextMenuKey = @"Software\Classes\*\shell\HexEditor";

    public const string ClassesKey = @"Software\Classes";

    public const string ProjectProgId = "HexEditor.Project";
    public const string WorkspaceProgId = "HexEditor.Workspace";
    public const string BinaryProgId = "HexEditor.Binary";

    /// <summary>「プログラムから開く」の候補にした拡張子を記録する値 (HexEditor.Binary のキー。一覧を変えたときと解除に使う)。</summary>
    public const string RegisteredExtensionsValue = "HexEditor.Extensions";

    /// <summary>設定 (候補にする拡張子の一覧。<c>;</c> 区切り。09 の UI-56 の仕様 2)。</summary>
    public const string OpenWithExtensionsKey = "shell.openWith.extensions";

    /// <summary>独自の形式 (既定のアプリとして関連付ける。UI-56 の仕様 1)。</summary>
    public static IReadOnlyList<(string Extension, string ProgId)> OwnFormats { get; } =
    [
        (".hexproj", ProjectProgId),
        (".hexworkspace", WorkspaceProgId),
    ];

    /// <summary>「プログラムから開く」の候補にする既定の拡張子 (UI-56 の仕様 2。既定のアプリにはしない)。</summary>
    public static IReadOnlyList<string> DefaultOpenWithExtensions { get; } = [".bin", ".dat", ".img", ".rom", ".dmp", ".raw", ".iso"];

    public static IReadOnlyList<ShellRegistrationEntry> Entries { get; } =
    [
        new(AppPathsId, AppPathsKey, [AppPathsKey], c => [new(AppPathsKey, null, c.ExePath), new(AppPathsKey, "Path", Path.GetDirectoryName(c.ExePath) ?? string.Empty)]),
        new(ContextMenuId, ContextMenuKey, [ContextMenuKey], c =>
        [
            new(ContextMenuKey, null, c.Labels.OpenWith),
            new(ContextMenuKey, "Icon", $"\"{c.ExePath}\",0"),
            new(ContextMenuKey + @"\command", null, Command(c.ExePath)),
        ]),
        new(
            FileAssociationsId,
            $@"{ClassesKey}\{ProjectProgId}",
            [
                $@"{ClassesKey}\{ProjectProgId}", $@"{ClassesKey}\{WorkspaceProgId}", $@"{ClassesKey}\{BinaryProgId}",
                .. OwnFormats.Select(f => $@"{ClassesKey}\{f.Extension}"),
            ],
            AssociationValues,
            extensions => [.. extensions.Select(e => ($@"{ClassesKey}\{e}\OpenWithProgids", BinaryProgId))]),
    ];

    /// <summary>開く操作のコマンド (<c>"exe" "%1"</c>)。複数のファイルを選んだ場合は Explorer がファイルごとに起動し、単一インスタンス (UI-15) が 1 つのウィンドウにまとめる。</summary>
    public static string Command(string exePath) => $"\"{exePath}\" \"%1\"";

    /// <summary>コマンドの文字列から exe のパスを取り出す (<c>"C:\x\HexEditor.exe" "%1"</c> → <c>C:\x\HexEditor.exe</c>)。</summary>
    public static string? ExeFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string text = command.Trim();
        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        int space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    /// <summary>
    /// 設定の拡張子の一覧を読む (<c>;</c> または <c>,</c> 区切り、先頭の <c>.</c> は省略可)。空なら既定の一覧。
    /// 区切りだけ (<c>;</c>) は「候補にしない」(設定画面ですべてのチェックを外した状態。<see cref="FormatExtensions"/>)。
    /// </summary>
    public static IReadOnlyList<string> ParseExtensions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DefaultOpenWithExtensions;
        }

        string[] parts = text.Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return [.. parts.Select(p => (p.StartsWith('.') ? p : "." + p).ToLowerInvariant())
            .Where(p => p.Length > 1 && p.Skip(1).All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-'))
            .Where(p => !OwnFormats.Any(f => f.Extension == p))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// 設定画面のチェックボックスで選んだ拡張子を設定の値にする (<see cref="ParseExtensions"/> の逆)。既定の一覧と同じなら空、
    /// 何も選ばなければ <c>;</c>。
    /// </summary>
    public static string FormatExtensions(IEnumerable<string> extensions)
    {
        List<string> list = [.. ParseExtensions(string.Join(';', extensions.Append(";")))];
        if (list.Count == DefaultOpenWithExtensions.Count && !list.Except(DefaultOpenWithExtensions, StringComparer.OrdinalIgnoreCase).Any())
        {
            return string.Empty;
        }

        return list.Count == 0 ? ";" : string.Join(';', list);
    }

    /// <summary>
    /// 一覧のうち <paramref name="disabled"/> (利用者が設定で解除した項目。state.json) 以外を登録する。冪等。
    /// 解除された項目は消す (更新後に復活させない。PKG-08 の仕様 2)。失敗した項目の Id と理由を返す。
    /// </summary>
    public static IReadOnlyList<string> Register(IUserRegistry registry, string exePath, IReadOnlySet<string> disabled, Func<bool>? timeUp = null) =>
        Register(registry, ShellRegistrationContext.For(exePath), disabled, null, timeUp);

    /// <summary>
    /// 一覧のうち <paramref name="only"/> (null ならすべて) で、<paramref name="disabled"/> 以外を登録する。
    /// </summary>
    public static IReadOnlyList<string> Register(IUserRegistry registry, ShellRegistrationContext context, IReadOnlySet<string> disabled,
        IReadOnlySet<string>? only = null, Func<bool>? timeUp = null)
    {
        var failures = new List<string>();
        foreach (ShellRegistrationEntry entry in Entries.Where(e => only is null || only.Contains(e.Id)))
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
                    Remove(registry, entry);
                    continue;
                }

                // 前回の登録で候補にした拡張子のうち、今回の一覧にないものの値を消す。
                if (entry.SharedValues is { } shared)
                {
                    IReadOnlyList<string> previous = RecordedExtensions(registry);
                    RemoveShared(registry, shared([.. previous.Except(context.OpenWithExtensions, StringComparer.OrdinalIgnoreCase)]));
                }

                foreach (RegistryValue value in entry.Values(context))
                {
                    registry.SetValue(value.Key, value.Name, value.Value);
                }

                foreach ((string key, string name) in entry.SharedValues?.Invoke(context.OpenWithExtensions) ?? [])
                {
                    registry.SetValue(key, name, string.Empty);
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
    public static IReadOnlyList<string> Unregister(IUserRegistry registry, Func<bool>? timeUp = null) => Unregister(registry, null, timeUp);

    /// <summary><paramref name="only"/> (null ならすべて) の項目を消す。</summary>
    public static IReadOnlyList<string> Unregister(IUserRegistry registry, IReadOnlySet<string>? only, Func<bool>? timeUp = null)
    {
        var failures = new List<string>();
        foreach (ShellRegistrationEntry entry in Entries.Where(e => only is null || only.Contains(e.Id)))
        {
            if (timeUp?.Invoke() == true)
            {
                failures.Add($"{entry.Id}: skipped (time limit)");
                continue;
            }

            try
            {
                Remove(registry, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                failures.Add($"{entry.Id}: {ex.GetType().Name}");
            }
        }

        return failures;
    }

    /// <summary>各項目の登録の状態 (登録した exe のパス)。</summary>
    public static IReadOnlyList<ShellRegistrationStatus> Status(IUserRegistry registry) =>
    [
        new(AppPathsId, registry.KeyExists(AppPathsKey), registry.GetValue(AppPathsKey, null)),
        new(ContextMenuId, registry.KeyExists(ContextMenuKey), ExeFromCommand(registry.GetValue(ContextMenuKey + @"\command", null))),
        new(FileAssociationsId, registry.KeyExists($@"{ClassesKey}\{ProjectProgId}"),
            ExeFromCommand(registry.GetValue($@"{ClassesKey}\{ProjectProgId}\shell\open\command", null))),
    ];

    /// <summary>候補として登録してある拡張子 (前回の登録の記録)。</summary>
    public static IReadOnlyList<string> RecordedExtensions(IUserRegistry registry) =>
        (registry.GetValue($@"{ClassesKey}\{BinaryProgId}", RegisteredExtensionsValue) ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<RegistryValue> AssociationValues(ShellRegistrationContext c)
    {
        string icon = $"\"{c.ExePath}\",0";
        var values = new List<RegistryValue>();
        foreach ((string progId, string label) in new[]
        {
            (ProjectProgId, c.Labels.ProjectType),
            (WorkspaceProgId, c.Labels.WorkspaceType),
            (BinaryProgId, c.Labels.BinaryType),
        })
        {
            string key = $@"{ClassesKey}\{progId}";
            values.Add(new(key, null, label));
            values.Add(new(key + @"\DefaultIcon", null, icon));
            values.Add(new(key + @"\shell\open\command", null, Command(c.ExePath)));
        }

        values.Add(new($@"{ClassesKey}\{BinaryProgId}", RegisteredExtensionsValue, string.Join(';', c.OpenWithExtensions)));

        // 独自の形式は既定のアプリにする (UI-56 の仕様 1)。
        foreach ((string extension, string progId) in OwnFormats)
        {
            values.Add(new($@"{ClassesKey}\{extension}", null, progId));
            values.Add(new($@"{ClassesKey}\{extension}\OpenWithProgids", progId, string.Empty));
        }

        return values;
    }

    private static void Remove(IUserRegistry registry, ShellRegistrationEntry entry)
    {
        if (entry.SharedValues is { } shared)
        {
            IReadOnlyList<string> extensions = [.. DefaultOpenWithExtensions.Union(RecordedExtensions(registry), StringComparer.OrdinalIgnoreCase)];
            RemoveShared(registry, shared(extensions));
        }

        foreach (string key in entry.OwnedKeys)
        {
            registry.DeleteKeyTree(key);
        }
    }

    /// <summary>共有のキーから自分の値だけを消し、空になったキー (と、空になった拡張子のキー) を消す。</summary>
    private static void RemoveShared(IUserRegistry registry, IReadOnlyList<(string Key, string Name)> values)
    {
        foreach ((string key, string name) in values)
        {
            if (!registry.KeyExists(key))
            {
                continue;
            }

            registry.DeleteValue(key, name);
            for (string? k = key; k is not null && k.Length > ClassesKey.Length && registry.IsKeyEmpty(k); k = Parent(k))
            {
                registry.DeleteKeyTree(k);
            }
        }
    }

    private static string? Parent(string key) => key.LastIndexOf('\\') is var i and > 0 ? key[..i] : null;
}
