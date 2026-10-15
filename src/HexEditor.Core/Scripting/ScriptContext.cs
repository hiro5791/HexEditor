using HexEditor.Core.Annotations;
using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Scripting.Api;
using HexEditor.Sdk;

namespace HexEditor.Core.Scripting;

/// <summary>
/// スクリプトのスレッド (JavaScript はイベントループのスレッド、C# はスレッドプール)。UI スレッドで起きたイベントや一括処理の中から
/// スクリプトの関数を呼ぶときに使う。
/// </summary>
public interface IScriptThread
{
    /// <summary>今のスレッドがスクリプトのスレッドか。</summary>
    bool IsCurrent { get; }

    /// <summary>スクリプトのスレッドで実行する (完了を待たない)。完了したら返す Task が終わる。</summary>
    Task PostAsync(Action action);

    /// <summary>スクリプトのスレッドで実行して結果を待つ (スクリプトのスレッドから呼んだ場合はその場で実行する)。</summary>
    T Invoke<T>(Func<T> func);
}

/// <summary>実行時間の数え方の制御 (AUTO-02 の仕様 4: 一括処理の完了待ち・利用者の入力待ち・一時停止の時間を数えない)。</summary>
public interface IExecutionClock
{
    /// <summary>戻り値を Dispose するまで実行時間を数えない。</summary>
    IDisposable Pause();
}

/// <summary>何もしない時計 (C# スクリプト。上限を強制できない)。</summary>
internal sealed class NoExecutionClock : IExecutionClock
{
    public static readonly NoExecutionClock Instance = new();

    public IDisposable Pause() => Nothing.Instance;

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// 1 回の実行の状態。API のオブジェクト (Core.Scripting.Api) はこれを通して許可・書き込みの排他・Undo の単位・強調表示・出力を扱う。
/// </summary>
public sealed class ScriptContext : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<Document, WriteSession> _writes = [];
    private readonly Dictionary<Document, AnnotationSet> _highlights = [];
    private readonly Dictionary<IScriptDocumentHost, ScriptDocument> _documents = [];
    private readonly HashSet<string> _granted = new(StringComparer.Ordinal);
    private readonly HashSet<string> _denied = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pickedRead = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pickedWrite = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _subscriptions;

    internal ScriptContext(ScriptHost host, ScriptRun run, IScriptEnvironment environment, ScriptRunRequest request, ScriptMetadata metadata,
        IReadOnlyDictionary<string, object?> args, string? scriptFolder)
    {
        Host = host;
        Run = run;
        Environment = environment;
        Request = request;
        Metadata = metadata;
        Args = args;
        ScriptFolder = scriptFolder;
    }

    public ScriptHost Host { get; }

    public ScriptRun Run { get; }

    public IScriptEnvironment Environment { get; }

    public IScriptDispatcher Dispatcher => Environment.Dispatcher;

    public ScriptRunRequest Request { get; }

    public ScriptMetadata Metadata { get; }

    public bool Headless => Request.Headless;

    public string Name => Run.Name;

    public string? Path => Run.Path;

    /// <summary>スクリプトのフォルダ (許可なしで読める。AUTO-18 の仕様 1)。</summary>
    public string? ScriptFolder { get; }

    public IReadOnlyDictionary<string, object?> Args { get; }

    /// <summary>スクリプト全体のキャンセル (<c>script.signal</c> / <c>Ct</c>。AUTO-05 の仕様 4)。</summary>
    public CancellationToken Token => _stop.Token;

    public bool StopRequested => _stop.IsCancellationRequested;

    /// <summary>実行の終わり (書き込みの排他・処理センターの項目を終える)。</summary>
    public Task Ended => _ended.Task;

    /// <summary>スクリプトのスレッド (実行環境が設定する)。</summary>
    public IScriptThread Thread { get; internal set; } = null!;

    /// <summary>実行時間の数え方 (JavaScript の実行環境が設定する)。</summary>
    public IExecutionClock Clock { get; internal set; } = NoExecutionClock.Instance;

    /// <summary>JavaScript の強制中断 (停止の第 2 段階)。実行環境が設定する。</summary>
    internal Action? ForceStop { get; set; }

