using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using HexEditor.Core.Engine;
using HexEditor.Core.Expressions;
using HexEditor.Core.Operations;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using HexEditor.Sdk;
using SdkRange = HexEditor.Sdk.ByteRange;

namespace HexEditor.Core.Scripting.Api;

/// <summary>
/// <c>Document</c> (AUTO-07)。データの読み書き (AUTO-08) と一括処理 (AUTO-09) は <c>ScriptDocument.*.cs</c> に分ける。
/// 読み込みはスクリプトのスレッドで最新のスナップショットから行い、変更・ビューの操作は UI スレッドで行う。
/// </summary>
public sealed partial class ScriptDocument : IDocument
{
    /// <summary>同期 API で 1 回に扱える量 (AUTO-05 の仕様 2)。</summary>
    public const long SyncLimit = 16L * 1024 * 1024;

    private readonly ScriptContext _ctx;
    private DocumentSnapshot _snapshot;
    private bool _attached;

    internal ScriptDocument(ScriptContext context, IScriptDocumentHost host)
    {
        _ctx = context;
        Host = host;
        Document = host.Document;
        _snapshot = Document.Current;
        Selection = new ScriptSelection(this);
        Bookmarks = new ScriptBookmarks(this);
        Highlights = new ScriptHighlights(this);
        _ctx.Dispatcher.Invoke(() =>
        {
            if (!Document.IsDisposed)
            {
                Document.Changed += OnChanged;
                _attached = true;
            }
        });
    }

    internal ScriptContext Context => _ctx;

    internal IScriptDocumentHost Host { get; }

    internal Document Document { get; }

    /// <summary>スクリプトのスレッドから読むスナップショット (UI スレッドの変更のたびに入れ替える)。</summary>
    internal DocumentSnapshot Snapshot => Volatile.Read(ref _snapshot);

    private void OnChanged(object? sender, DocumentChangedEventArgs e) => RefreshSnapshot();

    internal void RefreshSnapshot()
    {
        if (!Document.IsDisposed)
        {
            Volatile.Write(ref _snapshot, Document.Current);
        }
    }

    /// <summary>実行の終わり (UI スレッド)。</summary>
    internal void Detach()
    {
        if (_attached)
        {
            Document.Changed -= OnChanged;
            _attached = false;
        }
    }

    /// <summary>閉じたドキュメントの操作は分かりやすいエラーにする (AUTO-07 の仕様 2)。</summary>
    internal void ThrowIfClosed()
    {
        if (Host.IsClosed || Document.IsDisposed)
        {
            throw new Sdk.ArgumentError(Msg.T($"ドキュメント「{Host.Name}」は閉じられています。", $"The document \"{Host.Name}\" has been closed."));
        }
    }

    private T Ui<T>(Func<T> work)
    {
        ThrowIfClosed();
        return _ctx.Dispatcher.Invoke(() =>
        {
            ThrowIfClosed();
            return work();
        });
    }

    private void Ui(Action work) => Ui(() =>
    {
        work();
        return true;
    });

    // ======================== 属性 ========================

    public string Id => Document.Id.ToString("N");

    public string Kind => Host.Kind;

    public string? Path => Host.Path;

    public string Name => Host.Name;

    public long Length
    {
        get
        {
            ThrowIfClosed();
            return ScriptValues.CheckSafe(Snapshot.Length);
        }
    }

    public bool IsModified => Ui(() => Document.IsModified);

    public bool IsReadOnly => Ui(() => Document.IsReadOnly);

    public bool CanResize => Ui(() => Document.CanResize);

    public long BaseAddress => Ui(() => (long)Host.Editor.View.BaseAddress + Document.Source.BaseAddress);

    public int SectorSize => Ui(() => Host.Editor.SectorSize);

    public string Encoding => Ui(() => Host.Editor.TextEncoding.Id);

    internal void SetEncoding(EncodingArg encoding)
    {
        Encoding enc = ScriptText.Resolve(encoding);
        Ui(() => Host.Editor.TextEncoding = View.TextEncoding.FromId(EncodingIdOf(enc)));
    }

