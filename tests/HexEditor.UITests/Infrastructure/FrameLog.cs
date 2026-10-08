using System.Globalization;
using System.Text.Json.Nodes;

namespace HexEditor.UITests.Infrastructure;

/// <summary>
/// Hex ビューの診断の記録 (テスト方針 7.2「描画の計測ログ」。アプリの HexViewDiagnostics が書く CSV) を読んで集計する。
/// 行の形: <c>frame,時刻ms,間隔ms</c> / <c>render,時刻ms,描画ms,行数,作り直した行数,読み込み中のセル数,I/O待ち回数</c> /
/// <c>key,時刻ms,キー</c> / <c>present,時刻ms,キー入力からの ms</c>。
/// </summary>
public sealed class FrameLog
{
    public sealed record Frame(double Time, double Interval);

    public sealed record Render(double Time, double Milliseconds, int LoadingCells, int IoWaits);

    public sealed record Key(double Time, string Name);

    public List<Frame> Frames { get; } = [];

    public List<Render> Renders { get; } = [];

    public List<Key> Keys { get; } = [];

    /// <summary>前のフレームから起きた GC (世代ごとの回数と止まった時間)。遅いフレームの原因を調べるために出力する。</summary>
    public sealed record Gc(double Time, int Gen0, int Gen1, int Gen2, double PauseMilliseconds);

    public List<Gc> Collections { get; } = [];

    /// <summary>GC の開始 (世代・理由・種類。理由 1 は明示的な要求、種類 0 は停止する GC)。</summary>
    public sealed record GcStart(double Time, int Depth, int Reason, int Type);

    public List<GcStart> CollectionStarts { get; } = [];

    public static FrameLog Read(string path)
    {
        var log = new FrameLog();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            string[] f = line.Split(',');
            double Num(int i) => double.Parse(f[i], CultureInfo.InvariantCulture);
            switch (f[0])
            {
                case "frame" when f.Length >= 3:
                    log.Frames.Add(new Frame(Num(1), Num(2)));
                    break;
                case "render" when f.Length >= 7:
                    log.Renders.Add(new Render(Num(1), Num(2), (int)Num(5), (int)Num(6)));
                    break;
                case "key" when f.Length >= 3:
                    log.Keys.Add(new Key(Num(1), f[2]));
                    break;
                case "gcstart" when f.Length >= 5:
                    log.CollectionStarts.Add(new GcStart(Num(1), (int)Num(2), (int)Num(3), (int)Num(4)));
                    break;
                case "gc" when f.Length >= 6:
                    log.Collections.Add(new Gc(Num(1), (int)Num(2), (int)Num(3), (int)Num(4), Num(5)));
                    break;
            }
        }

        return log;
    }

    /// <summary>[<paramref name="from"/>, <paramref name="to"/>) の間に出したフレームの間隔。</summary>
    public IReadOnlyList<double> IntervalsBetween(double from, double to) =>
        [.. Frames.Where(f => f.Time - f.Interval >= from && f.Time < to).Select(f => f.Interval)];

    /// <summary>
    /// 各キー入力 (または <c>mark</c>) から、その後の描画を画面に出した最初のフレームまでの時間 (ms)。
    /// <paramref name="loaded"/> なら「読み込み中」のセルのない描画を待つ (仮表示でない値を出すまで)。
    /// 描画がない (表示が変わらない) キーは NaN。
    /// </summary>
    public IReadOnlyList<double> KeyToFrame(bool loaded = false)
    {
        var result = new List<double>();
        for (int i = 0; i < Keys.Count; i++)
        {
            double t = Keys[i].Time;
            double next = i + 1 < Keys.Count ? Keys[i + 1].Time : double.MaxValue;
            Render? render = Renders.FirstOrDefault(r => r.Time >= t && r.Time < next && (!loaded || r.LoadingCells == 0));
            Frame? frame = render is null ? null : Frames.FirstOrDefault(f => f.Time >= render.Time);
            result.Add(frame is null ? double.NaN : frame.Time - t);
        }

        return result;
    }

    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        double[] sorted = [.. values.Order()];
        return sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * p) - 1, 0, sorted.Length - 1)];
    }

    /// <summary>「60 fps を保つ」の判定 (テスト方針 7.3): フレームの間隔の 99 パーセンタイルが 16.7 ms 以下、かつ最大が 50 ms 以下。</summary>
    public static string Describe(IReadOnlyList<double> intervals) => intervals.Count == 0
        ? "フレームなし"
        : string.Create(CultureInfo.InvariantCulture,
            $"{intervals.Count} フレーム、平均 {intervals.Average():F2} ms、p99 {Percentile(intervals, 0.99):F2} ms、最大 {intervals.Max():F2} ms");

    /// <summary>前回の計測と比べる (6.6: 20% 以上の悪化は警告)。結果は TestResults/performance/&lt;名前&gt;.json に残す。</summary>
    public static string? CompareWithPrevious(string name, double value)
    {
        string folder = Path.Combine(AppLocator.RepositoryRoot, "TestResults", "performance");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name + ".json");
        string? warning = null;
        if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["value"]?.GetValue<double>() is double previous && previous > 0
            && value > previous * 1.2)
        {
            warning = string.Create(CultureInfo.InvariantCulture, $"警告: {name} が前回 ({previous:F2}) より 20% 以上悪化しました ({value:F2})。");
        }

        File.WriteAllText(path, new JsonObject { ["value"] = value, ["at"] = DateTimeOffset.Now.ToString("O") }.ToJsonString());
        return warning;
    }
}
