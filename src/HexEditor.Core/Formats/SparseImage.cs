using HexEditor.Core.Sources;
using Microsoft.Win32.SafeHandles;

namespace HexEditor.Core.Formats;

/// <summary>デコードしたデータの連続した範囲 1 つ。<see cref="DataPosition"/> はデータの一時ファイルの中の位置。</summary>
public readonly record struct ImageSegment(long Address, long Length, long DataPosition)
{
    public long End => Address + Length;
}

/// <summary>
/// アドレスを持つ形式 (Intel HEX・S-record) やエンコード形式をデコードした内容 (ENG-38、TOOL-04〜07 のインポート)。デコードしたデータは
/// 一時ファイル (閉じると消える) に置き、メモリには範囲の一覧だけを持つ。データのないアドレス (隙間) は塗りつぶしの値として読み、
/// <see cref="IGapSource"/> で隙間を示す。オフセット 0 は <see cref="Origin"/> のアドレス。
/// </summary>
public sealed class SparseImage : ByteSourceBase, IGapSource
{
    private readonly SafeFileHandle? _data;
    private readonly ImageSegment[] _segments;
    private readonly long _length;

    internal SparseImage(SafeFileHandle? data, ImageSegment[] segments, long origin, long length, byte fill, string displayName)
    {
        _data = data;
        _segments = segments;
        Origin = origin;
        _length = length;
        Fill = fill;
        DisplayName = displayName;
    }

    /// <summary>オフセット 0 に対応するアドレス (アドレスの配置が「最小アドレスを先頭」なら最小のアドレス、「そのまま」なら 0)。</summary>
    public long Origin { get; }

    /// <summary>隙間の値 (既定 FF)。</summary>
    public byte Fill { get; }

    /// <summary>データのある範囲 (アドレスの昇順、重なりなし)。</summary>
    public IReadOnlyList<ImageSegment> Segments => _segments;

    /// <summary>データのバイト数 (隙間を除く)。</summary>
    public long DataBytes => _segments.Sum(s => s.Length);

    public override string DisplayName { get; }

    public override string Identity { get; } = "image:" + Guid.NewGuid().ToString("N");

    public override long Length => _length;

    /// <summary>表示上のアドレスを持たせる (ドキュメントのベースアドレスは開く側が決める)。</summary>
    public override long BaseAddress => Origin;

    /// <summary>長さを変えられる (Base64 をデコードして開いた内容。ENG-38 の仕様 4)。</summary>
    public bool Resizable { get; set; }

    public override SourceCapabilities Capabilities => Resizable ? SourceCapabilities.CanResize : SourceCapabilities.None;

    public override ReadResult Read(long offset, Span<byte> buffer)
    {
        int count = ClampToLength(offset, buffer.Length);
        Span<byte> target = buffer[..count];
        target.Fill(Fill);
        long address = Origin + offset;
        for (int i = FirstEndingAfter(address); i < _segments.Length && _segments[i].Address < address + count; i++)
        {
            ImageSegment s = _segments[i];
            long from = Math.Max(s.Address, address);
            long to = Math.Min(s.End, address + count);
            Span<byte> dst = target.Slice((int)(from - address), (int)(to - from));
            ReadData(s.DataPosition + (from - s.Address), dst);
        }

        return new ReadResult(count);
    }

    public IEnumerable<(long Offset, long Length)> GapsIn(long offset, long length)
    {
        length = Math.Min(length, Math.Max(0, _length - offset));
        long address = Origin + offset;
        long end = address + length;
        long at = address;
        for (int i = FirstEndingAfter(address); i < _segments.Length && _segments[i].Address < end && at < end; i++)
        {
            ImageSegment s = _segments[i];
            if (s.Address > at)
            {
                yield return (at - Origin, Math.Min(s.Address, end) - at);
            }

            at = Math.Max(at, s.End);
        }

        if (at < end)
        {
            yield return (at - Origin, end - at);
        }
    }

    /// <summary>オフセットにデータがあるか。</summary>
    public bool HasData(long offset)
    {
        long address = Origin + offset;
        int i = FirstEndingAfter(address);
        return i < _segments.Length && _segments[i].Address <= address;
    }

    private void ReadData(long position, Span<byte> destination)
    {
        int done = 0;
        while (done < destination.Length)
        {
            int n = RandomAccess.Read(_data!, destination[done..], position + done);
            if (n == 0)
            {
                throw new IOException("デコードしたデータの一時ファイルの内容が足りません。");
            }

            done += n;
        }
    }

    /// <summary>終わりが <paramref name="address"/> より後にある最初の範囲の番号 (二分探索)。</summary>
    private int FirstEndingAfter(long address)
    {
        int lo = 0, hi = _segments.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_segments[mid].End <= address)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _data?.Dispose();
        }
    }
}

