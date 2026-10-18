namespace HexEditor.App.Services;

/// <summary>
/// アプリ全体の書き込み保護モード (12 の FOR-01) の状態。フェーズ 4 では、このモードで無効にする機能 (11 の TOOL-17 の安全な削除、
/// TOOL-21 の代替データストリームの作成・削除など) が状態を見るための口だけを持つ。起動の <c>--write-block</c> で有効にする。
/// モードの切り替え・ステータスバーの表示・読み取り専用のハンドルへの切り替えは、フェーズ 5 の FOR-01 が担当する。
/// </summary>
internal static class WriteBlockMode
{
    private static bool s_enabled;

    public static bool IsEnabled => s_enabled;

    /// <summary>状態が変わった。</summary>
    public static event EventHandler? Changed;

    public static void Initialize(bool enabled)
    {
        if (s_enabled != enabled)
        {
            s_enabled = enabled;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
