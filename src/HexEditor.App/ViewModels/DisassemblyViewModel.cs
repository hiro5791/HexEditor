using System.Globalization;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using HexEditor.App.Services;
using HexEditor.Core.Disassembly;
using HexEditor.Core.Disassembly.Images;
using HexEditor.Core.Disassembly.Msil;
using HexEditor.Core.Engine;

namespace HexEditor.App.ViewModels;

/// <summary>分岐の方向 (アドレス列の左の矢印。ANA-25 の仕様 5)。</summary>
public enum BranchDirection
{
    None,
    Forward,
    Backward,
}

/// <summary>
/// リスティングの表示の 1 行 (ネイティブの命令 ANA-23 と MSIL ANA-27 の共通)。<see cref="Offset"/> と <see cref="Length"/> はファイル上の範囲。
/// </summary>
public sealed record DisasmRow(
    ListingLineKind Kind,
    long Offset,
    int Length,
    string Address,
    string Bytes,
    string Mnemonic,
    string Operands,
    string Comment,
    string? Label,
    long? TargetOffset,
    BranchDirection Direction,
    bool IsInvalid,
    ListingLine? Native = null,
    MsilLine? Msil = null)
{
    /// <summary>行のテキスト (コピー。列をタブで区切る。ANA-23 の仕様 8)。</summary>
    public string TabSeparated => Kind == ListingLineKind.Heading
        ? Label + ":"
        : string.Join('\t', Address, Bytes, Mnemonic, Operands, Comment).TrimEnd('\t');

    /// <summary>命令の文字列 (ニーモニックとオペランド)。</summary>
    public string Instruction => Operands.Length == 0 ? Mnemonic : Mnemonic + " " + Operands;
}

/// <summary>セクションの一覧の行 (ANA-24 の仕様 3)。</summary>
public sealed record DisasmSectionRow(ImageSection Section, string Name, string Address, string VirtualSize, string FileOffset, string FileSize, string Flags);

/// <summary>シンボルの一覧の行 (ANA-24 の仕様 7)。</summary>
public sealed record DisasmSymbolRow(ImageSymbol Symbol, string Name, string Address, string Kind);

/// <summary>
/// 逆アセンブルパネルの状態 (ANA-23〜ANA-27)。ウィンドウごとに 1 つで、ドキュメントごとの設定 (アーキテクチャ・エンディアン・ベースアドレス・
/// 表示位置・選んだ行) を覚える。パネルの中身 (ドッキング・浮動パネルの間を移るたびに作り直す) はこれを共有する。
/// 表示する行は表示範囲だけを Core のリスティング (<see cref="DisassemblyListing"/>) から作る (仮想化)。
/// </summary>
public sealed partial class DisassemblyViewModel : ObservableObject
{
    /// <summary>ドキュメントごとの状態。</summary>
    private sealed class DocState
    {
        public ExecutableImage Image = ExecutableImage.Empty;
        public Task? ImageTask;
        public string ArchitectureId = DisasmArchitectures.Default;
        public bool ArchitectureChosen;
        public bool? BigEndian;
        public ulong? BaseAddress;
        public ImageSlice? Slice;
        public long Top;
        public int TopSkip;
        public long? SelectedOffset;
        public string? SelectedLabel;
        public bool Positioned;
        public MsilModule? Module;
        public MsilMethod? Method;
        public IReadOnlyList<MsilLine> MsilLines = [];
        public int MsilTop;
        public CancellationTokenSource? LabelScan;
        public DisassemblyListing? Listing;
    }

    private readonly ConditionalWeakTable<Document, DocState> _states = [];
    private DocState? _state;
    private DisasmFormat _format;
    private IReadOnlyList<DisasmRow> _rows = [];
    private int _visibleRows = 30;
    private readonly Dictionary<Document, EventHandler<DocumentChangedEventArgs>> _watched = [];
    private readonly Dictionary<Document, EventHandler> _loadedWatched = [];
    private bool _refreshQueued;

    public DisassemblyViewModel(DisasmFormat format) => _format = format;

    /// <summary>行を作り直した (描き直す)。</summary>
    public event EventHandler? RowsChanged;

    /// <summary>実行形式の解釈が終わった (セクション・シンボルの一覧を作り直す)。</summary>
    public event EventHandler? ImageChanged;

    /// <summary>行を選んだ (Hex ビューの選択を合わせる。ANA-23 の仕様 6)。</summary>
    public event EventHandler<DisasmRow>? RowActivated;

