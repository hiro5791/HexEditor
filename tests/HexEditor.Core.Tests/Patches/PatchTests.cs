using FsCheck;
using FsCheck.Fluent;
using HexEditor.Core.Engine;
using HexEditor.Core.Formats;
using HexEditor.Core.Patches;
using HexEditor.Core.Sources;
using HexEditor.TestData;
using static HexEditor.Core.Tests.Support.DocumentAssert;

namespace HexEditor.Core.Tests.Patches;

/// <summary>EDIT-42 パッチの作成・EDIT-43 パッチの適用 (IPS・IPS32・UPS・BPS・独自形式)、EDIT-44 自己適用型パッチの入れ物。</summary>
public sealed class PatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "HexEditorTests", "patch-" + Guid.NewGuid().ToString("N"));

    public PatchTests() => Directory.CreateDirectory(_dir);

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

    /// <summary>TD-EDIT-PATCH-SRC (種 4201 の乱数 1 MiB)。</summary>
    private static byte[] Src() => File.ReadAllBytes(TestDataCatalog.Get("TD-EDIT-PATCH-SRC"));

    private static byte[] Mod3() => File.ReadAllBytes(TestDataCatalog.Get("TD-EDIT-PATCH-MOD3"));

    private static byte[] Ins100() => File.ReadAllBytes(TestDataCatalog.Get("TD-EDIT-PATCH-INS100"));

    private static Document Open(byte[] data) => new(new MemoryByteSource(data), Options());

    private static byte[] Create(Document doc, PatchFormat format, PatchWriteOptions? options = null)
    {
        var creator = new PatchCreator(PatchDiff.FromSaved(doc.DataSnapshot));
        using var ms = new MemoryStream();
        creator.Write(format, ms, options);
        return ms.ToArray();
    }

    private static void Apply(Document doc, byte[] patch, bool reverse = false)
    {
        using LoadedPatch p = PatchFiles.Load(new MemoryStream(patch));
        p.ApplyTo(doc, reverse);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-42-01")]
    public void IpsFromThreeOverwritesHasThreeRecordsAndMatchesMod3()
    {
        byte[] src = Src(), mod3 = Mod3();
        using Document doc = Open(src);
        foreach (long at in new long[] { 0x100, 0x8000, 0xF0000 })
        {
            doc.Overwrite(at, [mod3[at]]);
        }

        var creator = new PatchCreator(PatchDiff.FromSaved(doc.DataSnapshot));
        Assert.Equal(3, creator.Estimate(PatchFormat.Ips).Records);
        byte[] ips = Create(doc, PatchFormat.Ips);

        // 別のツール (ここでは TOOL-12 の読み込みの規則による適用) で元ファイルに当てると書き換え後と一致する。Flips での確認は FlipsTests。
        Assert.Equal(mod3, IpsPatch.Apply(src, IpsPatch.Read(new MemoryStream(ips))));
        Assert.Equal(PatchFormat.Ips, PatchFiles.Detect(new MemoryStream(ips)));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-42-02")]
    public void BpsOfMiddleInsertIsSmallAndIpsWarns()
    {
        byte[] src = Src();
        using Document doc = Open(src);
        doc.InsertPattern(0x80000, 100, [0xAA]);
        var creator = new PatchCreator(PatchDiff.FromSaved(doc.DataSnapshot));
        Assert.True(creator.Estimate(PatchFormat.Ips).Inefficient);
        Assert.False(creator.Estimate(PatchFormat.Bps).Inefficient);
        byte[] bps = Create(doc, PatchFormat.Bps);
        Assert.True(bps.Length < 1024, $"BPS は {bps.Length} バイト");
        Assert.Equal(creator.Estimate(PatchFormat.Bps).EstimatedSize, bps.Length);

        using Document target = Open(Src());
        Apply(target, bps);
        Assert.Equal(Ins100(), ReadAll(target.Current));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-42-03")]
    public void IpsUnavailableAtSixteenMegabytes()
    {
        string path = TestDataCatalog.Get("TD-MARKERS-1G");
        using var doc = new Document(FileByteSource.Open(path), Options());
        doc.Overwrite(0x1000000, [0xFF]);
        var creator = new PatchCreator(PatchDiff.FromSaved(doc.DataSnapshot));
        Assert.Equal(PatchUnavailableReason.IpsOffsetLimit, creator.Estimate(PatchFormat.Ips).Unavailable);
        Assert.True(creator.Estimate(PatchFormat.Ips32).IsAvailable);
        Assert.Equal(PatchFormat.Ips32, creator.AutoIpsFormat());

        doc.Undo();
        doc.Overwrite(0xFFFFFF, [0xFF]);
        creator = new PatchCreator(PatchDiff.FromSaved(doc.DataSnapshot));
        Assert.True(creator.Estimate(PatchFormat.Ips).IsAvailable);
        Assert.Equal(PatchFormat.Ips, creator.AutoIpsFormat());
    }

    [Fact]
    [Trait(TC, "TC-EDIT-42-04")]
    public void HexPatchRoundTripForRandomEdits()
    {
        byte[] src = Src()[..(64 * 1024)];
        Gen<int[]> edits = Gen.Choose(0, int.MaxValue).ArrayOf().Where(a => a.Length is >= 1 and <= 50);
        Prop.ForAll(edits.ToArbitrary(), seeds =>
        {
            using Document doc = Open(src);
            foreach (int seed in seeds)
            {
                var rng = new Random(seed);
                long pos = doc.Length == 0 ? 0 : rng.NextInt64(doc.Length);
                byte[] data = new byte[rng.Next(1, 300)];
                rng.NextBytes(data);
                switch (rng.Next(3))
                {
                    case 0:
                        doc.Overwrite(pos, data);
                        break;
                    case 1:
                        doc.Insert(pos, data);
                        break;
                    default:
                        if (doc.Length > 0)
                        {
                            doc.Delete(pos, Math.Min(data.Length, doc.Length - pos));
                        }

                        break;
                }
            }

            byte[] expected = ReadAll(doc.Current);
            byte[] patch = Create(doc, PatchFormat.HexPatch);
            using Document target = Open(src);
            using LoadedPatch p = PatchFiles.Load(new MemoryStream(patch));
            Assert.Equal(PatchVerification.Match, p.Verify(target.Current));
            p.ApplyTo(target);
            Assert.Equal(expected, ReadAll(target.Current));

            // 逆適用で元に戻る (EDIT-43 の仕様 7)。
            p.ApplyTo(target, reverse: true);
            Assert.Equal(src, ReadAll(target.Current));
        }).Check(Config.QuickThrowOnFailure.WithMaxTest(200));
    }

    [Fact]
    [Trait(TC, "TC-EDIT-43-01")]
    public void ApplyIpsAndBpsInDocument()
    {
        // TD-EDIT-PATCH-IPS・TD-EDIT-PATCH-BPS (テストデータ生成の参照実装。Flips の出力での確認は FlipsTests)。
        byte[] ips = File.ReadAllBytes(TestDataCatalog.Get("TD-EDIT-PATCH-IPS"));
        byte[] bps = File.ReadAllBytes(TestDataCatalog.Get("TD-EDIT-PATCH-BPS"));
        using (Document doc = Open(Src()))
        {
            using LoadedPatch p = PatchFiles.Load(new MemoryStream(ips));
            Assert.Equal(PatchFormat.Ips, p.Format);
            Assert.Equal(3, p.RecordCount);
            Assert.Equal(PatchVerification.Unverifiable, p.Verify(doc.Current));
            p.ApplyTo(doc);
            Assert.Equal(Mod3(), ReadAll(doc.Current));
            Assert.True(doc.IsModified);
        }

        using (Document doc = Open(Src()))
        {
            using LoadedPatch p = PatchFiles.Load(new MemoryStream(bps));
            Assert.Equal(PatchFormat.Bps, p.Format);
            Assert.Equal(PatchVerification.Match, p.Verify(doc.Current));
            Assert.Equal(1_048_676, p.ResultLength(doc.Length));
            p.ApplyTo(doc);
            Assert.Equal(Ins100(), ReadAll(doc.Current));
        }
    }

    [Fact]
    public void PatchIsAppliedToTheOriginalDataWhileATransformViewIsActive()
    {
        // パッチは元のファイルに対するもの: 変換ビュー (EDIT-45) が有効でも逆変換せずに当て、確認も元のデータで行う。
        byte[] ips = File.ReadAllBytes(TestDataCatalog.Get("TD-EDIT-PATCH-IPS"));
        byte[] bps = File.ReadAllBytes(TestDataCatalog.Get("TD-EDIT-PATCH-BPS"));
        using (Document doc = Open(Src()))
        {
            doc.SetViewTransform(HexEditor.Core.Editing.TransformView.TransformDefinition.Xor([0x5A]).Create());
            using LoadedPatch p = PatchFiles.Load(new MemoryStream(ips));
            p.ApplyTo(doc);
            Assert.Equal(Mod3(), ReadAll(doc.DataSnapshot));
            Assert.NotNull(doc.ViewTransform);
        }

        using (Document doc = Open(Src()))
        {
            doc.SetViewTransform(HexEditor.Core.Editing.TransformView.TransformDefinition.Xor([0x5A]).Create());
            using LoadedPatch p = PatchFiles.Load(new MemoryStream(bps));
            Assert.Equal(PatchVerification.Match, p.Verify(doc.DataSnapshot));

            // 長さが変わるパッチは、変換ビューの間は当てない (EDIT-45 の仕様 8)。
            Assert.Throws<FixedLengthException>(() => p.ApplyTo(doc));
            Assert.False(doc.IsModified);
        }
    }

    [Fact]
    [Trait(TC, "TC-EDIT-43-02")]
    public void BpsOnOtherFileIsMismatch()
    {
        byte[] bps = File.ReadAllBytes(TestDataCatalog.Get("TD-EDIT-PATCH-BPS"));
        using Document doc = Open(File.ReadAllBytes(TestDataCatalog.Get("TD-EDIT-PATCH-OTHER")));
        using LoadedPatch p = PatchFiles.Load(new MemoryStream(bps));
        Assert.Equal(PatchVerification.Mismatch, p.Verify(doc.Current));
        Assert.False(doc.IsModified);

        // 承知の上で当てると、適用後の CRC-32 が合わないため取り消してエラーにする (仕様 5)。
        PatchException ex = Assert.Throws<PatchException>(() => p.ApplyTo(doc));
        Assert.Equal(PatchError.ResultMismatch, ex.Error);
        Assert.False(doc.IsModified);
    }

    [Fact]
    [Trait(TC, "TC-EDIT-43-03")]
    public void PatchIsOneUndoGroup()
    {
        byte[] bps = File.ReadAllBytes(TestDataCatalog.Get("TD-EDIT-PATCH-BPS"));
        using Document doc = Open(Src());
        using LoadedPatch p = PatchFiles.Load(new MemoryStream(bps));
        p.ApplyTo(doc);
        Assert.Equal(2, doc.History.Count);
        doc.Undo();
        Assert.Equal(1_048_576, doc.Length);
        Assert.Equal(Src(), ReadAll(doc.Current));
        Assert.False(doc.IsModified);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void UpsRoundTripAndReverse(int kind)
    {
        byte[] src = Src()[..4096];
        using Document doc = Open(src);
        switch (kind)
        {
            case 0:
                doc.Overwrite(10, [1, 2, 3, 0, 0, 5]);
                break;
            case 1:
                doc.InsertPattern(doc.Length, 300, [0x00, 0x00, 0x7F]); // 長くなった部分の 00 はレコードを分ける
                break;
            default:
                doc.Delete(1000, 2000);
                break;
        }

        byte[] expected = ReadAll(doc.Current);
        byte[] ups = Create(doc, PatchFormat.Ups);
        using LoadedPatch p = PatchFiles.Load(new MemoryStream(ups));
        Assert.Equal(PatchFormat.Ups, p.Format);
        Assert.True(p.CanReverse);
        using Document target = Open(src);
        Assert.Equal(PatchVerification.Match, p.Verify(target.Current));
        p.ApplyTo(target);
        Assert.Equal(expected, ReadAll(target.Current));
        Assert.Equal(PatchVerification.Match, p.Verify(target.Current, reverse: true));
        p.ApplyTo(target, reverse: true);
        Assert.Equal(src, ReadAll(target.Current));
    }

    [Fact]
    public void BpsBetweenHistoryPointsAndTargetCopyOverlap()
    {
        byte[] src = Src()[..8192];
        using Document doc = Open(src);
        doc.Delete(100, 50);
        DocumentSnapshot first = doc.DataSnapshot;
        doc.InsertCopy(0, 4000, 1000);
        doc.Overwrite(7000, [9, 9, 9]);
        PatchScript script = PatchDiff.Between(first, doc.DataSnapshot);
        Assert.Contains(script.Segments, s => s.FromSource && s.SourceOffset != s.TargetOffset);
        var creator = new PatchCreator(script);
        using var ms = new MemoryStream();
        creator.Write(PatchFormat.Bps, ms);
        using Document target = new(new MemoryByteSource(ReadAll(first)), Options());
        using LoadedPatch p = PatchFiles.Load(new MemoryStream(ms.ToArray()));
        p.ApplyTo(target);
        Assert.Equal(ReadAll(doc.Current), ReadAll(target.Current));
    }

    [Fact]
    public void BpsTargetCopyRunLengthIsExpanded()
    {
        // 手で組んだ BPS: TargetRead 1 バイト (AB) + TargetCopy で 999 バイト (直前の 1 バイトの繰り返し)。
        using var body = new MemoryStream();
        body.Write("BPS1"u8);
        WriteVarint(body, 0);
        WriteVarint(body, 1000);
        WriteVarint(body, 0);
        WriteVarint(body, 0UL << 2 | 1);
        body.WriteByte(0xAB);
        WriteVarint(body, 998UL << 2 | 3);
        WriteVarint(body, 0); // 相対位置 0 (書いた部分の先頭)
        byte[] target = Enumerable.Repeat((byte)0xAB, 1000).ToArray();
        body.Write(BitConverter.GetBytes(PatchCrc32.Compute([])));
        body.Write(BitConverter.GetBytes(PatchCrc32.Compute(target)));
        body.Write(BitConverter.GetBytes(PatchCrc32.Compute(body.ToArray())));
        using var doc = new Document(new MemoryByteSource([]), Options());
        using LoadedPatch p = PatchFiles.Load(new MemoryStream(body.ToArray()));
        p.ApplyTo(doc);
        Assert.Equal(target, ReadAll(doc.Current));
    }

    private static void WriteVarint(Stream s, ulong v)
    {
        while (true)
        {
            byte x = (byte)(v & 0x7F);
            v >>= 7;
            if (v == 0)
            {
                s.WriteByte((byte)(0x80 | x));
                return;
            }

            s.WriteByte(x);
            v--;
        }
    }

    [Fact]
    public void CorruptPatchesAreRejected()
    {
        byte[] bps = File.ReadAllBytes(TestDataCatalog.Get("TD-EDIT-PATCH-BPS"));
        bps[10] ^= 0xFF;
        Assert.Equal(PatchError.Corrupt, Assert.Throws<PatchException>(() => PatchFiles.Load(new MemoryStream(bps))).Error);
        Assert.Equal(PatchError.UnknownFormat, Assert.Throws<PatchException>(() => PatchFiles.Load(new MemoryStream([1, 2, 3, 4, 5, 6]))).Error);
    }

    [Fact]
    public void FixedLengthDocumentRefusesLengthChangingPatch()
    {
        byte[] bps = File.ReadAllBytes(TestDataCatalog.Get("TD-EDIT-PATCH-BPS"));
        using var doc = new Document(new FixedSource(Src()), Options());
        using LoadedPatch p = PatchFiles.Load(new MemoryStream(bps));
        Assert.Throws<FixedLengthException>(() => p.ApplyTo(doc));
    }

    [Fact]
    public void SelfApplyingContainerHoldsHexPatch()
    {
        byte[] src = Src()[..4096];
        using Document doc = Open(src);
        doc.Overwrite(5, [1, 2, 3]);
        string stub = Path.Combine(_dir, "stub.exe");
        File.WriteAllBytes(stub, [(byte)'M', (byte)'Z', .. new byte[1000]]);
        string exe = Path.Combine(_dir, "patch.exe");
        var creator = new PatchCreator(PatchDiff.FromSaved(doc.DataSnapshot));
        Assert.Equal(PatchUnavailableReason.StubMissing, creator.Estimate(PatchFormat.SelfApplying).Unavailable);
        using (var output = File.Create(exe))
        {
            creator.Write(PatchFormat.SelfApplying, output, new PatchWriteOptions { StubPath = stub, DefaultFileName = "target.bin", Message = "hi" });
        }

        using var stream = File.OpenRead(exe);
        Assert.Equal(PatchFormat.SelfApplying, PatchFiles.Detect(stream));
        (long offset, long length) = SelfPatchContainer.Find(stream)!.Value;
        Assert.Equal(1002, offset);
        using LoadedPatch p = PatchFiles.Load(new SubStream(stream, offset, length), ownsStream: true);
        HexPatchManifest manifest = ((HexPatch)p).Manifest;
        Assert.Equal("target.bin", manifest.DefaultFileName);
        Assert.Equal("hi", manifest.Message);

        // 適用プログラムと同じストリームでの適用。
        using var zip = new System.IO.Compression.ZipArchive(new SubStream(stream, offset, length));
        using var input = new MemoryStream(src);
        using var output2 = new MemoryStream();
        using (Stream data = zip.GetEntry(HexPatchManifest.DataEntry)!.Open())
        {
            manifest.ApplyStreaming(input, data, output2);
        }

        Assert.Equal(ReadAll(doc.Current), output2.ToArray());
        Assert.True(SourceIdentity.Compute(new MemoryStream(src)).Matches(manifest.Source));
        Assert.False(SourceIdentity.Compute(new MemoryStream(new byte[4096])).Matches(manifest.Source));
    }

    [Fact]
    public void TwoDifferentFilesUsePositionalDiff()
    {
        byte[] a = Src()[..5000], b = (byte[])a.Clone();
        b[17] ^= 1;
        b[4000] ^= 2;
        using Document left = Open(a), right = Open(b);
        PatchScript script = PatchDiff.Between(left.DataSnapshot, right.DataSnapshot);
        Assert.Equal(2, script.PositionalChanges().Count);
        var creator = new PatchCreator(script);
        using var ms = new MemoryStream();
        creator.Write(PatchFormat.Ips, ms);
        Assert.Equal(b, IpsPatch.Apply(a, IpsPatch.Read(new MemoryStream(ms.ToArray()))));
    }

    /// <summary>長さを変えられないデータソース (ディスクなどの代わり)。</summary>
    private sealed class FixedSource(byte[] data) : ByteSourceBase
    {
        public override string DisplayName => "fixed";

        public override string Identity => "fixed";

        public override long Length => data.Length;

        public override SourceCapabilities Capabilities => SourceCapabilities.CanWrite;

        public override ReadResult Read(long offset, Span<byte> buffer)
        {
            int n = ClampToLength(offset, buffer.Length);
            data.AsSpan((int)offset, n).CopyTo(buffer);
            return new ReadResult(n);
        }
    }
}
