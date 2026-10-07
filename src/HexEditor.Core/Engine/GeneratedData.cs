using System.Buffers.Binary;

namespace HexEditor.Core.Engine;

/// <summary>生成データ (ENG-03) の内容を計算する。</summary>
public static class GeneratedData
{
    public const int MaxPatternLength = 4096;

    private const ulong Golden = 0x9E3779B97F4A7C15UL;

    /// <summary>
    /// 乱数列の <paramref name="position"/> から <paramref name="destination"/> の長さ分を計算する。
    /// 位置 p のバイトは SplitMix64(seed + ⌊p / 8⌋ × golden) の下位から p mod 8 番目のバイト。
    /// </summary>
    public static void FillRandom(ulong seed, long position, Span<byte> destination)
    {
        Span<byte> word = stackalloc byte[8];
        int done = 0;
        while (done < destination.Length)
        {
            long p = position + done;
            ulong block = (ulong)(p >> 3);
            BinaryPrimitives.WriteUInt64LittleEndian(word, SplitMix64(unchecked(seed + block * Golden)));
            int start = (int)(p & 7);
            int n = Math.Min(8 - start, destination.Length - done);
            word.Slice(start, n).CopyTo(destination[done..]);
            done += n;
        }
    }

    /// <summary>パターンを位相 <paramref name="phase"/> から繰り返して埋める。</summary>
    public static void FillPattern(ReadOnlySpan<byte> pattern, long phase, Span<byte> destination)
    {
        int p = (int)(phase % pattern.Length);
        int done = 0;
        while (done < destination.Length)
        {
            int n = Math.Min(pattern.Length - p, destination.Length - done);
            pattern.Slice(p, n).CopyTo(destination[done..]);
            done += n;
            p = 0;
        }
    }

    private static ulong SplitMix64(ulong z)
    {
        unchecked
        {
            z += Golden;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
