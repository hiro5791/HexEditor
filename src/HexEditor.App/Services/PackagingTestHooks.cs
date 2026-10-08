#if HEX_TEST_HOOKS
using System.Text.Json;
using System.Text.Json.Nodes;
#endif

namespace HexEditor.App.Services;

/// <summary>
/// 配布・更新・国際化の機能のための、テスト用のビルドだけの仕組み (テスト方針 7.2)。--test-hooks の設定ファイルの次のキーを読む。
/// <list type="bullet">
/// <item><c>windowsLanguages</c>: Windows の表示言語の優先順位の代わり (OS の設定を変えずに UI-43 を確かめる。例: ["ms-MY", "en-US"])。</item>
/// </list>
/// 更新の確認先の差し替えは設定 <c>test.update.source</c> で行う (10 の PKG-17 の仕様 8。Velopack の再起動後も有効にするため)。
/// </summary>
public static class PackagingTestHooks
{
#if HEX_TEST_HOOKS
    /// <summary>テスト用のビルド (設定 test.update.source を読む。PKG-17 の仕様 8)。</summary>
    public const bool TestBuild = true;
#else
    public const bool TestBuild = false;
#endif

    /// <summary>更新の自動の確認の時計を進めた量 (テスト用の命令 advanceUpdateClock。「アプリの時刻を 24 時間進める」)。</summary>
    public static TimeSpan UpdateClockOffset { get; set; }

    /// <summary>Windows の表示言語の代わり。指定がなければ null。</summary>
    public static IReadOnlyList<string>? WindowsLanguages()
    {
#if HEX_TEST_HOOKS
        if (!TestHooks.Active || TestHooks.SettingsPath is not { } path)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path))?["windowsLanguages"] is JsonArray array
                ? [.. array.Select(n => n?.GetValue<string>()).OfType<string>()]
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
#else
        return null;
#endif
    }
}
