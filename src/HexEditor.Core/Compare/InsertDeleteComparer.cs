using System.Diagnostics;
using HexEditor.Core.Sources;

namespace HexEditor.Core.Compare;

/// <summary>
/// 挿入・削除を考慮した比較 (ANA-03)。一致の続く間は単純比較と同じ速さで進め、不一致が見つかったら再同期ウィンドウ (左右それぞれ
/// W バイト) の中で Myers の差分アルゴリズム (線形空間版、<see cref="MyersDiff"/>) を使って再同期点を探す。
/// <para>巨大なデータでの方針 (ANA-03 の「巨大ファイル」):</para>
/// <list type="number">
/// <item>メモリはウィンドウ 2 つ (各 W バイト)、ハッシュ表 (最大 2^21 項目)、読み込みの塊 (1 MiB) の分だけを使い、データの長さに比例しない。
/// W = 16 MB で作業用メモリは約 80 MB。</item>
/// <item>Myers は編集距離に上限 (<see cref="MaxEditDistance"/>) を設け、超えたら打ち切る (全く異なるデータで時間が W の 2 乗にならないように)。</item>
/// <item>Myers で M バイト以上の一致が見つからなければ、M バイト単位のローリングハッシュで左右の共通ブロックを探す。片側のウィンドウを
/// 索引にし、もう片側をウィンドウの 4 倍 (最大 64 MiB) 先まで流し読みして、最も近い (左右の位置の和が最小の) 共通ブロックを再同期点にする。
/// W より長い挿入・削除も、これで再同期できる (仕様 3)。</item>
/// <item>どちらでも見つからなければ、ウィンドウ全体を「変更」にして進む (仕様 2.5)。1 つのウィンドウに 5 秒以上かかった場合も
/// ウィンドウ全体を「変更」にし、打ち切ったウィンドウとして数える。</item>
/// </list>
/// 結果は最短の編集手順を保証しない (仕様 5)。ただし W がデータより長ければ、最小一致長 1 で最短になる (Myers の結果をそのまま使う)。
/// </summary>
internal static class InsertDeleteComparer
{
    /// <summary>Myers で探す編集距離の上限 (要素の数)。</summary>
    public const int MaxEditDistance = 4096;

    /// <summary>ローリングハッシュで流し読みする距離の上限。</summary>
    public const long MaxScanDistance = 64L * 1024 * 1024;

    private const int ChunkSize = 1 << 20;
    private const int MaxIndexEntries = 1 << 20;

    public static void Run(CompareOptions options, CompareResult result, CancellationToken cancellationToken, Action<long>? progress)
    {
        var run = new Runner(options, result, cancellationToken, progress);
        try
        {
            run.Execute();
        }
        finally
        {
            run.Sink.Flush();
        }
    }

    private sealed class Runner(CompareOptions options, CompareResult result, CancellationToken cancellationToken, Action<long>? progress)
    {
        private readonly CompareRange _left = result.Left;
        private readonly CompareRange _right = result.Right;
        private readonly int _unit = options.Unit;
        private readonly int _minMatch = options.MinMatch;
        // 読み込みの塊。短いデータでは、データの長さの分だけにする。
        private readonly byte[] _ca = new byte[(int)Math.Min(ChunkSize, Math.Max(result.Left.Length, result.Right.Length) + 1)];
        private readonly byte[] _cb = new byte[(int)Math.Min(ChunkSize, Math.Max(result.Left.Length, result.Right.Length) + 1)];
        private byte[] _wa = [];
        private byte[] _wb = [];

        public DiffSink Sink { get; } = new(result, options.MergeGap);

        /// <summary>ウィンドウの大きさ (単位の倍数)。</summary>
        private int Window => Math.Max(_unit, options.Window / _unit * _unit);

