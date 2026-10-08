using System.Diagnostics;
using System.Runtime.InteropServices;
using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.View;
using HexEditor.TestData;
using Xunit.Abstractions;

// 性能テストは計測の邪魔をしないよう、1 つずつ実行する (プロセスの I/O カウンタ・メモリ使用量も他のテストの影響を受けないようにする)。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace HexEditor.Performance.Tests;

/// <summary>性能テストの共通の道具 (計測・表示の読み込み・プロセスの I/O カウンタ・メモリ使用量)。</summary>
internal static class PerfSupport
{
    public const long MiB = TestDataCatalog.MiB;
    public const long GiB = TestDataCatalog.GiB;
    public const long TiB = TestDataCatalog.TiB;

    /// <summary>表示の行数 (1920×1080 の最大化したウィンドウで見える行数の目安)。</summary>
    public const int VisibleRows = 48;

    public static DocumentOptions Options() => new()
    {
        TempDirectory = Path.Combine(Path.GetTempPath(), "HexEditorTests", "recovery"),
    };

    /// <summary>
    /// このテストのプロセスが読み込まれた直後のプライベートバイト (テストの実行環境だけの分)。同じプロセスで先に動いたテストが
    /// 残したメモリ (OS に返されないネイティブの領域など) を、メモリ使用量の計測から除くのに使う。
    /// </summary>
    public static long StartupPrivateBytes { get; private set; }

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RecordStartupMemory()
    {
        using var process = Process.GetCurrentProcess();
        StartupPrivateBytes = process.PrivateMemorySize64;
    }

    public static Document Open(string id) => new(FileByteSource.Open(TestDataCatalog.Get(id)), Options());

    public static EditorState Editor(Document doc) => new(doc) { VisibleRows = VisibleRows };

    public static byte[] Read(Document doc, long offset, int length) => Read(doc.Current, offset, length);

    public static byte[] Read(DocumentSnapshot snapshot, long offset, int length)
    {
        byte[] buffer = new byte[length];
        Assert.True(snapshot.Read(offset, buffer).IsComplete);
        return buffer;
    }

    /// <summary>ファイルを直接読む (期待値)。</summary>
    public static byte[] ReadFile(string path, long offset, int length)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(path);
        byte[] buffer = new byte[length];
        Assert.Equal(length, RandomAccess.Read(handle, buffer, offset));
        return buffer;
    }

    /// <summary>
    /// 表示中の行 (一番上の行から <see cref="EditorState.VisibleRows"/> 行) を Hex ビューと同じ経路
    /// (<see cref="DocumentSnapshot.ReadForDisplay"/>) で読み、「読み込み中」のバイトがなくなるまで待つ (描画の完了の代わり)。
    /// </summary>
    public static byte[] ReadVisible(EditorState editor, TimeSpan? timeout = null)
    {
        Document doc = editor.Document;
        long start = editor.TopRow * editor.BytesPerRow;
        int length = (int)Math.Min((long)editor.VisibleRows * editor.BytesPerRow, Math.Max(0, doc.Length - start));
        byte[] bytes = new byte[length];
        var states = new ByteState[length];
        using var loaded = new SemaphoreSlim(0);
        void OnLoaded(object? sender, EventArgs e) => loaded.Release();
        doc.DataLoaded += OnLoaded;
        try
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                doc.Current.ReadForDisplay(start, bytes, states);
                if (!states.Contains(ByteState.Loading))
                {
                    Assert.DoesNotContain(ByteState.Unreadable, states);
                    return bytes;
                }

                TimeSpan left = (timeout ?? TimeSpan.FromSeconds(10)) - watch.Elapsed;
                Assert.True(left > TimeSpan.Zero, $"表示の読み込みが終わりません (オフセット 0x{start:X})");
                loaded.Wait(left < TimeSpan.FromMilliseconds(50) ? left : TimeSpan.FromMilliseconds(50));
            }
        }
        finally
        {
            doc.DataLoaded -= OnLoaded;
        }
    }

    /// <summary><paramref name="action"/> にかかった時間。</summary>
    public static TimeSpan Time(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return watch.Elapsed;
    }

    /// <summary>1 回目を除いた最大値 (1 回目は JIT とキャッシュの準備を含むため)。</summary>
    public static TimeSpan MaxExceptFirst(IReadOnlyList<TimeSpan> times) => times.Skip(1).Max();

    public static string Ms(TimeSpan t) => $"{t.TotalMilliseconds:F1} ms";

    public static string Summary(IReadOnlyList<TimeSpan> times) =>
        $"最大 {Ms(times.Skip(1).Max())} (1 回目 {Ms(times[0])}、中央値 {Ms(times.Order().ElementAt(times.Count / 2))}、{times.Count} 回)";

    /// <summary>GC を済ませてからのこのプロセスのプライベートバイト (テスト方針 7.3)。</summary>
    public static long PrivateBytesAfterGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    /// <summary>このプロセスの I/O の転送量 (読み込み・書き込みのバイト数。GetProcessIoCounters)。</summary>
    public static (long Read, long Written) IoCounters()
    {
        using var process = Process.GetCurrentProcess();
        Assert.True(GetProcessIoCounters(process.Handle, out IoCountersData counters));
        return ((long)counters.ReadTransferCount, (long)counters.WriteTransferCount);
    }

    /// <summary>
    /// テスト用フォルダに作るテストデータ (保存で書き換えるため、共有のキャッシュとは別に作る)。スパースファイルはスパースのまま作る。
    /// 終わったら <see cref="IDisposable.Dispose"/> でフォルダごと消す。
    /// </summary>
    public sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("hexeditor-perf").FullName;

        public string Generate(string id) => TestDataCatalog.Generate(id, Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>計測結果をテストの出力に書く (レポートで数値を確かめるため)。</summary>
    public static void Report(this ITestOutputHelper output, string message) => output.WriteLine(message);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCountersData
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCountersData counters);
}
