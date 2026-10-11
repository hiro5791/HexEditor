using System.Runtime.CompilerServices;

namespace HexEditor.Core.Tests.Support;

/// <summary>
/// テスト全体で、スレッドプールの最小スレッド数を増やす。多くのテストが並行に動き、Thread.Sleep で待つテストもあるため、
/// 混んだ CI のランナーではスレッドプールが飢え、ブロックキャッシュの読み込み (Task.Run と非同期のファイル読み込みの完了) が
/// 10 秒以上遅れることがある (BlockCacheTests の「読み込みが終わりません」)。
/// </summary>
internal static class ThreadPoolSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        ThreadPool.GetMinThreads(out int worker, out int io);
        ThreadPool.SetMinThreads(Math.Max(worker, 128), Math.Max(io, 64));
    }
}
