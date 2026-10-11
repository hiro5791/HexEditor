using HexEditor.Core.Annotations;
using HexEditor.Core.Devices;
using HexEditor.Core.Engine;
using HexEditor.Core.Search;
using HexEditor.Core.Sources;
using HexEditor.Core.Statistics;
using HexEditor.Core.View;

namespace HexEditor.Core.Tests.Devices;

/// <summary>ディスクのデータソースとほかの機能のつなぎ (FIND-01 の仕様 9、VIEW-42 の仕様 2 の 3) と、解析の注釈の共通の注釈レイヤー。</summary>
public sealed class DeviceIntegrationTests
{
    private static DeviceByteSource Disk(int sectorSize, out FakeDeviceAccess access)
    {
        access = new FakeDeviceAccess(new FakeDeviceSpec
        {
            Elevated = true,
            Disks = [new FakeDiskSpec { Number = 1, SectorSize = sectorSize, Size = 8L * 1024 * 1024 }],
        });
        IDeviceHandle handle = access.Open(DevicePath.PhysicalDrive(1), writable: false);
        return new DeviceByteSource(handle, access, new DeviceOpenInfo { Path = handle.Path, DisplayName = "Disk 1" });
    }

    [Fact]
    public void Searching_a_disk_reads_only_whole_sectors()
    {
        // FIND-01 の仕様 9: 検索のチャンクの位置・長さがセクタの途中でも、デバイスへの読み込みはセクタの倍数になる。
        using DeviceByteSource source = Disk(4096, out FakeDeviceAccess access);
        using (access)
        {
            source.RecordReads = true;
            using var doc = new Document(source);
            SearchPattern pattern = SearchPattern.Literal([0xDE, 0xAD, 0xBE, 0xEF]);
            _ = SearchEngine.Find(doc.Current, pattern, 123, forward: true, wrap: false, new SearchOptions { ChunkSize = 100_003 });
            Assert.NotEmpty(source.ReadLog);
            Assert.All(source.ReadLog, r =>
            {
                Assert.Equal(0, r.Offset % 4096);
                Assert.Equal(0, r.Length % 4096);
            });
        }
    }

    [Fact]
    public void Disks_default_to_sector_separators()
    {
        // VIEW-42 の仕様 2 の 3: データソースの種類ごとの既定値 (ディスクでは区切り線「セクタ」)。
        using DeviceByteSource source = Disk(512, out FakeDeviceAccess access);
        using (access)
        {
            ViewSettings view = ViewSettings.FromJson(source.ViewDefaults);
            Assert.Equal(SeparatorKind.Sector, view.Separator);
        }
    }

    [Fact]
    public void Analysis_annotations_go_to_the_common_annotation_layer()
    {
        // INSP-32: 統計の分類・埋め込まれた形式は注釈レイヤーの出どころ「解析」として載り、空にすると外れる。
        using var doc = new Document(new VirtualByteSource(0x10000));
        var sink = new AnnotationLayerSink();
        sink.SetAnnotations(doc, AnalysisAnnotationSources.Classes, [new AnalysisAnnotation(0x100, 0x40, "encrypted", "class.encrypted")]);
        AnnotationLayer layer = AnnotationLayer.For(doc);
        IAnnotationSource source = Assert.Single(layer.Sources, s => s.Id == AnalysisAnnotationSources.Classes);
        Assert.Equal(AnnotationOrigin.Analysis, source.Origin);
        PlacedAnnotation placed = Assert.Single(layer.QueryVisible(0x120, 0x121));
        Assert.Equal("encrypted", placed.Annotation.Label);

        sink.SetAnnotations(doc, AnalysisAnnotationSources.Classes, []);
        Assert.DoesNotContain(layer.Sources, s => s.Id == AnalysisAnnotationSources.Classes);
    }
}
