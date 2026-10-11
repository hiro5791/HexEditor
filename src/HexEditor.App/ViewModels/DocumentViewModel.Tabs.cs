using HexEditor.App.Services;
using HexEditor.Core.Files;
using HexEditor.Core.Tabs;
using HexEditor.Core.View;

namespace HexEditor.App.ViewModels;

/// <summary>タブの見出しの状態 (UI-09): ピン留め (UI-10)、同名のファイルの区別、ツールチップ、遅延して開くタブ (UI-31 の仕様 6)。</summary>
public sealed partial class DocumentViewModel
{
    /// <summary>今このタブを持っているウィンドウの状態 (別のウィンドウに移すと替わる。UI-11)。</summary>
    public MainViewModel? Owner { get; internal set; }

    /// <summary>ピン留め (UI-10)。</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetProperty(ref _isPinned, value))
            {
                OnPropertyChanged(nameof(TabTitle));
                OnPropertyChanged(nameof(Header));
                OnPropertyChanged(nameof(IconGlyph));
                OnPropertyChanged(nameof(ToolTip));
            }
        }
    }

    private bool _isPinned;

    /// <summary>同じファイル名のタブを区別する親フォルダ名 (例: <c> — a\x</c>。UI-09 の仕様 3)。なければ空。</summary>
    public string NameSuffix
    {
        get => _nameSuffix;
        set
        {
            if (SetProperty(ref _nameSuffix, value))
            {
                OnPropertyChanged(nameof(TabTitle));
                OnPropertyChanged(nameof(Header));
            }
        }
    }

    private string _nameSuffix = string.Empty;

    /// <summary>見出しの名前: ピン留めしたタブは先頭 8 文字 (UI-10 の仕様 2)、それ以外は表示名と区別の親フォルダ名。</summary>
    public string TabTitle => (IsPinned ? TabStripRules.PinnedTitle(DisplayName) : DisplayName + RangeLabel + NameSuffix) + ViewSuffix;

    /// <summary>タブのツールチップ (UI-09 の仕様 4): 完全なパス、サイズ、種類、読み取り専用かどうか。</summary>
    public string ToolTip
    {
        get
        {
            var lines = new List<string> { FilePath ?? MissingPath ?? PendingRecord?.Path ?? DisplayName };
            if (LinkParent is { } parent)
            {
                // 連動ビュー: 親のドキュメント名と範囲 (ENG-39 の画面)。
                lines.Add(Services.Loc.Format("Linked_ToolTip", parent.DisplayName + RangeLabel));
            }

            if (PendingRecord is null && !IsMissing)
            {
                lines.Add(Loc.Format("Tab_ToolTipSize", StatusFormat.ShortSize(Document.Length, System.Globalization.CultureInfo.CurrentCulture)
                    ?? StatusFormat.Number(Document.Length, System.Globalization.CultureInfo.CurrentCulture)));
            }

            lines.Add(Loc.Get(IsUntitled && !IsMissing && PendingRecord is null ? "Tab_ToolTipKind_Untitled" : "Tab_ToolTipKind_File"));
            if (Document.IsReadOnly && !IsMissing && PendingRecord is null)
            {
                lines.Add(Loc.Get("Tab_ToolTipReadOnly"));
            }

            if (IsPinned)
            {
                lines.Add(Loc.Get("Tab_Pinned"));
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>
    /// セッションから復元したまだ開いていないタブ (UI-31 の仕様 6)。初めて表示したときに開く。開いたタブでは null。
    /// </summary>
    public SessionTab? PendingRecord { get; init; }

    public bool IsPending => PendingRecord is not null;

    /// <summary>長時間処理を実行中 (タブの見出しに進捗リングを出す。UI-09 の仕様 2)。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    private bool _isBusy;

    /// <summary>種類のアイコン (Segoe Fluent Icons。UI-09 の仕様 2)。ピン留めはピンのアイコン (UI-10)。</summary>
    public string IconGlyph => IsPinned ? "" : IsLinkedView ? "" : IsUntitled && !IsMissing && PendingRecord is null ? "" : "";
}
