#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Services;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の振り分け (テスト用のビルドだけ)。命令に <c>"window": 番号</c> (0 から、作った順) があればそのウィンドウ、なければ
/// 最後にアクティブだったウィンドウで実行する。<c>windows</c> はすべてのウィンドウの一覧を返す。
/// </summary>
public static partial class WindowManager
{
    public static async Task<JsonObject> HandleTestCommandAsync(JsonObject request)
    {
        string cmd = request["cmd"]?.GetValue<string>() ?? string.Empty;
        switch (cmd)
        {
            case "windows":
                return TestWindows();
            case "activateWindow":
                // 操作の対象を変える (実際のアクティブ化の代わり。テストではウィンドウをアクティブにしない)。
                MarkActive(s_windows[(int)TestHookSettings.ReadLong(request["window"], 0)]);
                return new JsonObject { ["ok"] = true };
            case "exitAll":
                foreach (MainWindow w in s_windows.ToList())
                {
                    w.CloseForExit();
                }

                return new JsonObject { ["ok"] = true };
        }

        if (s_windows.Count == 0)
        {
            throw new InvalidOperationException("No window.");
        }

        MainWindow target = request["window"] is { } index ? s_windows[(int)TestHookSettings.ReadLong(index, 0)] : Current;
        return await target.HandleTestCommandAsync(request);
    }

    /// <summary>すべてのウィンドウ: 位置と大きさ (物理ピクセル)、タブ、アクティブなタブ、ウィンドウハンドル。</summary>
    private static JsonObject TestWindows()
    {
        var windows = new JsonArray();
        foreach (MainWindow w in s_windows)
        {
            windows.Add(w.TestWindowSummary());
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["windows"] = windows,
            ["current"] = LastActive is { } active ? s_windows.IndexOf(active) : -1,
        };
    }
}
#endif
