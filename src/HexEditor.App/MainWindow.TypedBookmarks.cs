using System.Diagnostics;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Engine;
using HexEditor.Core.Inspector;
using HexEditor.Core.View;

namespace HexEditor.App;

/// <summary>
/// 型付きブックマークと位置の式 (INSP-29) のつなぎ込み: 一覧の値の列と書き換え、編集のフライアウトの型・位置の式、ドキュメントの編集から
/// 200 ms 後の位置の式の再評価。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>位置の式の再評価を待つ時間 (最後の編集から。INSP-29 の仕様 6)。</summary>
    internal static readonly TimeSpan ExpressionDelay = TimeSpan.FromMilliseconds(200);

    private Microsoft.UI.Dispatching.DispatcherQueueTimer _expressionTimer = null!;
    private readonly HashSet<DocumentAnnotations> _expressionPending = [];

    /// <summary>再評価の記録 (テスト用の状態の表示): 最後の編集と、最後に再評価した時刻 (Stopwatch の値)。</summary>
    private long _lastExpressionEdit;
    private long _lastExpressionRun;

    /// <summary><see cref="InitializeAnnotations"/> から呼ぶ。</summary>
    private void InitializeTypedBookmarks()
    {
        _expressionTimer = DispatcherQueue.CreateTimer();
        _expressionTimer.Interval = ExpressionDelay;
        _expressionTimer.IsRepeating = false;
        _expressionTimer.Tick += (_, _) => RunPendingExpressions();
        Action<BookmarkRowViewModel>? prepare = _bookmarksVm.Rows.Prepare;
        _bookmarksVm.Rows.Prepare = row =>
        {
            prepare?.Invoke(row);
            row.ValueSource = TypedBookmarkValue;
        };
    }

    /// <summary>ドキュメントの編集で位置の式の再評価を予約する (<see cref="HookAnnotations"/> から呼ぶ)。外す処理を返す。</summary>
    private Action HookTypedBookmarks(DocumentViewModel doc, DocumentAnnotations annotations)
    {
        EventHandler<DocumentChangedEventArgs> changed = (_, e) =>
        {
            if (e.Kind == DocumentChangeKind.Saved)
            {
                return;
            }

            if (BookmarkExpressions.HasExpressions(annotations.Bookmarks))
            {
                // 最後の編集から 200 ms 後に評価し直す (仕様 6)。編集のたびに待ち直す。
                _expressionPending.Add(annotations);
                _lastExpressionEdit = Stopwatch.GetTimestamp();
                _expressionTimer.Stop();
                _expressionTimer.Start();
            }

            if (annotations.Document == Vm.Selected && annotations.Bookmarks.All.Any(b => b.TypeId is not null))
            {
                // 型付きブックマークの値の列を新しいバイトにする。
                _bookmarksVm.Rows.UpdateRows();
            }
        };
        doc.Document.Changed += changed;
        return () =>
        {
            doc.Document.Changed -= changed;
            _expressionPending.Remove(annotations);
        };
    }

    private void RunPendingExpressions()
    {
        foreach (DocumentAnnotations a in _expressionPending.ToList())
        {
            _expressionPending.Remove(a);
            ReevaluateExpressions(a);
        }

        _lastExpressionRun = Stopwatch.GetTimestamp();
    }

    /// <summary>位置の式を評価し直す。時間の目安を超えて残った分は、次の再評価に回す (「巨大ファイル・長時間処理」)。</summary>
    private void ReevaluateExpressions(DocumentAnnotations a)
    {
        if (a.Document.Document.IsDisposed)
        {
            return;
        }

        if (BookmarkExpressions.Reevaluate(a.Bookmarks, new EditorExpressionContext(a.Document.Editor), a.Document.Document.Length))
        {
            _expressionPending.Add(a);
            _expressionTimer.Stop();
            _expressionTimer.Start();
        }

        if (a.Document == Vm.Selected)
        {
            _bookmarksVm.Rows.UpdateRows();
            _bookmarkEditor?.UpdateExpressionInfo();
        }
    }

    /// <summary>編集のフライアウトの型・位置の式の変更をつなぐ (<see cref="EditBookmark"/> から呼ぶ)。</summary>
    private void HookBookmarkEditorTyped(BookmarkEditor editor, DocumentAnnotations annotations)
    {
        // 型の欄の追加の選択肢 (構造体テンプレートの型・ユーザー定義型。INSP-29 の仕様 1)
        editor.ExtraTypes = ExtraBookmarkTypes;
        editor.ExtraTypeLength = ExtraBookmarkTypeLength;
        editor.ExpressionChanged += (_, _) => ReevaluateExpressions(annotations);
        editor.TypeChanged += (_, b) =>
        {
            // 型を付けたら値の列を表示する (既定は非表示)。
            if (b.TypeId is not null)
            {
                BookmarkColumns.Show(BookmarkColumn.Value);
            }

            _bookmarksVm.Rows.UpdateRows([b]);
        };
    }

    /// <summary>ブックマーク一覧の値の列の書き換えをつなぐ (<see cref="CreateBookmarkListPanel"/> から呼ぶ)。</summary>
    private void HookBookmarkListTyped(BookmarkListPanel list)
    {
        list.EditValueOf = b => IsExtraBookmarkType(b.TypeId) ? TypedBookmarkValue(b)
            : TypedBookmarkData(b) is { } d ? InspectorDecoder.EditText(b.TypeId!, d.Data, d.States, d.Endian, _inspectorVm.Options) : string.Empty;
        list.CommitValue = CommitTypedBookmarkValue;
    }

    /// <summary>型付きブックマークの位置のバイト (最大 4 KB) とエンディアン。型がない・ドキュメントがなければ null。</summary>
    private (byte[] Data, ByteState[] States, Endianness Endian, DocumentViewModel Doc)? TypedBookmarkData(Bookmark b)
    {
        if (b.TypeId is not { } typeId || InspectorTypes.Find(typeId) is not { } type
            || (_bookmarksVm.OwnerOf(b)?.Document ?? Vm.Selected) is not { } doc || doc.Document.IsDisposed)
        {
            return null;
        }

        DocumentSnapshot snapshot = doc.Document.Current;
        int count = (int)Math.Clamp(snapshot.Length - b.Start, 0, type.Variable || InspectorTypes.IsString(typeId) ? InspectorDecoder.MaxReadLength : InspectorDecoder.ReadLength);
        byte[] data = new byte[count];
        var states = new ByteState[count];
        count = count > 0 ? snapshot.ReadForDisplay(b.Start, data, states) : 0;
        Endianness endian = b.TypeEndian ?? (doc.Editor.View.BigEndian ? Endianness.Big : Endianness.Little);
        return (data[..count], states[..count], endian, doc);
    }

    /// <summary>値の列の文字列 (インスペクタと同じ解釈と表示形式。INSP-29 の仕様 2)。</summary>
    private string TypedBookmarkValue(Bookmark b)
    {
        if (IsExtraBookmarkType(b.TypeId))
        {
            // 構造体テンプレートの型・ユーザー定義型 (INSP-29 の仕様 1。MainWindow.TemplateBookmarks.cs)
            return (_bookmarksVm.OwnerOf(b)?.Document ?? Vm.Selected) is { } owner && !owner.Document.IsDisposed
                ? ExtraBookmarkValue(b, owner, b.TypeEndian ?? (owner.Editor.View.BigEndian ? Endianness.Big : Endianness.Little))
                : string.Empty;
        }

        return TypedBookmarkData(b) is { } d ? InspectorDecoder.Decode(b.TypeId!, d.Data, d.States, d.Endian, _inspectorVm.Options).Text : string.Empty;
    }

    /// <summary>一覧の値の列からの書き換え (INSP-17 の規則: 常に上書き、長さを変えない、1 回の Undo)。書き込めなければ理由。</summary>
    private string? CommitTypedBookmarkValue(Bookmark b, string text)
    {
        if (IsExtraBookmarkType(b.TypeId))
        {
            if ((_bookmarksVm.OwnerOf(b)?.Document ?? Vm.Selected) is not { } owner || owner.Document.IsDisposed)
            {
                return Loc.Get("Command_NoDocument");
            }

            if (owner.Editor.ReadOnly || owner.Document.IsEditLocked)
            {
                return Loc.Get("Inspector_ReadOnlyTip");
            }

            string? error = CommitExtraBookmarkValue(b, owner, b.TypeEndian ?? (owner.Editor.View.BigEndian ? Endianness.Big : Endianness.Little), text);
            _bookmarksVm.Rows.UpdateRows([b]);
            return error;
        }

        if (TypedBookmarkData(b) is not { } d)
        {
            return Loc.Get("Command_NoDocument");
        }

        if (d.Doc.Editor.ReadOnly || d.Doc.Document.IsEditLocked)
        {
            return Loc.Get("Inspector_ReadOnlyTip");
        }

        InspectorEncodeResult result = InspectorEncoder.Encode(b.TypeId!, text, d.Endian, _inspectorVm.Options, d.Doc.Document.Length - b.Start,
            new EditorExpressionContext(d.Doc.Editor), d.Data);
        if (result.Bytes is not { } bytes)
        {
            return result.Error ?? Loc.Get("Inspector_ErrorInteger");
        }

        d.Doc.Document.Overwrite(b.Start, bytes, Loc.Get("Bookmarks_ValueUndo"));
        _bookmarksVm.Rows.UpdateRows([b]);
        return null;
    }
}
