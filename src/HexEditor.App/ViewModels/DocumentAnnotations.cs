using System.Text.Json;
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.Core.Bookmarks;
using HexEditor.Core.Files;
using HexEditor.Core.Inspector;
using HexEditor.Core.Sources;

namespace HexEditor.App.ViewModels;

/// <summary>
/// タブ 1 つ分の、ファイル本体とは別に保存するデータ (00-overview 10 章): ブックマーク (INSP-23) と、インスペクタのエンディアンの
/// 選択 (INSP-02 の仕様 1)。設定フォルダの <c>documents/</c> に保存する。無題のドキュメントでは保存しない。
/// </summary>
public sealed class DocumentAnnotations
{
    /// <summary>インスペクタの設定の付随データの種類。</summary>
    public const string InspectorKind = "inspector";

    private readonly DocumentDataStore _store;
    private InspectorEndianMode _endian;

    /// <summary>ドキュメントの色付けルールの付随データの種類 (INSP-33 の仕様 3)。</summary>
    public const string ColoringKind = "coloring";

    public DocumentAnnotations(DocumentViewModel document, DocumentDataStore store)
    {
        Document = document;
        _store = store;
        Bookmarks = BookmarkCollection.Attach(document.Document);

        // 共通の注釈レイヤー (INSP-32) にブックマークを出どころとして登録する (ツールチップ・注釈の列・凡例で使う)。
        Layer = Core.Annotations.AnnotationLayer.For(document.Document);
        if (Layer.Find(Core.Annotations.BookmarkAnnotationSource.SourceId) is null)
        {
            Layer.Register(new Core.Annotations.BookmarkAnnotationSource(Bookmarks));
        }
    }

    /// <summary>このドキュメントの注釈レイヤー (INSP-32)。</summary>
    public Core.Annotations.AnnotationLayer Layer { get; }

    /// <summary>ドキュメントの色付けルール (一覧の上ほど優先。INSP-33 の仕様 3、INSP-34 の仕様 1)。</summary>
    public IReadOnlyList<Core.Coloring.ColoringRule> ColoringRules { get; private set; } = [];

    /// <summary>色付けルールの評価 (表示範囲のチャンクごとのキャッシュ)。</summary>
    public Core.Coloring.ColoringEngine Coloring { get; } = new();

    /// <summary>ドキュメントの色付けルールが変わった。</summary>
    public event EventHandler? ColoringRulesChanged;

