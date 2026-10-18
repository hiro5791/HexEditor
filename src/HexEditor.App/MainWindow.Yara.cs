using HexEditor.App.Controls;
using HexEditor.App.Panels;
using HexEditor.App.Services;
using HexEditor.Core.Annotations;
using HexEditor.Core.Engine;
using HexEditor.Core.Panels;
using HexEditor.Core.Search.Yara;
using HexEditor.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace HexEditor.App;

/// <summary>
/// 検索 > YARA (FIND-33)。「YARA」パネル (下のパネル。ウィンドウごとに 1 つ) を開き、スキャンの結果の一致を注釈 (INSP-32。出どころ YARA) として
/// エディタに強調表示する。結果一覧を閉じると注釈も消す (INSP-32 の仕様 3)。
/// </summary>
public sealed partial class MainWindow
{
    public const string YaraPanelId = "yara";

    /// <summary>注釈の出どころの ID。</summary>
    private const string YaraAnnotationId = "yara";

    private readonly YaraPanel Yara = new();

    /// <summary>注釈を付けたドキュメント (結果一覧を閉じたら外す)。</summary>
    private Document? _yaraAnnotated;

    private static void RegisterYaraPanel()
    {
        if (PanelRegistry.Find(YaraPanelId) is null)
        {
            PanelRegistry.Register(new PanelRegistration(YaraPanelId, "Panel_Yara_Title", PanelDock.Bottom, ctx => ((MainWindow)ctx.Window).Yara)
            {
                CanFloat = false,
            });
        }
    }

    private void RegisterYaraCommands() => Commands.Register("search.yara", () => ShowPanel(YaraPanelId));

    /// <summary>InitializeSearchPanels から呼ぶ。</summary>
    private void InitializeYaraPanel()
    {
        AutomationProperties.SetAutomationId(Yara, "YaraPanel");
        Yara.EditorSource = () => Editor;
        Yara.PickFiles = PickYaraFilesAsync;
        Yara.PickFolder = PickYaraFolderAsync;
        Yara.AskName = AskRuleSetNameAsync;
        SearchResultsPanel list = Yara.Results;
        list.WindowId = AppWindow.Id;
        list.Operations = Vm.Operations;
        list.Confirm = ConfirmAsync;
        list.HighlightsChanged += (_, _) => UpdateMatchHighlights();
        list.Shown += (_, _) => ShowPanel(YaraPanelId, focus: false);
        list.BookmarksRequested += (_, e) => AddSearchResultBookmarks(e.Editor, e.Items, e.Group);
        list.NoticeRequested += (_, n) => ShowNotice(n.Message, n.Severity);
        list.ActivateRequested += (_, editor) => ActivateEditor(editor);
        list.Closed += (_, _) => ClearYaraAnnotations();
        Yara.Scanned += (_, e) => ApplyYaraAnnotations(e.Editor, e.Search);
        Yara.OpenFileRequested += (_, m) => OpenMultiFileResult(m.Path, m.Offset, m.Length);
    }

    /// <summary>一致した範囲を注釈にする (ツールチップにルール名と文字列の識別子。FIND-33 の仕様 9)。</summary>
    private void ApplyYaraAnnotations(EditorState editor, YaraSearch search)
    {
        ClearYaraAnnotations();
        var set = new AnnotationSet(YaraAnnotationId, AnnotationOrigin.Yara, Loc.Get("Yara_AnnotationSource"));
        var items = new List<Annotation>();
        if (search.Result is { } result)
        {
            long offset = search.ScanOffset;
            foreach (YaraRuleResult rule in result.Rules)
            {
                foreach (YaraStringResult s in rule.Strings)
                {
                    items.Add(new Annotation(offset + s.Offset, s.Length, rule.Rule.Name, null, s.Identifier) { Tag = rule.Rule.Name + ":" + s.Identifier });
                }
            }
        }

        set.AddRange(items);
        AnnotationLayer.For(editor.Document).Register(set);
        _yaraAnnotated = editor.Document;
    }

    private void ClearYaraAnnotations()
    {
        if (_yaraAnnotated is { IsDisposed: false } doc)
        {
            AnnotationLayer.For(doc).Unregister(YaraAnnotationId);
        }

        _yaraAnnotated = null;
    }

    private async Task<IReadOnlyList<string>> PickYaraFilesAsync()
    {
        const string settingsIdentifier = "HexEditor.YaraRules";
        if (TestHooks.OpenPickerResult(settingsIdentifier) is { } paths)
        {
            return paths;
        }

        var picker = new FileOpenPicker(AppWindow.Id) { SettingsIdentifier = settingsIdentifier };
        picker.FileTypeFilter.Add(".yar");
        picker.FileTypeFilter.Add(".yara");
        picker.FileTypeFilter.Add("*");
        return [.. (await picker.PickMultipleFilesAsync()).Select(f => f.Path)];
    }

    private async Task<string?> PickYaraFolderAsync()
    {
        const string settingsIdentifier = "HexEditor.YaraFolder";
        if (TestHooks.OpenPickerResult(settingsIdentifier) is { } paths)
        {
            return paths.FirstOrDefault();
        }

        var picker = new FolderPicker(AppWindow.Id) { SettingsIdentifier = settingsIdentifier };
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async Task<string?> AskRuleSetNameAsync(string current)
    {
        TextBox name = DialogParts.Field("Yara_SetName", Loc.Get("Yara_SetName"), current, monospace: false);
        ContentDialog dialog = DialogParts.Dialog(Root, "YaraSetNameDialog", Loc.Get("Yara_SaveSet"), name, Loc.Get("Yara_SaveRun"));
        name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0;
        dialog.IsPrimaryButtonEnabled = current.Length > 0;
        return await dialog.ShowQueuedAsync() == ContentDialogResult.Primary ? name.Text.Trim() : null;
    }
}
