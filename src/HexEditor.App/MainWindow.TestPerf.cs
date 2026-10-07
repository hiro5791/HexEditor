#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using Windows.System;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道の、性能のテストと結合テストの命令 (テスト方針 7.2「描画の計測ログ」)。命令の振り分けは
/// MainWindow.TestMenu.cs の HandleTestCommandAsync。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>
    /// 診断表示 (VIEW-04 の仕様 8) を有効・無効にする。{"enabled": true, "logPath": CSV のパス}。無効にすると記録を書き出して閉じる。
    /// </summary>
    private JsonObject TestDiagnostics(JsonObject request)
    {
        HexView view = CurrentView() ?? throw new InvalidOperationException("No hex view.");
        bool enabled = request["enabled"]?.GetValue<bool>() ?? true;
        if (enabled)
        {
            view.DiagnosticsLogPath = request["logPath"]?.GetValue<string>();
            view.DiagnosticsEnabled = true;
        }
        else
        {
            view.DiagnosticsEnabled = false;
            view.DiagnosticsLogPath = null;
        }

        return new JsonObject { ["enabled"] = view.DiagnosticsEnabled };
    }

    /// <summary>
    /// キー 1 つを、診断の記録に残してから処理する。"key" と違い、すぐには描画しない (通常の描画の予約に任せ、キー入力から
    /// 画面に出るまでを計る)。Hex ビューが処理しなければメニューのショートカットキーとして扱う。
    /// </summary>
    private JsonObject TestKeyMeasured(JsonObject request)
    {
        var key = Enum.Parse<VirtualKey>(request["key"]!.GetValue<string>(), ignoreCase: true);
        bool shift = request["shift"]?.GetValue<bool>() ?? false;
        bool ctrl = request["ctrl"]?.GetValue<bool>() ?? false;
        bool alt = request["alt"]?.GetValue<bool>() ?? false;
        HexView view = CurrentView() ?? throw new InvalidOperationException("No hex view.");
        string handledBy = "none";
        if (view.InjectMeasuredKey(key, shift, ctrl, alt))
        {
            handledBy = "hexView";
        }
        else if (FindAccelerator(key, shift, ctrl, alt) is { } item)
        {
            InvokeMenuItem(item);
            handledBy = "menu";
        }

        return new JsonObject { ["handledBy"] = handledBy };
    }

    /// <summary>キー以外の操作の開始を診断の記録に残す (この後の "invoke" などの反映までを計る)。</summary>
    private JsonObject TestMark()
    {
        (CurrentView() ?? throw new InvalidOperationException("No hex view.")).MarkDiagnostics();
        return new JsonObject();
    }

    /// <summary>
    /// 選択中のドキュメントに、外部のデータの挿入貼り付けと同じく、乱数のバイト列を 1 つの Undo 単位で挿入する
    /// (システムのクリップボードを使わずに追加バッファにデータを入れるため)。{"offset", "length", "seed"}。
    /// 答えにドキュメント ID (復旧用データのフォルダ名) を返す。
    /// </summary>
    private JsonObject TestInsertBytes(JsonObject request)
    {
        DocumentViewModel doc = Vm.Selected ?? throw new InvalidOperationException("No document.");
        long offset = TestHookSettings.ReadLong(request["offset"], 0);
        long length = TestHookSettings.ReadLong(request["length"], 0);
        var random = new Random((int)TestHookSettings.ReadLong(request["seed"], 1));
        byte[] chunk = new byte[(int)Math.Min(length, 16L * 1024 * 1024)];
        using (doc.Document.BeginGroup("貼り付け"))
        {
            for (long done = 0; done < length; done += chunk.Length)
            {
                int n = (int)Math.Min(chunk.Length, length - done);
                random.NextBytes(chunk.AsSpan(0, n));
                doc.Document.Insert(offset + done, chunk.AsSpan(0, n));
            }
        }

        return new JsonObject { ["documentId"] = doc.Document.Id.ToString("N"), ["length"] = doc.Document.Length };
    }
}
#endif
