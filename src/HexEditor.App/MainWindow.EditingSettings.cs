using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using HexEditor.App.ViewModels;
using HexEditor.Core.Engine;
using HexEditor.Core.View;

namespace HexEditor.App;

/// <summary>
/// 編集の設定 (UI-22 の「編集」の区画) を、このウィンドウのドキュメントに反映する: 既定の入力モード (EDIT-10 の仕様 1)、入力・削除・
/// 貼り付け・コピーの動作 (<see cref="EditingOptions"/>)、入力をまとめる間隔 (EDIT-19 の仕様 5)、保存時に履歴を消す (EDIT-19 の仕様 9)。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>既定の入力モードを当てたビュー (タブを別のウィンドウに移しても当て直さない)。</summary>
    private static readonly ConditionalWeakTable<EditorState, object> s_inputModeApplied = [];

    /// <summary>保存時に履歴を消す処理を付けたドキュメント。</summary>
    private static readonly ConditionalWeakTable<Document, object> s_saveHooked = [];

    /// <summary>コンストラクターから 1 回呼ぶ。</summary>
    private void InitializeEditingSettings()
    {
        foreach (DocumentViewModel doc in Vm.Documents)
        {
            ApplyEditingSettings(doc);
        }

        Vm.Documents.CollectionChanged += EditingSettings_DocumentsChanged;
        Action<IReadOnlyCollection<string>> settingsChanged = keys =>
        {
            if (keys.Any(k => k.StartsWith("edit.", StringComparison.Ordinal) || k.StartsWith("clipboard.", StringComparison.Ordinal)))
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    foreach (DocumentViewModel doc in Vm.Documents)
                    {
                        ApplyEditingSettings(doc);
                    }
                });
            }
        };
        App.Settings.Changed += settingsChanged;
        Closed += (_, _) =>
        {
            if (_closingConfirmed)
            {
                App.Settings.Changed -= settingsChanged;
                Vm.Documents.CollectionChanged -= EditingSettings_DocumentsChanged;
            }
        };
    }

    private void EditingSettings_DocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (DocumentViewModel doc in e.NewItems?.OfType<DocumentViewModel>() ?? [])
        {
            ApplyEditingSettings(doc);
        }
    }

    /// <summary>今の設定の編集の動作。</summary>
    internal static EditingOptions CurrentEditingOptions() =>
        EditingSettings.Read(App.Settings.GetString, App.Settings.GetBool, App.Settings.GetInt);

    private void ApplyEditingSettings(DocumentViewModel doc)
    {
        EditorState editor = doc.Editor;
        editor.Options = CurrentEditingOptions();
        ApplySelectionSettings(doc);
        doc.Document.History.CoalesceInterval = EditingSettings.CoalesceInterval(App.Settings.GetInt(EditingSettings.UndoCoalesceSecondsKey, 2));

        // 新しく開いたドキュメントのモードは設定「既定の入力モード」に従う (EDIT-10 の仕様 1)。固定長のドキュメントは常に上書き。
        if (!s_inputModeApplied.TryGetValue(editor, out _))
        {
            s_inputModeApplied.Add(editor, new object());
            if (App.Settings.GetString(EditingSettings.DefaultInputModeKey, "overwrite") == "insert" && !editor.InsertMode && doc.Document.CanResize)
            {
                editor.ToggleInsertMode();
            }
        }

        if (!s_saveHooked.TryGetValue(doc.Document, out _))
        {
            s_saveHooked.Add(doc.Document, new object());
            Document document = doc.Document;
            document.Changed += (_, e) =>
            {
                // 保存時に履歴を消す (EDIT-19 の仕様 9。既定オフ)。
                if (e.Kind == DocumentChangeKind.Saved && App.Settings.GetBool(EditingSettings.ClearHistoryOnSaveKey, false))
                {
                    document.ClearHistory();
                }
            };
        }
    }
}
