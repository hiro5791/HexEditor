using System.Buffers.Binary;
using System.Security.Cryptography;
using HexEditor.Core.Operations;
using HexEditor.Core.Tools.Files;

namespace HexEditor.Core.Wiping;

/// <summary>範囲 1 つ (対象の先頭からのバイト位置)。</summary>
public readonly record struct WipeRange(long Offset, long Length)
{
    public long End => Offset + Length;
}

/// <summary>書き込みエラーのときの選択 (FOR-30 の「エラー」: 残りを続けるか中止するか)。</summary>
public enum WipeErrorChoice
{
    /// <summary>失敗した位置を記録して残りを続ける。</summary>
    Continue,

    /// <summary>中止する。</summary>
    Abort,
}

/// <summary>書き込みエラー 1 件 (位置は対象の先頭からのバイト位置)。</summary>
public sealed record WipeWriteError(long Offset, long Length, int Pass, string Message);

/// <summary>消去の進み具合 (何回目か、回ごとの進捗。FOR-30 の「巨大ファイル・長時間処理」)。<see cref="Verifying"/> なら検証の読み直し中。</summary>
public readonly record struct WipeProgress(int Pass, int Passes, long PassDone, long PassTotal, bool Verifying);

/// <summary>消去の結果。</summary>
public sealed record WipeResult
{
    /// <summary>書き込んだバイト数 (全回数の合計)。</summary>
    public long BytesWritten { get; init; }

    /// <summary>対象の範囲の合計 (1 回分)。</summary>
    public long TotalBytes { get; init; }

    public int Passes { get; init; }

    /// <summary>書けなかった範囲 (どれかの回で書き込みに失敗した範囲)。</summary>
    public IReadOnlyList<WipeRange> Unwritten { get; init; } = [];

    /// <summary>書き込みエラーの一覧。</summary>
    public IReadOnlyList<WipeWriteError> Errors { get; init; } = [];

    /// <summary>検証で一致しなかった範囲 (書けなかった範囲を除く)。</summary>
    public IReadOnlyList<WipeRange> Mismatches { get; init; } = [];

    /// <summary>検証をした (オプションがオンで、最後まで書けた)。</summary>
    public bool Verified { get; init; }

    public bool Cancelled { get; init; }

    /// <summary>書き込みエラーで中止した。</summary>
    public bool Aborted { get; init; }

    /// <summary>
    /// キャンセル・中止したときに、<see cref="StoppedPass"/> 回目の上書きを終えた位置 (この位置より前の範囲は上書き済みで、後ろは前の回までの内容。
    /// 1 回目なら元のまま。対象の先頭からの位置)。検証中に止めた場合は -1 (上書きはすべて終わっている)。
    /// </summary>
    public long StoppedAt { get; init; } = -1;

    /// <summary>止めたときの回 (1 から。検証中なら 0)。</summary>
    public int StoppedPass { get; init; }

    public TimeSpan Elapsed { get; init; }

    public bool Succeeded => !Cancelled && !Aborted && Unwritten.Count == 0 && Mismatches.Count == 0;
}

/// <summary>
/// 消去の乱数列 (暗号論的な乱数。AES-256 のカウンターモードの鍵ストリーム)。鍵は回ごとに <see cref="RandomNumberGenerator"/> で作り、
/// 位置から同じ列を作り直せるため、検証で書いたデータを覚えておく必要がない (メモリを範囲の大きさに比例させない)。
/// </summary>
public sealed class WipeRandomStream : IDisposable
{
    private readonly Aes _aes;
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(8);

    public WipeRandomStream()
    {
        _aes = Aes.Create();
        _aes.Key = RandomNumberGenerator.GetBytes(32);
    }

    /// <summary>位置 <paramref name="position"/> (範囲の中の位置) からの乱数列で <paramref name="buffer"/> を埋める。</summary>
    public void Fill(Span<byte> buffer, long position)
    {
        long firstBlock = position / 16;
        int skip = (int)(position % 16);
        int blocks = (skip + buffer.Length + 15) / 16;
        byte[] counters = new byte[blocks * 16];
        for (int i = 0; i < blocks; i++)
        {
            _nonce.CopyTo(counters, i * 16);
            BinaryPrimitives.WriteInt64BigEndian(counters.AsSpan(i * 16 + 8), firstBlock + i);
        }

        byte[] stream = _aes.EncryptEcb(counters, PaddingMode.None);
        stream.AsSpan(skip, buffer.Length).CopyTo(buffer);
    }

