using System.ComponentModel;
using System.Runtime.CompilerServices;
using HexEditor.App.Controls;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Operations;
using Microsoft.UI.Xaml.Automation.Peers;

namespace HexEditor.App;

/// <summary>
/// ウィンドウ全体のスクリーンリーダー対応 (09 の UI-50、UI-51): Hex ビューの名前 (文書名) の追従と、Hex ビューの外で起きたこと
/// (長時間処理の完了。UI-51 の仕様 5) の読み上げ。
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Hex ビューごとの、名前を追っている文書と購読の解除。</summary>
    private readonly ConditionalWeakTable<HexView, DocumentNameTracking> _documentNames = new();

    /// <summary>最後に送ったウィンドウの読み上げ文 (時刻は Environment.TickCount64。UI テスト・診断用。最大 100 件)。</summary>
    private readonly List<(long Ms, string Id, string Text)> _windowAnnouncements = [];

    private sealed class DocumentNameTracking
    {
        public DocumentViewModel? Document { get; set; }

        public PropertyChangedEventHandler? Handler { get; set; }
    }

    /// <summary>
    /// Hex ビューの名前を文書名にし、文書名が変わったら (名前を付けて保存など) 追従する (UI-50 の仕様 2)。Hex ビューが読み込まれる
    /// たびに呼ぶ (タブの切り替えで別の文書に使い回されることがある)。
    /// </summary>
    private void TrackDocumentName(HexView view)
    {
        var document = view.DataContext as DocumentViewModel;
        DocumentNameTracking tracking = _documentNames.GetOrCreateValue(view);
        if (!ReferenceEquals(tracking.Document, document))
        {
            if (tracking.Document is { } old && tracking.Handler is { } handler)
            {
                old.PropertyChanged -= handler;
            }

            tracking.Document = document;
            tracking.Handler = null;
            if (document is not null)
            {
                var weakView = new WeakReference<HexView>(view);
                tracking.Handler = (sender, e) =>
                {
                    if (e.PropertyName == nameof(DocumentViewModel.DisplayName) && weakView.TryGetTarget(out HexView? v) && ReferenceEquals(v.DataContext, sender))
                    {
                        v.DocumentName = ((DocumentViewModel)sender!).DisplayName;
                    }
                };
                document.PropertyChanged += tracking.Handler;
            }
        }

        view.DocumentName = document?.DisplayName;
    }

    /// <summary>
    /// 終わった長時間処理を読み上げる (UI-51 の仕様 5)。進捗を表示した処理 (0.5 秒を超えたもの。ENG-09) の完了とキャンセル。
    /// 失敗は通知 (InfoBar。表示されると読み上げられる) で知らせるので、ここでは読まない。
    /// </summary>
    private void AnnounceFinishedOperation(LongRunningOperation op)
    {
        if (op.Elapsed <= LongRunningOperation.ShowDelay)
        {
            return;
        }

        string? key = op.State switch
        {
            OperationState.Completed => "A11y_OperationCompleted",
            OperationState.Cancelled => "A11y_OperationCancelled",
            _ => null,
        };
        if (key is not null)
        {
            AnnounceWindow(Loc.Format(key, op.Name), "Operation");
        }
    }

    /// <summary>ウィンドウからスクリーンリーダーへ読み上げ文を送る (Hex ビューの外の出来事)。</summary>
    internal void AnnounceWindow(string text, string activityId)
    {
        _windowAnnouncements.Add((Environment.TickCount64, activityId, text));
        if (_windowAnnouncements.Count > 100)
        {
            _windowAnnouncements.RemoveAt(0);
        }

        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(Root) ?? FrameworkElementAutomationPeer.CreatePeerForElement(Root);
        peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.All, text, "HexEditor." + activityId);
    }
}
