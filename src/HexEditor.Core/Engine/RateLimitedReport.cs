namespace HexEditor.Core.Engine;

/// <summary>
/// 何度も起きる出来事を、間隔をあけて 1 回だけ報告する (ログに同じ警告を並べないため)。報告してよいときは、前の報告から起きた回数
/// (今回を含む) を返す。複数のスレッドから呼べる。
/// </summary>
public sealed class RateLimitedReport(TimeSpan interval, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _lock = new();
    private long? _lastReport;
    private int _pending;

    /// <summary>出来事を 1 回数える。報告してよいなら true で、<paramref name="count"/> は前の報告からの回数 (今回を含む)。</summary>
    public bool TryReport(out int count)
    {
        lock (_lock)
        {
            _pending++;
            long now = _time.GetTimestamp();
            if (_lastReport is { } last && _time.GetElapsedTime(last, now) < interval)
            {
                count = 0;
                return false;
            }

            _lastReport = now;
            count = _pending;
            _pending = 0;
            return true;
        }
    }
}
