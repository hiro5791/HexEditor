using HexEditor.Core.Engine;
using HexEditor.Core.Operations;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Editing.Transforms;

/// <summary>ビットの挿入・削除の 1 つの編集 (順に行う)。</summary>
public abstract record BitEdit;

/// <summary><see cref="Offset"/> に内容を挿入する。</summary>
public sealed record BitEditInsert(long Offset, EditContent Content) : BitEdit;

/// <summary><see cref="Offset"/> から内容で上書きする。</summary>
public sealed record BitEditOverwrite(long Offset, EditContent Content) : BitEdit;

/// <summary>[Offset, Offset + Length) を削除する。</summary>
public sealed record BitEditDelete(long Offset, long Length) : BitEdit;

/// <summary>[Offset, Offset + Length) を内容で置き換える (長さは変わってもよい)。</summary>
public sealed record BitEditReplace(long Offset, long Length, EditContent Content) : BitEdit;

/// <summary>ビットの挿入・削除で行う編集 (EDIT-37)。<see cref="Apply"/> で 1 つの編集グループとして反映する。</summary>
public sealed class BitEditPlan(IReadOnlyList<BitEdit> edits, long rewriteBytes, long newLength) : IDisposable
{
    public IReadOnlyList<BitEdit> Edits { get; } = edits;

    /// <summary>書き直すバイト数 (ピース操作だけで済む場合は 0)。</summary>
    public long RewriteBytes { get; } = rewriteBytes;

    /// <summary>反映した後のドキュメントの長さ。</summary>
    public long NewLength { get; } = newLength;

    public void Apply(Document document, string description)
    {
        using (document.BeginGroup(description))
        {
            foreach (BitEdit edit in Edits)
            {
                switch (edit)
                {
                    case BitEditInsert insert:
                        document.InsertContent(insert.Offset, insert.Content, description);
                        break;
                    case BitEditOverwrite overwrite:
                        document.OverwriteContent(overwrite.Offset, overwrite.Content, description);
                        break;
                    case BitEditDelete delete:
                        document.Delete(delete.Offset, delete.Length, description);
                        break;
                    case BitEditReplace replace when replace.Content.Length == replace.Length:
                        document.OverwriteContent(replace.Offset, replace.Content, description);
                        break;
                    case BitEditReplace replace:
                        document.Delete(replace.Offset, replace.Length, description);
                        document.InsertContent(replace.Offset, replace.Content, description);
                        break;
                }
            }
        }
    }

    /// <summary>反映しなかった内容の一時ファイルを消す。</summary>
    public void Dispose()
    {
        foreach (BitEdit edit in Edits)
        {
            switch (edit)
            {
                case BitEditInsert i:
                    i.Content.Dispose();
                    break;
                case BitEditOverwrite o:
                    o.Content.Dispose();
                    break;
                case BitEditReplace r:
                    r.Content.Dispose();
                    break;
            }
        }
    }
}

/// <summary>ビットの挿入・削除の対象 (<see cref="BitShifter.Analyze"/> の結果)。</summary>
public sealed record BitShiftInfo(DataOperationError Error, long RegionStart, long RegionEnd, long OutputLength, bool UsesPieces)
{
    /// <summary>書き直すバイト数 (確認ダイアログの判定。EDIT-37 の「巨大ファイル」)。</summary>
    public long RewriteBytes => UsesPieces || Error != DataOperationError.None ? 0 : OutputLength;
}

/// <summary>
/// ビット単位の挿入と削除 (EDIT-37)。データを 1 本のビット列とみなし、位置より後ろのビットをずらす。ビット数が 8 の倍数なら
/// ピース操作 (一定時間) で行い、そうでなければずらす範囲を 1 MiB ずつ読んで書き直す長時間処理になる。
/// </summary>
public sealed class BitShifter
{
    /// <summary>これを超えて書き直す場合は実行前に確認する (EDIT-37 の「巨大ファイル」)。</summary>
    public const long ConfirmLimit = 1L << 30;

    private readonly string _tempDirectory;
    private readonly IVolumeInfoProvider? _volumes;

    public BitShifter(string tempDirectory, IVolumeInfoProvider? volumes = null)
    {
        _tempDirectory = tempDirectory;
        _volumes = volumes;
    }

