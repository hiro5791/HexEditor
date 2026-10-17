using HexEditor.Core.Processes;

namespace HexEditor.Cli.Services;

/// <summary>
/// コマンドラインが管理者として実行中か (AUTO-34 の仕様 8: ヘッドレス実行では <c>--trust</c> がある場合だけスクリプトを実行し、プラグインを読み込む)。
/// 判定はアプリと同じ (<see cref="Win32ProcessAccess.IsCurrentProcessElevated"/>)。
/// </summary>
public static class CliElevation
{
    /// <summary>
    /// テスト用の上書き (テスト用のビルド HEX_TEST_HOOKS だけが読む。<c>1</c> / <c>true</c> = 管理者、<c>0</c> / <c>false</c> = 管理者でない)。
    /// CI の実行環境は管理者として動くため、テストはこれで結果を決める。
    /// </summary>
    public const string TestVariable = "HEXED_TEST_ELEVATED";

    /// <summary>このプロセスが管理者として実行中か (テスト用のビルドでは <see cref="TestVariable"/> が優先)。</summary>
    public static bool Current => TestOverride() ?? Win32ProcessAccess.IsCurrentProcessElevated;

    /// <summary>テスト用の上書きの値 (製品版・指定なしは null)。</summary>
    internal static bool? TestOverride()
    {
#if HEX_TEST_HOOKS
        return Environment.GetEnvironmentVariable(TestVariable)?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" => true,
            "0" or "false" => false,
            _ => null,
        };
#else
        return null;
#endif
    }
}
