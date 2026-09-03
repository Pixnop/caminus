using Caminus.Core;

namespace Caminus.Core.Tests;

public class ExposureTests
{
    [Fact]
    public void SolAir_IsTheProductAndIsZeroAtNight()
    {
        // Roof under a sun at the zenith, open sky, no forest: the whole 12 K.
        Assert.Equal(12.0, Exposure.SolAir(12, 1, 1, 1), 6);
        // The same roof at night, under a dense forest, and half buried under a hill.
        Assert.Equal(0.0, Exposure.SolAir(12, 1, 0, 1), 6);
        Assert.Equal(3.6, Exposure.SolAir(12, 1, 1, 0.3), 6);
        Assert.Equal(6.0, Exposure.SolAir(12, 0.5, 1, 1), 6);
        // Nothing here may turn into cooling.
        Assert.Equal(0.0, Exposure.SolAir(12, 1, -0.4, 1), 6);
    }

    [Fact]
    public void Incidence_LightsTheWallTheSunFacesAndNotTheOneBehind()
    {
        // Sun 30° up in the south (+Z), so a unit vector (0, 0.5, 0.866).
        const double y = 0.5, z = 0.8660254;
        Assert.Equal(0.5, Exposure.Incidence(0, y, z, 0, 1, 0), 6);                      // roof: its height
        Assert.Equal(0.0, Exposure.Incidence(0, y, z, 0, -1, 0), 6);                     // floor: never
        Assert.Equal(0.7 * z, Exposure.Incidence(0, y, z, 0, 0, 1), 6);                  // south wall, head on
        Assert.Equal(0.0, Exposure.Incidence(0, y, z, 0, 0, -1), 6);                     // north wall, in the shade
        Assert.Equal(0.0, Exposure.Incidence(0, y, z, 1, 0, 0), 6);                      // east wall, edge on
        // Sun in the south-east: both walls catch a share of it and the sum stays under a head-on one.
        const double h = 0.6123724; // 0.866 / sqrt(2)
        Assert.Equal(0.7 * h, Exposure.Incidence(h, y, h, 1, 0, 0), 6);
        Assert.Equal(0.7 * h, Exposure.Incidence(h, y, h, 0, 0, 1), 6);
        // Sun below the horizon: nothing is lit, whatever it is pointing at.
        Assert.Equal(0.0, Exposure.Incidence(0, -0.2, 0.98, 0, 0, 1), 6);
        Assert.Equal(0.0, Exposure.Incidence(0, -0.2, 0.98, 0, 1, 0), 6);
    }

    [Fact]
    public void Beyond_CountsTheRockPastTheFirstWall()
    {
        // Room air spanning 10..12: the walls at 9 and 13 are still "against" it.
        Assert.Equal(0, Exposure.Beyond(11, 10, 12));
        Assert.Equal(0, Exposure.Beyond(9, 10, 12));
        Assert.Equal(0, Exposure.Beyond(13, 10, 12));
        Assert.Equal(1, Exposure.Beyond(8, 10, 12));
        Assert.Equal(3, Exposure.Beyond(16, 10, 12));
    }

    [Fact]
    public void Reach_HalvesAtTheFirstBlockOfRock()
    {
        Assert.Equal(1.0, Exposure.Reach(0), 6);
        Assert.Equal(0.5, Exposure.Reach(1), 6);
        Assert.Equal(0.25, Exposure.Reach(3), 6);
        // A lava lake at 12 heat units, 3 blocks under the floor, at 400 W per unit.
        Assert.Equal(1200, 12 * 400 * Exposure.Reach(3), 6);
    }
}
