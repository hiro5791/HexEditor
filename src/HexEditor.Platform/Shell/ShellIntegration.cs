namespace HexEditor.Platform.Shell;

/// <summary>この PC にある他の配布形態の HexEditor の場所 (10 の PKG-31、09 の UI-54 の仕様 7)。</summary>
public sealed record OtherInstallations(string LocalAppData)
{
    /// <summary>インストーラ版の exe (Velopack のインストール先 <c>%LocalAppData%\HexEditor\current\HexEditor.exe</c>)。</summary>
    public string InstallerExe => Path.Combine(LocalAppData, "HexEditor", "current", "HexEditor.exe");

    /// <summary>MSIX 版の実行エイリアス (インストールされていればある。PKG-03)。</summary>
    public string MsixAlias => Path.Combine(LocalAppData, "Microsoft", "WindowsApps", "hexeditor.exe");

    public bool MsixInstalled => File.Exists(MsixAlias);

    public bool IsInstallerExe(string? exe) =>
        exe is not null && Path.GetFullPath(exe).Equals(Path.GetFullPath(InstallerExe), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Explorer 連携の状態の見え方 (設定画面「Explorer 連携」と、起動時の InfoBar。09 の UI-54 の仕様 5〜7)。</summary>
public sealed record ShellIntegrationState(
    bool Supported,
    bool ContextMenuRegistered,
    bool FileAssociationsRegistered,
    bool Stale,
    bool OtherDistributionRegistered,
    string? RegisteredExe);

/// <summary>
/// Explorer 連携の項目 1 つ (右クリックメニュー <see cref="ShellRegistration.ContextMenuId"/>、ファイルの関連付け
/// <see cref="ShellRegistration.FileAssociationsId"/>) の状態。<see cref="Ours"/> は今の exe を指す登録。<see cref="Stale"/> は
/// ポータブル版で古い場所を指す登録 (UI-54 の仕様 6)。<see cref="OtherDistribution"/> は他の配布形態の exe を指す登録 (仕様 7)。
/// </summary>
public sealed record ShellItemState(string Id, bool Registered, bool Ours, bool Stale, bool OtherDistribution, string? RegisteredExe);

/// <summary>
/// Explorer 連携の登録・解除・状態 (09 の UI-54、UI-56、10 の PKG-08)。インストーラ版はインストール時のフックで登録し、
/// ポータブル版は設定画面のボタンで登録する。MSIX 版はマニフェストで宣言するため、ここでは登録しない (「Windows の設定で管理されます」)。
/// 開発中の実行も登録しない (PKG-12 の仕様 1 の 4)。
/// インストーラ版では、利用者が解除した項目を state.json (<paramref name="state"/>) に記録し、更新後のフックが登録し直さないようにする
/// (PKG-08 の仕様 5)。登録し直したら記録を消す。
/// </summary>
public sealed class ShellIntegration(IUserRegistry registry, Distribution distribution, string exePath, OtherInstallations others,
    Func<ShellLabels> labels, Func<IReadOnlyList<string>> extensions, Action? notifyAssociationsChanged = null, IStateSections? state = null,
    Action? notifyEnvironmentChanged = null)
{
    /// <summary>右クリックメニューと関連付けの項目 (App Paths は登録の対象外。インストーラ版のフックだけが扱う)。</summary>
    public static IReadOnlySet<string> ExplorerEntries { get; } = new HashSet<string> { ShellRegistration.ContextMenuId, ShellRegistration.FileAssociationsId };

    /// <summary>この配布形態で、アプリから登録・解除できるか。</summary>
    public bool Supported => distribution is Distribution.Installer or Distribution.Portable;

    public ShellIntegrationState State()
    {
        if (!Supported)
        {
            // MSIX 版: インストーラ版・ポータブル版が HKCU に登録していれば、他の配布形態の登録として示す (UI-54 の仕様 7。読むだけ)。
            if (distribution == Distribution.Msix)
            {
                IReadOnlyList<ShellRegistrationStatus> found = ShellRegistration.Status(registry);
                ShellRegistrationStatus? any = found.FirstOrDefault(s => ExplorerEntries.Contains(s.Id) && s.Registered);
                return new ShellIntegrationState(false, false, false, false, any is not null, any?.RegisteredExe);
            }

            return new ShellIntegrationState(false, false, false, false, others.MsixInstalled, null);
        }

        IReadOnlyList<ShellRegistrationStatus> status = ShellRegistration.Status(registry);
        ShellRegistrationStatus menu = status.First(s => s.Id == ShellRegistration.ContextMenuId);
        ShellRegistrationStatus assoc = status.First(s => s.Id == ShellRegistration.FileAssociationsId);
        string? registered = menu.RegisteredExe ?? assoc.RegisteredExe;
        bool ours = (menu.Registered && menu.PointsTo(exePath)) || (assoc.Registered && assoc.PointsTo(exePath));
        bool registeredElsewhere = registered is not null && !ours;

        // ポータブル版で、登録した exe が今の exe と違い、インストーラ版の exe でもない (フォルダを移動した。UI-54 の仕様 6)。
        bool stale = distribution == Distribution.Portable && registeredElsewhere && !others.IsInstallerExe(registered);

        // 他の配布形態の登録 (UI-54 の仕様 7): インストーラ版・ポータブル版の登録が別の配布形態の exe を指す、または MSIX 版がある。
        bool other = (registeredElsewhere && (distribution == Distribution.Portable ? others.IsInstallerExe(registered) : true)) || others.MsixInstalled;
        return new ShellIntegrationState(true, menu.Registered, assoc.Registered, stale, other, registered);
    }

    /// <summary>
    /// 項目ごとの状態 (設定画面「Explorer 連携」の右クリックメニューとファイルの関連付けの行。UI-54 の仕様 5〜7、UI-56)。
    /// 登録できない配布形態では空。
    /// </summary>
    public IReadOnlyList<ShellItemState> Items()
    {
        if (!Supported)
        {
            return [];
        }

        return
        [
            .. ShellRegistration.Status(registry).Where(s => ExplorerEntries.Contains(s.Id)).Select(s =>
            {
                bool ours = s.Registered && s.PointsTo(exePath);
                bool installer = s.Registered && !ours && others.IsInstallerExe(s.RegisteredExe);
                return new ShellItemState(s.Id, s.Registered, ours,
                    Stale: distribution == Distribution.Portable && s.Registered && !ours && !installer,
                    OtherDistribution: distribution == Distribution.Portable && installer || distribution == Distribution.Installer && s.Registered && !ours,
                    s.RegisteredExe);
            }),
        ];
    }

    /// <summary>
    /// ポータブル版の古い登録 (フォルダを移動した。UI-54 の仕様 6) のある項目。起動時の InfoBar の「更新する」「登録を解除する」は
    /// この項目だけを対象にする (登録していなかった項目を登録しない)。
    /// </summary>
    public IReadOnlySet<string> StaleItems() => Items().Where(i => i.Stale).Select(i => i.Id).ToHashSet();

    /// <summary>右クリックメニューと関連付けを登録する (ポータブル版の「登録する」、古い登録の「更新する」)。失敗した項目と理由を返す。</summary>
    public IReadOnlyList<string> Register(IReadOnlySet<string>? only = null)
    {
        if (!Supported)
        {
            return ["unsupported"];
        }

        var context = new ShellRegistrationContext(exePath, labels(), extensions());
        IReadOnlySet<string> items = only ?? ExplorerEntries;
        IReadOnlyList<string> failures = ShellRegistration.Register(registry, context, new HashSet<string>(), items);
        notifyAssociationsChanged?.Invoke();
        RecordDisabled(items, disabled: false);
        return failures;
    }

    /// <summary>
    /// 登録を解除する (「登録を解除する」。UI-54 の仕様 5、UI-56 の受け入れ基準 3: 関連するキーをすべて消す)。
    /// ポータブル版では、インストーラ版の exe を指す登録 (インストーラ版の登録) は消さない (「この PC から登録を解除」と同じ)。
    /// </summary>
    public IReadOnlyList<string> Unregister(IReadOnlySet<string>? only = null)
    {
        if (!Supported)
        {
            return ["unsupported"];
        }

        IReadOnlySet<string> items = only ?? ExplorerEntries;
        if (distribution == Distribution.Portable)
        {
            HashSet<string> installer =
            [
                .. ShellRegistration.Status(registry).Where(s => s.Registered && others.IsInstallerExe(s.RegisteredExe)).Select(s => s.Id),
            ];
            items = items.Where(i => !installer.Contains(i)).ToHashSet();
            if (items.Count == 0)
            {
                return [];
            }
        }

        IReadOnlyList<string> failures = ShellRegistration.Unregister(registry, items);
        notifyAssociationsChanged?.Invoke();
        RecordDisabled(items, disabled: true);
        return failures;
    }

    /// <summary>
    /// ポータブル版の「この PC から登録を解除」(10 の PKG-09 の仕様 4): レジストリの登録 (右クリックメニュー、ファイルの関連付け、PATH)、
    /// ジャンプリスト、トースト通知の登録、<c>%TEMP%\HexEditor-&lt;ハッシュ&gt;\</c> を消す。インストーラ版の登録 (インストーラ版の exe を
    /// 指すもの) は消さない。使用中の一時ファイルは終了時に消える (PKG-06 の仕様 4)。失敗した項目と理由を返す (仕様の「エラー」: 一覧で表示する)。
    /// </summary>
    /// <param name="clearJumpList">ジャンプリストを消す (アプリの AppUserModelID の一覧)。</param>
    /// <param name="unregisterToast">トースト通知の登録を解除する (Windows App SDK の AppNotificationManager.UnregisterAll)。</param>
    /// <param name="tempFolder">このコピーの一時フォルダ。</param>
    public IReadOnlyList<string> UnregisterFromThisPc(Action clearJumpList, Action unregisterToast, string tempFolder)
    {
        if (distribution != Distribution.Portable)
        {
            return ["unsupported"];
        }

        var failures = new List<string>();
        try
        {
            HashSet<string> ours =
            [
                .. ShellRegistration.Status(registry)
                    .Where(s => ExplorerEntries.Contains(s.Id) && s.Registered && !others.IsInstallerExe(s.RegisteredExe))
                    .Select(s => s.Id),
            ];
            if (ours.Count > 0)
            {
                failures.AddRange(ShellRegistration.Unregister(registry, ours));
                notifyAssociationsChanged?.Invoke();
            }

            if (UserPath.Remove(registry, UserPath.FolderFor(exePath)))
            {
                notifyEnvironmentChanged?.Invoke();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            failures.Add($"registry: {ex.GetType().Name}");
        }

        Step("jumpList", clearJumpList);
        Step("toast", unregisterToast);
        DataDirectory.DeleteTempAtExit(tempFolder);
        return failures;

        void Step(string id, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // COM の失敗 (通知が使えない環境など) を含め、残りの項目を続ける。
                failures.Add($"{id}: {ex.GetType().Name}");
            }
        }
    }

    /// <summary>「コマンドラインから使えるようにする」(PKG-08 の仕様 6) を変えられるか。インストーラ版だけ。</summary>
    public bool CommandLineSupported => distribution == Distribution.Installer;

    /// <summary>ユーザーの PATH にアプリのフォルダがあるか。</summary>
    public bool CommandLineEnabled => CommandLineSupported && UserPath.Contains(registry, UserPath.FolderFor(exePath));

    /// <summary>
    /// ユーザーの PATH にアプリのフォルダを足す・消す (設定画面「詳細」の「コマンドラインから使えるようにする」。PKG-08 の仕様 6)。
    /// 外した場合は state.json に記録し、更新後のフックで足し直さない。失敗した項目と理由を返す。
    /// </summary>
    public IReadOnlyList<string> SetCommandLineEnabled(bool enabled)
    {
        if (!CommandLineSupported)
        {
            return ["unsupported"];
        }

        try
        {
            string folder = UserPath.FolderFor(exePath);
            if (enabled ? UserPath.Add(registry, folder) : UserPath.Remove(registry, folder))
            {
                notifyEnvironmentChanged?.Invoke();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [$"{UserPath.Id}: {ex.GetType().Name}"];
        }

        RecordDisabled(new HashSet<string> { UserPath.Id }, disabled: !enabled);
        return [];
    }

    /// <summary>利用者が解除した項目の記録 (state.json の <c>shellRegistration.disabled</c>)。インストーラ版だけが使う (更新後のフック)。</summary>
    public IReadOnlySet<string> Disabled()
    {
        if (state?.ReadSection(InstallHooks.StateSection)[InstallHooks.DisabledKey] is System.Text.Json.Nodes.JsonArray array)
        {
            return array.Select(n => n is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string? s) ? s : null).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private void RecordDisabled(IReadOnlySet<string> items, bool disabled)
    {
        if (distribution != Distribution.Installer || state is null)
        {
            return;
        }

        HashSet<string> set = [.. Disabled()];
        bool changed = disabled ? items.Count(set.Add) > 0 : items.Count(set.Remove) > 0;
        if (!changed)
        {
            return;
        }

        System.Text.Json.Nodes.JsonObject section = state.ReadSection(InstallHooks.StateSection);
        section[InstallHooks.DisabledKey] = new System.Text.Json.Nodes.JsonArray([.. set.Order(StringComparer.Ordinal).Select(s => (System.Text.Json.Nodes.JsonNode?)s)]);
        state.WriteSection(InstallHooks.StateSection, section);
    }

    /// <summary>
    /// 表示言語を変えたときに登録し直す (UI-54 の仕様 1)。今の exe を指す登録があり、メニューの名前が今の表示言語と違えば書き直す。
    /// 書き直したら true。
    /// </summary>
    public bool RefreshLabels()
    {
        if (!Supported)
        {
            return false;
        }

        ShellRegistrationStatus menu = ShellRegistration.Status(registry).First(s => s.Id == ShellRegistration.ContextMenuId);
        if (!menu.Registered || !menu.PointsTo(exePath) || registry.GetValue(ShellRegistration.ContextMenuKey, null) == labels().OpenWith)
        {
            return false;
        }

        HashSet<string> only = [ShellRegistration.ContextMenuId];
        if (ShellRegistration.Status(registry).First(s => s.Id == ShellRegistration.FileAssociationsId) is { Registered: true } assoc && assoc.PointsTo(exePath))
        {
            only.Add(ShellRegistration.FileAssociationsId);
        }

        Register(only);
        return true;
    }
}
