using System.Diagnostics;
using System.Globalization;
using HexEditor.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace HexEditor.App.Controls;

/// <summary>
/// Hex ビューの診断表示 (VIEW-04 の仕様 8)。描画時間・フレームの間隔・読み込み待ち (仮表示のセル数) と、
/// キー入力から画面に出すまでの時間を記録する。既定はオフ。有効にすると右上に小さな表を重ね、
/// <see cref="LogPath"/> があれば CSV で書き出す (性能のテストが読む。00-test-strategy 7.2 の「診断表示の記録」)。
/// <para>
/// CSV の行: <c>frame,時刻ms,間隔ms</c> / <c>render,時刻ms,描画ms,行数,作り直した行数,読み込み中のセル数,I/O待ち回数</c> /
/// <c>key,時刻ms,キー</c> / <c>present,時刻ms,キー入力からの ms</c>。時刻は記録を始めてからのミリ秒。
/// 描画は I/O を待たない (VIEW-03 の仕様 1) ため、I/O 待ち回数は常に 0 を書く。
/// </para>
/// </summary>
internal sealed class HexViewDiagnostics(FrameworkElement panel, TextBlock text)
{
    private const int Window = 240;

    private readonly Queue<double> _renderTimes = new();
    private readonly Queue<double> _intervals = new();
    private readonly long _origin = Stopwatch.GetTimestamp();
    private bool _enabled;
    private bool _subscribed;
    private long _lastFrame = -1;
    private long _pendingKey = -1;
    private bool _renderedSinceKey;
    private long _lastOverlay;
    private long _framesWithLoading;
    private StreamWriter? _log;
    private string? _logPath;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            panel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (value)
            {
                Subscribe();
            }
            else
            {
                Stop();
            }
        }
    }

    public string? LogPath
    {
        get => _logPath;
        set
        {
            _log?.Dispose();
            _log = null;
            _logPath = value;
        }
    }

    public void RecordKey(VirtualKey key)
    {
        if (!_enabled)
        {
            return;
        }

        _pendingKey = Stopwatch.GetTimestamp();
        _renderedSinceKey = false;
        Write($"key,{Ms(_pendingKey)},{key}");
    }

    public void RecordRender(TimeSpan elapsed, int rows, int rebuilt, int loadingCells)
    {
        if (!_enabled)
        {
            return;
        }

        Push(_renderTimes, elapsed.TotalMilliseconds);
        if (loadingCells > 0)
        {
            _framesWithLoading++;
        }

        _renderedSinceKey = true;
        Write(string.Create(CultureInfo.InvariantCulture,
            $"render,{Ms(Stopwatch.GetTimestamp())},{elapsed.TotalMilliseconds:0.###},{rows},{rebuilt},{loadingCells},0"));
    }

    public void Stop()
    {
        if (_subscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _subscribed = false;
        }

        _log?.Flush();
    }

    private void Subscribe()
    {
        if (!_subscribed)
        {
            CompositionTarget.Rendering += OnRendering;
            _subscribed = true;
            _lastFrame = -1;
        }
    }

    /// <summary>フレームごと (UI スレッド)。</summary>
    private void OnRendering(object? sender, object e)
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastFrame >= 0)
        {
            double interval = Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds;
            Push(_intervals, interval);
            Write(string.Create(CultureInfo.InvariantCulture, $"frame,{Ms(now)},{interval:0.###}"));
        }

        _lastFrame = now;
        if (_pendingKey >= 0 && _renderedSinceKey)
        {
            // キー入力のあと、描き直した内容を出すフレーム。
            Write(string.Create(CultureInfo.InvariantCulture, $"present,{Ms(now)},{Stopwatch.GetElapsedTime(_pendingKey, now).TotalMilliseconds:0.###}"));
            _pendingKey = -1;
        }

        if (Stopwatch.GetElapsedTime(_lastOverlay, now).TotalMilliseconds >= 250)
        {
            _lastOverlay = now;
            UpdateOverlay();
            _log?.Flush();
        }
    }

    private void UpdateOverlay()
    {
        double[] intervals = [.. _intervals];
        Array.Sort(intervals);
        double p99 = intervals.Length == 0 ? 0 : intervals[Math.Min(intervals.Length - 1, (int)Math.Ceiling(intervals.Length * 0.99) - 1)];
        double fps = intervals.Length == 0 ? 0 : 1000 / intervals.Average();
        double renderAvg = _renderTimes.Count == 0 ? 0 : _renderTimes.Average();
        double renderMax = _renderTimes.Count == 0 ? 0 : _renderTimes.Max();
        text.Text = string.Create(CultureInfo.InvariantCulture,
            $"fps {fps,5:0.0}  p99 {p99,5:0.0} ms\nrender {renderAvg,5:0.00} / {renderMax,5:0.00} ms\nloading frames {_framesWithLoading}  io wait 0");
    }

    private static void Push(Queue<double> queue, double value)
    {
        if (queue.Count == Window)
        {
            queue.Dequeue();
        }

        queue.Enqueue(value);
    }

    private string Ms(long timestamp) =>
        Stopwatch.GetElapsedTime(_origin, timestamp).TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture);

    private void Write(string line)
    {
        if (_logPath is null)
        {
            return;
        }

        try
        {
            _log ??= new StreamWriter(_logPath, append: true) { AutoFlush = false };
            _log.WriteLine(line);
        }
        catch (IOException ex)
        {
            AppLog.Warning($"HexView: 診断の記録を書けません ({ex.HResult:X8})");
            _logPath = null;
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLog.Warning($"HexView: 診断の記録を書けません ({ex.HResult:X8})");
            _logPath = null;
        }
    }
}
