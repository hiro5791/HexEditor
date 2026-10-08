using HexEditor.Core.Engine;
using HexEditor.Core.Files;
using HexEditor.Core.Sources;
using HexEditor.Core.View;

namespace HexEditor.App.ViewModels;

/// <summary>
/// タブ 1 つ分のファイルの状態: 外部変更 (ENG-19)、見つからないファイルのタブ (UI-31 の「エラー」)、前回の位置 (ENG-16、UI-12、UI-31)。
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>外部変更の監視 (ENG-19)。ファイルでなければ null。</summary>
    public WatchedFile? Watch { get; set; }

    /// <summary>知らせて、利用者がまだ選んでいない外部変更 (タブのアイコン。ENG-19 の「画面」)。</summary>
    public ExternalChangeKind ExternalChange
    {
        get => _externalChange;
        set
        {
            if (SetProperty(ref _externalChange, value))
            {
                OnPropertyChanged(nameof(Header));
                OnPropertyChanged(nameof(HasExternalChange));
            }
        }
    }

    private ExternalChangeKind _externalChange;

    /// <summary>タブに外部変更の印を付ける (色ではなく形で区別する)。</summary>
    public bool HasExternalChange => _externalChange != ExternalChangeKind.None;

    /// <summary>
    /// 外部の変更を「無視」した (ENG-19 の仕様 5)、または変更していない部分が外部の内容に変わっている (仕様 6)。保存するときは
    /// 「外部で変更されたファイルを上書きします」と確かめ、安全な保存で全体を書く (その場保存ではファイルの内容が混ざるため)。
    /// </summary>
    public bool OverwritesExternalChange { get; set; }

    /// <summary>元のファイルが削除・移動された (仕様 8)。保存するときは「元の場所にファイルを作り直します」と確かめる。</summary>
    public bool SourceDeleted { get; set; }

    /// <summary>セッションのファイルが見つからなかったタブ (UI-31 の「エラー」)。そのパス。見つかったタブでは null。</summary>
    public string? MissingPath { get; init; }

    public bool IsMissing => MissingPath is not null;

    /// <summary>見つからないタブの文言「ファイルが見つかりません: &lt;パス&gt;」。</summary>
    public string MissingText => MissingPath is null ? string.Empty : Services.Loc.Format("Session_Missing", MissingPath);

    /// <summary>セッションの記録 (見つからないタブは、見つかるまで元の記録をそのまま残す)。</summary>
    public SessionTab? MissingRecord { get; init; }

    /// <summary>開いたときのファイルの値 (セッション・前回の位置の記録用)。</summary>
    public FileStamp? OpenedStamp => (Document.Source as FileByteSource)?.Stamp;

    /// <summary>
    /// 位置を戻す (前回の位置・閉じたタブ・セッション)。長さを超える位置は末尾に置き (ENG-16 の仕様 5)、ジャンプ履歴には記録しない。
    /// </summary>
    public void RestorePosition(long cursor, long selectionStart, long selectionLength, long topRow)
    {
        long length = Document.Length;
        if (selectionLength > 0 && selectionStart >= 0 && selectionStart + selectionLength <= length)
        {
            Editor.Select(selectionStart, selectionLength);
        }
        else
        {
            Editor.Click(Math.Clamp(cursor, 0, length), ActiveColumn.Hex, lowNibble: false, extend: false);
        }

        Editor.ScrollToRow(topRow);
    }

    /// <summary>セッション・閉じたタブの記録。</summary>
    public SessionTab ToSessionTab()
    {
        if (MissingRecord is { } missing)
        {
            return missing;
        }

        FileStamp? stamp = OpenedStamp;
        return new SessionTab
        {
            Kind = FilePath is null ? SessionTabKind.Untitled : SessionTabKind.File,
            Path = FilePath,
            DisplayName = DisplayName,
            ReadOnly = Document.ReadOnlyReason is ReadOnlyReason.User or ReadOnlyReason.OpenedReadOnly,
            Cursor = Editor.Cursor,
            SelectionStart = Editor.SelectionStart,
            SelectionLength = Editor.SelectionLength,
            TopRow = Editor.TopRow,
            Length = stamp?.Length ?? Document.Length,
            LastWriteTimeUtc = stamp?.LastWriteTimeUtc ?? default,
        };
    }
}
