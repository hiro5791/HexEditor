using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App;

/// <summary>
/// ステータスバーの右端に一時的な文 (検索の結果「12 件見つかりました」「先頭に戻って検索しました」など。FIND-02 の仕様 5、FIND-09 の
/// 仕様 4) を出す。決めた時間がたったら消す。
/// </summary>
public sealed partial class MainWindow
{
    private TextBlock? _statusMessage;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _statusMessageTimer;

    /// <summary>今ステータスバーに出している一時的な文 (なければ空。テスト用)。</summary>
    internal string StatusMessageText => _statusMessage?.Text ?? string.Empty;

    /// <summary>一時的な文を出しておく既定の時間 (UI-06 の仕様 7)。</summary>
    public static readonly TimeSpan StatusMessageDefaultDuration = TimeSpan.FromSeconds(5);

    /// <summary>
    /// ステータスバーに一時的な文を出す (UI-06 の仕様 7)。ウィンドウごとに 1 つで、新しい文が前の文を置き換える。
    /// <paramref name="duration"/> (既定 5 秒) 後に消す。スクリーンリーダーには丁寧な通知で読み上げる。
    /// </summary>
    public void ShowStatusMessage(string message, TimeSpan? duration = null)
    {
        if (_statusMessage is null)
        {
            _statusMessage = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false,
            };
            AutomationProperties.SetAutomationId(_statusMessage, "Status_Message");
            AutomationProperties.SetLiveSetting(_statusMessage, AutomationLiveSetting.Polite);

            // 項目の並び (列 0) の右に、文の列を加える。
            if (StatusBar.ColumnDefinitions.Count == 0)
            {
                StatusBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                StatusBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            }

            Grid.SetColumn(_statusMessage, StatusBar.ColumnDefinitions.Count - 1);
            StatusBar.Children.Add(_statusMessage);
            _statusMessageTimer = DispatcherQueue.CreateTimer();
            _statusMessageTimer.IsRepeating = false;
            _statusMessageTimer.Tick += (_, _) => _statusMessage.Text = string.Empty;
        }

        _statusMessage.Text = message;
        _statusMessageTimer!.Stop();
        _statusMessageTimer.Interval = duration ?? StatusMessageDefaultDuration;
        _statusMessageTimer.Start();
        AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(_statusMessage) ?? FrameworkElementAutomationPeer.CreatePeerForElement(_statusMessage);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