    public void Dispose() => _aes.Dispose();
}

/// <summary>
/// 範囲の消去 (FOR-30・FOR-31 が共通に使う)。TOOL-17 の方式 (<see cref="ShredMethod"/>) に従って範囲を順に上書きし、各回の後に
/// ディスクへ反映する。オプションで最後の回を読み直して確かめる (パターンは内容を比べ、乱数は書いた乱数列を鍵から作り直して比べる)。
/// メモリは範囲の大きさ・数に比例させない (1 MiB ずつ書き、範囲は回ごとに列挙し直す)。
/// </summary>
public static class WipeEngine
{
    public const int ChunkSize = 1 << 20;

    /// <summary>
    /// 消去する。<paramref name="ranges"/> は重ならず昇順で、回ごとに同じ内容を列挙できること (反復子のメソッドなら毎回作り直す)。
    /// <paramref name="totalBytes"/> を省くと 1 回列挙して数える。<paramref name="onError"/> が null なら書き込みエラーで中止する。
    /// <paramref name="chunkAlignment"/> は 1 回の書き込みの単位をそろえる値 (直接書き込みはクラスタ単位で止める。FOR-31)。
    /// キャンセルは書き込みの区切りで止まり、<see cref="WipeResult.Cancelled"/> を返す (例外にしない)。
    /// </summary>
    public static WipeResult Execute(IWipeTarget target, IEnumerable<WipeRange> ranges, ShredMethod method, bool verify,
        LongRunningOperation? operation = null, Func<WipeWriteError, WipeErrorChoice>? onError = null, Action<WipeProgress>? progress = null,
        CancellationToken cancellationToken = default, int chunkAlignment = 1, long? totalBytes = null, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        long started = time.GetTimestamp();
        CancellationToken token = operation?.CancellationToken ?? cancellationToken;
        long total = totalBytes ?? ranges.Sum(r => r.Length);
        int passes = method.Passes.Count;
        operation?.SetTotal(total * passes + (verify ? total : 0));
        int align = Math.Max(1, chunkAlignment);
        int chunk = Math.Max(align, ChunkSize / align * align);
        byte[] buffer = new byte[chunk];
        var errors = new List<WipeWriteError>();
        var failed = new List<WipeRange>();
        long written = 0;
        long done = 0;
        WipeRandomStream? lastRandom = null;
        try
        {
            for (int pass = 0; pass < passes; pass++)
            {
                ShredPass shred = method.Passes[pass];
                // 乱数の回は回ごとに新しい鍵。最後の回の鍵は検証のために残す (それ以外はこの回の終わりに捨てる)。
                WipeRandomStream? random = shred.IsRandom ? new WipeRandomStream() : null;
                if (random is not null)
                {
                    lastRandom?.Dispose();
                    lastRandom = random;
                }
                long passDone = 0;
                foreach (WipeRange range in ranges)
                {
                    for (long position = range.Offset; position < range.End;)
                    {
                        if (token.IsCancellationRequested)
                        {
                            return Stop(cancelled: true, aborted: false, position, pass + 1);
                        }

                        int n = (int)Math.Min(buffer.Length, range.End - position);
                        Span<byte> data = buffer.AsSpan(0, n);
                        Fill(shred, random, data, position - range.Offset, range.Offset);
                        try
                        {
                            target.Write(position, data);
                            written += n;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            var error = new WipeWriteError(position, n, pass + 1, ex.Message);
                            errors.Add(error);
                            AddRange(failed, new WipeRange(position, n));
                            if (onError?.Invoke(error) != WipeErrorChoice.Continue)
                            {
                                return Stop(cancelled: false, aborted: true, position, pass + 1);
                            }
                        }

                        position += n;
                        passDone += n;
                        done += n;
                        operation?.Report(done);
                        progress?.Invoke(new WipeProgress(pass + 1, passes, passDone, total, false));
                    }
                }

                try
                {
                    target.Flush();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add(new WipeWriteError(0, 0, pass + 1, ex.Message));
                }
            }
        }
        catch
        {
            lastRandom?.Dispose();
            throw;
        }

        // 検証 (最後の回のパターンと一致するか。書けなかった範囲は比べない)。
        var mismatches = new List<WipeRange>();
        if (verify)
        {
            ShredPass lastPass = method.Passes[^1];
            byte[] expected = new byte[buffer.Length];
            long passDone = 0;
            foreach (WipeRange range in ranges)
            {
                for (long pos = range.Offset; pos < range.End;)
                {
                    if (token.IsCancellationRequested)
                    {
                        return Stop(cancelled: true, aborted: false, -1, 0);
                    }

                    int n = (int)Math.Min(buffer.Length, range.End - pos);
                    Fill(lastPass, lastRandom, expected.AsSpan(0, n), pos - range.Offset, range.Offset);
                    bool readOk = true;
                    try
                    {
                        target.Read(pos, buffer.AsSpan(0, n));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        readOk = false;
                    }

                    IEnumerable<WipeRange> bad = readOk ? Differences(buffer.AsSpan(0, n), expected.AsSpan(0, n), pos) : [new WipeRange(pos, n)];
                    foreach (WipeRange b in bad)
                    {
                        if (!Covered(failed, b))
                        {
                            AddRange(mismatches, b);
                        }
                    }

                    pos += n;
                    passDone += n;
                    done += n;
                    operation?.Report(done);
                    progress?.Invoke(new WipeProgress(passes, passes, passDone, total, true));
                }
            }
        }

        lastRandom?.Dispose();
        return new WipeResult
        {
            BytesWritten = written,
            TotalBytes = total,
            Passes = passes,
            Unwritten = failed,
            Errors = errors,
            Mismatches = mismatches,
            Verified = verify,
            Elapsed = time.GetElapsedTime(started),
        };

        WipeResult Stop(bool cancelled, bool aborted, long stoppedAt, int stoppedPass)
        {
            lastRandom?.Dispose();
            try
            {
                target.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return new WipeResult
            {
                BytesWritten = written,
                TotalBytes = total,
                Passes = passes,
                Unwritten = failed,
                Errors = errors,
                Cancelled = cancelled,
                Aborted = aborted,
                StoppedAt = stoppedAt,
                StoppedPass = stoppedPass,
                Elapsed = time.GetElapsedTime(started),
            };
        }
    }

    /// <summary>1 回分の内容を作る。パターンは範囲の中の位置からの位相で繰り返し、乱数は範囲ごとの位置 (対象の先頭からの位置) で作る。</summary>
    private static void Fill(ShredPass pass, WipeRandomStream? random, Span<byte> buffer, long withinRange, long rangeStart)
    {
        if (pass.IsRandom)
        {
            random!.Fill(buffer, rangeStart + withinRange);
            return;
        }

        SecureDelete.Fill(pass, buffer, withinRange);
    }

    private static List<WipeRange> Differences(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected, long baseOffset)
    {
        var list = new List<WipeRange>();
        int i = 0;
        while (i < actual.Length)
        {
            i += actual[i..].CommonPrefixLength(expected[i..]);
            if (i >= actual.Length)
            {
                break;
            }

            int end = i;
            while (end < actual.Length && actual[end] != expected[end])
            {
                end++;
            }

            list.Add(new WipeRange(baseOffset + i, end - i));
            i = end;
        }

        return list;
    }

    /// <summary>範囲を一覧に加える (隣り合う・重なる範囲はまとめる)。</summary>
    private static void AddRange(List<WipeRange> list, WipeRange range)
    {
        if (list.Count > 0 && range.Offset <= list[^1].End)
        {
            WipeRange last = list[^1];
            list[^1] = new WipeRange(last.Offset, Math.Max(last.End, range.End) - last.Offset);
        }
        else
        {
            list.Add(range);
        }
    }

    private static bool Covered(List<WipeRange> failed, WipeRange range) =>
        failed.Any(f => f.Offset <= range.Offset && f.End >= range.End);

    /// <summary>範囲を昇順に並べ、重なり・隣接をまとめる。</summary>
    public static IReadOnlyList<WipeRange> Normalize(IEnumerable<WipeRange> ranges)
    {
        var list = new List<WipeRange>();
        foreach (WipeRange r in ranges.Where(r => r.Length > 0).OrderBy(r => r.Offset))
        {
            AddRange(list, r);
        }

        return list;
    }

    /// <summary>所要時間の目安 (確認ダイアログ)。<paramref name="bytesPerSecond"/> は想定の書き込み速度 (既定 100 MiB/s)。</summary>
    public static TimeSpan Estimate(long bytes, int passes, bool verify, double bytesPerSecond = 100d * 1024 * 1024) =>
        TimeSpan.FromSeconds(Math.Ceiling(bytes * (double)(passes + (verify ? 1 : 0)) / bytesPerSecond));
}