        public void Execute()
        {
            long nL = _left.Length;
            long nR = _right.Length;
            long i = 0;
            long j = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 1. 一致が続く限り進める。
                (long k, Unreadable? bad) = CommonPrefix(i, j);
                result.AddMatched(k);
                i += k;
                j += k;
                Report(i, j);
                if (bad is { } u)
                {
                    long len = Math.Max(1, Math.Min(u.Length, Math.Max(nL - i, nR - j)));
                    long l = Math.Min(len, nL - i);
                    long r = Math.Min(len, nR - j);
                    Sink.Add(new DiffRange(DiffKind.Unreadable, _left.Start + i, l, _right.Start + j, r), 0);
                    i += l;
                    j += r;
                    continue;
                }

                if (i >= nL && j >= nR)
                {
                    break;
                }

                if (i >= nL || j >= nR)
                {
                    Change(i, j, nL - i, nR - j);
                    i = nL;
                    j = nR;
                    break;
                }

                (i, j) = Resync(i, j);
            }

            Report(nL, nR);
        }

        private void Report(long i, long j)
        {
            result.ReportPosition(i, j);
            progress?.Invoke(Math.Max(i, j));
        }

        /// <summary>差分を書く (左右の長さから種類を決める)。</summary>
        private void Change(long i, long j, long leftLength, long rightLength)
        {
            if (leftLength == 0 && rightLength == 0)
            {
                return;
            }

            Sink.Add(new DiffRange(DiffRange.KindFor(leftLength, rightLength), _left.Start + i, leftLength, _right.Start + j, rightLength),
                Math.Max(leftLength, rightLength));
        }

        private readonly record struct Unreadable(long Length);