    public static BitShifter For(Document document, IVolumeInfoProvider? volumes = null) =>
        new(Path.Combine(document.Options.TempDirectory, document.Id.ToString("N")), volumes);

    /// <summary>ビット列の中の位置 (先頭からのビット数)。</summary>
    public static long LinearPosition(DataOperationSpec spec) =>
        spec.BitOffset * 8 + (spec.LsbFirst ? spec.BitIndex : 7 - spec.BitIndex);

    /// <summary>
    /// 対象を調べる。<paramref name="selection"/> は「選択範囲の中だけ」のずらす範囲 (位置を含む選択範囲)。
    /// </summary>
    public static BitShiftInfo Analyze(long documentLength, bool canResize, TargetRange? selection, DataOperationSpec spec)
    {
        DataOperationError error = spec.Validate();
        bool insert = spec.Kind == DataOperationKind.InsertBits;
        long position = LinearPosition(spec);
        long start = spec.BitOffset;
        long k = spec.BitCount;
        if (error != DataOperationError.None)
        {
            return new BitShiftInfo(error, 0, 0, 0, false);
        }

        if (spec.BitScope == BitShiftScope.ToEnd)
        {
            if (!canResize)
            {
                return new BitShiftInfo(DataOperationError.FixedLength, 0, 0, 0, false);
            }

            long totalBits = documentLength * 8;
            if (position > totalBits || (!insert && position >= totalBits))
            {
                return new BitShiftInfo(DataOperationError.PositionOutOfRange, 0, 0, 0, false);
            }

            if (!insert && position + k > totalBits)
            {
                return new BitShiftInfo(DataOperationError.BitCountOutOfRange, 0, 0, 0, false);
            }

            long newBits = insert ? totalBits + k : totalBits - k;
            long newLength = (newBits + 7) / 8;
            return new BitShiftInfo(DataOperationError.None, start, documentLength, newLength - start, k % 8 == 0);
        }

        if (selection is not { Length: > 0 } region || position < region.Offset * 8 || position >= region.End * 8)
        {
            return new BitShiftInfo(DataOperationError.PositionOutOfRange, 0, 0, 0, false);
        }

        if (!insert && position + k > region.End * 8)
        {
            return new BitShiftInfo(DataOperationError.BitCountOutOfRange, 0, 0, 0, false);
        }

        return new BitShiftInfo(DataOperationError.None, start, region.End, region.End - start, k % 8 == 0 && canResize);
    }

    /// <summary>編集を作る。ピース操作で済まない場合は書き直した内容を一時ファイルに作る (長時間処理)。</summary>
    public BitEditPlan Build(DocumentSnapshot snapshot, bool canResize, TargetRange? selection, DataOperationSpec spec,
        LongRunningOperation? operation = null)
    {
        BitShiftInfo info = Analyze(snapshot.Length, canResize, selection, spec);
        if (info.Error != DataOperationError.None)
        {
            throw new ArgumentException($"ビットの挿入・削除の設定が正しくありません: {info.Error}", nameof(spec));
        }

        long newLength = spec.BitScope == BitShiftScope.ToEnd ? info.RegionStart + info.OutputLength : snapshot.Length;
        if (info.UsesPieces)
        {
            return new BitEditPlan(PiecePlan(snapshot, info, spec), 0, newLength);
        }

        var region = new BitRegion(snapshot, info, spec);
        long length = info.OutputLength;
        operation?.SetTotal(length);
        EditContent content;
        if (length <= DataOperationRunner.InMemoryLimit)
        {
            byte[] data = new byte[length];
            region.Fill(0, data);
            content = EditContent.Bytes(data);
        }
        else
        {
            CheckSpace(length);
            var writer = new TempContentWriter(_tempDirectory, length, "bitshift");
            try
            {
                byte[] buffer = new byte[DataOperationRunner.ChunkSize];
                long done = 0;
                while (done < length)
                {
                    operation?.CancellationToken.ThrowIfCancellationRequested();
                    int n = (int)Math.Min(buffer.Length, length - done);
                    region.Fill(done, buffer.AsSpan(0, n));
                    writer.Write(buffer.AsSpan(0, n));
                    done += n;
                    operation?.Report(done);
                }

                content = writer.Complete();
            }
            finally
            {
                writer.Dispose();
            }
        }

        return new BitEditPlan([new BitEditReplace(info.RegionStart, info.RegionEnd - info.RegionStart, content)], length, newLength);
    }

