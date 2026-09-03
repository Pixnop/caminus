using Caminus.Core;

namespace Caminus.Core.Tests;

public class StratificationTests
{
    [Fact]
    public void Gradient_GrowsWithThePowerThenSaturates()
    {
        Assert.Equal(0, Stratification.Gradient(0, 0.4, 2.0));
        Assert.Equal(1.6, Stratification.Gradient(4000, 0.4, 2.0), 6); // one lit firepit
        Assert.Equal(2.0, Stratification.Gradient(40000, 0.4, 2.0), 6); // capped
    }

    [Fact]
    public void Temperature_IsSymmetricAroundTheMidHeight()
    {
        // Interior blocks 1..3, mid height 2.5: floor block 1 is 1 m below, ceiling block 3 is 1 m above.
        Assert.Equal(18.4, Stratification.At(20, 1.6, 1, 2.5), 6);
        Assert.Equal(20.0, Stratification.At(20, 1.6, 2, 2.5), 6);
        Assert.Equal(21.6, Stratification.At(20, 1.6, 3, 2.5), 6);
    }
}
