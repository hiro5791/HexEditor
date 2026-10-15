using HexEditor.Sdk;

namespace HexEditor.Core.Scripting.Api;

/// <summary>
/// <c>app.on(event, handler)</c> (AUTO-06 の仕様 4)。イベントは UI スレッドで起き、ハンドラはスクリプトのスレッドで呼ぶ。
/// 購読したスクリプトは <c>app.stop()</c> か停止まで常駐する。<c>selectionChanged</c> は 100 ms ごとにまとめて 1 回通知する。
/// </summary>
internal sealed class ScriptEvents(ScriptContext ctx)
{
    /// <summary>選択範囲の変化をまとめる間隔。</summary>
    public static readonly TimeSpan SelectionInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>保存の直前のハンドラを待つ上限 (保存の前のフックと同じ 5 秒。AUTO-25)。</summary>
    public static readonly TimeSpan BeforeSaveTimeout = TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> Names =
        ["documentOpened", "documentClosed", "activeDocumentChanged", "beforeSave", "afterSave", "selectionChanged"];

    private readonly Lock _lock = new();
    private readonly List<Subscription> _subscriptions = [];

    private ScriptContext Ctx => ctx;

    public IEventSubscription Subscribe(string eventName, Action<ScriptEventArgs> handler)
    {
        if (!Names.Contains(eventName))
        {
            throw new Sdk.ArgumentError($"イベントの名前が不明です: {eventName} ({string.Join(", ", Names)})");
        }

        var sub = new Subscription(this, eventName, handler);
        lock (_lock)
        {
            _subscriptions.Add(sub);
        }

        ctx.AddSubscription();
        ctx.Dispatcher.Invoke(sub.Attach);
        return sub;
    }

    private void Remove(Subscription sub)
    {
        bool removed;
        lock (_lock)
        {
            removed = _subscriptions.Remove(sub);
        }

        if (removed)
        {
            ctx.Dispatcher.Invoke(sub.Detach);
            ctx.RemoveSubscription();
        }
    }

    /// <summary>実行の終わりにすべての購読を外す (UI スレッドから呼んでよい)。</summary>
    public void UnsubscribeAll()
    {
        List<Subscription> all;
        lock (_lock)
        {
            all = [.. _subscriptions];
            _subscriptions.Clear();
        }

        foreach (Subscription s in all)
        {
            ctx.Dispatcher.Invoke(s.Detach);
        }
    }

    private Task Deliver(Action<ScriptEventArgs> handler, ScriptEventArgs args) =>
        ctx.StopRequested ? Task.CompletedTask : ctx.Thread.PostAsync(() => handler(args));

    private sealed class Subscription(ScriptEvents owner, string name, Action<ScriptEventArgs> handler) : IEventSubscription
    {
        private IScriptEventSource Source => owner.Ctx.Environment.Events;

        private long _lastSelection;
        private int _selectionPending;

        public void Off() => owner.Remove(this);

        public void Attach()
        {
            switch (name)
            {
                case "documentOpened":
                    Source.DocumentOpened += OnOpened;
                    break;
                case "documentClosed":
                    Source.DocumentClosed += OnClosed;
                    break;
                case "activeDocumentChanged":
                    Source.ActiveDocumentChanged += OnActive;
                    break;
                case "beforeSave":
                    Source.BeforeSave += OnBeforeSave;
                    break;
                case "afterSave":
                    Source.AfterSave += OnAfterSave;
                    break;
                case "selectionChanged":
                    Source.SelectionChanged += OnSelection;
                    break;
            }
        }

        public void Detach()
        {
            Source.DocumentOpened -= OnOpened;
            Source.DocumentClosed -= OnClosed;
            Source.ActiveDocumentChanged -= OnActive;
            Source.BeforeSave -= OnBeforeSave;
            Source.AfterSave -= OnAfterSave;
            Source.SelectionChanged -= OnSelection;
        }

        private ScriptEventArgs Args(IScriptDocumentHost? doc, Action<string>? cancel = null) =>
            new EventArgsImpl(name, owner.Ctx.WrapOrNull(doc), cancel);

        private void OnOpened(object? sender, IScriptDocumentHost doc) => _ = owner.Deliver(handler, Args(doc));

        private void OnClosed(object? sender, IScriptDocumentHost doc) => _ = owner.Deliver(handler, Args(doc));

        private void OnActive(object? sender, EventArgs e) => _ = owner.Deliver(handler, Args(owner.Ctx.Environment.ActiveDocument));

        private void OnAfterSave(object? sender, IScriptDocumentHost doc) => _ = owner.Deliver(handler, Args(doc));

        private void OnBeforeSave(object? sender, BeforeSaveEventArgs e)
        {
            // 保存する側はハンドラの完了を待ち、cancel(理由) があれば保存を中止する。
            e.Defer(owner.Deliver(handler, Args(e.Document, e.Cancel)));
        }

        private void OnSelection(object? sender, IScriptDocumentHost doc)
        {
            if (Interlocked.Exchange(ref _selectionPending, 1) == 1)
            {
                return;
            }

            TimeProvider time = owner.Ctx.Run.Time;
            long now = time.GetTimestamp();
            TimeSpan since = _lastSelection == 0 ? TimeSpan.MaxValue : time.GetElapsedTime(_lastSelection, now);
            TimeSpan delay = since >= SelectionInterval ? TimeSpan.Zero : SelectionInterval - since;
            _ = Task.Run(async () =>
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, time).ConfigureAwait(false);
                }

                _lastSelection = time.GetTimestamp();
                Volatile.Write(ref _selectionPending, 0);
                await owner.Deliver(handler, Args(doc)).ConfigureAwait(false);
            });
        }
    }

    private sealed class EventArgsImpl(string name, IDocument? document, Action<string>? cancel) : ScriptEventArgs
    {
        public override string Name => name;

        public override IDocument? Document => document;

        public override void Cancel(string reason)
        {
            if (cancel is null)
            {
                throw new Sdk.ArgumentError($"{name} は中止できません。");
            }

            cancel(reason);
        }
    }
}
