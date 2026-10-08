#if HEX_TEST_HOOKS
using System.Text.Json.Nodes;
using HexEditor.Core.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace HexEditor.App;

/// <summary>編集のコマンド (EDIT-04〜EDIT-30) のテスト用の命令。</summary>
public sealed partial class MainWindow
{
    private JsonObject? HandleEditTestCommand(string cmd, JsonObject request) => cmd switch
    {
        // 読み取り専用の理由を設定する (ENG-14 の開く処理の代わり。「読み取り専用で開く」や読み取り専用属性のファイル)。
        "setReadOnly" => TestSetReadOnly(request),

        // ドキュメントの一時フォルダのファイル (塗りつぶし・挿入の実データの一時ファイルの後始末を確かめる)。
        "documentTempFiles" => TestDocumentTempFiles(),

        // メニューの項目の有効・無効とチェック (メニューを開かずに読む)。
        "menuItem" => TestMenuItem(request["id"]!.GetValue<string>()),

        // チェックボックス・ラジオボタンを選ぶ (UI オートメーションで押すとフォーカスが動くため)。
        "setChecked" => TestSetChecked(request["id"]!.GetValue<string>(), request["value"]?.GetValue<bool>() ?? true),

        // 一覧 (ListView・ComboBox) の項目の文字列・有効かどうか・選択。
        "listItems" => TestListItems(request["id"]!.GetValue<string>()),

        // 開いている ContentDialog のボタン (PrimaryButton・SecondaryButton・CloseButton) を、アプリの中で同期して押す。
        "dialogButton" => TestDialogButton(request["name"]!.GetValue<string>()),

        // 開いている ContentDialog の数 (閉じる動きが終わるまでは数える)。
        "openDialogs" => new JsonObject
        {
            ["count"] = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Count(p => p.Child is ContentDialog),
            ["closed"] = _editDialogsClosed,
        },
        _ => null,
    };

    private JsonObject TestDialogButton(string name)
    {
        var pending = new Queue<DependencyObject>(VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
            .Select(p => (DependencyObject)p.Child).Where(c => c is not null));
        while (pending.Count > 0)
        {
            DependencyObject node = pending.Dequeue();
            if (node is Button { Name: var n } button && n == name && button.Visibility == Visibility.Visible)
            {
                if (!button.IsEnabled)
                {
                    throw new InvalidOperationException($"Dialog button {name} is disabled.");
                }

                ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button)).Invoke();
                return new JsonObject();
            }

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                pending.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }

        throw new ArgumentException($"Dialog button not found: {name}");
    }

    private JsonObject TestMenuItem(string id)
    {
        MenuFlyoutItem item = MenuItems().FirstOrDefault(i => AutomationProperties.GetAutomationId(i) == id)
            ?? throw new ArgumentException($"Menu item not found: {id}");
        return new JsonObject
        {
            ["enabled"] = item.IsEnabled,
            ["checked"] = item is ToggleMenuFlyoutItem toggle ? toggle.IsChecked : null,
            ["text"] = item.Text,
        };
    }

    private JsonObject TestSetChecked(string id, bool value)
    {
        if (FindElement(id) is not ToggleButton toggle)
        {
            throw new ArgumentException($"Toggle button not found: {id}");
        }

        toggle.IsChecked = value;
        return new JsonObject();
    }

    private JsonObject TestListItems(string id)
    {
        if (FindElement(id) is not Selector selector)
        {
            throw new ArgumentException($"Selector not found: {id}");
        }

        var items = new JsonArray();
        foreach (object item in selector.Items)
        {
            items.Add(new JsonObject
            {
                ["text"] = item is ContentControl c ? c.Content?.ToString() : item?.ToString(),
                ["enabled"] = item is not Control control || control.IsEnabled,
            });
        }

        return new JsonObject { ["items"] = items, ["selectedIndex"] = selector.SelectedIndex };
    }

    private JsonObject TestSetReadOnly(JsonObject request)
    {
        Document document = Vm.Selected?.Document ?? throw new InvalidOperationException("No document.");
        document.SetReadOnly(Enum.Parse<ReadOnlyReason>(request["reason"]?.GetValue<string>() ?? "User", ignoreCase: true));
        return new JsonObject { ["reason"] = document.ReadOnlyReason.ToString() };
    }

    private JsonObject TestDocumentTempFiles()
    {
        Document document = Vm.Selected?.Document ?? throw new InvalidOperationException("No document.");
        string folder = Path.Combine(document.Options.TempDirectory, document.Id.ToString("N"));
        string[] files = Directory.Exists(folder) ? Directory.GetFiles(folder).Select(Path.GetFileName).OfType<string>().ToArray() : [];
        return new JsonObject { ["folder"] = folder, ["files"] = new JsonArray([.. files.Select(f => (JsonNode?)f)]) };
    }
}
#endif
