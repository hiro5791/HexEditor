using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using HexEditor.App.Services;
using HexEditor.App.ViewModels;
using HexEditor.Core.Annotations;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.Templates;
using HexEditor.Core.Templates.Debugging;
using HexEditor.Core.Templates.Loading;
using HexEditor.Core.Templates.Presentation;

namespace HexEditor.App.Templates;

/// <summary>ドキュメントの適用 1 つ (TPL-20)。</summary>
public sealed class TemplateApplicationEntry
{
    private static int _nextId;

    public TemplateApplicationEntry(DocumentTemplates owner, TemplateApplication application, bool isAuto, TemplateDebugger? debugger)
    {
        Owner = owner;
        Application = application;
        IsAuto = isAuto;
        Debugger = debugger;
        Id = Interlocked.Increment(ref _nextId);
    }

    public int Id { get; }

    public DocumentTemplates Owner { get; }

    public TemplateApplication Application { get; }

    public TemplateApplicationInfo Info => Application.Info;

    public TemplateResult? Result => Application.Result;

    /// <summary>自動適用で付けた (エラーなら InfoBar で知らせる。TPL-30 の「エラー」)。</summary>
    public bool IsAuto { get; }

    /// <summary>デバッグの適用 (保存しない。TPL-32)。</summary>
    public TemplateDebugger? Debugger { get; }

    public bool IsFollow => Info.Kind == TemplateApplyKind.Follow;

    /// <summary>適用の名前 (テンプレートの表示名)。</summary>
    public string DisplayName => Application.Program?.Metadata.GetDisplayName(System.Globalization.CultureInfo.CurrentUICulture, Info.TemplatePath)
        ?? Path.GetFileNameWithoutExtension(Info.TemplatePath);

    /// <summary>評価に 0.5 秒以上かかったら処理センターに出す処理 (TPL-28 の仕様 5)。</summary>
    public Core.Operations.LongRunningOperation? Operation { get; set; }
}

/// <summary>
/// 1 つのドキュメントの適用の一覧 (TPL-20 の仕様 3。上限 32 個)。適用の記録をドキュメントに付随するデータに保存して開き直したときに
/// 復元し (仕様 7)、編集・カーソルの移動・テンプレートのファイルの保存で評価し直す。色分けは共通の注釈レイヤー (INSP-32) の出どころ
/// 「テンプレート」(VIEW-17 の層 9) として載せる。イベントは UI スレッドで出す。
/// </summary>
public sealed class DocumentTemplates
{
    /// <summary>付随データの種類 (適用の一覧)。</summary>
    public const string DataKind = "templates";

    private static readonly ConditionalWeakTable<Document, DocumentTemplates> Attached = new();

    private readonly List<TemplateApplicationEntry> _entries = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _queue;
    private readonly TemplateAnnotationSource _annotations;
    private bool _loaded;

    private DocumentTemplates(DocumentViewModel owner)
    {
        Owner = owner;
        Document = owner.Document;
        _queue = App.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _annotations = new TemplateAnnotationSource(this);
        AnnotationLayer.For(Document).Register(_annotations);
        Document.Changed += (_, _) => DocumentChanged();
    }

    /// <summary>ドキュメントの適用の一覧 (なければ作る)。</summary>
    public static DocumentTemplates For(DocumentViewModel doc)
    {
        DocumentTemplates t = Attached.GetValue(doc.Document, _ => new DocumentTemplates(doc));
        t.Owner = doc;
        return t;
    }

    public static DocumentTemplates? Find(Document document) => Attached.TryGetValue(document, out DocumentTemplates? t) ? t : null;

    public DocumentViewModel Owner { get; private set; }

    public Document Document { get; }

    public IReadOnlyList<TemplateApplicationEntry> Entries => _entries;

    /// <summary>適用の一覧が変わった (追加・解除)。</summary>
    public event EventHandler? Changed;

    /// <summary>適用の評価を始めた・終わった (結果ツリーと色分けを更新する)。</summary>
    public event EventHandler<TemplateApplicationEntry>? ResultChanged;

    /// <summary>適用の記録があった (開いたときに復元したもの。自動適用の提案をしない。TPL-30 の仕様 6)。</summary>
    public bool HadRecord { get; private set; }

