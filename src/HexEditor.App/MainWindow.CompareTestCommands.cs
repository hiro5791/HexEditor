#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;

namespace HexEditor.App;

/// <summary>テスト用の命令の通り道の、比較 (ANA-01〜ANA-08) の命令。</summary>
public sealed partial class MainWindow
{
    /// <summary>比較の命令。知らない命令なら null。</summary>
    private Task<JsonObject?> HandleCompareTestCommandsAsync(string cmd, JsonObject request) => Task.FromResult<JsonObject?>(null);
}
#endif
