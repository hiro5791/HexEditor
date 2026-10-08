using System.Runtime.InteropServices;
using HexEditor.App.Hosting;
using HexEditor.Platform.Shell;

namespace HexEditor.App.Services;

/// <summary>
/// Explorer 連携 (09 の UI-54 右クリックメニュー、UI-56 ファイルの関連付け) のアプリ側。登録の内容と判定は Platform の
/// <see cref="ShellIntegration"/>。表示の文字列は今の表示言語のリソースから取る (UI-54 の仕様 1)。
/// 登録・解除できるのはインストーラ版とポータブル版だけ (開発中の実行と UI テストでは登録しない。レジストリを変えない)。
/// </summary>
public static class ExplorerIntegration
{
    private static ShellIntegration? _shell;

    public static ShellIntegration Shell => _shell ??= Create();

    /// <summary>
    /// ポータブル版の「この PC から登録を解除」(10 の PKG-09 の仕様 4)。設定画面のボタンとテスト用の命令が呼ぶ。
    /// 成功なら「フォルダを削除すればアンインストールは完了です」、失敗した項目があればその一覧の文言を返す。
    /// </summary>
    public static (IReadOnlyList<string> Failures, string Message) UnregisterFromThisPc()
    {
        IAppEnvironment env = Program.Environment;
        IReadOnlyList<string> failures = Shell.UnregisterFromThisPc(() => JumpListService.DeleteList(env.AppUserModelId), ToastNotifier.UnregisterAll,
            env.Locations.Temp);
        if (failures.Count > 0)
        {
            AppLog.Warning("Unregister from this PC failed: " + string.Join(", ", failures));
        }
        else
        {
            AppLog.Info("Unregistered from this PC.");
        }

        return (failures, failures.Count == 0
            ? Loc.Get("Shell_UnregisterPcDone")
            : Loc.Format("Shell_UnregisterPcFailed", string.Join(", ", failures)));
    }

    /// <summary>今の表示言語の文字列。リソースを読めない段階 (インストールのフック) では英語。</summary>
    public static ShellLabels Labels()
    {
        try
        {
            return new ShellLabels(Loc.Get("Shell_OpenWith"), Loc.Get("Shell_ProjectType"), Loc.Get("Shell_WorkspaceType"), Loc.Get("Shell_BinaryType"));
        }
        catch (Exception)
        {
            return ShellLabels.English;
        }
    }

    private static ShellIntegration Create()
    {
        IAppEnvironment env = Program.Environment;
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HexEditor.exe");
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new ShellIntegration(new WindowsUserRegistry(), env.Distribution, exe, new OtherInstallations(localAppData), Labels,
            () => ShellRegistration.ParseExtensions(App.Settings?.GetString(ShellRegistration.OpenWithExtensionsKey, string.Empty)),
            NotifyAssociationsChanged, new FlushedStateSections(), UserPath.NotifyEnvironmentChanged);
    }

    /// <summary>
    /// 利用者が解除した項目の記録 (state.json。PKG-08 の仕様 5)。更新後のフックが別のプロセスで読むため、書いたらすぐにファイルに書き出す。
    /// </summary>
    private sealed class FlushedStateSections : IStateSections
    {
        private readonly AppStateSections _inner = new();

        public System.Text.Json.Nodes.JsonObject ReadSection(string section) => _inner.ReadSection(section);

        public bool WriteSection(string section, System.Text.Json.Nodes.JsonObject value)
        {
            bool written = _inner.WriteSection(section, value);
            Commands.CommandService.State.Flush();
            return written;
        }
    }

    /// <summary>登録・解除の後に Explorer に知らせる (UI-56 の仕様 5)。</summary>
    private static void NotifyAssociationsChanged()
    {
        const int ShcneAssocChanged = 0x08000000;
        SHChangeNotify(ShcneAssocChanged, 0, 0, 0);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