    private static string EncodingIdOf(Encoding enc) =>
        EncodingCatalog.All.FirstOrDefault(e => e.CodePage == enc.CodePage)?.Id ?? "cp" + enc.CodePage;

    public long Cursor
    {
        get => Ui(() => ScriptValues.CheckSafe(Host.Editor.Cursor));
        set
        {
            CheckOffset(value, allowEnd: true);
            Ui(() => Host.Editor.Select(value, 0));
        }
    }

    public ISelection Selection { get; }

    public string ActiveColumn
    {
        get => Ui(() => Host.Editor.ActiveColumn == View.ActiveColumn.Hex ? "hex" : "text");
        set
        {
            View.ActiveColumn wanted = value switch
            {
                "hex" => View.ActiveColumn.Hex,
                "text" => View.ActiveColumn.Text,
                _ => throw new Sdk.ArgumentError(Msg.T($"activeColumn は \"hex\" か \"text\" です: {value}", $"activeColumn must be \"hex\" or \"text\": {value}")),
            };
            Ui(() =>
            {
                if (Host.Editor.ActiveColumn != wanted)
                {
                    Host.Editor.ToggleColumn();
                }
            });
        }
    }

    public IBookmarks Bookmarks { get; }

    public IHighlights Highlights { get; }

    // ======================== 入力式 ========================

    /// <summary>入力式の名前の値 (UI スレッドで読む)。</summary>
    internal IExpressionContext ExpressionContext() => new EditorExpressionContext(Host.Editor);

    /// <summary>接頭辞のない数値の基数 (ビューのオフセットの基数に合わせる。00 の 6.1 の※)。UI スレッドで読む。</summary>
    internal DefaultRadix ExpressionRadix() => Host.Editor.View.Radix == OffsetRadix.Decimal ? DefaultRadix.Decimal : DefaultRadix.Hexadecimal;

    internal long ResolveOffset(OffsetArg offset)
    {
        if (offset.Value is long v)
        {
            return v;
        }

        string text = offset.Expression ?? throw new Sdk.ArgumentError(Msg.T("オフセットを指定してください。", "Specify an offset."));
        return Ui(() => ScriptValues.EvaluateNumber(text, ExpressionContext(), ExpressionRadix()));
    }

    internal SdkRange ResolveRange(RangeArg range)
    {
        if (range.Range is SdkRange r)
        {
            return r;
        }

        string text = range.Expression ?? throw new Sdk.ArgumentError(Msg.T("範囲を指定してください。", "Specify a range."));
        return Ui(() => ScriptValues.EvaluateRange(text, ExpressionContext(), ExpressionRadix()));
    }

    /// <summary>範囲を解決し、ドキュメントの中にあるか確かめる。</summary>
    internal SdkRange CheckedRange(RangeArg? range)
    {
        SdkRange r = range is RangeArg a ? ResolveRange(a) : new SdkRange(0, Snapshot.Length);
        long length = Snapshot.Length;
        if (r.Start < 0 || r.Length < 0 || r.Start > length || r.Length > length - r.Start)
        {
            throw new Sdk.RangeError(Msg.T($"範囲がドキュメントの外です: 0x{r.Start:X}+0x{r.Length:X} (長さ 0x{length:X})", $"The range is outside the document: 0x{r.Start:X}+0x{r.Length:X} (length 0x{length:X})"));
        }

        return r;
    }

    internal void CheckOffset(long offset, bool allowEnd)
    {
        long length = Snapshot.Length;
        if (offset < 0 || offset > length || (!allowEnd && offset == length))
        {
            throw new Sdk.RangeError(Msg.T($"オフセットがドキュメントの外です: 0x{offset:X} (長さ 0x{length:X})", $"The offset is outside the document: 0x{offset:X} (length 0x{length:X})"));
        }
    }

    // ======================== 保存・閉じる・Undo ========================

