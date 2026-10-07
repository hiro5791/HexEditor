using HexEditor.App.Services;
using HexEditor.Core.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace HexEditor.App.Controls;

/// <summary>
/// 1 つの範囲の通知を InfoBar で並べる (UI-36)。最大 3 件を新しい順に出し、それ以上は「他に N 件」のリンクにする
/// (押すと履歴を開く)。InfoBar は開いたときに内容をスクリーンリーダーに読み上げさせる。
/// </summary>
public sealed partial class NotificationHost : UserControl
{
    private readonly StackPanel _panel = new();
    private readonly Dictionary<Notification, InfoBar> _bars = [];
    private readonly HyperlinkButton _overflow = new() { Margin = new Thickness(12, 0, 12, 4) };
    private NotificationCenter? _center;

    public NotificationHost()
    {
        Content = _panel;
        AutomationProperties.SetAutomationId(_overflow, "Notifications_More");
        _overflow.Click += (_, _) => OverflowClicked?.Invoke(this, EventArgs.Empty);
        // 読み込まれている間だけ通知の変更を受け取る (タブの中身は切り替えで作り直される)。
        Unloaded += (_, _) =>
        {
            if (_center is not null)
            {
                _center.Changed -= Center_Changed;
            }
        };
        Loaded += (_, _) =>
        {
            if (_center is not null)
            {
                _center.Changed -= Center_Changed;
                _center.Changed += Center_Changed;
            }

            Rebuild();
        };
    }

    /// <summary>「他に N 件」を押した (履歴を開く)。</summary>
    public event EventHandler? OverflowClicked;

    public static readonly DependencyProperty ScopeProperty = DependencyProperty.Register(
        nameof(Scope), typeof(NotificationScope), typeof(NotificationHost),
        new PropertyMetadata(NotificationScope.Window, (d, _) => ((NotificationHost)d).Rebuild()));

    public static readonly DependencyProperty OwnerProperty = DependencyProperty.Register(
        nameof(Owner), typeof(object), typeof(NotificationHost), new PropertyMetadata(null, (d, _) => ((NotificationHost)d).Rebuild()));

    public static readonly DependencyProperty CenterProperty = DependencyProperty.Register(
        nameof(Center), typeof(NotificationCenter), typeof(NotificationHost),
        new PropertyMetadata(null, (d, e) => ((NotificationHost)d).OnCenterChanged((NotificationCenter?)e.OldValue, (NotificationCenter?)e.NewValue)));

    public NotificationScope Scope
    {
        get => (NotificationScope)GetValue(ScopeProperty);
        set => SetValue(ScopeProperty, value);
    }

    /// <summary>文書の範囲のときの文書。</summary>
    public object? Owner
    {
        get => GetValue(OwnerProperty);
        set => SetValue(OwnerProperty, value);
    }

    public NotificationCenter? Center
    {
        get => (NotificationCenter?)GetValue(CenterProperty);
        set => SetValue(CenterProperty, value);
    }

    private void OnCenterChanged(NotificationCenter? oldCenter, NotificationCenter? newCenter)
    {
        if (oldCenter is not null)
        {
            oldCenter.Changed -= Center_Changed;
        }

        _center = newCenter;
        if (newCenter is not null)
        {
            newCenter.Changed += Center_Changed;
        }

        Rebuild();
    }

    private void Center_Changed(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        IReadOnlyList<Notification> visible = _center?.Visible(Scope, Owner) ?? [];
        foreach (Notification gone in _bars.Keys.Except(visible).ToList())
        {
            InfoBar bar = _bars[gone];
            gone.PropertyChanged -= Notification_PropertyChanged;
            _bars.Remove(gone);
            bar.IsOpen = false;
        }

        _panel.Children.Clear();
        foreach (Notification notification in visible)
        {
            if (!_bars.TryGetValue(notification, out InfoBar? bar))
            {
                bar = CreateBar(notification);
                _bars[notification] = bar;
            }

            _panel.Children.Add(bar);
        }

        int overflow = _center?.Overflow(Scope, Owner) ?? 0;
        if (overflow > 0)
        {
            _overflow.Content = Loc.Format("Notifications_More", overflow);
            _panel.Children.Add(_overflow);
        }
    }

    private InfoBar CreateBar(Notification notification)
    {
        var bar = new InfoBar
        {
            Message = notification.DisplayMessage,
            Severity = (InfoBarSeverity)(int)notification.Severity,
            IsClosable = true,
            Tag = notification,
        };
        AutomationProperties.SetAutomationId(bar, "Notification");
        bar.CloseButtonClick += (_, _) => _center?.Dismiss(notification);

        var buttons = new List<(string Label, Action Execute)>();
        if (notification.Undo is { } undo)
        {
            buttons.Add((undo.Label, undo.Execute));
        }

        buttons.AddRange(notification.Actions.Select(a => (a.Label, a.Execute)));
        if (buttons.Count > 0)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
            foreach ((string label, Action execute) in buttons)
            {
                var button = new Button { Content = label };
                AutomationProperties.SetAutomationId(button, "Notification_Action");
                button.Click += (_, _) =>
                {
                    execute();
                    _center?.Dismiss(notification);
                };
                panel.Children.Add(button);
            }

            bar.Content = panel;
        }

        // マウスが乗っている間・フォーカスがある間は自動で閉じない (仕様 4)。
        bar.PointerEntered += (_, _) => notification.IsHeld = true;
        bar.PointerExited += (_, _) => notification.IsHeld = bar.FocusState != FocusState.Unfocused;
        bar.GotFocus += (_, _) => notification.IsHeld = true;
        bar.LostFocus += (_, _) => notification.IsHeld = false;
        notification.PropertyChanged += Notification_PropertyChanged;
        bar.IsOpen = true;
        return bar;
    }

    private void Notification_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is Notification n && _bars.TryGetValue(n, out InfoBar? bar))
        {
            bar.Message = n.DisplayMessage;
        }
    }
}
