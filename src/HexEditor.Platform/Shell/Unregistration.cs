namespace HexEditor.Platform.Shell;

/// <summary>コマンドラインの <c>--unregister</c> の結果。<see cref="Supported"/> が false なら何もしなかった (MSIX 版・開発中の実行)。</summary>
public sealed record UnregisterResult(bool Supported, IReadOnlyList<string> Failures)
{
    /// <summary>終了コード (08 の AUTO-36 の 9): すべて消せたら (何もしなかった場合も) 0、消せない項目があれば 3 (入出力のエラー)。</summary>
    public int ExitCode => Failures.Count == 0 ? 0 : Unregistration.ExitFailed;
}

/// <summary>
/// <c>HexEditor.exe --unregister</c> (08 の AUTO-36 の 9、10 の PKG-09)。GUI を起動せず、配布形態ごとに
/// 設定画面の「この PC から登録を解除」(ポータブル版) またはアンインストール前のフック (インストーラ版) と同じ解除を行う。
/// MSIX 版は登録を Windows が管理するため、開発中の実行は登録しないため、何もしない。
/// </summary>
public static class Unregistration
{
    public const int ExitFailed = 3;

    /// <param name="portable">ポータブル版の解除 (<see cref="ShellIntegration.UnregisterFromThisPc"/>)。</param>
    /// <param name="installer">インストーラ版の解除 (<see cref="InstallHooks.Unregister"/>)。</param>
    public static UnregisterResult Run(Distribution distribution, Func<IReadOnlyList<string>> portable, Func<IReadOnlyList<string>> installer) =>
        distribution switch
        {
            Distribution.Portable => new UnregisterResult(true, portable()),
            Distribution.Installer => new UnregisterResult(true, installer()),
            _ => new UnregisterResult(false, []),
        };
}
