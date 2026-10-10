using HexEditor.Core.Formats;
using HexEditor.Core.Sources;

namespace HexEditor.App.ViewModels;

/// <summary>
/// 特別な開き方をしたタブの状態: 範囲を指定して開いた (ENG-13)、エンコード形式をデコードして開いた (ENG-38・TOOL-11)、選択範囲の連動ビュー (ENG-39)。
/// </summary>
public sealed partial class DocumentViewModel
{
    /// <summary>範囲を指定して開いたファイル (ENG-13)。範囲でなければ null。</summary>
    public FileByteSource? RangeSource => Document.Source is FileByteSource { IsRange: true } range && Document.LinkedParent is null ? range : null;

    /// <summary>範囲を指定して開いたタブか。</summary>
    public bool IsRangeDocument => RangeSource is not null;

    /// <summary>
    /// 見出しに付ける範囲 (<c> [0x10000000–0x13FFFFFF]</c>。ENG-13 の仕様 5、ENG-39 の仕様 1)。範囲のタブでなければ空。
    /// </summary>
    public string RangeLabel
    {
        get => _rangeLabel;
        set
        {
            if (SetProperty(ref _rangeLabel, value))
            {
                OnPropertyChanged(nameof(TabTitle));
                OnPropertyChanged(nameof(Header));
            }
        }
    }

    private string _rangeLabel = string.Empty;

    /// <summary>範囲の表記 (閉区間)。</summary>
    public static string FormatRange(long start, long length) => $" [0x{start:X}–0x{start + Math.Max(0, length - 1):X}]";

    // ---- エンコード形式 (ENG-38・TOOL-11) ----

    /// <summary>デコードして開いた形式の、元の形式で保存するための設定。デコードしていなければ null。</summary>
    public EncodedFileSettings? Encoded { get; set; }

    /// <summary>デコードしたときの元のファイルの値 (復旧用データに記録し、復旧のときに変わったかを調べる)。</summary>
    public FileStamp? EncodedStamp { get; set; }

    /// <summary>ステータスバーに出す形式 (「Intel HEX」など。ENG-38 の画面)。</summary>
    public string FileFormatText => Encoded?.DisplayName ?? string.Empty;

    /// <summary>表示上のベースアドレス (デコードした内容の最小のアドレス)。保存で元のアドレスに戻すときに使う。</summary>
    public long EncodedBaseAddress { get; set; }

    /// <summary>開いたときの誤りの一覧 (ENG-38 の仕様 6)。</summary>
    public IReadOnlyList<ImportIssue> FormatIssues { get; set; } = [];

    /// <summary>データとして読めない行が失われる確認を済ませた (TOOL-11 の仕様 4。最初の保存のときだけ確認する)。</summary>
    public bool ForeignLinesConfirmed { get; set; }

    // ---- 連動ビュー (ENG-39) ----

    /// <summary>連動ビューの親のタブ (連動ビューでなければ null)。</summary>
    public DocumentViewModel? LinkParent { get; set; }

    public bool IsLinkedView => LinkParent is not null;
}
