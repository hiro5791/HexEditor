using HexEditor.Core.Engine;
using HexEditor.Core.Sources;
using HexEditor.Core.View;

namespace HexEditor.Core.Tests.View;

/// <summary>ENG-17 の確認ダイアログに出す変更の量。</summary>
public sealed class ChangeSummaryTests
{
    private static Document Create(int length) => new(new MemoryByteSource(new byte[length], "test"), new DocumentOptions
    {
        TempDirectory = Path.Combine(Path.GetTempPath(), "hexeditor-summary"),
    });

    [Fact]
    public void UnchangedDocumentHasNoChanges()
    {
        using Document doc = Create(100);
        Assert.Equal(new ChangeSummary(0, 0), ChangeSummary.Of(doc.Current));
    }

    [Fact]
    public void CountsSeparatePlacesAndBytes()
    {
        using Document doc = Create(100);
        doc.Overwrite(10, [1, 2, 3]);
        doc.Overwrite(50, [4]);
        doc.Insert(0, [9, 9]);
        Assert.Equal(new ChangeSummary(3, 6), ChangeSummary.Of(doc.Current));
    }

    [Fact]
    public void AdjacentEditsAreOnePlace()
    {
        using Document doc = Create(100);
        doc.Overwrite(10, [1]);
        doc.Overwrite(11, [2]);
        Assert.Equal(new ChangeSummary(1, 2), ChangeSummary.Of(doc.Current));
    }

    [Fact]
    public void DeletionsCountAsPlaces()
    {
        using Document doc = Create(100);
        doc.Delete(20, 5);
        doc.Delete(90, 5);
        Assert.Equal(new ChangeSummary(2, 0), ChangeSummary.Of(doc.Current));
    }
}