    public DocumentViewModel? Document { get; private set; }

    public DisasmFormat Format => _format;

    public ExecutableImage Image => _state?.Image ?? ExecutableImage.Empty;

    public DisassemblyListing? Listing => _state is { } s ? ListingOf(s) : null;

    public string ArchitectureId => _state?.ArchitectureId ?? DisasmArchitectures.Default;

    public DisasmArchitecture Architecture => DisasmArchitectures.Find(ArchitectureId) ?? DisasmArchitectures.Get(DisasmArchitectures.Default);

    /// <summary>MSIL の表示か (ANA-27)。</summary>
    public bool IsMsil => ArchitectureId == DisasmArchitectures.Msil;

    public bool? BigEndian => _state?.BigEndian;

    public ulong? BaseAddress => _state?.BaseAddress;

    public ImageSlice? Slice => _state?.Slice;

    public MsilModule? Msil => _state?.Module;

    public MsilMethod? CurrentMethod => _state?.Method;

    public IReadOnlyList<DisasmRow> Rows => _rows;

    /// <summary>選んだ行 (表示範囲の外なら null)。</summary>
    public DisasmRow? SelectedRow => _state is { SelectedOffset: { } o } s
        ? _rows.FirstOrDefault(r => r.Offset == o && r.Label == s.SelectedLabel) ?? _rows.FirstOrDefault(r => r.Offset == o && r.Kind == ListingLineKind.Instruction)
        : null;

    public long? SelectedOffset => _state?.SelectedOffset;

    /// <summary>カーソルに追従する (ANA-23 の仕様 6。既定でオン)。</summary>
    [ObservableProperty]
    public partial bool FollowCursor { get; set; } = true;

    /// <summary>表示範囲の半分以上が db (「このアーキテクチャのコードではない可能性があります」)。</summary>
    public bool MostlyData { get; private set; }

    /// <summary>逆アセンブルエンジンを読み込めない理由 (読み込めれば null)。</summary>
    public string? EngineError { get; private set; }

    /// <summary>リスティングの行の数 (パネルの高さから決まる)。</summary>
    public int VisibleRowCount
    {
        get => _visibleRows;
        set
        {
            int v = Math.Clamp(value, 1, 400);
            if (v != _visibleRows)
            {
                _visibleRows = v;
                Refresh();
            }
        }
    }

    // ---- ドキュメント ----

