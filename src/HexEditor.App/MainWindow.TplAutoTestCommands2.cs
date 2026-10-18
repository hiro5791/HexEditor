#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;

namespace HexEditor.App;

/// <summary>テスト用の命令のつづき (オンラインのテンプレート (TPL-36)、外部からの自動操作 (AUTO-45))。</summary>
public sealed partial class MainWindow
{
    private Task<JsonObject?> HandleTplAutoTestCommands2Async(string cmd, JsonObject request) => Task.FromResult<JsonObject?>(null);
}
#endif
