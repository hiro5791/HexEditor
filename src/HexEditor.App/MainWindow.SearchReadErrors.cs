using HexEditor.App.Services;
using HexEditor.Core.Notifications;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// 検索中の読み込みエラーの確認 (FIND-01 の「エラー」): 読めない範囲を飛ばして続けるか中止するかを、そのタブの InfoBar で尋ねる。
/// 検索のスレッドは答えを待つ。InfoBar を × で閉じたら「飛ばして続ける」として扱う。
/// </summary>
public sealed partial class MainWindow
{
    private Task<UnreadableAction> AskUnreadableAsync(UnreadableRange range, EditorState editor, CancellationToken token)
    {
        var answer = new TaskCompletionSource<UnreadableAction>(TaskCreationOptions.RunContinuationsAsynchronously);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (answer.Task.IsCompleted)
            {
                return;
            }

            ViewModels.DocumentViewModel? doc = Vm.Documents.FirstOrDefault(d => d.Editor == editor);
            string reason = Loc.Get("Find_Unreadable_Reason_" + range.Reason);
            Notification? notice = null;
            void Answer(UnreadableAction action)
            {
                if (answer.TrySetResult(action))
                {
                    AppLog.Info($"Search read error at 0x{range.Offset:X}: {action}");
                }
            }

            notice = ShowNotice(
                Loc.Format("Find_Unreadable_Ask", StatusFormat.Hex(range.Offset), range.Length.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), reason),
                InfoBarSeverity.Warning,
                doc,
                actions:
                [
                    new NotificationAction(Loc.Get("Find_Unreadable_Skip"), () => Answer(UnreadableAction.Skip)),
                    new NotificationAction(Loc.Get("Find_Unreadable_Abort"), () => Answer(UnreadableAction.Abort)),
                ]);

            // × で閉じた (答えずに消えた) 場合は飛ばして続ける。検索を取り消したら InfoBar を消す。
            void Changed(object? sender, EventArgs e)
            {
                if (!Vm.Notifications.Open.Contains(notice))
                {
                    // 操作ボタンの処理が閉じた後に届く場合があるため、少し後 (優先度の低い処理) で答える。
                    Vm.Notifications.Changed -= Changed;
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Answer(UnreadableAction.Skip));
                }
            }

            Vm.Notifications.Changed += Changed;
            answer.Task.ContinueWith(
                _ => DispatcherQueue.TryEnqueue(() =>
                {
                    Vm.Notifications.Changed -= Changed;
                    Vm.Notifications.Dismiss(notice);
                }),
                TaskScheduler.Default);
        });

        CancellationTokenRegistration registration = token.Register(() => answer.TrySetCanceled(token));
        answer.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        return answer.Task;
    }
}