    public void Attach(DocumentViewModel? doc)
    {
        Document = doc;
        _state = doc is null ? null : _states.GetValue(doc.Document, _ => new DocState());
        if (doc is not null && !_watched.ContainsKey(doc.Document))
        {
            Document d = doc.Document;
            EventHandler<DocumentChangedEventArgs> handler = (_, e) => DocumentChanged(d, e);
            _watched[d] = handler;
            d.Changed += handler;

            // リモートのデータソース: リスティングは読み込みを待たずに作るので、読み込みが届いたら作り直す (UI スレッドで待たない)。
            Microsoft.UI.Dispatching.DispatcherQueue? queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            EventHandler loaded = (_, _) => DataLoaded(d, queue);
            _loadedWatched[d] = loaded;
            d.DataLoaded += loaded;
        }

        if (_state is { ImageTask: null } s && doc is not null)
        {
            StartImage(doc.Document, s);
        }

        Refresh();
        ImageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>実行形式の解釈をバックグラウンドで始める (ANA-24 の「巨大ファイル」)。終わるまではシンボルなしで表示する。</summary>
    private void StartImage(Document document, DocState s, ImageSlice? slice = null)
    {
        DocumentSnapshot snapshot = document.Current;
        Microsoft.UI.Dispatching.DispatcherQueue? queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        s.ImageTask = Task.Run(() => ExecutableParser.Parse(snapshot, slice)).ContinueWith(t =>
        {
            void Apply()
            {
                if (t.Status != TaskStatus.RanToCompletion)
                {
                    return;
                }

                s.Image = t.Result;
                if (!s.ArchitectureChosen && s.Image.ArchitectureId is { } arch)
                {
                    s.ArchitectureId = arch;
                }

                ResetListing(s);
                if (Document?.Document == document)
                {
                    if (!s.Positioned && s.Image.EntryPoint is { } entry && s.Image.ToOffset(entry) is { } offset)
                    {
                        s.Top = offset;
                        s.TopSkip = 0;
                    }

                    Refresh();
                    ImageChanged?.Invoke(this, EventArgs.Empty);
                }
            }

            if (queue is null || !queue.TryEnqueue(Apply))
            {
                Apply();
            }
        }, TaskScheduler.Default);
    }

    /// <summary>ウィンドウを閉じたときに、ドキュメントの変更の通知を外す (ドキュメントはほかのウィンドウに移っていることがある)。</summary>
    public void Detach()
    {
        foreach ((Document d, EventHandler<DocumentChangedEventArgs> handler) in _watched)
        {
            d.Changed -= handler;
        }

        _watched.Clear();
        foreach ((Document d, EventHandler handler) in _loadedWatched)
        {
            d.DataLoaded -= handler;
        }

        _loadedWatched.Clear();
        foreach (DocState s in _states.Select(p => p.Value))
        {
            ResetListing(s);
        }

        Document = null;
        _state = null;
    }

    /// <summary>実行形式の解釈を待つ (テスト用の命令とコマンドの前)。</summary>
    public Task WhenImageReadyAsync() => _state?.ImageTask ?? Task.CompletedTask;

    private void DocumentChanged(Document document, DocumentChangedEventArgs e)
    {
        if (!_states.TryGetValue(document, out DocState? s))
        {
            return;
        }

        s.Listing?.Invalidate(e.Offset);
        if (s.Module is not null)
        {
            // MSIL は編集した内容で読み直す。
            s.Module.Dispose();
            s.Module = null;
            s.MsilLines = [];
        }

        if (Document?.Document == document)
        {
            Refresh();
        }
    }

    /// <summary>読み込みが届いた (任意のスレッド): 表示中のリスティングが読み込み待ちで止まっていたら、UI スレッドで作り直す。</summary>
    private void DataLoaded(Document document, Microsoft.UI.Dispatching.DispatcherQueue? queue)
    {
        if (queue is null || !_states.TryGetValue(document, out DocState? s) || s.Listing is not { HasPendingReads: true } || _refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        if (!queue.TryEnqueue(() =>
        {
            _refreshQueued = false;
            if (Document?.Document == document)
            {
                Refresh();
            }
        }))
        {
            _refreshQueued = false;
        }
    }

    /// <summary>
    /// UI スレッドで少しのバイトを読む: リモートのデータソースでは読み込みを待たず、キャッシュにある分だけ (読み込み中なら読めた長さまで)。
    /// </summary>
    internal static int ReadForUi(Document document, long offset, Span<byte> destination)
    {
        if (document.Source is not Core.Sources.IRemoteByteSource)
        {
            return document.Current.Read(offset, destination).BytesReturned;
        }

        Span<ByteState> states = destination.Length <= 256 ? stackalloc ByteState[destination.Length] : new ByteState[destination.Length];
        int n = document.Current.ReadForDisplay(offset, destination, states);
        int loading = states[..n].IndexOf(ByteState.Loading);
        return loading >= 0 ? loading : n;
    }

    private DisassemblyListing ListingOf(DocState s)
    {
        if (s.Listing is not null && s.Listing.Format != _format)
        {
            ResetListing(s);
        }

        if (s.Listing is null)
        {
            Document doc = Document!.Document;
            s.Listing = new DisassemblyListing(() => doc.Current, s.Image, new DisasmViewOptions
            {
                ArchitectureId = s.ArchitectureId == DisasmArchitectures.Msil ? DisasmArchitectures.Default : s.ArchitectureId,
                BigEndian = s.BigEndian,
                BaseAddress = s.BaseAddress,
                Format = _format,
            })
            {
                NonBlocking = doc.Source is Core.Sources.IRemoteByteSource,
            };
            if (s.Image.IsExecutable)
            {
                // ラベルの自動生成のため、実行可能なセクションを先に解釈しておく (ANA-25 の仕様 6)。
                s.LabelScan?.Cancel();
                var cts = new CancellationTokenSource();
                s.LabelScan = cts;
                DisassemblyListing listing = s.Listing;
                Microsoft.UI.Dispatching.DispatcherQueue? queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                _ = Task.Run(() => listing.ScanLabels(cts.Token), cts.Token).ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion)
                    {
                        LabelsScanned = true;
                        queue?.TryEnqueue(() =>
                        {
                            if (ReferenceEquals(s.Listing, listing))
                            {
                                Refresh();
                            }
                        });
                    }
                }, TaskScheduler.Default);
            }
        }