    public Task SaveAsync(AsyncOptions? options = null) => ScriptErrorMap.GuardAsync(async () =>
    {
        ThrowIfClosed();
        if (Ui(() => Document.IsReadOnly))
        {
            throw new ReadOnlyError(Msg.T($"ドキュメント「{Name}」は読み取り専用です。", $"The document \"{Name}\" is read-only."));
        }

        // スクリプトが app.open で開いたファイルへの保存は file-write を求める (AUTO-34 の仕様 3)。
        _ctx.RequireSaveToOpenedFile(Host);
        _ctx.ReleaseWrite(this);
        using CancellationTokenSource cts = _ctx.LinkedToken(options?.Signal);
        using (_ctx.Clock.Pause())
        {
            await _ctx.Dispatcher.InvokeAsync(async () =>
            {
                await Host.SaveAsync(cts.Token).ConfigureAwait(true);
                return true;
            }).ConfigureAwait(false);
        }
    });

    public Task SaveAsAsync(string path, AsyncOptions? options = null) => ScriptErrorMap.GuardAsync(async () =>
    {
        ThrowIfClosed();
        string full = System.IO.Path.GetFullPath(path);
        _ctx.RequireFileWrite(full);
        _ctx.ReleaseWrite(this);
        using CancellationTokenSource cts = _ctx.LinkedToken(options?.Signal);
        using (_ctx.Clock.Pause())
        {
            await _ctx.Dispatcher.InvokeAsync(async () =>
            {
                await Host.SaveAsAsync(full, cts.Token).ConfigureAwait(true);
                return true;
            }).ConfigureAwait(false);
        }
    });

    public Task CloseAsync(CloseOptions? options = null) => ScriptErrorMap.GuardAsync(async () =>
    {
        if (Host.IsClosed)
        {
            return;
        }

        _ctx.ReleaseWrite(this);
        using (_ctx.Clock.Pause())
        {
            await _ctx.Dispatcher.InvokeAsync(() => Host.CloseAsync(options?.Discard ?? false)).ConfigureAwait(false);
        }
    });

    public void Undo() => ScriptErrorMap.Guard(() =>
    {
        WriteSession session = _ctx.AcquireWrite(this);
        _ctx.Dispatcher.Invoke(() => session.ApplyUngrouped(d =>
        {
            if (d.History.CanUndo)
            {
                d.Undo();
            }
        }));
    });

    public void Redo() => ScriptErrorMap.Guard(() =>
    {
        WriteSession session = _ctx.AcquireWrite(this);
        _ctx.Dispatcher.Invoke(() => session.ApplyUngrouped(d =>
        {
            if (d.History.CanRedo)
            {
                d.Redo();
            }
        }));
    });

    public void BeginUndoGroup(string name)
    {
        WriteSession session = _ctx.AcquireWrite(this);
        _ctx.Dispatcher.Invoke(() => session.BeginExplicit(string.IsNullOrWhiteSpace(name) ? _ctx.Environment.Localize(ScriptTexts.UndoName, _ctx.Name) : name));
    }

    public void EndUndoGroup()
    {
        WriteSession session = _ctx.AcquireWrite(this);
        _ctx.Dispatcher.Invoke(session.EndExplicit);
    }

    // ======================== 移動 ========================

    public void GoTo(OffsetArg offset)
    {
        long o = ResolveOffset(offset);
        CheckOffset(o, allowEnd: true);
        Ui(() => Host.Editor.GoTo(o));
    }

    public void ScrollTo(OffsetArg offset)
    {
        long o = ResolveOffset(offset);
        CheckOffset(o, allowEnd: true);
        Ui(() => Host.Editor.ScrollToRow(Host.Editor.Layout.RowOf(o)));
    }

    public Task<IDocument> OpenRangeAsync(RangeArg range) => ScriptErrorMap.GuardAsync(async () =>
    {
        SdkRange r = CheckedRange(range);
        IScriptDocumentHost host;
        using (_ctx.Clock.Pause())
        {
            host = await _ctx.Dispatcher.InvokeAsync(() => Host.OpenRangeAsync(r.Start, r.Length)).ConfigureAwait(false);
        }

        return (IDocument)_ctx.Wrap(host);
    });

    // ======================== テンプレート (AUTO-13) ========================

