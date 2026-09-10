using StageManager.Core;

namespace StageManager.Core.Tests;

public class StageLayoutTests
{
    private static readonly RectPx WorkArea = new(0, 0, 1920, 1040);
    private static readonly LayoutSettings Settings = new(StripWidth: 200, Margin: 12, Side: StripSide.Left);

    [Fact]
    public void StripIsOnTheRightByDefault()
    {
        Assert.Equal(StripSide.Right, new LayoutSettings().Side);
    }

    [Fact]
    public void AvailableArea_ExcludesStripAndMargins_OnEitherSide()
    {
        Assert.Equal(new RectPx(212, 12, 1908, 1028), StageLayout.AvailableArea(WorkArea, Settings));
        Assert.Equal(new RectPx(12, 12, 1708, 1028), StageLayout.AvailableArea(WorkArea, Settings with { Side = StripSide.Right }));
    }

    [Fact]
    public void StripArea_IsFullHeightBandOnTheChosenEdge()
    {
        Assert.Equal(new RectPx(0, 0, 200, 1040), StageLayout.StripArea(WorkArea, Settings));
        Assert.Equal(new RectPx(1720, 0, 1920, 1040), StageLayout.StripArea(WorkArea, Settings with { Side = StripSide.Right }));

        var offsetWorkArea = new RectPx(100, 50, 2020, 1090);
        Assert.Equal(new RectPx(1820, 50, 2020, 1090), StageLayout.StripArea(offsetWorkArea, Settings with { Side = StripSide.Right }));
    }

    [Fact]
    public void Place_CentersWindowWhenAsked()
    {
        var area = StageLayout.AvailableArea(WorkArea, Settings);
        var placed = StageLayout.Place(new RectPx(0, 0, 800, 600), area, center: true);
        Assert.Equal(RectPx.FromSize(212 + (1696 - 800) / 2, 12 + (1016 - 600) / 2, 800, 600), placed);
    }

    [Fact]
    public void Place_ShrinksOversizedWindowToAvailableArea()
    {
        var area = StageLayout.AvailableArea(WorkArea, Settings);
        var placed = StageLayout.Place(new RectPx(-50, -50, 3000, 2000), area, center: true);
        Assert.Equal(area, placed);
    }

    [Fact]
    public void Place_ClampsPositionWithoutCentering()
    {
        var area = StageLayout.AvailableArea(WorkArea, Settings);
        var overlappingStrip = new RectPx(20, 300, 820, 900);
        var placed = StageLayout.Place(overlappingStrip, area, center: false);
        Assert.Equal(RectPx.FromSize(212, 300, 800, 600), placed);

        var alreadyInside = new RectPx(500, 300, 1300, 900);
        Assert.Equal(alreadyInside, StageLayout.Place(alreadyInside, area, center: false));
    }

    [Fact]
    public void Place_ReturnsWindowUnchangedWhenAreaIsDegenerate()
    {
        var window = new RectPx(10, 10, 100, 100);
        Assert.Equal(window, StageLayout.Place(window, new RectPx(0, 0, 0, 0), center: true));
    }
}
