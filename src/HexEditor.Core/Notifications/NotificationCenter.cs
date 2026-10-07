using System.Collections.ObjectModel;
using System.ComponentModel;

namespace HexEditor.Core.Notifications;

/// <summary>通知の範囲 (UI-36 の仕様 1)。</summary>
public enum NotificationScope
{
    /// <summary>タブの中 (そのタブがアクティブなときだけ見える)。</summary>
    Document,

    /// <summary>ウィンドウの作業領域の上部。</summary>
    Window,

    /// <summary>アプリ全体 (すべてのウィンドウ)。</summary>
    App,
}

/// <summary>通知の重要度 (UI-36 の仕様 2。WinUI の InfoBarSeverity と同じ並び)。</summary>
public enum NotificationSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>通知に付けるボタン (仕様 5)。</summary>
public sealed record NotificationAction(string Label, Action Execute);

/// <summary>表示中の通知 1 件。同じ内容が繰り返されたら <see cref="Count"/> を増やす。</summary>
public sealed class Notification : INotifyPropertyChanged
{
    private int _count = 1;

    internal Notification(NotificationScope scope, object? owner, NotificationSeverity severity, string message,
        NotificationAction? undo, IReadOnlyList<NotificationAction> actions, DateTime shownAt)
    {
        Scope = scope;
        Owner = owner;
        Severity = severity;
        Message = message;
        Undo = undo;
        Actions = actions;
        ShownAt = shownAt;
    }

    public NotificationScope Scope { get; }

    /// <summary>文書の範囲の通知が属する文書 (それ以外は null)。</summary>
    public object? Owner { get; }

    public NotificationSeverity Severity { get; }

    public string Message { get; }

    /// <summary>「元に戻す」ボタン (取り消せる操作の通知だけ)。</summary>
    public NotificationAction? Undo { get; }

    /// <summary>他の操作ボタン (最大 2 つ)。</summary>
    public IReadOnlyList<NotificationAction> Actions { get; }

    /// <summary>同じ内容が出た回数 (仕様 3)。</summary>
    public int Count
    {
        get => _count;
        internal set
        {
            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayMessage)));
        }
    }

    /// <summary>表示する文言。2 回以上なら末尾に「×N」を付ける。</summary>
    public string DisplayMessage => Count > 1 ? $"{Message} ×{Count}" : Message;

    /// <summary>最後に出た時刻 (自動で閉じるまでの時間はここから数える)。</summary>
    public DateTime ShownAt { get; internal set; }

    /// <summary>マウスが乗っている、またはキーボードフォーカスがある (自動で閉じない。仕様 4)。</summary>
    public bool IsHeld { get; set; }

    /// <summary>自動で閉じる重要度か。</summary>
    public bool AutoCloses => Severity is NotificationSeverity.Informational or NotificationSeverity.Success;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>通知の履歴の 1 件 (仕様 7)。</summary>
public sealed record NotificationHistoryItem(DateTime Time, NotificationScope Scope, NotificationSeverity Severity, string Message);

/// <summary>
/// 通知の管理 (UI-36)。範囲ごとに最大 3 件を表示し、それ以上は件数だけを示す。同じ内容は 1 件にまとめて回数を数える。
/// <c>Informational</c> と <c>Success</c> は 8 秒で自動で閉じる。直近 100 件の履歴を持つ。UI のスレッドから呼ぶ。
/// </summary>
public sealed class NotificationCenter
{
    public const int MaxVisiblePerScope = 3;
    public const int HistoryCapacity = 100;
    public const int MaxActions = 2;

    public static readonly TimeSpan AutoCloseAfter = TimeSpan.FromSeconds(8);

    private readonly List<Notification> _open = [];
    private readonly Func<DateTime> _clock;

    public NotificationCenter(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>新しい順の履歴。</summary>
    public ObservableCollection<NotificationHistoryItem> History { get; } = [];

    /// <summary>表示中の通知が変わった。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 通知を出す。同じ範囲・文書・重要度・文言の通知が開いていれば、それの回数を増やして時刻を更新する。
    /// </summary>
    public Notification Show(NotificationScope scope, NotificationSeverity severity, string message, object? owner = null,
        NotificationAction? undo = null, IReadOnlyList<NotificationAction>? actions = null)
    {
        if (actions is { Count: > MaxActions })
        {
            throw new ArgumentException($"操作ボタンは最大 {MaxActions} つです。", nameof(actions));
        }

        DateTime now = _clock();
        AddHistory(new NotificationHistoryItem(now, scope, severity, message));
        Notification? same = _open.FirstOrDefault(n => n.Scope == scope && Equals(n.Owner, owner)
            && n.Severity == severity && n.Message == message && n.Undo is null && undo is null);
        if (same is not null)
        {
            same.Count++;
            same.ShownAt = now;

            // 最新のものとして先頭に出す。
            _open.Remove(same);
            _open.Insert(0, same);
            Changed?.Invoke(this, EventArgs.Empty);
            return same;
        }

        var notification = new Notification(scope, owner, severity, message, undo, actions ?? [], now);
        _open.Insert(0, notification);
        Changed?.Invoke(this, EventArgs.Empty);
        return notification;
    }

    /// <summary>範囲 (と文書) に表示する通知 (新しい順、最大 3 件)。アプリの範囲はウィンドウの範囲と一緒に返す。</summary>
    public IReadOnlyList<Notification> Visible(NotificationScope scope, object? owner = null) =>
        InScope(scope, owner).Take(MaxVisiblePerScope).ToList();

    /// <summary>表示しきれない件数 (「他に N 件」)。</summary>
    public int Overflow(NotificationScope scope, object? owner = null) =>
        Math.Max(0, InScope(scope, owner).Count() - MaxVisiblePerScope);

    /// <summary>開いているすべての通知。</summary>
    public IReadOnlyList<Notification> Open => _open;

    /// <summary>閉じる (×ボタン、操作ボタンの実行後)。</summary>
    public void Dismiss(Notification notification)
    {
        if (_open.Remove(notification))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>文書を閉じたら、その文書の通知を消す。</summary>
    public void DismissOwnedBy(object owner)
    {
        if (_open.RemoveAll(n => Equals(n.Owner, owner)) > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 自動で閉じる通知のうち、8 秒たったものを閉じる (仕様 4)。マウスが乗っている・フォーカスがあるものは閉じない。
    /// 定期的に (1 秒ごとなど) 呼ぶ。
    /// </summary>
    public void Tick()
    {
        DateTime now = _clock();
        int removed = _open.RemoveAll(n => n.AutoCloses && !n.IsHeld && now - n.ShownAt >= AutoCloseAfter);
        if (removed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private IEnumerable<Notification> InScope(NotificationScope scope, object? owner) => scope switch
    {
        NotificationScope.Document => _open.Where(n => n.Scope == NotificationScope.Document && Equals(n.Owner, owner)),
        _ => _open.Where(n => n.Scope is NotificationScope.Window or NotificationScope.App),
    };

    private void AddHistory(NotificationHistoryItem item)
    {
        History.Insert(0, item);
        while (History.Count > HistoryCapacity)
        {
            History.RemoveAt(History.Count - 1);
        }
    }
}