    /// <summary>ドキュメントの色付けルールを置き換えて保存する (無題のドキュメントでは保存しない)。</summary>
    public void SetColoringRules(IReadOnlyList<Core.Coloring.ColoringRule> rules)
    {
        ColoringRules = [.. rules.Take(Core.Coloring.ColoringRule.MaxRules)];
        ColoringRulesChanged?.Invoke(this, EventArgs.Empty);
        if (Path is not { } path || !Loaded)
        {
            return;
        }

        try
        {
            if (ColoringRules.Count == 0)
            {
                _store.Delete(path, ColoringKind);
            }
            else
            {
                _store.WriteObject(path, ColoringKind, Stamp, new JsonObject { ["rules"] = JsonNode.Parse(Core.Coloring.ColoringRule.Serialize(ColoringRules)) });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Document data: cannot write coloring rules ({ex.GetType().Name}: {ex.Message})");
        }
    }

    public DocumentViewModel Document { get; }

    public BookmarkCollection Bookmarks { get; }

    /// <summary>保存していないブックマークの変更がある。</summary>
    public bool Dirty { get; set; }

    /// <summary>インスペクタのエンディアン (ドキュメントごと)。変えたらすぐ保存する。</summary>
    public InspectorEndianMode InspectorEndian
    {
        get => _endian;
        set
        {
            if (_endian != value)
            {
                _endian = value;
                SaveInspector();
            }
        }
    }

    /// <summary>ブックマークが記録されていたが、ファイルが変わっていたので適用を待っているもの。</summary>
    public LoadedBookmarks? Pending { get; private set; }

    private string? Path => Document.FilePath;

    private FileStamp? Stamp => (Document.Document.Source as FileByteSource)?.Stamp;

    /// <summary>
    /// 記録した付随データを読む。ファイルのサイズ・更新日時が記録と違えば、ブックマークは適用せずに <see cref="Pending"/> に残す
    /// (利用者に確認してから <see cref="ApplyPending"/> する)。ファイルの読み込みと解析は別のスレッドで行い (100 万件でも UI を
    /// 止めない)、適用は呼び出し元のスレッド (UI スレッド) で行う。読み終わるまでは保存しない (空の一覧で上書きしないため)。
    /// </summary>
    public async Task LoadAsync()
    {
        if (Path is not { } path)
        {
            Loaded = true;
            return;
        }

        Task<(InspectorEndianMode? Endian, LoadedBookmarks? Bookmarks, IReadOnlyList<Core.Coloring.ColoringRule>? Rules)> read = Task.Run(() => Read(_store, path));
        Track(read);
        (InspectorEndianMode? endian, LoadedBookmarks? loaded, IReadOnlyList<Core.Coloring.ColoringRule>? rules) = await read;
        if (endian is { } mode)
        {
            _endian = mode;
        }

        if (rules is { Count: > 0 } && ColoringRules.Count == 0)
        {
            ColoringRules = rules;
            ColoringRulesChanged?.Invoke(this, EventArgs.Empty);
        }

        if (loaded is not null)
        {
            if (Dirty)
            {
                // 読み終わる前に利用者がブックマークを変えた: 変えた内容を残す (記録は次の保存で上書きされる)。
                AppLog.Info("Document data: bookmarks were changed while loading; the saved list was not applied");
            }
            else if (loaded.Header.Matches(Stamp))
            {
                BookmarkStore.Apply(Bookmarks, loaded, Document.Document.Length);
                Dirty = false;
            }
            else
            {
                Pending = loaded;
            }
        }

        Loaded = true;
    }

    /// <summary>付随データを読み終えた (<see cref="LoadAsync"/>)。</summary>
    public bool Loaded { get; private set; }

    private static (InspectorEndianMode?, LoadedBookmarks?, IReadOnlyList<Core.Coloring.ColoringRule>?) Read(DocumentDataStore store, string path)
    {
        InspectorEndianMode? endian = null;
        LoadedBookmarks? bookmarks = null;
        IReadOnlyList<Core.Coloring.ColoringRule>? rules = null;
        try
        {
            if (store.ReadObject(path, InspectorKind) is { } inspector
                && Enum.TryParse(inspector.Value["endian"]?.GetValue<string>(), ignoreCase: true, out InspectorEndianMode mode))
            {
                endian = mode;
            }

            bookmarks = BookmarkStore.Load(store, path);
            if (store.ReadObject(path, ColoringKind) is { } coloring && coloring.Value["rules"] is JsonArray list)
            {
                rules = Core.Coloring.ColoringRule.Parse(list.ToJsonString());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            AppLog.Warning($"Document data: cannot read ({ex.GetType().Name}: {ex.Message})");
        }

        return (endian, bookmarks, rules);
    }

    /// <summary>ファイルが変わっていたブックマークを、利用者の確認の後で適用する。</summary>
    public void ApplyPending()
    {
        if (Pending is { } pending)
        {
            Pending = null;
            BookmarkStore.Apply(Bookmarks, pending, Document.Document.Length);
            Dirty = true;
        }
    }

    public void DiscardPending() => Pending = null;

    /// <summary>
    /// ブックマークを保存する (変更があるとき、または <paramref name="force"/>)。保存する内容はここで写し取り、ファイルへの書き出しは
    /// 別のスレッドで順に行う (100 万件でも UI を止めない)。書き終わりを待つには <see cref="WhenWritesDoneAsync"/>。
    /// </summary>
    public void SaveBookmarks(bool force = false)
    {
        if (Path is not { } path || !force && !Dirty || Pending is not null || !Loaded)
        {
            return;
        }

        LoadedBookmarks snapshot = BookmarkStore.Capture(Bookmarks);
        FileStamp? stamp = Stamp;
        DocumentDataStore store = _store;
        Dirty = false;
        lock (WritesLock)
        {
            // 書き出しは 1 本の列で順に行う (同じファイルへの書き出しが前後しないように)。
            _writes = _writes.ContinueWith(_ => Write(store, path, stamp, snapshot), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            Track(_writes);
        }
    }

    private static void Write(DocumentDataStore store, string path, FileStamp? stamp, LoadedBookmarks snapshot)
    {
        try
        {
            BookmarkStore.Write(store, path, stamp, snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Document data: cannot write bookmarks ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            // 写しの領域を使い回す (100 万件の保存のたびに大きな配列を作らない)。
            BookmarkStore.Release(snapshot);
        }
    }

    private static readonly object WritesLock = new();
    private static Task _writes = Task.CompletedTask;
    private static readonly List<Task> InFlight = [];

    private static void Track(Task task)
    {
        lock (InFlight)
        {
            InFlight.RemoveAll(t => t.IsCompleted);
            InFlight.Add(task);
        }
    }

    /// <summary>付随データの読み書きがすべて終わるまで待つ (アプリの終了、テストの待ち合わせ)。</summary>
    public static Task WhenWritesDoneAsync()
    {
        lock (InFlight)
        {
            return Task.WhenAll([.. InFlight]);
        }
    }

    /// <summary>付随データの書き出しが終わるまで待つ (ウィンドウを閉じたとき。UI スレッドを止めてよい場面だけ)。</summary>
    public static bool WaitForWrites(TimeSpan timeout)
    {
        Task all = WhenWritesDoneAsync();
        try
        {
            return all.Wait(timeout);
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    private void SaveInspector()
    {
        if (Path is not { } path)
        {
            return;
        }

        try
        {
            if (_endian == InspectorEndianMode.Document)
            {
                _store.Delete(path, InspectorKind);
            }
            else
            {
                _store.WriteObject(path, InspectorKind, Stamp, new JsonObject { ["endian"] = _endian.ToString().ToLowerInvariant() });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Document data: cannot write inspector settings ({ex.GetType().Name}: {ex.Message})");
        }
    }
}
