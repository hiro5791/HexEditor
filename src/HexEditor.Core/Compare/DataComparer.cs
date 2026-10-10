using System.Diagnostics;

namespace HexEditor.Core.Compare;

/// <summary>
/// 比較の入口 (ANA-01 の仕様 5)。方式に応じて単純比較 (ANA-02) か挿入・削除を考慮した比較 (ANA-03) を行い、結果を
/// <see cref="CompareResult"/> に書く。長時間処理の中 (バックグラウンドのスレッド) で呼ぶ。キャンセルした場合は
/// <see cref="OperationCanceledException"/> を投げるが、それまでに見つかった差分は結果に残る (<see cref="CompareResult.StoppedAt"/>)。
/// </summary>
public static class DataComparer
{
    /// <param name="progress">比較した位置 (相対位置の大きい方) を知らせる。</param>
    public static void Run(CompareOptions options, CompareResult result, CancellationToken cancellationToken = default, Action<long>? progress = null)
    {
        if (options.Validate() is { } bad)
        {
            throw new ArgumentException("比較のオプションが範囲外です: " + bad, nameof(options));
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            if (options.Method == CompareMethod.Simple)
            {
                SimpleComparer.Run(options, result, cancellationToken, progress);
            }
            else
            {
                InsertDeleteComparer.Run(options, result, cancellationToken, progress);
            }

            result.State = CompareState.Completed;
        }
        catch (OperationCanceledException)
        {
            result.State = CompareState.Cancelled;
            throw;
        }
        catch (Exception ex)
        {
            result.State = CompareState.Failed;
            result.Error = ex;
            throw;
        }
        finally
        {
            result.Elapsed = Stopwatch.GetElapsedTime(started);
        }
    }

    /// <summary>
    /// ピースツリーから求めた差分 (保存済みの内容との比較の方式 2。ANA-08) を結果にする。左は保存済みの内容 (ディスク上)、右は編集中。
    /// </summary>
    public static void FromPieces(IReadOnlyList<DiffRange> diffs, CompareResult result)
    {
        long different = 0;
        foreach (DiffRange d in diffs)
        {
            result.Diffs.Add(d);
            result.Count(d.Kind, d.MaxLength);
            different += d.LeftLength;
        }

        result.AddMatched(Math.Max(0, result.Left.Length - different));
        result.ReportPosition(result.Left.Length, result.Right.Length);
        result.State = CompareState.Completed;
    }
}