    /// <summary>
    /// 適用を加えて評価を始める。上限 (32 個) に達していれば null。<paramref name="unsaved"/> は「試しに実行」の保存していない内容
    /// (TPL-31 の仕様 4)。
    /// </summary>
    public TemplateApplicationEntry? Add(TemplateApplicationInfo info, TemplateProgram? unsaved = null, bool isAuto = false, TemplateDebugger? debugger = null)
    {
        if (_entries.Count >= TemplateApplicationSet.MaxApplications)
        {
            return null;
        }

        Document document = Document;
        string? fileName = Owner.FilePath;
        TemplateApplyOptions options = TemplateService.ApplyOptions(isAuto) with
        {
            BigEndian = info.BigEndian ?? (Owner.Editor.View.BigEndian ? true : null),
            Debugger = debugger,
        };
        var application = new TemplateApplication(info, () => TemplateDataSource.FromSnapshot(document.Current, fileName),
            TemplateService.LoadOptions, options, loader: TemplateService.Load)
        {
            AutoReevaluate = TemplateService.AutoReevaluate && debugger is null,
        };
        var entry = new TemplateApplicationEntry(this, application, isAuto, debugger);
        application.Started += (_, _) => Post(entry);
        application.ResultChanged += (_, _) => Post(entry);
        _entries.Add(entry);
        if (unsaved is not null)
        {
            application.SetUnsavedProgram(unsaved);
        }
        else
        {
            application.Start();
        }

        if (debugger is null && unsaved is null)
        {
            TemplateService.AddRecent(info.TemplatePath);
        }

        Save();
        Changed?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    private void Post(TemplateApplicationEntry entry) => _queue.TryEnqueue(() =>
    {
        if (_entries.Contains(entry))
        {
            _annotations.Invalidate(entry);
            ResultChanged?.Invoke(this, entry);
        }
    });

    /// <summary>適用を解除する (結果ツリーと色分けを消す。仕様 8)。</summary>
    public void Remove(TemplateApplicationEntry entry)
    {
        if (!_entries.Remove(entry))
        {
            return;
        }

        entry.Debugger?.Resume(TemplateDebugCommand.Stop);
        entry.Application.Dispose();
        _annotations.Invalidate(entry);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>適用の設定を変えて評価し直す (開始オフセット・エンディアン・種類)。</summary>
    public void Update(TemplateApplicationEntry entry, TemplateApplicationInfo info)
    {
        entry.Application.Update(info);
        Save();
    }

    // ───────────── 評価のきっかけ ─────────────

    private void DocumentChanged()
    {
        bool auto = TemplateService.AutoReevaluate;
        foreach (TemplateApplicationEntry e in _entries.ToList())
        {
            if (e.Debugger is not null)
            {
                continue;
            }

            e.Application.AutoReevaluate = auto;
            e.Application.DocumentChanged();
        }
    }

    /// <summary>カーソルが動いた (追従の適用を評価し直す。TPL-26 の仕様 2)。</summary>
    public void CursorMoved(long offset)
    {
        foreach (TemplateApplicationEntry e in _entries)
        {
            if (e.IsFollow)
            {
                e.Application.MoveCursor(offset);
            }
        }
    }

    /// <summary>テンプレートを保存した: そのテンプレートを使う適用を評価し直す (TPL-20 の仕様 6、TPL-31 の仕様 6)。</summary>
    public void TemplateSaved(string path)
    {
        foreach (TemplateApplicationEntry e in _entries)
        {
            if (string.Equals(e.Info.TemplatePath, path, StringComparison.OrdinalIgnoreCase) || e.Application.Program?.Files.Contains(path, StringComparer.OrdinalIgnoreCase) == true)
            {
                if (e.Application.IsUnsaved)
                {
                    e.Application.SetUnsavedProgram(null);
                }
                else
                {
                    e.Application.Reevaluate();
                }
            }
        }
    }

    // ───────────── 保存と復元 (TPL-20 の仕様 7) ─────────────

    /// <summary>適用の一覧を付随データに書く (無題のドキュメント・「試しに実行」・デバッグの適用は保存しない)。</summary>
    public void Save()
    {
        if (!_loaded || Owner.FilePath is not { } path || ViewOptions.Documents is not { } store)
        {
            return;
        }

        var set = new TemplateApplicationSet();
        foreach (TemplateApplicationEntry e in _entries.Where(e => e.Debugger is null && !e.Application.IsUnsaved))
        {
            set.TryAdd(e.Info);
        }

        try
        {
            if (set.Items.Count == 0)
            {
                store.Delete(path, DataKind);
            }
            else
            {
                store.WriteObject(path, DataKind, (Document.Source as FileByteSource)?.Stamp,
                    new JsonObject { ["applications"] = JsonNode.Parse(set.ToJson()) });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Document data: cannot write template applications ({ex.GetType().Name})");
        }
    }

    /// <summary>付随データの適用を読み込んで適用し直す (1 度だけ)。適用の記録があれば true。</summary>
    public bool Restore()
    {
        if (_loaded)
        {
            return HadRecord;
        }

        _loaded = true;
        if (Owner.FilePath is not { } path || ViewOptions.Documents is not { } store)
        {
            return false;
        }

        TemplateApplicationSet set;
        try
        {
            set = store.ReadObject(path, DataKind) is { } data && data.Value["applications"] is JsonArray list
                ? TemplateApplicationSet.FromJson(list.ToJsonString())
                : new TemplateApplicationSet();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            AppLog.Warning($"Document data: cannot read template applications ({ex.GetType().Name})");
            return false;
        }

        HadRecord = set.Items.Count > 0;
        foreach (TemplateApplicationInfo info in set.Items)
        {
            Add(info);
        }

        return HadRecord;
    }

    /// <summary>復元を済ませたことにする (新規・自動適用の判定の前)。</summary>
    public void MarkLoaded() => _loaded = true;

    public bool Loaded => _loaded;

    /// <summary>ドキュメントを閉じた: すべての評価を止める。</summary>
    public void Close()
    {
        foreach (TemplateApplicationEntry e in _entries)
        {
            e.Debugger?.Resume(TemplateDebugCommand.Stop);
            e.Application.Dispose();
        }

        _entries.Clear();
        AnnotationLayer.Find(Document)?.Unregister(TemplateAnnotationSource.SourceId);
    }

    /// <summary>色分けの範囲 (テスト用の命令)。</summary>
    internal TemplateAnnotationSource Annotations => _annotations;
}

/// <summary>色分けの範囲の印 (注釈の <see cref="Annotation.Tag"/>)。描くときに色と不透明度から背景の色を作る。</summary>
public sealed record TemplatePaintTag(TemplateColor Color, int ApplicationId, TemplateNode Node);

/// <summary>
/// テンプレートの色分けを注釈の出どころ「テンプレート」として出す (INSP-32、TPL-23)。表示範囲の色分けの範囲は評価器のスレッドで求め
/// (UI スレッドを止めない)、求め終わったら描き直す。追従の適用は色分けしない (枠線だけ。TPL-26 の仕様 5)。
/// </summary>
public sealed class TemplateAnnotationSource(DocumentTemplates owner) : IAnnotationSource
{
    public const string SourceId = "templates";

    /// <summary>表示範囲の前後に余分に求める量。</summary>
    private const long Margin = 16 * 1024;

    private readonly Dictionary<int, Cache> _cache = [];

    private sealed class Cache
    {
        public TemplateResult? Result;
        public long From;
        public long To;
        public IReadOnlyList<TemplatePaintSegment> Segments = [];
        public bool Pending;
        public bool Stale;
    }

    public string Id => SourceId;

    public AnnotationOrigin Origin => AnnotationOrigin.Template;

    public string DisplayName => string.Join(", ", owner.Entries.Where(e => !e.IsFollow).Select(e => e.DisplayName));

    public event EventHandler? Changed;

    /// <summary>適用の結果が変わった: 求め直す。</summary>
    public void Invalidate(TemplateApplicationEntry entry)
    {
        if (_cache.TryGetValue(entry.Id, out Cache? c))
        {
            c.Stale = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Query(long start, long end, List<Annotation> output)
    {
        if (!TemplateService.ColoringEnabled)
        {
            return;
        }

        foreach (TemplateApplicationEntry entry in owner.Entries)
        {
            if (entry.IsFollow || entry.Result is not { } result)
            {
                continue;
            }

            if (!_cache.TryGetValue(entry.Id, out Cache? cache))
            {
                cache = new Cache();
                _cache[entry.Id] = cache;
            }

            bool covered = ReferenceEquals(cache.Result, result) && !cache.Stale && cache.From <= start && cache.To >= end;
            if (!covered && !cache.Pending)
            {
                Compute(entry, cache, result, Math.Max(0, start - Margin), end + Margin);
            }

            if (!ReferenceEquals(cache.Result, result) && !cache.Pending)
            {
                continue;
            }

            bool single = end - start <= 1;
            foreach (TemplatePaintSegment s in cache.Segments)
            {
                if (s.Start < end && s.End > start)
                {
                    string description = single ? Describe(s.Node) : string.Empty;
                    output.Add(new Annotation(s.Start, s.Length, s.Node.Path, null, description)
                    {
                        Tag = new TemplatePaintTag(s.Color, entry.Id, s.Node),
                    });
                }
            }
        }
    }

    private void Compute(TemplateApplicationEntry entry, Cache cache, TemplateResult result, long from, long to)
    {
        cache.Pending = true;
        _ = result.InvokeAsync(() => TemplateColoring.Segments(result, from, to)).ContinueWith(t =>
        {
            App.DispatcherQueue?.TryEnqueue(() =>
            {
                cache.Pending = false;
                if (t.IsCompletedSuccessfully)
                {
                    cache.Result = result;
                    cache.From = from;
                    cache.To = to;
                    cache.Segments = t.Result;
                    cache.Stale = false;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            });
        }, TaskScheduler.Default);
    }

    /// <summary>ツールチップの説明 (パターンのパス・型・値。TPL-23 の仕様 7)。</summary>
    private static string Describe(TemplateNode node)
    {
        try
        {
            string value = node.Kind is TemplateNodeKind.Struct or TemplateNodeKind.Union ? string.Empty : node.GetDisplay(TemplateDisplayContext.Current);
            string bits = node.BitOffset is { } bo && node.BitSize is { } bs
                ? " · " + Loc.Format("Tpl_Bits", bo, bo + bs - 1)
                : string.Empty;
            return $"`{node.TypeName}`" + (value.Length > 0 ? " = " + value : string.Empty) + bits;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return string.Empty;
        }
    }

    /// <summary>テスト用: 求めた色分けの範囲。</summary>
    internal IReadOnlyList<TemplatePaintSegment> CachedSegments(TemplateApplicationEntry entry) =>
        _cache.TryGetValue(entry.Id, out Cache? c) ? c.Segments : [];
}
