using HexEditor.Core.Forensics.Audit;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Forensics.Carving;

/// <summary>切り出す長さをどう決めたか (FOR-19 の仕様 3。マニフェストの「長さの決め方」)。</summary>
public enum LengthMethod
{
    /// <summary>構造の検証 (構造をたどった終端)。</summary>
    Structure,

    /// <summary>サイズの取得 (ヘッダー内の値)。</summary>
    SizeField,

    /// <summary>フッターが見つかった。</summary>
    Footer,

    /// <summary>最大サイズ (または対象範囲の終わり) で切った。</summary>
    MaxSize,
}

/// <summary>見つかったファイル 1 件。</summary>
public sealed record CarvedFile(long Number, CarvingSignature Signature, long Offset, long Length, LengthMethod Method, long SectorSize, bool Exported)
{
    public long End => Offset + Length;

    /// <summary>開始 LBA (開始位置を含むセクタ)。</summary>
    public long Lba => SectorSize > 0 ? Offset / SectorSize : 0;

    /// <summary>書き出し先の相対パス (<c>jpeg/0000000000100000.jpg</c>)。区切りは <c>/</c>。</summary>
    public string RelativePath => $"{Signature.Folder}/{Offset:X16}.{Signature.Extension.TrimStart('.')}";

    public string Type => Signature.Name;
}

/// <summary>
/// 結果の一覧。1 件 40 バイト足らずの構造体を 64Ki 件ずつのブロックに入れて持つので、結果が 100 万件を超えても使えるメモリは小さい
/// (FOR-19 の「巨大ファイル・長時間処理」)。追加は 1 つのスレッドから、読み出しは任意のスレッドから行う。
/// </summary>
public sealed class CarvedFileStore
{
    private const int BlockBits = 16;
    private const int BlockSize = 1 << BlockBits;

    private struct Entry
    {
        public long Offset;
        public long Length;
        public int Signature;
        public byte Method;
        public bool Exported;
    }

    private readonly object _gate = new();
    private readonly List<Entry[]> _blocks = [];
    private readonly IReadOnlyList<CarvingSignature> _signatures;
    private readonly long _sectorSize;
    private long _count;

    public CarvedFileStore(IReadOnlyList<CarvingSignature> signatures, long sectorSize)
    {
        _signatures = signatures;
        _sectorSize = sectorSize;
    }

    public long Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>件数が変わった。</summary>
    public event EventHandler? Changed;

