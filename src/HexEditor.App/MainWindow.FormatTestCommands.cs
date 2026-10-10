#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// テスト用の命令の通り道のうち、ファイルの形式の命令: 詳細を指定して開く・インポート / エクスポートのダイアログの操作、ドキュメントの状態 (ベースアドレス・
/// 形式・バイトの状態)、誤りの一覧、通知のボタン。
/// </summary>
public sealed partial class MainWindow
{
    private async Task<JsonObject?> HandleFormatTestCommandAsync(string cmd, JsonObject request)
    {
        switch (cmd)
        {
            case "startCommand":
                // ダイアログを開くコマンドを、閉じるのを待たずに始める。
                _ = Commands.ExecuteAsync(request["id"]!.GetValue<string>());
                return new JsonObject();
            case "selectTab":
                Vm.Selected = Vm.Documents[(int)TestHookSettings.ReadLong(request["index"], 0)];
                return new JsonObject();
            case "formatDoc":
                return TestFormatDocument(request);
            case "byteStates":
                return TestByteStates(request);
            case "formatIssues":
                return new JsonObject
                {
                    ["rows"] = new JsonArray([.. FormatIssuesShown.Select(i => (JsonNode?)new JsonObject
                    {
                        ["line"] = i.Line,
                        ["column"] = i.Column,
                        ["kind"] = i.Kind.ToString(),
                        ["content"] = i.Content,
                        ["text"] = FormatIssueRow(i),
                    })]),
                    ["shown"] = IsPanelShown(FormatIssuesPanelId),
                };
            case "notificationAction":
                return TestNotificationAction(request);
            case "openAdvancedSet":
                return TestOpenAdvanced(request);
            case "transfer":
                return await TestTransferAsync(request);
            case "openRangeInTab":
            {
                // 選択範囲・ブックマークを新しいタブで開く (ENG-39) を、範囲を指定して呼ぶ。開くまでの時間はアプリの中で計る。
                DocumentViewModel doc = Vm.Selected ?? throw new InvalidOperationException("No document.");
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                DocumentViewModel? vm = OpenRangeInNewTab(doc, TestHookSettings.ReadLong(request["offset"], 0), TestHookSettings.ReadLong(request["length"], 0),
                    request["name"]?.GetValue<string>(), request["copy"]?.GetValue<bool>() ?? false);
                return new JsonObject
                {
                    ["opened"] = vm is not null,
                    ["elapsedMs"] = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                };
            }

            default:
                return null;
        }
    }

    private JsonObject TestFormatDocument(JsonObject request)
    {
        DocumentViewModel doc = request["index"] is { } i ? Vm.Documents[(int)TestHookSettings.ReadLong(i, 0)]
            : Vm.Selected ?? throw new InvalidOperationException("No document.");
        Document d = doc.Document;
        return new JsonObject
        {
            ["name"] = doc.DisplayName,
            ["title"] = doc.TabTitle,
            ["header"] = doc.Header,
            ["toolTip"] = doc.ToolTip,
            ["path"] = doc.FilePath,
            ["length"] = d.Length,
            ["baseAddress"] = (long)doc.Editor.View.BaseAddress,
            ["format"] = doc.Encoded?.Format,
            ["formatText"] = doc.FormatText,
            ["modeText"] = doc.ModeText,
            ["readOnly"] = d.IsReadOnly,
            ["canResize"] = d.CanResize,
            ["modified"] = d.IsModified,
            ["linked"] = doc.IsLinkedView,
            ["linkStart"] = d.LinkedParent is null ? null : d.LinkStart,
            ["disconnected"] = d.IsLinkDisconnected,
            ["range"] = doc.RangeLabel,
            ["isRange"] = doc.IsRangeDocument,
            ["issues"] = doc.FormatIssues.Count,
            ["sha256"] = request["hash"]?.GetValue<bool>() == true ? Sha256(d.Current) : null,
            ["tabs"] = Vm.Documents.Count,
        };
    }

