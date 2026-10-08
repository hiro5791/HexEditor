using HexEditor.App.Services;
using HexEditor.Platform.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>
/// 更新の InfoBar (10 の PKG-22)。アプリ全体の通知として、配布形態をまたいで同じ文言で出し、ボタンだけを配布形態で変える。
/// ボタンは最大 3 つ (「新しい版がある」の 3 つ: 更新の方法、リリースノート、この版をスキップ) のため、通知の一覧 (UI-36。ボタンは 2 つまで)
/// とは別のこの帯に出す。「再起動して更新」は、長時間処理の実行中は無効にして理由を表示する (PKG-18 の仕様 5)。
/// 手動の確認で最新のときだけ 8 秒で閉じる。
/// </summary>
public sealed partial class UpdateBar : UserControl
{
    private readonly InfoBar _bar = new() { IsClosable = true, IsOpen = false };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _reason = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Dictionary<UpdateButton, Button> _byKind = [];
    private readonly DispatcherTimer _closeTimer = new();

    /// <summary>Platform の UpdatePresentation が返すリソースキー (ここで表示言語の文言に直す)。</summary>
    private static readonly HashSet<string> MessageKeys =
    [
        "Update_Available", "Update_Ready", "Update_Updated", "Update_ApplyFailed", "Update_UpToDate", "Update_Failed", "Update_DownloadFailed",
        "Update_Reason_NoConnection", "Update_Reason_RateLimited", "Update_Reason_Server", "Update_Reason_BadResponse", "Update_Reason_Offline",
        "Update_Reason_NotSupported",
    ];

    public UpdateBar()
    {
        AutomationProperties.SetAutomationId(_bar, "UpdateBar");
        AutomationProperties.SetAutomationId(_reason, "UpdateBar_Reason");
        _reason.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        var content = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
        content.Children.Add(_buttons);
        content.Children.Add(_reason);
        _bar.Content = content;
        _bar.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            Hide();
        };
        Content = _bar;
        Visibility = Visibility.Collapsed;
    }

    /// <summary>利用者が閉じた。</summary>
    public event EventHandler? Closed;

    /// <summary>表示中の内容 (テスト用)。</summary>
    public UpdateMessage? Current { get; private set; }

    public string MessageText => _bar.Message;

    public IReadOnlyDictionary<UpdateButton, Button> Buttons => _byKind;

    public string ReasonText => _reason.Visibility == Visibility.Visible ? _reason.Text : string.Empty;

    /// <summary>内容を表示する。<paramref name="action"/> はボタンを押したときの処理。</summary>
    public void Show(UpdateMessage message, Action<UpdateButton> action)
    {
        _closeTimer.Stop();
        Current = message;
        _bar.Severity = message.Severity switch
        {
            UpdateMessageSeverity.Success => InfoBarSeverity.Success,
            UpdateMessageSeverity.Warning => InfoBarSeverity.Warning,
            UpdateMessageSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        if (!MessageKeys.Contains(message.MessageKey))
        {
            AppLog.Warning($"Unknown update message key: {message.MessageKey}");
        }

        object[] args = [.. message.Arguments.Select(a => a is string s && MessageKeys.Contains(s) ? Loc.Get(s) : a)];
        _bar.Message = Loc.Format(message.MessageKey, args);
        _buttons.Children.Clear();
        _byKind.Clear();
        foreach (UpdateButton kind in message.Buttons)
        {
            var button = new Button { Content = Loc.Get("Update_Button_" + kind) };
            AutomationProperties.SetAutomationId(button, "Update_" + kind);
            if (kind is UpdateButton.Download or UpdateButton.OpenStore or UpdateButton.OpenDownloadPage or UpdateButton.RestartToUpdate)
            {
                button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            }

            button.Click += (_, _) => action(kind);
            _buttons.Children.Add(button);
            _byKind[kind] = button;
        }

        _buttons.Visibility = _byKind.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _reason.Visibility = Visibility.Collapsed;
        Visibility = Visibility.Visible;
        _bar.IsOpen = true;

        // 表示をスクリーンリーダーに読み上げさせる (UI-36 の仕様 6)。
        if (FrameworkElementAutomationPeer.FromElement(_bar) is { } peer)
        {
            peer.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent, _bar.Message, "UpdateBar");
        }

        if (message.AutoClose)
        {
            _closeTimer.Interval = UpdatePresentation.UpToDateCloseAfter;
            _closeTimer.Start();
        }
    }

    /// <summary>「再起動して更新」の有効・無効と、無効の理由 (PKG-18 の仕様 5)。</summary>
    public void SetRestartEnabled(bool enabled, string reason)
    {
        if (_byKind.TryGetValue(UpdateButton.RestartToUpdate, out Button? restart))
        {
            restart.IsEnabled = enabled;
            AutomationProperties.SetHelpText(restart, enabled ? string.Empty : reason);
            _reason.Text = reason;
            _reason.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// オフラインモード (09 の UI-58 の仕様 3): 通信する「ダウンロード」「Microsoft Store で更新」を無効にして理由を表示する。
    /// ブラウザでページを開くボタン (ダウンロードページ、リリースノート) は無効にしない (開く前に URL を表示する)。
    /// </summary>
    public void SetOffline(bool offline, string reason)
    {
        bool any = false;
        foreach (UpdateButton kind in (UpdateButton[])[UpdateButton.Download, UpdateButton.OpenStore])
        {
            if (_byKind.TryGetValue(kind, out Button? button))
            {
                any = true;
                button.IsEnabled = !offline;
                AutomationProperties.SetHelpText(button, offline ? reason : string.Empty);
            }
        }

        if (any)
        {
            _reason.Text = reason;
            _reason.Visibility = offline ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>閉じるボタンを押したのと同じ (テスト用の命令)。<see cref="Closed"/> が起きる。</summary>
    public void CloseByUser() => _bar.IsOpen = false;

    public void Hide()
    {
        _closeTimer.Stop();
        Current = null;
        _bar.IsOpen = false;
        Visibility = Visibility.Collapsed;
    }
}
