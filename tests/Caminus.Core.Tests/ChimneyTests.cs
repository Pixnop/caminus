namespace Caminus.Core.Tests;

public class ChimneyTests
{
    private const double Cd = 0.6;

    [Fact]
    public void Draft_IsZeroWhenTheRoomIsNotWarmerThanOutside()
    {
        Assert.Equal(0, Chimney.Draft(Cd, 1, 0.2, 6, 5, 5));
        Assert.Equal(0, Chimney.Draft(Cd, 1, 0.2, 6, -5, 5));
    }

    [Fact]
    public void Draft_GrowsWithHeightAndWithTheTemperatureDifference()
    {
        double low = Chimney.Draft(Cd, 1, 0.2, 4, 25, 5);
        double high = Chimney.Draft(Cd, 1, 0.2, 8, 25, 5);
        double hot = Chimney.Draft(Cd, 1, 0.2, 4, 45, 5);
        Assert.True(high > low, $"{high} vs {low}");
        Assert.True(hot > low, $"{hot} vs {low}");
        // Both terms are under a square root: doubling either is a factor sqrt(2).
        Assert.Equal(Math.Sqrt(2), high / low, 3);
    }

    [Fact]
    public void Draft_IsZeroWithoutAFlueOrWithoutAnInlet()
    {
        Assert.Equal(0, Chimney.Draft(Cd, 0, 0.2, 6, 25, 5));
        Assert.Equal(0, Chimney.Draft(Cd, 1, 0, 6, 25, 5));
    }

    [Fact]
    public void SeriesArea_IsNeverLargerThanTheSmallerOpening()
    {
        Assert.True(Chimney.SeriesArea(1, 0.2) <= 0.2);
        Assert.True(Chimney.SeriesArea(0.2, 1) <= 0.2);
        // Two equal openings in series pass what one of area A/sqrt(2) would.
        Assert.Equal(1 / Math.Sqrt(2), Chimney.SeriesArea(1, 1), 6);
    }

    [Fact]
    public void AirChanges_AddTheDraftToTheEnvelopeLeakage()
    {
        Assert.Equal(0.3, Chimney.AirChanges(0, 27, 0.3), 6);
        // 0.1 m³/s through 27 m³ is 360 m³/h, i.e. 13.3 changes.
        Assert.Equal(0.3 + 360.0 / 27, Chimney.AirChanges(0.1, 27, 0.3), 6);
    }

    [Fact]
    public void Smoke_RisesWithoutAirChangesAndDecaysWithThem()
    {
        double filling = Chimney.Smoke(0, 2.0, 0, 0.25);
        Assert.Equal(0.5, filling, 6);
        Assert.True(Chimney.Smoke(filling, 2.0, 0, 0.25) > filling);
        // Same source, but the room's air is renewed 40 times an hour: it settles near 2/40.
        Assert.True(Chimney.Smoke(filling, 2.0, 40, 0.25) < 0.1);
        // No source at all and the haze empties out.
        Assert.True(Chimney.Smoke(0.8, 0, 5, 0.25) < 0.8);
        Assert.Equal(0, Chimney.Smoke(0.8, 0, 5, 100), 6);
    }

    [Fact]
    public void Smoke_StaysInRange()
    {
        Assert.Equal(1, Chimney.Smoke(0.9, 2.0, 0.3, 10));
        Assert.Equal(0, Chimney.Smoke(0, 0, 0.3, 1));
    }

    [Fact]
    public void Level_NamesTheThreeBands()
    {
        Assert.Equal("none", Chimney.Level(0.05));
        Assert.Equal("light", Chimney.Level(0.2));
        Assert.Equal("heavy", Chimney.Level(0.7));
    }

    [Fact]
    public void Conductance_IsTheHeatOneCubicMetrePerSecondCarries()
    {
        Assert.Equal(1.2 * 1005, Chimney.Conductance(1), 6);
        Assert.Equal(0, Chimney.Conductance(-1));
    }
}
