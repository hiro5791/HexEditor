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
/// Explorer 連携の登録・解除・状態 (09 の UI-54、UI-56、10 の PKG-08)。インストーラ版はインストール時のフックで登録し、
/// ポータブル版は設定画面のボタンで登録する。MSIX 版はマニフェストで宣言するため、ここでは登録しない (「Windows の設定で管理されます」)。
/// 開発中の実行も登録しない (PKG-12 の仕様 1 の 4)。
/// </summary>
public sealed class ShellIntegration(IUserRegistry registry, Distribution distribution, string exePath, OtherInstallations others,
    Func<ShellLabels> labels, Func<IReadOnlyList<string>> extensions, Action? notifyAssociationsChanged = null)
{
    /// <summary>右クリックメニューと関連付けの項目 (App Paths は登録の対象外。インストーラ版のフックだけが扱う)。</summary>
    public static IReadOnlySet<string> ExplorerEntries { get; } = new HashSet<string> { ShellRegistration.ContextMenuId, ShellRegistration.FileAssociationsId };

    /// <summary>この配布形態で、アプリから登録・解除できるか。</summary>
    public bool Supported => distribution is Distribution.Installer or Distribution.Portable;

    public ShellIntegrationState State()
    {
        if (!Supported)
        {
            return new ShellIntegrationState(false, false, false, false, distribution == Distribution.Msix ? false : others.MsixInstalled, null);
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

    /// <summary>右クリックメニューと関連付けを登録する (ポータブル版の「登録する」、古い登録の「更新する」)。失敗した項目と理由を返す。</summary>
    public IReadOnlyList<string> Register(IReadOnlySet<string>? only = null)
    {
        if (!Supported)
        {
            return ["unsupported"];
        }

        var context = new ShellRegistrationContext(exePath, labels(), extensions());
        IReadOnlyList<string> failures = ShellRegistration.Register(registry, context, new HashSet<string>(), only ?? ExplorerEntries);
        notifyAssociationsChanged?.Invoke();
        return failures;
    }

    /// <summary>登録を解除する (「登録を解除する」。UI-54 の仕様 5、UI-56 の受け入れ基準 3: 関連するキーをすべて消す)。</summary>
    public IReadOnlyList<string> Unregister(IReadOnlySet<string>? only = null)
    {
        if (!Supported)
        {
            return ["unsupported"];
        }

        IReadOnlyList<string> failures = ShellRegistration.Unregister(registry, only ?? ExplorerEntries);
        notifyAssociationsChanged?.Invoke();
        return failures;
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
