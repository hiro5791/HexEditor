using HexEditor.Core.View;

namespace HexEditor.Core.Tests.View;

/// <summary>UI-08 の仕様 1 ズームの倍率の段階。</summary>
public sealed class ZoomLevelsTests
{
    [Fact]
    public void Steps_are_the_fourteen_levels_of_the_spec() =>
        Assert.Equal([50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300, 400], ZoomLevels.Steps);

    [Theory]
    [InlineData(100, 110)]
    [InlineData(110, 125)]
    [InlineData(300, 400)]
    [InlineData(400, 400)]
    [InlineData(101, 110)]
    public void Zoom_in_moves_one_step(int from, int to) => Assert.Equal(to, ZoomLevels.ZoomIn(from));

    [Theory]
    [InlineData(100, 90)]
    [InlineData(67, 50)]
    [InlineData(50, 50)]
    [InlineData(101, 100)]
    public void Zoom_out_moves_one_step(int from, int to) => Assert.Equal(to, ZoomLevels.ZoomOut(from));

    [Fact]
    public void Two_steps_from_100_is_125_and_five_is_200() // TC-UI-08-01、TC-UI-08-07 の倍率
    {
        Assert.Equal(125, ZoomLevels.Step(100, 2));
        Assert.Equal(200, ZoomLevels.Step(100, 5));
        Assert.Equal(90, ZoomLevels.Step(200, -6));
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1000, 400)]
    [InlineData(120, 125)]
    [InlineData(100, 100)]
    public void Normalize_snaps_to_the_nearest_step(int value, int expected) => Assert.Equal(expected, ZoomLevels.Normalize(value));
}