    private static string Sha256(DocumentSnapshot snapshot)
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 20];
        for (long offset = 0; offset < snapshot.Length; offset += buffer.Length)
        {
            int n = (int)Math.Min(buffer.Length, snapshot.Length - offset);
            snapshot.Read(offset, buffer.AsSpan(0, n));
            hash.AppendData(buffer, 0, n);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>値と状態 (Valid / NoData など): {offset, length}。読み込みが終わるまで待つ。</summary>
    private JsonObject TestByteStates(JsonObject request)
    {
        DocumentViewModel doc = Vm.Selected ?? throw new InvalidOperationException("No document.");
        long offset = TestHookSettings.ReadLong(request["offset"], 0);
        int length = (int)TestHookSettings.ReadLong(request["length"], 16);
        byte[] bytes = new byte[length];
        var states = new ByteState[length];
        int n = 0;
        for (int attempt = 0; attempt < 2000; attempt++)
        {
            n = doc.Document.Current.ReadForDisplay(offset, bytes, states);
            if (!states.AsSpan(0, n).Contains(ByteState.Loading))
            {
                break;
            }

            Thread.Sleep(5);
        }

        return new JsonObject
        {
            ["hex"] = Convert.ToHexString(bytes, 0, n),
            ["states"] = new JsonArray([.. states.Take(n).Select(s => (JsonNode?)s.ToString())]),
        };
    }

    /// <summary>開いている通知のボタンを押す: {label}。</summary>
    private JsonObject TestNotificationAction(JsonObject request)
    {
        string label = request["label"]!.GetValue<string>();
        foreach (Core.Notifications.Notification n in Vm.Notifications.Open)
        {
            if (n.Actions.FirstOrDefault(a => a.Label == label) is { } action)
            {
                action.Execute();
                return new JsonObject { ["invoked"] = true };
            }
        }

        return new JsonObject { ["invoked"] = false };
    }

    /// <summary>「詳細を指定して開く」のダイアログ: {path, range, start, length, format} を入れて状態を返す。</summary>
    private JsonObject TestOpenAdvanced(JsonObject request)
    {
        OpenAdvancedState s = OpenAdvancedForTest ?? throw new InvalidOperationException("The dialog is not open.");
        if (request["path"]?.GetValue<string>() is { } path)
        {
            s.Path.Text = path;
        }

        if (request["range"]?.GetValue<bool>() is { } range)
        {
            s.Range.IsChecked = range;
        }

        if (request["start"]?.GetValue<string>() is { } start)
        {
            s.Start.Text = start;
        }

        if (request["length"]?.GetValue<string>() is { } length)
        {
            s.Length.Text = length;
        }

        if (request["format"]?.GetValue<string>() is { } format)
        {
            s.Format.SelectedIndex = Array.IndexOf(OpenFormats, format);
        }

        return new JsonObject
        {
            ["startResult"] = s.StartResult.Text,
            ["lengthResult"] = s.LengthResult.Text,
            ["canOpen"] = s.Dialog.IsPrimaryButtonEnabled,
            ["startInvalid"] = s.Start.BorderBrush is not null && s.Start.ReadLocalValue(Control.BorderBrushProperty) != Microsoft.UI.Xaml.DependencyProperty.UnsetValue,
            ["lengthInvalid"] = s.Length.ReadLocalValue(Control.BorderBrushProperty) != Microsoft.UI.Xaml.DependencyProperty.UnsetValue,
        };
    }

    /// <summary>
    /// インポート / エクスポートのダイアログ: {path, format, fields: {key: value}, target: index} を入れ、プレビューが更新されるのを待って状態を返す。
    /// </summary>
    private async Task<JsonObject> TestTransferAsync(JsonObject request)
    {
        TransferDialogState s = TransferForTest ?? throw new InvalidOperationException("The dialog is not open.");
        if (request["format"]?.GetValue<string>() is { } format)
        {
            s.Format.SelectedIndex = s.Formats.ToList().IndexOf(format);
        }

        if (request["path"]?.GetValue<string>() is { } path)
        {
            s.Path.Text = path;
        }

        if (request["target"] is { } target)
        {
            s.Targets[(int)TestHookSettings.ReadLong(target, 0)].IsChecked = true;
        }

        if (request["fields"] is JsonObject fields)
        {
            foreach ((string key, JsonNode? value) in fields)
            {
                string text = value!.ToString();
                switch (s.Fields[key])
                {
                    case TextBox box:
                        box.Text = text;
                        break;
                    case CheckBox check:
                        check.IsChecked = text == "true";
                        break;
                    case ComboBox combo:
                        combo.SelectedIndex = int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
                        break;
                }
            }
        }

        // 入力の変更でプレビューを作り直す処理の完了を待つ。
        for (int i = 0; i < 3; i++)
        {
            await Task.Yield();
            await s.Pending;
        }

        return new JsonObject
        {
            ["format"] = s.Formats[Math.Max(0, s.Format.SelectedIndex)],
            ["preview"] = s.Preview.Text,
            ["summary"] = s.Summary.Text,
            ["canRun"] = s.Dialog.IsPrimaryButtonEnabled,
            ["fields"] = new JsonArray([.. s.Fields.Keys.Select(k => (JsonNode?)k)]),
        };
    }
}
#endif
