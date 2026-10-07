using HexEditor.Core.Notifications;

namespace HexEditor.Core.Tests.Notifications;

/// <summary>UI-36 通知 (表示の規則)。</summary>
public sealed class NotificationCenterTests
{
    private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private NotificationCenter Create() => new(() => _now);

    [Fact]
    public void SameWarningIsMergedWithCount()
    {
        NotificationCenter center = Create();
        for (int i = 0; i < 5; i++)
        {
            center.Show(NotificationScope.Window, NotificationSeverity.Warning, "Folders can't be opened.");
        }

        Notification only = Assert.Single(center.Visible(NotificationScope.Window));
        Assert.Equal(5, only.Count);
        Assert.Equal("Folders can't be opened. ×5", only.DisplayMessage);
        Assert.Equal(5, center.History.Count);
    }

    [Fact]
    public void ErrorsAndWarningsDoNotAutoClose()
    {
        NotificationCenter center = Create();
        center.Show(NotificationScope.Window, NotificationSeverity.Error, "Can't open.");
        center.Show(NotificationScope.Window, NotificationSeverity.Warning, "Skipped.");
        center.Show(NotificationScope.Window, NotificationSeverity.Informational, "Info.");
        center.Show(NotificationScope.Window, NotificationSeverity.Success, "Done.");

        _now += TimeSpan.FromSeconds(7.9);
        center.Tick();
        Assert.Equal(4, center.Open.Count);

        _now += TimeSpan.FromSeconds(0.2);
        center.Tick();
        Assert.Equal(["Skipped.", "Can't open."], center.Open.Select(n => n.Message));
    }

    [Fact]
    public void HeldNotificationStaysOpen()
    {
        NotificationCenter center = Create();
        Notification info = center.Show(NotificationScope.Window, NotificationSeverity.Informational, "Info.");
        info.IsHeld = true;
        _now += TimeSpan.FromSeconds(30);
        center.Tick();
        Assert.Single(center.Open);

        info.IsHeld = false;
        center.Tick();
        Assert.Empty(center.Open);
    }

    [Fact]
    public void ShowsAtMostThreePerScopeAndCountsTheRest()
    {
        NotificationCenter center = Create();
        var doc = new object();
        for (int i = 0; i < 5; i++)
        {
            center.Show(NotificationScope.Window, NotificationSeverity.Error, $"Error {i}");
        }

        center.Show(NotificationScope.Document, NotificationSeverity.Warning, "Doc", owner: doc);

        Assert.Equal(["Error 4", "Error 3", "Error 2"], center.Visible(NotificationScope.Window).Select(n => n.Message));
        Assert.Equal(2, center.Overflow(NotificationScope.Window));
        Assert.Single(center.Visible(NotificationScope.Document, doc));
        Assert.Empty(center.Visible(NotificationScope.Document, new object()));

        center.DismissOwnedBy(doc);
        Assert.Empty(center.Visible(NotificationScope.Document, doc));
    }

    [Fact]
    public void HistoryKeepsLatestHundred()
    {
        NotificationCenter center = Create();
        for (int i = 0; i < 120; i++)
        {
            center.Show(NotificationScope.App, NotificationSeverity.Informational, $"N{i}");
        }

        Assert.Equal(NotificationCenter.HistoryCapacity, center.History.Count);
        Assert.Equal("N119", center.History[0].Message);
    }

    [Fact]
    public void AtMostTwoActions()
    {
        NotificationCenter center = Create();
        NotificationAction a = new("A", () => { });
        Assert.Throws<ArgumentException>(() =>
            center.Show(NotificationScope.Window, NotificationSeverity.Error, "x", actions: [a, a, a]));
    }
}
