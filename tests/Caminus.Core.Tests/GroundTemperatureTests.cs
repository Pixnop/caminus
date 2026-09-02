using Caminus.Core;

namespace Caminus.Core.Tests;

public class GroundTemperatureTests
{
    [Fact]
    public void Surface_FollowsTheSeasonalWave()
    {
        Assert.Equal(-5, GroundTemperature.At(10, 15, 20, 0, 20, 365), 6);       // coldest day
        Assert.Equal(25, GroundTemperature.At(10, 15, 20, 0, 20 + 182.5, 365), 6); // half a year later
    }

    [Fact]
    public void Depth_DampsTheSwing()
    {
        double swing(double depth)
        {
            double min = double.MaxValue, max = double.MinValue;
            for (int d = 0; d < 365; d++)
            {
                double t = GroundTemperature.At(10, 15, 20, depth, d, 365);
                min = Math.Min(min, t); max = Math.Max(max, t);
            }
            return max - min;
        }
        Assert.True(swing(3) < swing(0) * 0.7, $"3 m: {swing(3):0.0} vs surface {swing(0):0.0}");
        Assert.True(swing(10) < 1.0, $"10 m still swings by {swing(10):0.0} K");
    }

    [Fact]
    public void Depth_DelaysTheMinimum()
    {
        int coldest(double depth)
        {
            int best = 0; double min = double.MaxValue;
            for (int d = 0; d < 365; d++)
            {
                double t = GroundTemperature.At(10, 15, 20, depth, d, 365);
                if (t < min) { min = t; best = d; }
            }
            return best;
        }
        Assert.Equal(20, coldest(0));
        Assert.InRange(coldest(3) - 20, 60, 85); // lag = z/2·sqrt(P/(pi·alpha)) = 72 days at 3 m
    }
}
