namespace HexEditor.Platform.Network;

/// <summary>
/// 帯域を使い切らないダウンロード (10 の PKG-18 の仕様 1: 更新のダウンロードは低い優先度にする)。
/// Windows の HTTP には「低い優先度」の指定がないため、読み取りの速さに上限を設け、他の通信のための余裕を残す。
/// 上限は 1 秒ごとの平均で守り、超えた分だけ待つ (小さな差分パッケージは数秒で終わる)。
/// </summary>
public static class ThrottledCopy
{
    /// <summary>更新のダウンロードの速さの上限 (バイト/秒)。一般的な家庭の回線 (数十 Mbps) の一部にとどめる。</summary>
    public const long UpdateBytesPerSecond = 4L * 1024 * 1024;

    private const int ChunkSize = 64 * 1024;

    /// <summary>
    /// <paramref name="source"/> を <paramref name="destination"/> に写す。<paramref name="bytesPerSecond"/> を超えないように待つ
    /// (0 以下なら上限なし)。<paramref name="length"/> が分かれば進み具合 (0〜100) を <paramref name="progress"/> に知らせる。
    /// </summary>
    public static async Task CopyAsync(Stream source, Stream destination, long? length, long bytesPerSecond, Action<int>? progress,
        CancellationToken cancellationToken, TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        time ??= TimeProvider.System;
        delay ??= (d, ct) => Task.Delay(d, time, ct);
        byte[] buffer = new byte[ChunkSize];
        long total = 0;
        int lastPercent = -1;
        long started = time.GetTimestamp();
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            if (length is > 0 && progress is not null)
            {
                int percent = (int)Math.Clamp(total * 100 / length.Value, 0, 100);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress(percent);
                }
            }

            if (bytesPerSecond > 0 && WaitFor(total, bytesPerSecond, time.GetElapsedTime(started)) is { } wait)
            {
                await delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }

        if (lastPercent != 100)
        {
            progress?.Invoke(100);
        }
    }

    /// <summary><paramref name="total"/> バイトを読んだ時点で、上限を守るために待つ時間。待たなくてよければ null。</summary>
    public static TimeSpan? WaitFor(long total, long bytesPerSecond, TimeSpan elapsed)
    {
        TimeSpan earliest = TimeSpan.FromSeconds((double)total / bytesPerSecond);
        return earliest > elapsed ? earliest - elapsed : null;
    }
}