    public Task<ITemplateNode> ApplyTemplateAsync(string nameOrPath, ApplyTemplateOptions? options = null) => ScriptErrorMap.GuardAsync(async () =>
    {
        ThrowIfClosed();
        IScriptTemplateEngine engine = _ctx.Environment.Templates ?? throw new Sdk.ArgumentError(Msg.T("テンプレートの機能が使えません。", "The template feature is not available."));
        long offset = options?.Offset is OffsetArg o ? ResolveOffset(o) : 0;
        CheckOffset(offset, allowEnd: true);
        using CancellationTokenSource cts = _ctx.LinkedToken(options?.Signal);
        ITemplateResultNode root;
        using (_ctx.Clock.Pause())
        {
            root = await engine.ApplyAsync(Host, nameOrPath, offset, options?.Show ?? true, cts.Token).ConfigureAwait(false);
        }

        return (ITemplateNode)new ScriptTemplateNode(this, root);
    });

    // ======================== 一括処理の共通 ========================

    /// <summary>
    /// 処理センターに「スクリプト: 名前 - 処理名」として出して実行する (AUTO-09 の画面)。キャンセルは <c>options.signal</c> と
    /// スクリプト全体の停止。待っている間は実行時間に数えない。
    /// </summary>
    internal async Task<T> RunBulkAsync<T>(string operation, long? totalBytes, AsyncOptions? options, Func<LongRunningOperation, T> work)
    {
        ThrowIfClosed();
        using CancellationTokenSource cts = _ctx.LinkedToken(options?.Signal);
        OperationCenter center = _ctx.OperationsFor(this);
        string name = _ctx.Environment.Localize(ScriptTexts.BulkOperation, _ctx.Name, operation);
        Action<double, string?>? progress = options?.Progress;
        try
        {
            return await _ctx.Dispatcher.InvokeAsync(() => center.RunAsync(name, OperationKind.ReadOnly, Document, totalBytes, op =>
            {
                using CancellationTokenRegistration reg = cts.Token.Register(op.Cancel);
                EventHandler? onProgress = null;
                if (progress is not null)
                {
                    long last = 0;
                    onProgress = (_, _) =>
                    {
                        long now = Environment.TickCount64;
                        if (now - last >= 100 && op.Fraction is double f)
                        {
                            last = now;
                            progress(f, null);
                        }
                    };
                    op.ProgressChanged += onProgress;
                }

                try
                {
                    cts.Token.ThrowIfCancellationRequested();
                    return Task.FromResult(work(op));
                }
                finally
                {
                    if (onProgress is not null)
                    {
                        op.ProgressChanged -= onProgress;
                    }
                }
            })).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            throw new CanceledError(Msg.T("キャンセルされました。", "Canceled."), ex);
        }
    }

    /// <summary>スナップショットから読む (読めないバイトは <see cref="IoError"/>)。</summary>
    internal static void ReadExact(DocumentSnapshot snapshot, long offset, Span<byte> destination)
    {
        ReadResult result = snapshot.Read(offset, destination);
        if (!result.IsComplete)
        {
            UnreadableRange bad = result.Unreadable[0];
            throw new IoError(Msg.T($"オフセット 0x{bad.Offset:X} を読み込めません ({bad.Reason})。", $"Cannot read offset 0x{bad.Offset:X} ({bad.Reason}).")) { Offset = bad.Offset };
        }
    }

    /// <summary>パターンの引数をバイト列にする (Hex 文字列・バイト列。<c>random</c> は呼び出し側で扱う)。</summary>
    internal static byte[] PatternBytes(BytePattern pattern, bool allowRandom)
    {
        if (pattern.Bytes is byte[] b)
        {
            return b;
        }

        string text = pattern.Text ?? "";
        if (allowRandom && text.Equals("random", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return ScriptValues.ParseHex(text);
    }

    internal static bool IsRandom(BytePattern pattern) => pattern.Bytes is null && "random".Equals(pattern.Text?.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static ulong NewSeed() => BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));

    public override string ToString() => $"Document({Name})";
}
