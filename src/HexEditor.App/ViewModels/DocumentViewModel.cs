using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Engine;
using HexEditor.Core.Recovery;
using HexEditor.Core.View;
using System.Globalization;

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

        // カーソルの値は、読み込みが終わってから表示する (読み込みの通知はスレッドプールから来る)。
        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Document.DataLoaded += (_, _) => queue?.TryEnqueue(() => OnPropertyChanged(nameof(ValueText)));
    }

    public Document Document { get; }

    /// <summary>通知 (文書の範囲の通知をタブの中に出すため。UI-36)。</summary>
    public Core.Notifications.NotificationCenter? Notifications { get; init; }

    /// <summary>このドキュメントの復旧用データ (ENG-27)。作れなかった場合は null (編集は続けられる)。</summary>
    public DocumentRecovery? Recovery { get; init; }

    private DocumentSnapshot? _lastRecorded;

    /// <summary>
    /// 前回の書き出しから内容が変わっていれば、書き出す内容を取る (UI スレッドで呼ぶ)。変更がなくなっていれば
    /// 復旧用データを消す (仕様 5)。
    /// </summary>
    public RecoveryCapture? CaptureRecoveryIfChanged()
    {
        if (Recovery is null)
        {
            return null;
        }

        if (!Document.IsModified)
        {
            if (_lastRecorded is not null)
            {
                Recovery.Clear();
                _lastRecorded = null;
            }

            return null;
        }

        if (ReferenceEquals(Document.Current, _lastRecorded))
        {
            return null;
        }

        RecoveryCapture? capture = DocumentRecovery.Capture(Document, Editor.Cursor, Editor.SelectionStart, Editor.SelectionLength);
        _lastRecorded = capture?.Snapshot ?? _lastRecorded;
        return capture;
    }

    /// <summary>書き出しに失敗したら、次の機会にもう一度書く。</summary>
    public void ForgetRecorded() => _lastRecorded = null;

    /// <summary>保存した: 復旧用データを消す (仕様 5)。</summary>
    public void OnSaved()
    {
        Recovery?.Clear();
        _lastRecorded = null;
    }

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

    // ---- ステータスバーの値 (VIEW-40) ----

    private int _hexDigits = StatusFormat.MinHexDigits;

    /// <summary>オフセットの 16 進の桁数。開いている間は増えるときだけ変える (幅が揺れないように。VIEW-19 の仕様 3)。</summary>
    public int HexDigits
    {
        get
        {
            _hexDigits = Math.Max(_hexDigits, StatusFormat.HexDigits(Document.Length));
            return _hexDigits;
        }
    }

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public string CursorText => Loc.Format("Status_Offset", StatusFormat.Offset(Editor.Cursor, HexDigits));

    /// <summary>カーソルの値: 「値: 4F (79)」。末尾 (データのない位置) と読み込み中は表示しない。</summary>
    public string ValueText
    {
        get
        {
            if (Editor.Cursor >= Document.Length)
            {
                return string.Empty;
            }

            Span<byte> value = stackalloc byte[1];
            Span<ByteState> state = stackalloc ByteState[1];
            Document.Current.ReadForDisplay(Editor.Cursor, value, state);
            if (state[0] != ByteState.Valid)
            {
                return string.Empty;
            }

            (string hex, string dec) = StatusFormat.ByteValue(value[0], Culture);
            return Loc.Format("Status_Value", hex, dec);
        }
    }

    /// <summary>選択範囲: 「選択: 0x1F00–0x1FFF (長さ 0x100 = 256)」。開始と最後のバイトの閉区間 (VIEW-40 の仕様 3)。</summary>
    public string SelectionText => Editor.HasSelection
        ? Loc.Format("Status_SelectionRange", StatusFormat.Hex(Editor.SelectionStart), StatusFormat.Hex(Editor.SelectionStart + Editor.SelectionLength - 1),
            StatusFormat.Hex(Editor.SelectionLength), StatusFormat.Number(Editor.SelectionLength, Culture))
        : string.Empty;

    public string SelectionToolTip => Editor.HasSelection
        ? Loc.Format("Status_SelectionTip", StatusFormat.Offset(Editor.SelectionStart, HexDigits),
            StatusFormat.Offset(Editor.SelectionStart + Editor.SelectionLength - 1, HexDigits),
            StatusFormat.Hex(Editor.SelectionLength), StatusFormat.Number(Editor.SelectionLength, Culture))
        : string.Empty;

    public string ModeText => Editor.ReadOnly ? Loc.Get("Status_ReadOnly")
        : !Document.CanResize ? Loc.Get("Status_OverwriteFixed")
        : Editor.InsertMode ? Loc.Get("Status_Insert") : Loc.Get("Status_Overwrite");

    public string ColumnText => Editor.ActiveColumn == ActiveColumn.Hex ? Loc.Get("Status_ColumnHex") : Loc.Get("Status_ColumnText");

    /// <summary>テキスト列の文字コード (フェーズ 0 は ASCII。VIEW-21)。</summary>
    public string EncodingText => "ASCII";

    /// <summary>ファイルサイズ: 「サイズ: 1.50 GB (1,610,612,736 バイト)」(VIEW-40 の仕様 2)。</summary>
    public string SizeText => StatusFormat.ShortSize(Document.Length, Culture) is { } size
        ? Loc.Format("Status_Size", size, StatusFormat.Number(Document.Length, Culture))
        : Loc.Format("Status_SizeBytes", StatusFormat.Number(Document.Length, Culture));

    public string ModifiedText => Document.IsModified ? "● " + Loc.Get("Status_Modified") : string.Empty;

    /// <summary>パスを持たない項目をコピーした一時ファイル (ENG-12 の仕様 2)。閉じるときに消す。</summary>
    public string? TemporaryFile { get; set; }

    /// <summary>閉じる: ドキュメントを解放してから、復旧用データをフォルダごと消す (仕様 5)。</summary>
    public void Dispose()
    {
        Document.Dispose();
        Recovery?.Dispose();
        if (TemporaryFile is not null)
        {
            try
            {
                File.Delete(TemporaryFile);
                Directory.Delete(Path.GetDirectoryName(TemporaryFile)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
