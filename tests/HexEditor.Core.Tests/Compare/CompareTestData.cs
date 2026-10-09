using System.Buffers.Binary;
using HexEditor.Core.Compare;
using HexEditor.Core.Sources;
using HexEditor.TestData;

namespace HexEditor.Core.Tests.Compare;

/// <summary>
/// 比較のテスト用のデータ。内容を関数で計算するので、1 GiB の長さでもメモリを使わない (テスト方針: 巨大なデータは仮想のデータソースで)。
/// 読み込みに遅延を入れたり、読めない範囲を作ったりできる。
/// </summary>
internal sealed class FunctionData(long length, Action<long, Span<byte>> content) : ICompareData
{
    public long Length { get; } = length;

    /// <summary>1 回の読み込みごとに呼ぶ (遅延を入れるなど)。</summary>
    public Action<long, int>? OnRead { get; init; }

    public List<UnreadableRange> Bad { get; } = [];

    public ReadResult Read(long offset, Span<byte> destination)
    {
        int n = (int)Math.Clamp(Length - offset, 0, destination.Length);
        OnRead?.Invoke(offset, n);
        content(offset, destination[..n]);
        List<UnreadableRange>? bad = null;
        foreach (UnreadableRange u in Bad)
        {
            long s = Math.Max(u.Offset, offset);
            long e = Math.Min(u.End, offset + n);
            if (s < e)
            {
                destination.Slice((int)(s - offset), (int)(e - s)).Clear();
                (bad ??= []).Add(new UnreadableRange(s, e - s, UnreadableReason.IoError));
            }
        }

        return new ReadResult(n, bad);
    }
}

internal static class CompareTestData
{
    public const long MiB = 1024 * 1024;
    public const long GiB = 1024 * MiB;

    /// <summary>TD-ANA-RAND-1G などの乱数 (TestDataCatalog.Random と同じ値。8 バイトずつ書いて速くしたもの)。</summary>
    public static void Random(ulong seed, long offset, Span<byte> destination)
    {
        int head = (int)Math.Min(destination.Length, (8 - (offset & 7)) & 7);
        TestDataCatalog.Random(seed, offset, destination[..head]);
        long word = (offset + head) >> 3;
        int i = head;
        for (; i + 8 <= destination.Length; i += 8, word++)
        {
            ulong z = unchecked(seed + (ulong)word * 0x9E3779B97F4A7C15UL + 0x9E3779B97F4A7C15UL);
            z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
            z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
            z ^= z >> 31;
            BinaryPrimitives.WriteUInt64LittleEndian(destination[i..], z);
        }

        TestDataCatalog.Random(seed, offset + i, destination[i..]);
    }

    /// <summary>TD-MARKERS-1G の内容 (2^20 ごとと末尾の目印、それ以外は 00)。</summary>
    public static void Markers(long length, long offset, Span<byte> destination)
    {
        destination.Clear();
        long end = offset + destination.Length;
        int markerLength = TestDataCatalog.Marker(0).Length;
        var positions = new List<long>();
        for (long p = Math.Max(0, (offset - markerLength) / MiB * MiB); p < end; p += MiB)
        {
            positions.Add(p);
        }

        positions.Add(length - markerLength);
        foreach (long p in positions)
        {
            byte[] marker = TestDataCatalog.Marker(p);
            for (int k = 0; k < marker.Length; k++)
            {
                long at = p + k;
                if (at >= offset && at < end)
                {
                    destination[(int)(at - offset)] = marker[k];
                }
            }
        }
    }

    /// <summary>
    /// 元のデータの <paramref name="at"/> に <paramref name="inserted"/> を挿入し、<paramref name="at"/> から <paramref name="deleted"/>
    /// バイトを削除したデータ (挿入・削除を考慮した比較のテストデータ)。
    /// </summary>
    public static Action<long, Span<byte>> Edited(Action<long, Span<byte>> original, long at, byte[] inserted, long deleted) => (offset, dst) =>
    {
        for (int i = 0; i < dst.Length;)
        {
            long p = offset + i;
            if (p < at)
            {
                int n = (int)Math.Min(dst.Length - i, at - p);
                original(p, dst.Slice(i, n));
                i += n;
            }
            else if (p < at + inserted.Length)
            {
                int n = (int)Math.Min(dst.Length - i, at + inserted.Length - p);
                inserted.AsSpan((int)(p - at), n).CopyTo(dst[i..]);
                i += n;
            }
            else
            {
                int n = dst.Length - i;
                original(p - inserted.Length + deleted, dst.Slice(i, n));
                i += n;
            }
        }
    };

    public static CompareResult Diff(ICompareData left, ICompareData right, CompareOptions options) =>
        Diff(CompareRange.Whole(left), CompareRange.Whole(right), options);

    public static CompareResult Diff(CompareRange left, CompareRange right, CompareOptions options, DiffStore? store = null)
    {
        var result = new CompareResult(options.Method, left, right, store);
        DataComparer.Run(options, result);
        return result;
    }

    public static CompareResult Diff(byte[] left, byte[] right, CompareOptions options) =>
        Diff(CompareData.FromBytes(left), CompareData.FromBytes(right), options);

    public static List<DiffRange> All(CompareResult result) => [.. result.Diffs.Enumerate()];

    /// <summary>差分の列を左に適用して右を作る (差分の列が正しい編集手順になっているかの確認)。</summary>
    public static byte[] Apply(byte[] left, byte[] right, IEnumerable<DiffRange> diffs)
    {
        var output = new List<byte>();
        long l = 0;
        foreach (DiffRange d in diffs)
        {
            Assert.True(d.LeftOffset >= l, $"差分の順序が不正です: {d}");
            // 差分の手前の一致区間: 左右で同じでなければならない。
            output.AddRange(left.AsSpan((int)l, (int)(d.LeftOffset - l)).ToArray());
            output.AddRange(right.AsSpan((int)d.RightOffset, (int)d.RightLength).ToArray());
            l = d.LeftEnd;
        }

        output.AddRange(left.AsSpan((int)l).ToArray());
        return [.. output];
    }
}
