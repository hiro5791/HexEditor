using System.Security.Cryptography;
using HexEditor.Core.Engine;
using HexEditor.Core.Saving;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Saving;

/// <summary>ENG-13 範囲を指定して部分的に開く (保存)、ENG-24 ずらしながらのその場保存。</summary>
[Collection("InPlaceSaver")]
public sealed class RangeAndShiftSaveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("hexeditor-shift").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Journals => Path.Combine(_dir, "recovery");

    private SaveSettings Settings(IVolumeInfoProvider? volumes = null) => new()
    {
        JournalDirectory = Journals,
        Volumes = volumes ?? SystemVolumeInfoProvider.Instance,
        SpillDirectory = Path.Combine(_dir, "spill"),
    };

    private static void Execute(SavePlan plan)
    {
        SaveResult result = SavePlanner.Execute(plan);
        SavePlanner.Complete(plan, result);
    }

    // ---- ENG-13 ----

    [Fact]
    public void Range_document_reads_the_range_with_its_address()
    {
        string path = Path.Combine(_dir, "seq.bin");
        File.Copy(TestDataCatalog.Get("TD-SEQ-1M"), path);
        using var source = FileByteSource.OpenRange(path, 0x40000, 0x100000, resizable: false);
        Assert.Equal(0xC0000, source.Length); // 末尾までに切り詰める
        Assert.Equal(0x40000, source.BaseAddress);
        Assert.False(source.Capabilities.HasFlag(SourceCapabilities.CanResize));
        byte[] b = new byte[4];
        source.Read(0x10, b);
        Assert.Equal(new byte[] { 0x10, 0x11, 0x12, 0x13 }, b);
        Assert.Throws<ArgumentOutOfRangeException>(() => FileByteSource.OpenRange(path, 0x100000, 1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => FileByteSource.OpenRange(path, 0, 0, false));
    }

    [Fact]
    [Trait(TC, "TC-ENG-13-02")]
    public void Changing_one_byte_in_a_range_writes_only_that_byte()
    {
        string path = TestDataCatalog.Generate("TD-ENG-SPARSE-10G", _dir);
        const long Start = 5L << 30;
        const long Length = 1 << 20;
        long fileLength = new FileInfo(path).Length;
        byte[] Window()
        {
            using var check = FileByteSource.Open(path);
            byte[] w = new byte[Length + 8192];
            check.Read(Start - 4096, w);
            return w;
        }

        byte[] before = Window();
        using var doc = new Document(FileByteSource.OpenRange(path, Start, Length, resizable: false), Options());
        doc.Overwrite(0x10, [0x5A]);
        SavePlan plan = SavePlanner.Plan(doc, null, Settings());
        Assert.Equal(SaveMethod.InPlace, plan.Method);
        Execute(plan);

        byte[] after = Window();
        Assert.Equal(fileLength, new FileInfo(path).Length);
        Assert.Equal(0x5A, after[4096 + 0x10]);
        before[4096 + 0x10] = 0x5A;
        Assert.Equal(before, after);

        // 保存の後も範囲のドキュメントのまま (オフセット 0 は開始位置)。
        Assert.Equal(0x5A, Read(doc.Current, 0x10, 1)[0]);
        Assert.Equal(Length, doc.Length);
    }

    [Fact]
    [Trait(TC, "TC-ENG-13-03")]
    public void Inserting_into_a_resizable_range_keeps_the_data_around_it()
    {
        string path = Path.Combine(_dir, "seq.bin");
        File.Copy(TestDataCatalog.Get("TD-SEQ-1M"), path);
        byte[] original = File.ReadAllBytes(path);
        using var doc = new Document(FileByteSource.OpenRange(path, 0x40000, 0x1000, resizable: true), Options());
        doc.InsertPattern(0x800, 10, [0xEE]);
        SavePlan plan = SavePlanner.Plan(doc, null, Settings());
        Assert.Equal(SaveMethod.Safe, plan.Method);
        Execute(plan);

        byte[] saved = File.ReadAllBytes(path);
        Assert.Equal(1_048_586, saved.Length);
        Assert.Equal(original[..0x40800], saved[..0x40800]);
        Assert.All(saved[0x40800..0x4080A], b => Assert.Equal(0xEE, b));
        Assert.Equal(original[0x40800..], saved[0x4080A..]);
        Assert.Equal(0x100A, doc.Length);
        Assert.False(doc.IsModified);
        Assert.Equal(0x40000, doc.Source.BaseAddress);
    }

    // ---- ENG-24 ----

    private static void ShiftSave(Document doc, SaveSettings settings)
    {
        SavePlan plan = SavePlanner.UseShiftInPlace(SavePlanner.Plan(doc, null, settings));
        Assert.Equal(SaveIssue.ConfirmShift, plan.Issue);
        Execute(SavePlanner.ConfirmShift(plan));
    }

    [Fact]
    public void Shift_save_inserting_at_the_start_and_undo()
    {
        string path = Path.Combine(_dir, "seq.bin");
        File.Copy(TestDataCatalog.Get("TD-SEQ-1M"), path);
        byte[] original = File.ReadAllBytes(path);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.InsertPattern(0, 1024, [0x5A]);
        ShiftSave(doc, Settings());

        byte[] saved = File.ReadAllBytes(path);
        Assert.Equal([.. Enumerable.Repeat((byte)0x5A, 1024), .. original], saved);
        Assert.Empty(ShiftSaver.FindInterrupted(Journals));
        Assert.False(doc.IsModified);

        // 保存前の Undo 履歴は退避した旧内容を読む。
        doc.Undo();
        Assert.Equal(original, ReadAll(doc.Current));
    }

    [Fact]
    public void Shift_save_handles_swapped_ranges_with_a_spill()
    {
        string path = Path.Combine(_dir, "seq.bin");
        File.Copy(TestDataCatalog.Get("TD-SEQ-1M"), path);
        using var doc = new Document(FileByteSource.Open(path), Options());

        // 前半と後半の入れ替え (循環する依存関係) と、長さの変更。
        doc.InsertCopy(0, 0x80000, 0x80000);
        doc.Delete(0x100000, 0x80000);
        doc.Delete(0x10, 0x20);
        byte[] expected = ReadAll(doc.Current);
        SavePlan plan = SavePlanner.UseShiftInPlace(SavePlanner.Plan(doc, null, Settings()));
        Assert.True(plan.Shift!.SpillBytes > 0);
        Execute(SavePlanner.ConfirmShift(plan));
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public void Shift_option_is_offered_only_when_the_growth_fits()
    {
        // 空き容量不足のダイアログの「その場でずらしながら保存」は、伸びる分 + 16 MiB の空きがある場合だけ出す (ENG-25 の仕様 1・4)。
        string path = Path.Combine(_dir, "seq.bin");
        File.Copy(TestDataCatalog.Get("TD-SEQ-1M"), path);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.InsertPattern(0, 1024, [0x5A]);

        var volumes = new FakeVolumes(4L << 20);
        SavePlan plan = SavePlanner.Plan(doc, null, Settings(volumes));
        Assert.Equal(SaveIssue.InsufficientSpace, plan.Issue);
        Assert.False(plan.CanShift);

        // 安全な保存 (1 MiB + 1 KiB + 16 MiB) は入らないが、伸びる分 (1 KiB + 16 MiB) は入る。
        volumes.Free = (16L << 20) + (512 << 10);
        plan = SavePlanner.Plan(doc, null, Settings(volumes));
        Assert.Equal(SaveIssue.InsufficientSpace, plan.Issue);
        Assert.True(plan.CanShift);
    }

    [Fact]
    public void Shift_save_checks_the_spill_space_before_writing()
    {
        // 退避ファイルの置き場所には、退避の量 + 16 MiB が要る (ENG-25 の仕様 1)。足りなければ書き始める前に空き容量不足にする。
        string path = Path.Combine(_dir, "seq.bin");
        File.Copy(TestDataCatalog.Get("TD-SEQ-1M"), path);
        byte[] original = File.ReadAllBytes(path);
        string spill = Directory.CreateDirectory(Path.Combine(_dir, "spill")).FullName;
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.InsertCopy(0, 0x80000, 0x80000);
        doc.Delete(0x100000, 0x80000);
        doc.Delete(0x10, 0x20);

        var volumes = new FakeVolumes(1L << 40) { SpillFolder = spill, SpillFree = 16L << 20 };
        SavePlan plan = SavePlanner.UseShiftInPlace(SavePlanner.Plan(doc, null, Settings(volumes)));
        Assert.Equal(SaveIssue.InsufficientSpace, plan.Issue);
        Assert.False(plan.CanShift);
        Assert.True(plan.Space!.Required > 16L << 20);
        Assert.Equal(16L << 20, plan.Space.Available);
        Assert.Equal(original, File.ReadAllBytes(path));

        // 保存先の空きも足りない場合は、ダイアログに「その場でずらしながら保存」を出さない。
        volumes.Free = 16L << 20;
        Assert.False(SavePlanner.Plan(doc, null, Settings(volumes)).CanShift);

        volumes.SpillFree = 64L << 20;
        Assert.True(SavePlanner.Plan(doc, null, Settings(volumes)).CanShift);
    }

    [Fact]
    [Trait(TC, "TC-ENG-24-01")]
    public void Shift_save_works_when_free_space_is_smaller_than_the_file()
    {
        // 空き容量がファイルサイズより小さいドライブ (仮想ディスク) の代わりに、ボリュームの情報を差し替えて、空き容量を 32 MiB の
        // ファイルより小さい 20 MiB にする。安全な保存 (32 MiB + 16 MiB) は入らないが、ずらしながらの保存 (伸びる 1 KiB + 16 MiB) は入る。
        string path = Path.Combine(_dir, "big.bin");
        TestDataCatalog.Generate("TD-RANDOM-16M", _dir);
        byte[] half = File.ReadAllBytes(Path.Combine(_dir, "TD-RANDOM-16M.bin"));
        byte[] original = [.. half, .. half];
        File.WriteAllBytes(path, original);
        var volumes = new FakeVolumes(20L << 20);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.InsertPattern(0, 1024, [0x5A]);

        SavePlan plan = SavePlanner.Plan(doc, null, Settings(volumes));
        Assert.Equal(SaveIssue.InsufficientSpace, plan.Issue);
        Assert.True(original.Length > volumes.Free);
        Assert.True(plan.CanShift);
        plan = SavePlanner.UseShiftInPlace(plan);
        Assert.Equal(SaveIssue.ConfirmShift, plan.Issue);
        Assert.Equal(original.Length + 1024, plan.Shift!.WriteBytes);
        Execute(SavePlanner.ConfirmShift(plan));
        Assert.Equal([.. Enumerable.Repeat((byte)0x5A, 1024), .. original], File.ReadAllBytes(path));

        // もう一度 (実行のたびに確認する計画になる)。
        doc.Insert(100, [1]);
        SavePlan again = SavePlanner.UseShiftInPlace(SavePlanner.Plan(doc, null, Settings(volumes)));
        Assert.Equal(SaveIssue.ConfirmShift, again.Issue);
    }

    [Fact]
    public void Undo_history_is_discarded_when_the_backup_does_not_fit()
    {
        string path = Path.Combine(_dir, "seq.bin");
        File.Copy(TestDataCatalog.Get("TD-SEQ-1M"), path);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Insert(0, [1, 2, 3]);
        // 伸びる分 (3 バイト + 余裕 16 MiB) は入るが、Undo 用の退避 (約 1 MiB + 余裕) は入らない空き容量。
        SavePlan plan = SavePlanner.UseShiftInPlace(SavePlanner.Plan(doc, null, Settings(new FakeVolumes((16 << 20) + (512 << 10)))));
        Assert.True(plan.Shift!.DiscardHistory);
        Execute(SavePlanner.ConfirmShift(plan));
        Assert.False(doc.History.CanUndo);
        Assert.Equal(1_048_579, new FileInfo(path).Length);
    }

    [Fact]
    public void Setting_selects_shift_save_for_length_changes()
    {
        string path = Path.Combine(_dir, "seq.bin");
        File.Copy(TestDataCatalog.Get("TD-SEQ-1M"), path);
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Delete(0, 1);
        SavePlan plan = SavePlanner.Plan(doc, null, Settings() with { ShiftWhenLengthChanges = true });
        Assert.Equal((SaveMethod.ShiftInPlace, SaveIssue.ConfirmShift), (plan.Method, plan.Issue));
    }

    public enum EditKind
    {
        Insert,
        Delete,
        Overwrite,
        Duplicate,
        Fill,
    }

    public sealed record Edit(EditKind Kind, int A, int B, int C, byte Value);

    /// <summary>挿入・削除・上書き・範囲の複製 (入れ替えになる循環を含む)・塗りつぶしを 1〜50 回含む編集パターン (種から作る)。</summary>
    private static Edit[][] Patterns(int count, int seed)
    {
        var random = new Random(seed);
        var patterns = new Edit[count][];
        for (int p = 0; p < count; p++)
        {
            patterns[p] = new Edit[random.Next(1, 51)];
            for (int i = 0; i < patterns[p].Length; i++)
            {
                patterns[p][i] = new Edit((EditKind)random.Next(5), random.Next(), random.Next(1, 70_000), random.Next(), (byte)random.Next(256));
            }
        }

        return patterns;
    }

    private static void Apply(Document doc, Edit e)
    {
        long length = doc.Length;
        long at = length == 0 ? 0 : e.A % (length + 1);
        long span = Math.Min(e.B, Math.Max(0, length - at));
        switch (e.Kind)
        {
            case EditKind.Insert:
                doc.InsertPattern(at, e.B % 2048 + 1, [e.Value, (byte)(e.Value + 1)]);
                break;
            case EditKind.Delete when span > 0:
                doc.Delete(at, span);
                break;
            case EditKind.Overwrite when span > 0:
                doc.Overwrite(at, Enumerable.Range(0, (int)Math.Min(span, 4096)).Select(i => (byte)(e.Value ^ i)).ToArray());
                break;
            case EditKind.Duplicate when length > 0:
                long from = e.C % length;
                doc.InsertCopy(at, from, Math.Min(e.B, length - from));
                break;
            case EditKind.Fill when span > 0:
                doc.OverwritePattern(at, span, [e.Value]);
                break;
        }
    }

    private void RunPatterns(string sourceId, int count, int seed)
    {
        string original = TestDataCatalog.Get(sourceId);
        Edit[][] patterns = Patterns(count, seed);
        int index = 0;
        foreach (Edit[] pattern in patterns)
        {
            string a = Path.Combine(_dir, $"shift-{index}.bin");
            string b = Path.Combine(_dir, $"safe-{index}.bin");
            File.Copy(original, a);
            File.Copy(original, b);
            using (var shifted = new Document(FileByteSource.Open(a), Options()))
            using (var safe = new Document(FileByteSource.Open(b), Options()))
            {
                foreach (Edit e in pattern)
                {
                    Apply(shifted, e);
                    Apply(safe, e);
                }

                if (shifted.IsModified)
                {
                    if (InPlaceSaver.CanSaveInPlace(shifted.Current, a))
                    {
                        Execute(SavePlanner.Plan(shifted, null, Settings()));
                    }
                    else
                    {
                        ShiftSave(shifted, Settings());
                    }

                    Execute(SavePlanner.UseSafeSave(SavePlanner.Plan(safe, null, Settings())));
                }
            }

            Assert.True(SHA256.HashData(File.ReadAllBytes(a)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(b))), $"pattern {index}");
            File.Delete(a);
            File.Delete(b);
            index++;
        }
    }

    [Fact]
    [Trait(TC, "TC-ENG-24-02")]
    public void Shift_save_matches_safe_save_for_random_edits()
    {
        // プルリクエストごとの規模。1,000 パターンの全規模は Nightly で行う。
        RunPatterns("TD-SEQ-1M", 60, 24_02);
        RunPatterns("TD-EMPTY", 20, 24_03);
    }

    [Fact]
    [Trait(TC, "TC-ENG-24-02")]
    [Trait("Category", "Nightly")]
    public void Shift_save_matches_safe_save_for_random_edits_full_scale()
    {
        RunPatterns("TD-SEQ-1M", 1000, 24_12);
        RunPatterns("TD-EMPTY", 100, 24_13);
    }

    /// <summary>空き容量を差し替えるボリュームの情報。</summary>
    private sealed class FakeVolumes(long free) : IVolumeInfoProvider
    {
        public long Free { get; set; } = free;

        /// <summary>このフォルダ (退避ファイルの置き場所) だけ空き容量を <see cref="SpillFree"/> にする。</summary>
        public string? SpillFolder { get; init; }

        public long SpillFree { get; set; }

        public VolumeInfo? GetVolume(string folder) =>
            SystemVolumeInfoProvider.Instance.GetVolume(folder) is { } real
                ? real with
                {
                    AvailableFreeSpace = SpillFolder is not null && folder.StartsWith(SpillFolder, StringComparison.OrdinalIgnoreCase)
                        ? SpillFree : Free,
                }
                : null;
    }
}
