using HexEditor.Core.Templates.Syntax;

namespace HexEditor.Core.Templates.Runtime;

/// <summary>アドレス空間 (ドキュメントまたはセクション)。読み込みは小さなブロックのキャッシュを通す。</summary>
internal sealed class Space
{
    private const int BlockSize = 64 * 1024;
    private readonly byte[] _block = new byte[BlockSize];
    private long _blockStart = -1;
    private int _blockLength;

    public Space(TemplateDataSource document)
    {
        Document = document;
    }

    public Space(TemplateSection section)
    {
        Section = section;
    }

    public TemplateDataSource? Document { get; }

    public TemplateSection? Section { get; }

    public long Length => Section?.Length ?? Document!.Length;

    public string? Name => Section?.Name;

    /// <summary>読む。範囲外・読めないバイトは <see cref="TemplateError"/> にする。</summary>
    public void Read(long offset, Span<byte> destination, Evaluator ev, string what)
    {
        if (offset < 0 || destination.Length > Length || offset > Length - destination.Length)
        {
            throw ev.OutOfRange(what, offset);
        }

        if (destination.Length > BlockSize / 2)
        {
            ReadDirect(offset, destination, ev, what);
            return;
        }

        int done = 0;
        while (done < destination.Length)
        {
            long pos = offset + done;
            if (_blockStart < 0 || pos < _blockStart || pos >= _blockStart + _blockLength)
            {
                LoadBlock(pos - (pos % BlockSize), ev, what);
            }

            int at = (int)(pos - _blockStart);
            int n = Math.Min(destination.Length - done, _blockLength - at);
            if (n <= 0)
            {
                throw ev.OutOfRange(what, offset);
            }

            _block.AsSpan(at, n).CopyTo(destination[done..]);
            done += n;
        }
    }

    private void LoadBlock(long start, Evaluator ev, string what)
    {
        ev.CheckCancel();
        int want = (int)Math.Min(BlockSize, Length - start);
        int n = ReadRaw(start, _block.AsSpan(0, want), out long bad);
        _blockStart = start;
        _blockLength = n;
        if (bad >= 0)
        {
            _blockStart = -1;
            throw ev.Unreadable(bad);
        }
    }

    public void ReadDirect(long offset, Span<byte> destination, Evaluator ev, string what)
    {
        ev.CheckCancel();
        int n = ReadRaw(offset, destination, out long bad);
        if (bad >= 0)
        {
            throw ev.Unreadable(bad);
        }

        if (n < destination.Length)
        {
            throw ev.OutOfRange(what, offset);
        }
    }

    private int ReadRaw(long offset, Span<byte> destination, out long bad)
    {
        if (Section is not null)
        {
            bad = -1;
            return Section.Read(offset, destination);
        }

        return Document!.Read(offset, destination, out bad);
    }

    public void Invalidate() => _blockStart = -1;
}

/// <summary>C 風のビットフィールドのまとまり (<c>u16 a : 4, b : 12;</c>)。</summary>
internal sealed class BitGroup
{
    public required long Start { get; init; }

    public required PrimType Base { get; init; }

    public required ulong Value { get; init; }

    public required bool BigEndian { get; init; }

    public int Used { get; set; }
}

/// <summary>構造体 (または関数) の評価の状態。</summary>
internal sealed class Frame
{
    public required TemplateNode Node { get; init; }

    public Frame? Parent { get; init; }

    public required Scope Locals { get; init; }

    public Dictionary<string, List<TemplateNode>> Members { get; } = new(StringComparer.Ordinal);

    public List<TemplateNode>? Children { get; init; }

    public long Start { get; init; }

    public required Space Space { get; set; }

    public bool BigEndian { get; set; }

    public bool MsbFirst { get; set; }

    public string Encoding { get; set; } = "latin1";

    public long MaxEnd { get; set; }

    /// <summary>パターンを 1 つでも置いた (構造体のサイズの計算に使う)。</summary>
    public bool HasPattern { get; set; }

    public bool IsUnion { get; init; }

    public string[] Namespace { get; init; } = [];

    public Dictionary<string, RtType>? TypeArgs { get; init; }

    public Dictionary<string, Val>? ValueArgs { get; init; }

    /// <summary>ビット単位の読み込みの位置 (アドレス空間の先頭からのビット数。-1 はバイト境界)。</summary>
    public long BitPos { get; set; } = -1;

    public BitGroup? OpenGroup { get; set; }

    /// <summary><c>eof</c> の判定に使う範囲の終わり (.ksy の部分ストリーム)。</summary>
    public long WindowEnd { get; set; } = long.MaxValue;

    public long IoBase { get; set; }

    /// <summary><c>last</c> (until の条件の中の直前の要素、または直前に宣言したパターン)。</summary>
    public TemplateNode? Last { get; set; }

    public long ArrayIndex { get; init; } = -1;

    public int Depth { get; init; }

    public bool IsFunction { get; init; }

    public StructDecl? Decl { get; init; }

    /// <summary>部分評価の打ち切りの条件 (<c>[[size(式)]]</c> を中身を評価せずに求める。TPL-07 の仕様 5)。</summary>
    public Func<bool>? StopWhen { get; set; }
}