        return s.Listing;
    }

    /// <summary>ラベルの先読みが終わった (テスト用)。</summary>
    public bool LabelsScanned { get; private set; }

    private static void ResetListing(DocState s)
    {
        s.LabelScan?.Cancel();
        s.Listing?.Dispose();
        s.Listing = null;
    }

    // ---- 設定 ----

    public void SetFormat(DisasmFormat format)
    {
        if (format == _format)
        {
            return;
        }

        // 表示設定はアプリ全体で共有する (ANA-26 の仕様 8)。ほかのドキュメントのリスティングは、そのドキュメントに切り替えたときに作り直す。
        _format = format;
        if (_state is not null)
        {
            ResetListing(_state);
            _state.MsilLines = [];
        }

        Refresh();
    }

    public void SetArchitecture(string id)
    {
        if (_state is not { } s || s.ArchitectureId == id || DisasmArchitectures.Find(id) is null && id != DisasmArchitectures.Msil)
        {
            return;
        }

        s.ArchitectureId = id;
        s.ArchitectureChosen = true;
        ResetListing(s);
        Refresh();
        ImageChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetEndian(bool? big)
    {
        if (_state is not { } s || s.BigEndian == big)
        {
            return;
        }

        s.BigEndian = big;
        ResetListing(s);
        Refresh();
    }

    public void SetBaseAddress(ulong? address)
    {
        if (_state is not { } s || s.BaseAddress == address)
        {
            return;
        }

        s.BaseAddress = address;
        ResetListing(s);
        Refresh();
    }

    /// <summary>ユニバーサルバイナリのスライスを選ぶ (ANA-24 の仕様 1)。解釈し直して、そのアーキテクチャにする。</summary>
    public async Task SetSliceAsync(ImageSlice slice)
    {
        if (_state is not { } s || Document is not { } doc)
        {
            return;
        }

        s.Slice = slice;
        s.ArchitectureChosen = false;
        s.Positioned = false;
        StartImage(doc.Document, s, slice);
        await (s.ImageTask ?? Task.CompletedTask);
    }

    // ---- 行 ----

    /// <summary>表示する行を作り直す。</summary>
    public void Refresh()
    {
        if (_state is not { } s || Document is null)
        {
            _rows = [];
            MostlyData = false;
            EngineError = null;
            RowsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (IsMsil)
        {
            _rows = MsilRows(s);
            MostlyData = false;
            EngineError = null;
        }
        else
        {
            DisassemblyListing listing = ListingOf(s);
            EngineError = listing.EngineError;
            IReadOnlyList<ListingLine> lines = listing.Lines(s.Top, _visibleRows, s.TopSkip);
            _rows = [.. lines.Select(l => RowOf(listing, l))];
            MostlyData = DisassemblyListing.MostlyData(lines);
        }

        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private DisasmRow RowOf(DisassemblyListing listing, ListingLine l)
    {
        if (l.Kind == ListingLineKind.Heading)
        {
            return new DisasmRow(l.Kind, l.Offset, l.Length, listing.AddressText(l), string.Empty, string.Empty, string.Empty, string.Empty, l.Label,
                null, BranchDirection.None, false, l);
        }

        DisasmInstruction i = l.Instruction!;
        BranchDirection direction = i.IsBranch && i.BranchTarget is { } t ? t > i.Address ? BranchDirection.Forward : BranchDirection.Backward : BranchDirection.None;
        return new DisasmRow(l.Kind, l.Offset, l.Length, listing.AddressText(l), _format.ShowBytes ? l.Bytes : string.Empty, i.Mnemonic, i.Operands,
            _format.ShowComments ? l.Comment : string.Empty, null, l.TargetOffset, direction, i.IsInvalid, l);
    }

    // ---- スクロール (スクロールバーはバイトのオフセットに比例。ANA-23 の「巨大ファイル」) ----

    public double ScrollMaximum => IsMsil ? Math.Max(0, (_state?.MsilLines.Count ?? 0) - 1) : Math.Max(0, (Document?.Document.Length ?? 1) - 1);

    public double ScrollValue => IsMsil ? _state?.MsilTop ?? 0 : _state?.Top ?? 0;

    public void ScrollTo(double value)
    {
        if (_state is not { } s || Document is null)
        {
            return;
        }

        if (IsMsil)
        {
            s.MsilTop = (int)Math.Clamp(value, 0, Math.Max(0, s.MsilLines.Count - 1));
        }
        else
        {
            s.Top = ListingOf(s).InstructionStart((long)Math.Clamp(value, 0, ScrollMaximum));
            s.TopSkip = 0;
        }

        s.Positioned = true;
        Refresh();
    }

    /// <summary>行単位でスクロールする (負なら上へ)。</summary>
    public void ScrollLines(int delta)
    {
        if (_state is not { } s || Document is null || delta == 0)
        {
            return;
        }

        s.Positioned = true;
        if (IsMsil)
        {
            s.MsilTop = Math.Clamp(s.MsilTop + delta, 0, Math.Max(0, s.MsilLines.Count - 1));
            Refresh();
            return;
        }

        DisassemblyListing listing = ListingOf(s);
        for (int n = 0; n < Math.Abs(delta); n++)
        {
            if (delta > 0)
            {
                IReadOnlyList<ListingLine> two = listing.Lines(s.Top, 2, s.TopSkip);
                if (two.Count < 2)
                {
                    break;
                }

                s.TopSkip = two[1].Offset == two[0].Offset ? s.TopSkip + 1 : 0;
                s.Top = two[1].Offset;
            }
            else if (s.TopSkip > 0)
            {
                s.TopSkip--;
            }
            else if (listing.PreviousInstruction(s.Top) is { } previous)
            {
                s.Top = previous;
                s.TopSkip = listing.HeadingCount(previous);
            }
            else
            {
                break;
            }
        }

        Refresh();
    }

    /// <summary>
    /// <paramref name="offset"/> を含む命令の行を選び、表示範囲の外なら上から 1/3 の位置に出す。<paramref name="activate"/> なら Hex ビューの選択も合わせる。
    /// </summary>
    public void GoToOffset(long offset, bool activate = false)
    {
        if (_state is not { } s || Document is null)
        {
            return;
        }

        s.Positioned = true;
        if (IsMsil)
        {
            int index = s.MsilLines.ToList().FindIndex(l => l.Offset <= offset && offset < l.Offset + Math.Max(1, l.Length) && l.Kind == MsilLineKind.Instruction);
            if (index >= 0)
            {
                if (index < s.MsilTop || index >= s.MsilTop + _visibleRows)
                {
                    s.MsilTop = Math.Max(0, index - _visibleRows / 3);
                }

                s.SelectedOffset = s.MsilLines[index].Offset;
                s.SelectedLabel = null;
                Refresh();
                if (activate && SelectedRow is { } row)
                {
                    RowActivated?.Invoke(this, row);
                }
            }

            return;
        }

        DisassemblyListing listing = ListingOf(s);
        long start = listing.InstructionStart(offset);
        s.SelectedOffset = start;
        s.SelectedLabel = null;
        if (!_rows.Any(r => r.Offset == start && r.Kind == ListingLineKind.Instruction)
            || _rows.Count > 0 && _rows[^1].Offset == start && _rows.Count >= _visibleRows)
        {
            s.Top = start;
            s.TopSkip = 0;
            Refresh();
            ScrollLines(-(_visibleRows / 3));
        }
        else
        {
            Refresh();
        }

        if (activate && SelectedRow is { } selected)
        {
            RowActivated?.Invoke(this, selected);
        }
    }

    /// <summary>表示範囲の行を選ぶ (クリック・キー)。Hex ビューの選択を合わせる。</summary>
    public void Select(DisasmRow row, bool activate = true)
    {
        if (_state is not { } s)
        {
            return;
        }

        s.SelectedOffset = row.Offset;
        s.SelectedLabel = row.Label;
        RowsChanged?.Invoke(this, EventArgs.Empty);
        if (activate)
        {
            RowActivated?.Invoke(this, row);
        }
    }

    /// <summary>選んだ行を上下に動かす (↑ / ↓ / PageUp / PageDown)。</summary>
    public void MoveSelection(int delta)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        int index = SelectedRow is { } r ? IndexOf(r) : -1;
        int target = index < 0 ? 0 : index + delta;
        if (target < 0)
        {
            ScrollLines(target);
            target = 0;
        }
        else if (target >= _rows.Count - 1 && _rows.Count >= _visibleRows)
        {
            int over = target - (_rows.Count - 2);
            ScrollLines(over);
            target = Math.Min(_rows.Count - 2, Math.Max(0, target - over));
        }

        target = Math.Clamp(target, 0, _rows.Count - 1);
        Select(_rows[target]);
    }

    private int IndexOf(DisasmRow row)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (ReferenceEquals(_rows[i], row) || _rows[i] == row)
            {
                return i;
            }
        }

        return -1;
    }

    // ---- 一覧 (ANA-24 の仕様 3・7) ----

    public IReadOnlyList<DisasmSectionRow> Sections => [.. Image.Sections.Select(s => new DisasmSectionRow(s, s.Name,
        Hex(s.VirtualAddress), Hex(s.VirtualSize), Hex((ulong)s.FileOffset), Hex((ulong)s.FileSize),
        (s.Read ? "R" : "-") + (s.Write ? "W" : "-") + (s.Execute ? "X" : "-")))];

    /// <summary>シンボルの一覧 (名前で絞り込む)。100 万件を超えても全件を持つ (一覧は仮想化して表示する)。</summary>
    public IReadOnlyList<DisasmSymbolRow> SymbolsMatching(string filter)
    {
        IEnumerable<ImageSymbol> symbols = Image.Symbols;
        if (filter.Length > 0)
        {
            symbols = symbols.Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || s.RawName.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        return [.. symbols.OrderBy(s => s.Address).Select(s => new DisasmSymbolRow(s, s.Name, Hex(s.Address), Loc.Get("Disasm_SymbolKind_" + s.Kind)))];
    }

    private string Hex(ulong value) => _format.Hex(value);

    // ---- MSIL (ANA-27) ----

    /// <summary>MSIL のモジュールを開く (メタデータの読み込みはバックグラウンド。ANA-27 の「巨大ファイル」)。</summary>
    public async Task<MsilModule?> OpenMsilAsync()
    {
        if (_state is not { } s || Document is not { } doc)
        {
            return null;
        }

        await WhenImageReadyAsync();
        if (s.Module is null && s.Image.IsDotNet)
        {
            DocumentSnapshot snapshot = doc.Document.Current;
            ExecutableImage image = s.Image;
            s.Module = await Task.Run(() => MsilModule.TryOpen(snapshot, image));
        }

        return s.Module;
    }

    public async Task SelectMethodAsync(MsilMethod method)
    {
        if (_state is not { } s || await OpenMsilAsync() is not { } module)
        {
            return;
        }

        s.Method = method;
        s.MsilLines = module.Disassemble(method);
        s.MsilTop = 0;
        s.SelectedOffset = s.MsilLines.FirstOrDefault(l => l.Kind == MsilLineKind.Instruction)?.Offset;
        s.SelectedLabel = null;
        Refresh();
        OnPropertyChanged(nameof(CurrentMethod));
    }

    private IReadOnlyList<DisasmRow> MsilRows(DocState s)
    {
        if (s.Method is { } method && s.MsilLines.Count == 0 && s.Module is { } module)
        {
            s.MsilLines = module.Disassemble(method);
        }

        var rows = new List<DisasmRow>(_visibleRows);
        int end = Math.Min(s.MsilLines.Count, s.MsilTop + _visibleRows);
        for (int i = s.MsilTop; i < end; i++)
        {
            MsilLine l = s.MsilLines[i];
            long? target = l.BranchTarget is { } il ? s.MsilLines.FirstOrDefault(x => x.Kind == MsilLineKind.Instruction && x.IlOffset == il)?.Offset : null;
            BranchDirection direction = l.BranchTarget is { } b ? b > l.IlOffset ? BranchDirection.Forward : BranchDirection.Backward : BranchDirection.None;
            rows.Add(l.Kind == MsilLineKind.Info
                ? new DisasmRow(ListingLineKind.Heading, l.Offset, l.Length, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, l.Mnemonic,
                    null, BranchDirection.None, false, null, l)
                : new DisasmRow(ListingLineKind.Instruction, l.Offset, l.Length, "IL_" + l.IlOffset.ToString("x4", CultureInfo.InvariantCulture),
                    _format.ShowBytes ? BytesOf(l) : string.Empty, l.Mnemonic, l.Operand, string.Empty, null, target, direction, false, null, l));
        }

        return rows;
    }

    private string BytesOf(MsilLine line)
    {
        if (Document is not { } doc)
        {
            return string.Empty;
        }

        int n = Math.Min(line.Length, _format.MaxBytes);
        byte[] b = new byte[n];
        int read = ReadForUi(doc.Document, line.Offset, b);
        string text = string.Join(' ', b.Take(read).Select(x => x.ToString(_format.UppercaseHex ? "X2" : "x2", CultureInfo.InvariantCulture)));
        return line.Length > _format.MaxBytes ? text + " …" : text;
    }

    /// <summary>MSIL の分岐先の行 (同じメソッドの中)。</summary>
    public MsilLine? MsilLineAt(int ilOffset) =>
        _state?.MsilLines.FirstOrDefault(l => l.Kind == MsilLineKind.Instruction && l.IlOffset == ilOffset);
}