    /// <summary>番号 (1 から) を付けて加える。</summary>
    internal long Add(int signature, long offset, long length, LengthMethod method)
    {
        long number;
        lock (_gate)
        {
            long index = _count;
            if ((index >> BlockBits) >= _blocks.Count)
            {
                _blocks.Add(new Entry[BlockSize]);
            }

            _blocks[(int)(index >> BlockBits)][index & (BlockSize - 1)] = new Entry { Offset = offset, Length = length, Signature = signature, Method = (byte)method };
            _count++;
            number = _count;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return number;
    }

    internal void MarkExported(long number)
    {
        lock (_gate)
        {
            long index = number - 1;
            if (index >= 0 && index < _count)
            {
                _blocks[(int)(index >> BlockBits)][index & (BlockSize - 1)].Exported = true;
            }
        }
    }

    /// <summary>0 から数えた位置の 1 件。</summary>
    public CarvedFile this[long index]
    {
        get
        {
            lock (_gate)
            {
                if (index < 0 || index >= _count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                Entry e = _blocks[(int)(index >> BlockBits)][index & (BlockSize - 1)];
                return new CarvedFile(index + 1, _signatures[e.Signature], e.Offset, e.Length, (LengthMethod)e.Method, _sectorSize, e.Exported);
            }
        }
    }

    /// <summary>全件を順に。</summary>
    public IEnumerable<CarvedFile> Enumerate()
    {
        long count = Count;
        for (long i = 0; i < count; i++)
        {
            yield return this[i];
        }
    }
}

/// <summary>書き出し先の問題への対応 (FOR-19 の「エラー」: 空き容量が不足した)。</summary>
public enum OutputDecisionKind
{
    /// <summary>もう一度試す (容量を空けた後など)。</summary>
    Retry,

    /// <summary>書き出し先を変える。</summary>
    ChangeFolder,

    /// <summary>中止する。</summary>
    Abort,
}

public sealed record OutputDecision(OutputDecisionKind Kind, string? NewFolder = null);

/// <summary>書き出し先の問題の説明。</summary>
public sealed record OutputProblemInfo(OutputProblem Kind, string Folder, long RequiredBytes, long FreeBytes, string Message);

/// <summary>カービングの進捗。</summary>
public sealed record CarvingProgress(long BytesScanned, long TotalBytes, long FilesFound, long FilesExported, IReadOnlyDictionary<string, long> CountsByType, long CurrentOffset);

/// <summary>カービングの実行の設定 (FOR-19 の仕様 1)。</summary>
public sealed record CarvingOptions
{
    /// <summary>使う定義 (有効かどうかは呼び出し側で選んでおく)。</summary>
    public required IReadOnlyList<CarvingSignature> Signatures { get; init; }

    /// <summary>対象範囲 (ドキュメント全体・選択範囲・未割り当て領域・スラック)。</summary>
    public required IReadOnlyList<SearchRange> Ranges { get; init; }

    /// <summary>見つけたファイルの内側をスキップする (既定オフ)。</summary>
    public bool SkipInside { get; init; }

    /// <summary>書き出し先のフォルダ。null なら書き出さず、マニフェストも作らない。</summary>
    public string? OutputFolder { get; init; }

    /// <summary>ファイルを書き出す (オフなら一覧とマニフェストだけ)。</summary>
    public bool Export { get; init; } = true;

    public int SectorSize { get; init; } = 512;

    /// <summary>走査の 1 チャンクの大きさ。</summary>
    public int ChunkSize { get; init; } = 4 * 1024 * 1024;

    /// <summary>先読みして並列に走査するチャンクの数。</summary>
    public int Parallelism { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    public IStructureResolver Resolver { get; init; } = NativeStructureResolver.Instance;

    /// <summary>空き容量の不足など書き出し先の問題が起きたときに呼ぶ (走査はその間止まる)。null なら中止する。</summary>
    public Func<OutputProblemInfo, CancellationToken, Task<OutputDecision>>? OnOutputProblem { get; init; }

    /// <summary>書き出し先の空き容量の取得 (テストで差し替える)。</summary>
    public Func<string, long?>? FreeSpace { get; init; }

    public IProgress<CarvingProgress>? Progress { get; init; }

    /// <summary>監査ログ (開始・終了、件数、マニフェストのハッシュを記録する。FOR-19 の仕様 7)。</summary>
    public ForensicAudit? Audit { get; init; }

    public string? EvidenceId { get; init; }

    public string? SourcePath { get; init; }

    /// <summary>書き出しの 1 ファイルごとに続ける前の空きを残す量。</summary>
    public long FreeSpaceReserve { get; init; } = 16L * 1024 * 1024;
}

/// <summary>カービングの結果。</summary>
public sealed class CarvingResult
{
    public required CarvedFileStore Files { get; init; }

    /// <summary>読めなかった範囲 (そのチャンクは飛ばした。マニフェストの末尾にも記録する)。</summary>
    public required IReadOnlyList<UnreadableRange> Unreadable { get; init; }

    public bool Cancelled { get; init; }

    /// <summary>書き出し先の問題で中止した。</summary>
    public bool AbortedByOutputProblem { get; init; }

    public long BytesScanned { get; init; }

    public long TotalBytes { get; init; }

    public TimeSpan Elapsed { get; init; }

    public string? ManifestPath { get; init; }

    /// <summary>マニフェストの SHA-256 (小文字の 16 進)。</summary>
    public string? ManifestSha256 { get; init; }

    public required IReadOnlyDictionary<string, long> CountsByType { get; init; }

    public string? OutputFolder { get; init; }
}