/// <summary>
/// <see cref="SparseImage"/> を作る。データは一時ファイルに追記し (1 MiB ずつまとめて書く)、範囲の一覧だけをメモリに持つ。同じアドレスに
/// 後からデータが来た場合は後のデータを使う (重なりは <see cref="Add"/> の戻り値で知らせる。ENG-38 の仕様 7)。
/// </summary>
public sealed class SparseImageBuilder : IDisposable
{
    private const int BufferSize = 1024 * 1024;

    private readonly string _directory;
    private readonly List<ImageSegment> _segments = [];
    private SafeFileHandle? _file;
    private readonly byte[] _buffer = new byte[BufferSize];
    private int _buffered;
    private long _written;

    public SparseImageBuilder(string tempDirectory)
    {
        _directory = tempDirectory;
    }

    /// <summary>これまでに加えたデータのバイト数 (重なりで上書きされた分を含む)。</summary>
    public long AddedBytes => _written + _buffered;

    /// <summary>範囲の数。</summary>
    public int SegmentCount => _segments.Count;

    /// <summary>最小のアドレス (データがなければ null)。</summary>
    public long? MinAddress => _segments.Count > 0 ? _segments[0].Address : null;

    /// <summary>最大のアドレス + 1 (データがなければ null)。</summary>
    public long? EndAddress => _segments.Count > 0 ? _segments[^1].End : null;

    /// <summary>
    /// <paramref name="address"/> にデータを加える。前に加えたデータと重なれば、重なった部分を後のデータで置き換えて true を返す。
    /// </summary>
    public bool Add(long address, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return false;
        }

        long position = AddedBytes;
        Append(data);
        var segment = new ImageSegment(address, data.Length, position);

        // よくある場合: 前の範囲の続き (アドレスもデータの位置も連続) なら 1 つにまとめる。
        if (_segments.Count == 0 || _segments[^1].End <= address)
        {
            if (_segments.Count > 0 && _segments[^1].End == address && _segments[^1].DataPosition + _segments[^1].Length == position)
            {
                ImageSegment last = _segments[^1];
                _segments[^1] = last with { Length = last.Length + data.Length };
            }
            else
            {
                _segments.Add(segment);
            }

            return false;
        }

        // 重なる、または前に入る: 重なる範囲を削って挿入する。
        int index = FirstEndingAfter(address);
        bool overlapped = false;
        var replaced = new List<ImageSegment>();
        int removeFrom = index;
        int removeTo = index;
        while (removeTo < _segments.Count && _segments[removeTo].Address < segment.End)
        {
            ImageSegment s = _segments[removeTo];
            overlapped = true;
            if (s.Address < address)
            {
                replaced.Add(s with { Length = address - s.Address });
            }

            if (s.End > segment.End)
            {
                long cut = segment.End - s.Address;
                replaced.Add(new ImageSegment(segment.End, s.End - segment.End, s.DataPosition + cut));
            }

            removeTo++;
        }

        _segments.RemoveRange(removeFrom, removeTo - removeFrom);
        replaced.Add(segment);
        replaced.Sort((a, b) => a.Address.CompareTo(b.Address));
        _segments.InsertRange(removeFrom, replaced);
        return overlapped;
    }

    private int FirstEndingAfter(long address)
    {
        int lo = 0, hi = _segments.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_segments[mid].End <= address)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            int n = Math.Min(data.Length, BufferSize - _buffered);
            data[..n].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += n;
            data = data[n..];
            if (_buffered == BufferSize)
            {
                FlushBuffer();
            }
        }
    }

    private void FlushBuffer()
    {
        if (_buffered == 0)
        {
            return;
        }

        if (_file is null)
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, $"decoded-{Guid.NewGuid():N}.bin");
            _file = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
        }

        RandomAccess.Write(_file, _buffer.AsSpan(0, _buffered), _written);
        _written += _buffered;
        _buffered = 0;
    }

    /// <summary>
    /// 作り終える。<paramref name="alignLowest"/> なら最小のアドレスをオフセット 0 にし、そうでなければアドレス 0 をオフセット 0 にする
    /// (TOOL-05 の仕様 2 のアドレスの配置)。以後この一時ファイルは返したデータソースが持つ。
    /// </summary>
    public SparseImage Build(bool alignLowest, byte fill, string displayName)
    {
        FlushBuffer();
        long origin = alignLowest && _segments.Count > 0 ? _segments[0].Address : 0;
        long length = _segments.Count > 0 ? _segments[^1].End - origin : 0;
        SafeFileHandle? file = _file;
        _file = null;
        return new SparseImage(file, [.. _segments], origin, length, fill, displayName);
    }

    /// <summary>連続したデータ (Base64 など。アドレス 0 から順に加えたもの) として作り終える。</summary>
    public SparseImage BuildContiguous(string displayName) => Build(alignLowest: false, 0, displayName);

    public void Dispose()
    {
        _file?.Dispose();
        _file = null;
    }
}