    /// <summary>プレビュー (EDIT-37 の「画面」): 位置のバイトから最大 <paramref name="count"/> バイトの、変更前と変更後。</summary>
    public static (byte[] Before, byte[] After) Preview(DocumentSnapshot snapshot, bool canResize, TargetRange? selection, DataOperationSpec spec,
        int count = 4)
    {
        BitShiftInfo info = Analyze(snapshot.Length, canResize, selection, spec);
        if (info.Error != DataOperationError.None)
        {
            return ([], []);
        }

        byte[] before = new byte[(int)Math.Min(count, Math.Max(0, snapshot.Length - info.RegionStart))];
        snapshot.Read(info.RegionStart, before);
        byte[] after = new byte[(int)Math.Min(count, info.OutputLength)];
        new BitRegion(snapshot, info, spec).Fill(0, after);
        return (before, after);
    }

    /// <summary>ビット数が 8 の倍数の場合のピース操作 (EDIT-37 の仕様 4)。</summary>
    private static List<BitEdit> PiecePlan(DocumentSnapshot snapshot, BitShiftInfo info, DataOperationSpec spec)
    {
        bool insert = spec.Kind == DataOperationKind.InsertBits;
        bool toEnd = spec.BitScope == BitShiftScope.ToEnd;
        long b = info.RegionStart;
        long e = info.RegionEnd;
        long m = spec.BitCount / 8;
        int j = (int)(LinearPosition(spec) - b * 8);
        byte fill = spec.FillWithOne ? (byte)0xFF : (byte)0;

        // 並び順が最下位ビットからの場合、バイトの中の「前」は下位ビット。前の j ビットのマスクを並び順に合わせる。
        byte head = (byte)(spec.LsbFirst ? (1 << j) - 1 : (0xFF << (8 - j)) & 0xFF);
        var edits = new List<BitEdit>();
        if (insert)
        {
            if (j == 0)
            {
                edits.Add(new BitEditInsert(b, EditContent.Fill(fill, m)));
            }
            else
            {
                byte original = ReadByte(snapshot, b);
                byte first = (byte)((original & head) | (fill & ~head));
                byte last = (byte)((fill & head) | (original & ~head));
                edits.Add(new BitEditOverwrite(b, EditContent.Bytes([first])));
                if (m > 1)
                {
                    edits.Add(new BitEditInsert(b + 1, EditContent.Fill(fill, m - 1)));
                }

                edits.Add(new BitEditInsert(b + m, EditContent.Bytes([last])));
            }

            if (!toEnd)
            {
                // はみ出した分を捨てる (長さは変わらない)。
                edits.Add(new BitEditDelete(e, m));
            }

            return edits;
        }

        if (j == 0)
        {
            edits.Add(new BitEditDelete(b, m));
        }
        else
        {
            byte original = ReadByte(snapshot, b);
            byte next = ReadByte(snapshot, b + m);
            edits.Add(new BitEditOverwrite(b, EditContent.Bytes([(byte)((original & head) | (next & ~head))])));
            edits.Add(new BitEditDelete(b + 1, m));
        }

        if (!toEnd)
        {
            // 空いたビットは 0 で埋める (長さは変わらない)。
            edits.Add(new BitEditInsert(e - m, EditContent.Fill(0, m)));
        }

        return edits;
    }

    private static byte ReadByte(DocumentSnapshot snapshot, long offset)
    {
        Span<byte> one = stackalloc byte[1];
        ReadResult r = snapshot.Read(offset, one);
        if (r.BytesReturned < 1 || r.Unreadable.Count > 0)
        {
            throw new DataReadException(offset);
        }

        return one[0];
    }

    private void CheckSpace(long required)
    {
        if (_volumes is null)
        {
            return;
        }

        Directory.CreateDirectory(_tempDirectory);
        if (_volumes.GetVolume(_tempDirectory)?.AvailableFreeSpace is long available && available < required)
        {
            throw new TempSpaceException(required, available);
        }
    }