    /// <summary><c>app.stop()</c> が呼ばれた (正常な終了)。</summary>
    internal bool StopCalled { get; set; }

    /// <summary><c>app.exit(コード)</c> の値 (AUTO-41 の仕様 8)。</summary>
    public int? ExitCode { get; internal set; }

    /// <summary>API の入口 (<c>app</c>)。</summary>
    public ScriptApp App { get; internal set; } = null!;

    /// <summary>処理センターの「スクリプト: 名前」の項目 (<c>ui.progress</c> の報告先。AUTO-01 の仕様 6)。</summary>
    internal LongRunningOperation? RunOperation { get; set; }

    /// <summary>イベントの購読の数 (1 以上なら常駐。AUTO-06 の仕様 4)。</summary>
    public int Subscriptions => Volatile.Read(ref _subscriptions);

    internal void AddSubscription()
    {
        if (Interlocked.Increment(ref _subscriptions) == 1)
        {
            Run.IsResident = true;
        }
    }

    internal void RemoveSubscription()
    {
        if (Interlocked.Decrement(ref _subscriptions) == 0)
        {
            Run.IsResident = false;
            SubscriptionsEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>購読がなくなった (常駐を終えてよい)。</summary>
    internal event EventHandler? SubscriptionsEnded;

    /// <summary>停止の第 1 段階: キャンセルを求める (AUTO-01 の仕様 8)。猶予の後に強制中断する。</summary>
    public void RequestStop()
    {
        try
        {
            if (_stop.IsCancellationRequested)
            {
                return;
            }

            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 実行は終わっている。
            return;
        }

        Host.ScheduleForceStop(this);
    }

    // ---- ドキュメントの包み ----

    /// <summary>ドキュメントの API のオブジェクト (同じタブには同じオブジェクトを返す)。</summary>
    public ScriptDocument Wrap(IScriptDocumentHost host)
    {
        lock (_lock)
        {
            if (!_documents.TryGetValue(host, out ScriptDocument? doc))
            {
                doc = new ScriptDocument(this, host);
                _documents[host] = doc;
            }

            return doc;
        }
    }

    public ScriptDocument? WrapOrNull(IScriptDocumentHost? host) => host is null ? null : Wrap(host);

    // ---- 許可 (AUTO-34) ----

    /// <summary>実行前に与えた許可 (信頼の確認で認めた宣言、「常に許可する」の記録、コマンドラインの --allow)。</summary>
    internal void Grant(IEnumerable<string> permissions)
    {
        lock (_lock)
        {
            foreach (string p in permissions)
            {
                _granted.Add(p);
            }
        }
    }

    public bool HasPermission(string permission)
    {
        lock (_lock)
        {
            return _granted.Contains(permission);
        }
    }

    /// <summary>
    /// 許可を確かめる。宣言していない許可は、対話的な実行ならその場で確認する (AUTO-34 の仕様 4)。拒否・ヘッドレスなら
    /// <see cref="PermissionError"/>。確認の待ち時間は実行時間に数えない。
    /// </summary>
    public void RequirePermission(string permission, string detail)
    {
        if (HasPermission(permission))
        {
            return;
        }

        bool denied;
        lock (_lock)
        {
            denied = _denied.Contains(permission);
        }

        if (!denied && !Headless)
        {
            bool allowed;
            using (Clock.Pause())
            {
                allowed = Dispatcher.InvokeAsync(() => Environment.Ui.ConfirmPermissionAsync(Name, permission, detail)).GetAwaiter().GetResult();
            }

            lock (_lock)
            {
                (allowed ? _granted : _denied).Add(permission);
            }

            if (allowed)
            {
                return;
            }
        }

        throw new PermissionError(Msg.T($"許可「{permission}」がありません: {detail}", $"The permission \"{permission}\" is not granted: {detail}"), permission);
    }

    /// <summary>利用者がダイアログで選んだパスを、この実行中に限り扱えるようにする (AUTO-16 の仕様 1、AUTO-18 の仕様 1)。</summary>
    internal void AddPickedPath(string path, bool write)
    {
        string full = System.IO.Path.GetFullPath(path);
        lock (_lock)
        {
            _pickedRead.Add(full);
            if (write)
            {
                _pickedWrite.Add(full);
            }
        }
    }

    /// <summary>
    /// ファイルの読み取りを確かめる: 選んだパス・選んだフォルダの中・スクリプトのフォルダ以下は許可なし。それ以外は <c>file-read</c>。
    /// </summary>
    public void RequireFileRead(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (IsPicked(full, write: false) || (ScriptFolder is not null && IsUnder(full, ScriptFolder)))
        {
            return;
        }

        RequirePermission(ScriptPermissions.FileRead, full);
    }

    /// <summary>ファイルの書き込みを確かめる: 書き込み用に選んだパス・選んだフォルダの中は許可なし。それ以外は <c>file-write</c>。</summary>
    public void RequireFileWrite(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (IsPicked(full, write: true))
        {
            return;
        }

        RequirePermission(ScriptPermissions.FileWrite, full);
    }

    private bool IsPicked(string full, bool write)
    {
        lock (_lock)
        {
            HashSet<string> set = write ? _pickedWrite : _pickedRead;
            return set.Contains(full) || set.Any(p => Directory.Exists(p) && IsUnder(full, p));
        }
    }

    internal static bool IsUnder(string full, string folder)
    {
        string root = System.IO.Path.GetFullPath(folder).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    // ---- 書き込みの排他と Undo の単位 (AUTO-01 の仕様 6・7) ----

    /// <summary>書き込み中のドキュメント (変更したドキュメントの記録に使う)。</summary>
    internal IReadOnlyList<WriteSession> WriteSessions
    {
        get
        {
            lock (_lock)
            {
                return [.. _writes.Values];
            }
        }
    }

    /// <summary>
    /// ドキュメントに書き込む準備をする (スクリプトのスレッドから呼ぶ)。最初の書き込みで、そのドキュメントの処理センターの排他を取る。
    /// 他のスクリプトや書き換えの処理が使っている間は待つ (待ち時間は実行時間に数えない。AUTO-01 の仕様 6)。排他を取っている間は
    /// 利用者の編集を受け付けず、エディタ上部に InfoBar を出す。
    /// </summary>
    /// <summary>
    /// 書き込みの排他を手放す (保存・閉じる前。保存は処理センターの排他を使うため、持ったままだと待ち合わせになる)。次の書き込みで取り直す
    /// (Undo の単位はそこで区切れる)。変更したことは記録に残す。
    /// </summary>
    internal void ReleaseWrite(ScriptDocument doc)
    {
        WriteSession? session;
        lock (_lock)
        {
            if (!_writes.TryGetValue(doc.Document, out session))
            {
                return;
            }

            _writes.Remove(doc.Document);
            if (session.Changed)
            {
                _released.Add(session.Document.Name);
            }
        }

        Dispatcher.Invoke(session.Release);
    }

    private readonly HashSet<string> _released = new(StringComparer.Ordinal);

    internal WriteSession AcquireWrite(ScriptDocument doc)
    {
        WriteSession? session;
        lock (_lock)
        {
            _writes.TryGetValue(doc.Document, out session);
        }

        if (session is not null)
        {
            return session;
        }

        if (doc.Document.IsReadOnly)
        {
            throw new ReadOnlyError(Msg.T($"ドキュメント「{doc.Name}」は読み取り専用です。", $"The document \"{doc.Name}\" is read-only."));
        }

        session = new WriteSession(this, doc);
        lock (_lock)
        {
            _writes[doc.Document] = session;
        }

        using (Clock.Pause())
        {
            session.Start();
        }

        return session;
    }

    /// <summary>
    /// UI スレッドでドキュメントを書き換える。スクリプトの書き込みのために一時的に編集の排他を外し、Undo の単位 (既定は実行全体で 1 つ。
    /// <c>beginUndoGroup</c> で区切った場合はその単位) に入れる。
    /// </summary>
    internal T Edit<T>(ScriptDocument doc, Func<Document, T> edit)
    {
        WriteSession session = AcquireWrite(doc);
        return Dispatcher.Invoke(() => session.Apply(edit));
    }

    internal void Edit(ScriptDocument doc, Action<Document> edit) => Edit(doc, d =>
    {
        edit(d);
        return true;
    });

    // ---- 強調表示 (AUTO-07) ----

    internal AnnotationSet HighlightsFor(Document document)
    {
        lock (_lock)
        {
            if (!_highlights.TryGetValue(document, out AnnotationSet? set))
            {
                set = new AnnotationSet("script:" + Run.Id.ToString("N"), AnnotationOrigin.Script, Name);
                _highlights[document] = set;
                Dispatcher.Invoke(() => AnnotationLayer.For(document).Register(set));
            }

            return set;
        }
    }

    // ---- 出力 ----

    public void Output(ScriptOutputKind kind, string text, string? path = null, int line = 0, int column = 0) =>
        Environment.Output.Write(Run.Channel, kind, text, path, line, column);

    /// <summary>キャンセルされていれば <see cref="CanceledError"/>。</summary>
    public void ThrowIfStopped()
    {
        if (_stop.IsCancellationRequested)
        {
            throw new CanceledError(Msg.T("スクリプトは停止されました。", "The script was stopped."));
        }
    }

    /// <summary>非同期 API の <c>options.signal</c> とスクリプト全体のキャンセルをつなぐ。</summary>
    public CancellationTokenSource LinkedToken(CancellationToken? signal) =>
        signal is { } s && s.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(Token, s) : CancellationTokenSource.CreateLinkedTokenSource(Token);

    /// <summary>処理センター (ドキュメントのウィンドウのもの)。</summary>
    internal OperationCenter OperationsFor(ScriptDocument? doc) => Environment.OperationsFor(doc?.Host);

    /// <summary>実行の終わりの片付け (UI スレッドへの受け渡しを含む。スクリプトのスレッドで呼ぶ)。</summary>
    internal IReadOnlyList<string> Finish()
    {
        List<WriteSession> sessions;
        List<(Document Doc, AnnotationSet Set)> highlights;
        List<ScriptDocument> documents;
        lock (_lock)
        {
            sessions = [.. _writes.Values];
            highlights = [.. _highlights.Select(p => (p.Key, p.Value))];
            documents = [.. _documents.Values];
        }

        try
        {
            App?.Events.UnsubscribeAll();
            Dispatcher.Invoke(() => documents.ForEach(d => d.Detach()));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }

        var modified = new List<string>();
        lock (_lock)
        {
            modified.AddRange(_released);
        }
        foreach (WriteSession s in sessions)
        {
            try
            {
                Dispatcher.Invoke(s.End);
                if (s.Changed)
                {
                    if (!modified.Contains(s.Document.Name)) { modified.Add(s.Document.Name); }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }
        }

        // 強調表示はスクリプトの終了で消す (AUTO-07 の仕様 1)。
        foreach ((Document doc, AnnotationSet set) in highlights)
        {
            try
            {
                Dispatcher.Invoke(() => AnnotationLayer.Find(doc)?.Unregister(set.Id));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }
        }

        Run.IsResident = false;
        _ended.TrySetResult();
        return modified;
    }

    public void Dispose() => _stop.Dispose();
}

/// <summary>
/// 1 つのドキュメントへの書き込みの排他と Undo の単位 (AUTO-01 の仕様 6・7)。処理センターの「変更する処理」として排他を取り、
/// 実行が終わるまで持ち続ける。
/// </summary>
internal sealed class WriteSession(ScriptContext context, ScriptDocument document)
{
    private readonly TaskCompletionSource _acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>排他を手放す (保存の前。保存は処理センターの排他を使うため)。UI スレッドで呼ぶ。</summary>
    public void Release()
    {
        CloseGroup();
        _released.TrySetResult();
    }
    private IDisposable? _group;
    private bool _explicitGroup;

    public ScriptDocument Document { get; } = document;

    /// <summary>このスクリプトが変更を加えた。</summary>
    public bool Changed { get; private set; }

    private Document Doc => Document.Document;

    /// <summary>排他を取るまで待つ (スクリプトのスレッドで呼ぶ)。</summary>
    public void Start()
    {
        OperationCenter center = context.OperationsFor(Document);
        string name = context.Environment.Localize(ScriptTexts.WriteOperation, context.Name);

        // 処理センターの通知は UI スレッドで始める (他の処理と同じ)。
        context.Dispatcher.Invoke(() =>
        {
            _ = center.RunAsync(name, OperationKind.ModifiesDocument, Doc, null, async op =>
            {
                _acquired.TrySetResult();
                using CancellationTokenRegistration reg = op.CancellationToken.Register(context.RequestStop);
                await Task.WhenAny(context.Ended, _released.Task).ConfigureAwait(false);
            }, locked =>
            {
                // UI スレッドとは限らないので受け渡す。
                _ = context.Dispatcher.InvokeAsync(() =>
                {
                    if (!Doc.IsDisposed)
                    {
                        Doc.SetEditLock(locked);
                    }

                    Document.Host.ShowScriptWriting(locked ? context.Run : null);
                });
            });
        });

        try
        {
            _acquired.Task.Wait(context.Token);
        }
        catch (OperationCanceledException)
        {
            throw new CanceledError(Msg.T("書き込みの順番を待っている間にスクリプトが停止されました。", "The script was stopped while waiting for its turn to write."));
        }

        // 排他の表示 (InfoBar と編集の排他) が UI スレッドで済むのを待つ。
        context.Dispatcher.Invoke(() => { });
    }

    /// <summary>UI スレッドで書き換える。</summary>
    public T Apply<T>(Func<Document, T> edit)
    {
        if (Doc.IsDisposed)
        {
            throw new ArgumentError(Msg.T($"ドキュメント「{Document.Name}」は閉じられています。", $"The document \"{Document.Name}\" has been closed."));
        }

        bool locked = Doc.IsEditLocked;
        Doc.SetEditLock(false);
        try
        {
            _group ??= Doc.BeginGroup(context.Environment.Localize(ScriptTexts.UndoName, context.Name));
            int before = Doc.History.CurrentIndex;
            var beforeEntry = Doc.History.CurrentEntry;
            T result = edit(Doc);
            if (Doc.History.CurrentIndex != before || !ReferenceEquals(Doc.History.CurrentEntry, beforeEntry))
            {
                Changed = true;
            }

            Document.RefreshSnapshot();
            return result;
        }
        finally
        {
            Doc.SetEditLock(locked);
        }
    }

    /// <summary>Undo の単位に入れずに変更する (Undo / Redo。今の単位は先に閉じる)。UI スレッド。</summary>
    public void ApplyUngrouped(Action<Document> edit)
    {
        CloseGroup();
        if (Doc.IsDisposed)
        {
            throw new ArgumentError(Msg.T($"ドキュメント「{Document.Name}」は閉じられています。", $"The document \"{Document.Name}\" has been closed."));
        }

        bool locked = Doc.IsEditLocked;
        Doc.SetEditLock(false);
        try
        {
            edit(Doc);
            Changed = true;
            Document.RefreshSnapshot();
        }
        finally
        {
            Doc.SetEditLock(locked);
        }
    }

    /// <summary><c>beginUndoGroup</c>: 今の単位を閉じ、名前付きの単位を始める (AUTO-07)。</summary>
    public void BeginExplicit(string name)
    {
        _group?.Dispose();
        _group = Doc.BeginGroup(name);
        _explicitGroup = true;
    }

    /// <summary><c>endUndoGroup</c>: 名前付きの単位を閉じる。以降の書き込みは新しい既定の単位に入る。</summary>
    public void EndExplicit()
    {
        if (_explicitGroup)
        {
            _group?.Dispose();
            _group = null;
            _explicitGroup = false;
        }
    }

    /// <summary>Undo / Redo の前に今の単位を閉じる。</summary>
    public void CloseGroup()
    {
        _group?.Dispose();
        _group = null;
        _explicitGroup = false;
    }

    /// <summary>実行の終わり (UI スレッド)。例外で終わった場合も、それまでの編集は Undo の単位として残る (AUTO-01 の仕様 9)。</summary>
    public void End() => CloseGroup();
}