        /// <summary>
        /// (i, j) から一致の続く長さ (単位の倍数) を求める。途中で読めない位置に当たったら、そこまでの長さと読めない範囲の長さを返す。
        /// </summary>
        private (long Length, Unreadable? Bad) CommonPrefix(long i, long j)
        {
            long nL = _left.Length;
            long nR = _right.Length;
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = (int)Math.Min(_ca.Length, Math.Min(nL - i - total, nR - j - total));
                if (n <= 0)
                {
                    return (RoundDown(total, i + total >= nL && j + total >= nR), null);
                }

                long li = i + total;
                long rj = j + total;
                (ReadResult ra, ReadResult rb) = SimpleComparer.ReadBoth(_left, _left.Start + li, _ca, _right, _right.Start + rj, _cb, n);
                (int badLeft, long badLeftLength) = FirstUnreadable(ra, _left.Start + li, n);
                (int badRight, long badRightLength) = FirstUnreadable(rb, _right.Start + rj, n);
                int limit = Math.Min(badLeft, badRight);
                int k = _ca.AsSpan(0, limit).CommonPrefixLength(_cb.AsSpan(0, limit));
                if (k < limit)
                {
                    return (RoundDown(total + k, false), null);
                }

                total += limit;
                if (limit < n)
                {
                    // 読めない位置。単位の途中なら単位の先頭から読み込み不可にする。
                    long rounded = RoundDown(total, false);
                    long length = Math.Max(badLeft == limit ? badLeftLength : 0, badRight == limit ? badRightLength : 0) + (total - rounded);
                    return (rounded, new Unreadable(length));
                }
            }
        }

        private long RoundDown(long length, bool atEnd) => _unit == 1 || atEnd ? length : length - length % _unit;

        private static (int Position, long Length) FirstUnreadable(ReadResult r, long start, int n)
        {
            int position = Math.Min(n, r.BytesReturned);
            long length = n - position;
            foreach (UnreadableRange u in r.Unreadable)
            {
                long s = Math.Max(u.Offset, start) - start;
                if (s < position && u.End > start)
                {
                    position = (int)s;
                    length = u.End - Math.Max(u.Offset, start);
                }
            }

            return (position, Math.Max(1, length));
        }

        /// <summary>
        /// (i, j) で不一致が見つかった。再同期点を探し、そこまでの差分を書いて、次に一致を調べる位置を返す。
        /// </summary>
        private (long I, long J) Resync(long i, long j)
        {
            long nL = _left.Length;
            long nR = _right.Length;
            long deadline = Stopwatch.GetTimestamp() + (long)(options.WindowTimeLimit.TotalSeconds * Stopwatch.Frequency);
            int wA = (int)Math.Min(Window, nL - i);
            int wB = (int)Math.Min(Window, nR - j);
            wA = ReadWindow(_left, i, wA, ref _wa);
            wB = ReadWindow(_right, j, wB, ref _wb);
            bool final = i + wA == nL && j + wB == nR;

            // 2. ウィンドウ内で Myers を使う。
            var symbols = new UnitSymbols(_wa, _wb, wA, wB, _unit);
            List<MyersDiff.Match>? matches = MyersDiff.Matches(symbols, symbols.CountA, symbols.CountB, MaxEditDistance, deadline, cancellationToken);
            bool timedOut = matches is null && Stopwatch.GetTimestamp() > deadline;
            if (matches is not null)
            {
                var anchors = matches.Where(m => symbols.ByteLength(m.A, m.Length, left: true) >= _minMatch).ToList();
                if (final)
                {
                    EmitAnchored(i, j, anchors, symbols, includeLast: true);
                    return (nL, nR);
                }

                if (anchors.Count > 0)
                {
                    // ウィンドウの後ろ半分の対応付けは、ウィンドウで切ったことの影響を受けうるので、前半の一致区間だけを使う (前半になければ
                    // 最初の 1 つ)。最後に使う一致区間はその先頭から一致を調べ直す (ウィンドウの端を越えて続くことがあるため)。
                    int front = anchors.Count(a => a.A < symbols.CountA / 2 && a.B < symbols.CountB / 2);
                    anchors.RemoveRange(Math.Max(1, front), anchors.Count - Math.Max(1, front));
                    MyersDiff.Match last = anchors[^1];
                    EmitAnchored(i, j, anchors, symbols, includeLast: false);
                    return (i + (long)last.A * _unit, j + (long)last.B * _unit);
                }
            }

            // 3. ローリングハッシュで最も近い共通ブロックを探す。
            if (!timedOut && NearestCommonBlock(i, j, wA, wB, deadline) is { } found)
            {
                Change(i, j, found.P, found.Q);
                return (i + found.P, j + found.Q);
            }

            // 4. 見つからない (または時間切れ): ウィンドウ全体を「変更」にする。
            if (timedOut || Stopwatch.GetTimestamp() > deadline)
            {
                result.AbortedWindows++;
            }

            Change(i, j, wA, wB);
            return (i + wA, j + wB);
        }

        /// <summary>一致区間の間を差分として書く。<paramref name="includeLast"/> なら、最後の一致区間の後ろからウィンドウの末尾までも書く。</summary>
        private void EmitAnchored(long i, long j, List<MyersDiff.Match> anchors, UnitSymbols symbols, bool includeLast)
        {
            long ca = 0;
            long cb = 0;
            for (int x = 0; x < anchors.Count; x++)
            {
                MyersDiff.Match m = anchors[x];
                long a = (long)m.A * _unit;
                long b = (long)m.B * _unit;
                Change(i + ca, j + cb, a - ca, b - cb);
                if (!includeLast && x == anchors.Count - 1)
                {
                    return;
                }

                long la = symbols.ByteLength(m.A, m.Length, left: true);
                long lb = symbols.ByteLength(m.B, m.Length, left: false);
                result.AddMatched(Math.Min(la, lb));
                ca = a + la;
                cb = b + lb;
            }

            Change(i + ca, j + cb, symbols.LengthA - ca, symbols.LengthB - cb);
        }

        /// <summary>ウィンドウを読む。読めない位置があれば、その手前までに縮める (縮めた長さを返す)。</summary>
        private int ReadWindow(CompareRange side, long position, int length, ref byte[] buffer)
        {
            if (buffer.Length < length)
            {
                buffer = new byte[Math.Max(length, Math.Min(Window, buffer.Length * 2))];
            }

            ReadResult r = side.Data.Read(side.Start + position, buffer.AsSpan(0, length));
            (int bad, _) = FirstUnreadable(r, side.Start + position, length);
            int rounded = bad - bad % _unit;
            return bad == length || rounded == 0 ? bad : rounded;
        }

        /// <summary>
        /// 最小一致長 M バイトの共通ブロックのうち、左右の位置の和が最小のもの (P、Q はそれぞれの相対位置、単位の倍数)。
        /// 左のウィンドウを索引にして右を流し読みしたものと、右のウィンドウを索引にして左を流し読みしたもののうち近い方。
        /// </summary>
        private (long P, long Q)? NearestCommonBlock(long i, long j, int wA, int wB, long deadline)
        {
            int m = _minMatch;
            if (wA < m || wB < m)
            {
                return null;
            }

            long scan = Math.Min(MaxScanDistance, 4L * Window);
            (long P, long Q)? best = null;
            (long, long)? fromRight = Scan(_wa, wA, _right, j, Math.Min(_right.Length - j, scan), deadline, indexIsLeft: true);
            if (fromRight is { } r)
            {
                best = r;
            }

            (long, long)? fromLeft = Scan(_wb, wB, _left, i, Math.Min(_left.Length - i, scan), deadline, indexIsLeft: false);
            if (fromLeft is { } l && (best is null || l.Item1 + l.Item2 < best.Value.P + best.Value.Q))
            {
                best = l;
            }

            return best;
        }

        /// <summary>
        /// 片側のウィンドウ <paramref name="index"/> の M バイトのブロックを索引にして、もう片側を <paramref name="from"/> から
        /// <paramref name="distance"/> バイト流し読みする。見つかった組 (左の相対位置、右の相対位置) のうち位置の和が最小のもの。
        /// </summary>
        private (long, long)? Scan(byte[] index, int indexLength, CompareRange other, long from, long distance, long deadline, bool indexIsLeft)
        {
            int m = _minMatch;
            int step = Math.Max(_unit, (int)Math.Min(int.MaxValue, (indexLength - m + 1 + MaxIndexEntries - 1) / MaxIndexEntries));
            step = (step + _unit - 1) / _unit * _unit;
            var table = new BlockTable(Math.Min(MaxIndexEntries, (indexLength - m) / step + 1));
            for (int p = 0; p + m <= indexLength; p += step)
            {
                table.Add(Hash(index.AsSpan(p, m)), p);
            }

            ulong power = 1;
            for (int x = 0; x < m - 1; x++)
            {
                power *= Base;
            }

            long bestCost = long.MaxValue;
            (long, long)? best = null;
            byte[] chunk = indexIsLeft ? _cb : _ca;
            byte[] ring = new byte[m];
            ulong h = 0;
            long chunkStart = 0;
            int chunkLength = 0;
            for (long pos = 0; pos < distance; pos++)
            {
                long q = pos - m + 1;
                if (q >= bestCost)
                {
                    break;
                }

                if ((pos & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Stopwatch.GetTimestamp() > deadline)
                    {
                        break;
                    }
                }

                if (pos >= chunkStart + chunkLength)
                {
                    chunkStart = pos;
                    int n = (int)Math.Min(chunk.Length, distance - pos);
                    ReadResult r = other.Data.Read(other.Start + from + pos, chunk.AsSpan(0, n));
                    (chunkLength, _) = FirstUnreadable(r, other.Start + from + pos, n);
                    if (chunkLength == 0)
                    {
                        break;
                    }
                }

                byte next = chunk[(int)(pos - chunkStart)];
                int slot = (int)(pos % m);
                if (pos >= m)
                {
                    h -= ring[slot] * power;
                }

                h = h * Base + next;
                ring[slot] = next;
                if (q < 0 || q % _unit != 0)
                {
                    continue;
                }

                foreach (int p in table.Find(h))
                {
                    if (p + q == 0 || p + q >= bestCost || !SameBlock(index, p, ring, q, m))
                    {
                        continue;
                    }

                    bestCost = p + q;
                    best = indexIsLeft ? (p, q) : (q, p);
                }
            }

            return best;
        }

        /// <summary>索引側の位置 p のブロックと、流し読みしている側の位置 q のブロック (リングバッファ) が等しいか。</summary>
        private static bool SameBlock(byte[] index, int p, byte[] ring, long q, int m)
        {
            for (int t = 0; t < m; t++)
            {
                if (index[p + t] != ring[(int)((q + t) % m)])
                {
                    return false;
                }
            }

            return true;
        }

        private const ulong Base = 0x100000001B3UL;

        private static ulong Hash(ReadOnlySpan<byte> data)
        {
            ulong h = 0;
            foreach (byte b in data)
            {
                h = h * Base + b;
            }

            return h;
        }
    }

    /// <summary>ブロックのハッシュから位置を引く表 (開番地法。同じハッシュの項目は最初の 4 つまで)。</summary>
    private sealed class BlockTable
    {
        private readonly ulong[] _keys;
        private readonly int[] _values;
        private readonly int _mask;

        public BlockTable(int entries)
        {
            int capacity = 16;
            while (capacity < entries * 2)
            {
                capacity <<= 1;
            }

            _keys = new ulong[capacity];
            _values = new int[capacity];
            _values.AsSpan().Fill(-1);
            _mask = capacity - 1;
        }

        public void Add(ulong hash, int position)
        {
            int slot = (int)(Mix(hash) & (ulong)_mask);
            int same = 0;
            for (int probe = 0; probe <= _mask; probe++)
            {
                if (_values[slot] < 0)
                {
                    _keys[slot] = hash;
                    _values[slot] = position;
                    return;
                }

                if (_keys[slot] == hash && ++same >= 4)
                {
                    return;
                }

                slot = (slot + 1) & _mask;
            }
        }

        public IEnumerable<int> Find(ulong hash)
        {
            int slot = (int)(Mix(hash) & (ulong)_mask);
            for (int probe = 0; probe <= _mask && _values[slot] >= 0; probe++)
            {
                if (_keys[slot] == hash)
                {
                    yield return _values[slot];
                }

                slot = (slot + 1) & _mask;
            }
        }

        private static ulong Mix(ulong h)
        {
            h ^= h >> 33;
            h *= 0xFF51AFD7ED558CCDUL;
            h ^= h >> 33;
            return h;
        }
    }

    /// <summary>比較の単位ごとの要素。最後の要素は単位より短いことがある (長さも比べる)。</summary>
    private readonly struct UnitSymbols(byte[] a, byte[] b, int lengthA, int lengthB, int unit) : ISymbolComparer
    {
        public int LengthA => lengthA;

        public int LengthB => lengthB;

        public int CountA => (lengthA + unit - 1) / unit;

        public int CountB => (lengthB + unit - 1) / unit;

        public bool Equal(int x, int y)
        {
            if (unit == 1)
            {
                return a[x] == b[y];
            }

            int la = Math.Min(unit, lengthA - x * unit);
            int lb = Math.Min(unit, lengthB - y * unit);
            return la == lb && a.AsSpan(x * unit, la).SequenceEqual(b.AsSpan(y * unit, lb));
        }

        /// <summary><paramref name="start"/> 番目から <paramref name="count"/> 個の要素のバイト数。</summary>
        public long ByteLength(int start, int count, bool left) =>
            Math.Min((long)(start + count) * unit, left ? lengthA : lengthB) - (long)start * unit;
    }
}
