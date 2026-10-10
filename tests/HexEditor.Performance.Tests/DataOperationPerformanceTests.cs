using HexEditor.Core.Editing.Transforms;
using HexEditor.Core.Engine;
using HexEditor.Core.View;
using HexEditor.TestData;
using Xunit.Abstractions;
using static HexEditor.Performance.Tests.PerfSupport;

namespace HexEditor.Performance.Tests;

/// <summary>
/// ビット単位の挿入と削除 (EDIT-37) の性能。ダイアログの実行と同じ <see cref="BitShifter"/> を直接呼び、表示中の行を読み終えるまでを計る。
/// </summary>
[Trait("Category", "Performance")]
public sealed class DataOperationPerformanceTests(ITestOutputHelper output)
{
    private const string TC = "TC";
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);

    [Fact]
    [Trait(TC, "TC-EDIT-37-02")]
    public void InsertingEightBitsIntoOneHundredGigabytesIsFast()
    {
        using Document doc = Open("TD-SPARSE-100G");
        EditorState editor = Editor(doc);
        long length = doc.Length;
        var spec = new DataOperationSpec
        {
            Kind = DataOperationKind.InsertBits,
            BitOffset = 0x10,
            BitIndex = 7,
            BitCount = 8,
            FillWithOne = true,
            BitScope = BitShiftScope.ToEnd,
        };
        var times = new List<TimeSpan>();
        for (int i = 0; i < 12; i++)
        {
            times.Add(Time(() =>
            {
                // ピース操作で行うため確認ダイアログは出ない (書き直すバイトが 0)。
                Assert.Equal(0, BitShifter.Analyze(doc.Length, doc.CanResize, null, spec).RewriteBytes);
                using BitEditPlan plan = BitShifter.For(doc).Build(doc.Current, doc.CanResize, null, spec);
                plan.Apply(doc, "ビットの挿入");
                ReadVisible(editor);
            }));
            Assert.Equal(length + 1, doc.Length);
            Assert.Equal(0xFF, Read(doc, 0x10, 1)[0]);
            Assert.Equal(TestDataCatalog.Marker(0x40000000), Read(doc, 0x40000001, 17));
            editor.Undo();
            Assert.Equal(length, doc.Length);
        }

        output.Report($"100 GB への 8 ビットの挿入: {Summary(times)}");
        TimeLimit(MaxExceptFirst(times) <= Limit, Summary(times));
    }

    /// <summary>
    /// 長時間処理のデータ演算の処理速度 (EDIT-31 の「巨大ファイル」2): XOR (鍵) とバイトスワップの速度が、同じ範囲を 1 MiB ずつ読んで一時ファイルに
    /// 書くだけ (計算なし) の速度の 80% 以上。読み込み元はスパースファイル (TD-SPARSE-100G) の先頭 512 MiB で、比べるのは計算の分の遅れ。
    /// </summary>
    [Theory]
    [InlineData(DataOperationKind.Xor)]
    [InlineData(DataOperationKind.ByteSwap32)]
    public void Data_operation_throughput_is_at_least_80_percent_of_copying(DataOperationKind kind)
    {
        const long length = 512 * MiB;
        using Document doc = Open("TD-SPARSE-100G");
        TargetRange[] range = [new(0, length)];
        var spec = new DataOperationSpec
        {
            Kind = kind,
            OperandSource = kind == DataOperationKind.Xor ? OperandSource.KeyBytes : OperandSource.Number,
            Key = [0xDE, 0xAD, 0xBE],
        };
        DataOperationRunner runner = DataOperationRunner.For(doc);
        string temp = Path.Combine(doc.Options.TempDirectory, doc.Id.ToString("N"));

        var copies = new List<TimeSpan>();
        var operations = new List<TimeSpan>();
        for (int i = 0; i < 5; i++)
        {
            copies.Add(Time(() => CopyToTemp(doc.Current, length, temp)));
            operations.Add(Time(() =>
            {
                DataOperationResult result = runner.Run(doc.Current, range, spec);
                Assert.True(result.Stats.Changed || kind != DataOperationKind.Xor);
                TransformApplier.DisposeAll(result.Replacements);
            }));
        }

        TimeSpan copy = copies.Skip(1).Min();
        TimeSpan operation = operations.Skip(1).Min();
        double ratio = copy.TotalSeconds / operation.TotalSeconds;
        string message = $"{kind}: 演算 {length / MiB / operation.TotalSeconds:F0} MiB/s、読み書きだけ {length / MiB / copy.TotalSeconds:F0} MiB/s (比 {ratio:P0})";
        output.Report(message);
        TimeLimit(ratio >= 0.8, message);
    }

    /// <summary>計算なしで 1 MiB ずつ読み、一時ファイルに書く (演算と同じ読み書きの経路)。</summary>
    private static void CopyToTemp(DocumentSnapshot snapshot, long length, string folder)
    {
        using var writer = new TempContentWriter(folder, length, "perf");
        byte[] buffer = new byte[DataOperationRunner.ChunkSize];
        for (long position = 0; position < length; position += buffer.Length)
        {
            int n = (int)Math.Min(buffer.Length, length - position);
            Assert.True(snapshot.Read(position, buffer.AsSpan(0, n)).IsComplete);
            writer.Write(buffer.AsSpan(0, n));
        }
    }
}