    /// <summary>
    /// ずらす範囲 (位置のバイトから範囲の終わりまで) をビット列として読み、ずらした結果のバイトを作る。範囲の外のビットは 0 とみなす
    /// (「選択範囲の中だけ」の削除で空いたビット、「ドキュメントの末尾まで」の最後のバイトの余り)。並び順が最下位ビットからの場合は
    /// 各バイトのビット順を反転してから最上位ビットからの列として扱う。
    /// </summary>
    private sealed class BitRegion(DocumentSnapshot snapshot, BitShiftInfo info, DataOperationSpec spec)
    {
        private readonly long _start = info.RegionStart;
        private readonly long _length = info.RegionEnd - info.RegionStart;
        private readonly int _p = (int)(LinearPosition(spec) - info.RegionStart * 8);
        private readonly long _k = spec.BitCount;
        private readonly bool _insert = spec.Kind == DataOperationKind.InsertBits;
        private readonly byte _fill = spec.FillWithOne ? (byte)0xFF : (byte)0;
        private readonly bool _lsb = spec.LsbFirst;
        private byte[] _window = [];
        private long _windowStart;

        /// <summary>結果の位置 <paramref name="q0"/> からのバイトを <paramref name="destination"/> に作る。</summary>
        public void Fill(long q0, Span<byte> destination)
        {
            long q1 = q0 + destination.Length;

            // ずらした部分の元のバイトの範囲を先に読んでおく。
            long shift = _insert ? -_k : _k;
            long fromBit = Math.Max(0, 8 * q0 + shift);
            long toBit = Math.Max(0, 8 * q1 + shift + 8);
            Load(fromBit >> 3, (toBit >> 3) + 1);
            for (long q = q0; q < q1; q++)
            {
                destination[(int)(q - q0)] = Output(q);
            }
        }

        private byte Output(long q)
        {
            byte value;
            long bit = 8 * q;
            if (_insert && bit >= _p + _k)
            {
                value = Shifted(bit - _k);
            }
            else if (_insert && bit >= _p && bit + 8 <= _p + _k)
            {
                value = _fill;
            }
            else if (!_insert && bit >= _p)
            {
                value = Shifted(bit + _k);
            }
            else
            {
                // 位置の前のビット・埋めるビット・ずらしたビットが混ざるバイトは 1 ビットずつ作る。
                int v = 0;
                for (int i = 0; i < 8; i++)
                {
                    long at = bit + i;
                    int b = at < _p ? Bit(at)
                        : _insert ? (at < _p + _k ? _fill & 1 : Bit(at - _k))
                        : Bit(at + _k);
                    v = (v << 1) | b;
                }

                value = (byte)v;
            }

            return _lsb ? ElementProcessor.ReverseBits(value) : value;
        }

        private byte Shifted(long s)
        {
            int r = (int)(s & 7);
            long index = s >> 3;
            return r == 0 ? Source(index) : (byte)((Source(index) << r) | (Source(index + 1) >> (8 - r)));
        }

        private int Bit(long at) => (Source(at >> 3) >> (7 - (int)(at & 7))) & 1;

        /// <summary>範囲の中の位置 <paramref name="index"/> のバイト (最上位ビットからの並びにしたもの。範囲の外は 0)。</summary>
        private byte Source(long index)
        {
            if (index < 0 || index >= _length)
            {
                return 0;
            }

            if (index < _windowStart || index >= _windowStart + _window.Length)
            {
                Load(index, index + DataOperationRunner.ChunkSize + 2);
            }

            return _window[index - _windowStart];
        }

        private void Load(long from, long to)
        {
            from = Math.Clamp(from, 0, _length);
            to = Math.Clamp(to, from, _length);
            if (from >= _windowStart && to <= _windowStart + _window.Length && _window.Length > 0)
            {
                return;
            }

            int n = (int)Math.Max(1, Math.Min(to - from, int.MaxValue / 2));
            n = (int)Math.Min(n, _length - from);
            if (n <= 0)
            {
                _window = [];
                _windowStart = from;
                return;
            }

            byte[] buffer = new byte[n];
            ReadResult result = snapshot.Read(_start + from, buffer);
            if (result.Unreadable.Count > 0)
            {
                throw new DataReadException(result.Unreadable[0].Offset);
            }

            if (result.BytesReturned < n)
            {
                throw new DataReadException(_start + from + result.BytesReturned);
            }

            if (_lsb)
            {
                for (int i = 0; i < buffer.Length; i++)
                {
                    buffer[i] = ElementProcessor.ReverseBits(buffer[i]);
                }
            }

            _window = buffer;
            _windowStart = from;
        }
    }
}
