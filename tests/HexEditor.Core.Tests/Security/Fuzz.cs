using System.Buffers.Binary;
using System.Text;

namespace HexEditor.Core.Tests.Security;

/// <summary>
/// 信頼できない入力を読む処理の、決定的な短いファジング (フェーズ 4 の監査)。種の入力 (正しいデータ) を変異させたものと乱数のデータを
/// 順に与え、許された例外以外が出ない・1 件ごとの時間とメモリ (そのスレッドで確保した量) に上限があることを確かめる。
/// 無限ループは見張りのタイムアウトで、範囲外の読み込みは IndexOutOfRange などの想定外の例外で分かる。
/// スタックの深さは 1 MiB (アプリの UI スレッドと同じ) で動かす。
/// </summary>
public static class Fuzz
{
    private static readonly long[] Interesting =
        [0, 1, -1, 2, 0x7F, 0x80, 0xFF, 0x100, 0x7FFF, 0x8000, 0xFFFF, 0x10000, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, long.MaxValue, long.MinValue, 0x40000000];

    /// <summary>種を変異させた入力 <paramref name="mutations"/> 件と、乱数の入力 <paramref name="randoms"/> 件 (種と同じ順で毎回同じ)。</summary>
    public static IEnumerable<byte[]> Cases(int seed, IReadOnlyList<byte[]> seeds, int mutations, int randoms = 0, int maxRandomLength = 512,
        int maxLength = 1 << 20)
    {
        var random = new Random(seed);
        foreach (byte[] s in seeds)
        {
            yield return s;
        }

        for (int i = 0; i < mutations && seeds.Count > 0; i++)
        {
            yield return Mutate(random, seeds[random.Next(seeds.Count)], maxLength);
        }

        for (int i = 0; i < randoms; i++)
        {
            byte[] data = new byte[random.Next(maxRandomLength + 1)];
            random.NextBytes(data);
            yield return data;
        }
    }

    /// <summary>1〜4 回の変異 (ビット反転、特別な値、挿入、削除、重複、切り詰め)。</summary>
    public static byte[] Mutate(Random random, byte[] seed, int maxLength = 1 << 20)
    {
        var data = new List<byte>(seed);
        int edits = random.Next(1, 5);
        for (int e = 0; e < edits; e++)
        {
            int at = data.Count == 0 ? 0 : random.Next(data.Count);
            switch (random.Next(9))
            {
                case 0 when data.Count > 0:
                    data[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 1 when data.Count > 0:
                    data[at] = (byte)random.Next(256);
                    break;
                case 2 when data.Count >= 4:
                    {
                        at = Math.Min(at, data.Count - 4);
                        byte[] word = new byte[4];
                        long v = Interesting[random.Next(Interesting.Length)];
                        if (random.Next(2) == 0)
                        {
                            BinaryPrimitives.WriteInt32LittleEndian(word, (int)v);
                        }
                        else
                        {
                            BinaryPrimitives.WriteInt32BigEndian(word, (int)v);
                        }

                        for (int i = 0; i < 4; i++)
                        {
                            data[at + i] = word[i];
                        }

                        break;
                    }

                case 3 when data.Count > 0:
                    data[at] = (byte)Interesting[random.Next(Interesting.Length)];
                    break;
                case 4:
                    {
                        int n = random.Next(1, 17);
                        byte[] insert = new byte[n];
                        random.NextBytes(insert);
                        data.InsertRange(Math.Min(at, data.Count), insert);
                        break;
                    }

                case 5 when data.Count > 0:
                    data.RemoveRange(at, Math.Min(data.Count - at, random.Next(1, 33)));
                    break;
                case 6 when data.Count > 0:
                    {
                        int n = Math.Min(data.Count - at, random.Next(1, 65));
                        data.InsertRange(random.Next(data.Count + 1), data.GetRange(at, n));
                        break;
                    }

                case 7 when data.Count > 0:
                    data.RemoveRange(at, data.Count - at);
                    break;
                default:
                    if (data.Count > 0)
                    {
                        data[at] = (byte)~data[at];
                    }

                    break;
            }
        }

        return [.. data.Take(maxLength)];
    }

    /// <summary>文字列の変異 (ルールのソース・JSON など)。<paramref name="alphabet"/> から挿入・置換する。</summary>
    public static string MutateText(Random random, string seed, string alphabet)
    {
        var s = new StringBuilder(seed);
        int edits = random.Next(1, 5);
        for (int e = 0; e < edits; e++)
        {
            int at = s.Length == 0 ? 0 : random.Next(s.Length);
            switch (random.Next(5))
            {
                case 0 when s.Length > 0:
                    s.Remove(at, Math.Min(s.Length - at, random.Next(1, 8)));
                    break;
                case 1 when s.Length > 0:
                    s[at] = alphabet[random.Next(alphabet.Length)];
                    break;
                case 2 when s.Length > 0:
                    {
                        int n = Math.Min(s.Length - at, random.Next(1, 40));
                        s.Insert(random.Next(s.Length + 1), s.ToString(at, n));
                        break;
                    }

                default:
                    s.Insert(at, alphabet[random.Next(alphabet.Length)]);
                    break;
            }
        }

        return s.ToString();
    }

    /// <summary>
    /// 入力を順に <paramref name="target"/> に与える。<paramref name="allowed"/> が真の例外 (文書にある誤りの型) は成功とする。
    /// 1 件が <paramref name="timeoutMs"/> を超える、または <paramref name="maxAllocatedBytes"/> を超えて確保したら失敗。
    /// </summary>
    public static void Run<T>(IEnumerable<T> cases, Action<T> target, Func<Exception, bool>? allowed = null, int timeoutMs = 20_000,
        long maxAllocatedBytes = 512L * 1024 * 1024, Func<T, string>? describe = null)
    {
        describe ??= c => c is byte[] b ? $"{b.Length} bytes: {Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, 96)))}" : c?.ToString() ?? "null";
        var failures = new List<string>();
        object gate = new();
        string currentText = string.Empty;
        long startedAt = 0;
        int index = -1;
        var thread = new Thread(() =>
        {
            foreach (T item in cases)
            {
                lock (gate)
                {
                    index++;
                    currentText = describe(item);
                    startedAt = Environment.TickCount64;
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                try
                {
                    target(item);
                }
                catch (Exception ex) when (allowed?.Invoke(ex) == true)
                {
                }
                catch (Exception ex)
                {
                    lock (gate)
                    {
                        failures.Add($"case {index} ({currentText}): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                    }
                }

                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                lock (gate)
                {
                    if (allocated > maxAllocatedBytes)
                    {
                        failures.Add($"case {index} ({currentText}): allocated {allocated:N0} bytes");
                    }

                    startedAt = 0;
                    if (failures.Count >= 5)
                    {
                        return;
                    }
                }
            }
        }, 1024 * 1024) { IsBackground = true, Name = "fuzz" };
        thread.Start();

        // 見張り: 1 件の処理が上限を超えたら失敗にする (処理のスレッドは背景のスレッドなので、テストのプロセスの終了を妨げない)。
        while (!thread.Join(100))
        {
            lock (gate)
            {
                if (startedAt != 0 && Environment.TickCount64 - startedAt > timeoutMs)
                {
                    Assert.Fail($"timed out after {timeoutMs} ms (infinite loop or unbounded work): case {index} ({currentText})");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }
}
