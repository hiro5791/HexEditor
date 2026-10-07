using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.View;

namespace HexEditor.App.ViewModels;

/// <summary>タブ 1 つ分のドキュメント。</summary>
public sealed partial class DocumentViewModel : ObservableObject, IDisposable
{
    public DocumentViewModel(Document document, string? filePath, string displayName)
    {
        Document = document;
        Editor = new EditorState(document);
        FilePath = filePath;
        DisplayName = displayName;
        // ステータスバー・タブの見出しをまとめて更新する (空の名前は全プロパティの変更)。
        Document.Changed += (_, _) => OnPropertyChanged(string.Empty);
        Editor.Changed += (_, _) => OnPropertyChanged(string.Empty);
    }

    public Document Document { get; }

    public EditorState Editor { get; }

    /// <summary>保存先のパス。無題のドキュメントでは null。</summary>
    public string? FilePath { get; private set; }

    public string DisplayName { get; private set; }

    /// <summary>タブの見出し。変更があれば先頭に ● を付ける (色だけに頼らない)。</summary>
    public string Header => Document.IsModified ? "● " + DisplayName : DisplayName;

    public string ToolTip => FilePath ?? DisplayName;

    public bool IsUntitled => FilePath is null;

    public void SetSavedPath(string path)
    {
        FilePath = path;
        DisplayName = Path.GetFileName(path);
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(ToolTip));
    }

    /// <summary>ステータスバーの値 (VIEW-40)。</summary>
    public string CursorText => Loc.Format("Status_Offset", FormatOffset(Editor.Cursor));

    public string SelectionText => Editor.HasSelection
        ? Loc.Format("Status_Selection", FormatOffset(Editor.SelectionStart), FormatOffset(Editor.SelectionStart + Editor.SelectionLength - 1), Editor.SelectionLength.ToString("N0"))
        : string.Empty;

    public string ModeText => !Document.CanResize ? Loc.Get("Status_OverwriteFixed")
        : Editor.InsertMode ? Loc.Get("Status_Insert") : Loc.Get("Status_Overwrite");

    public string ColumnText => Editor.ActiveColumn == ActiveColumn.Hex ? Loc.Get("Status_ColumnHex") : Loc.Get("Status_ColumnText");

    public string LengthText => Loc.Format("Status_Length", Document.Length.ToString("N0"));

    public string ModifiedText => Document.IsModified ? Loc.Get("Status_Modified") : string.Empty;

    public static string FormatOffset(long offset) => "0x" + offset.ToString("X");

    public void Dispose() => Document.Dispose();
}
