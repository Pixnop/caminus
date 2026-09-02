using Caminus.Core;

namespace Caminus.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void ThermalNetwork_TypeExists()
    {
        Assert.NotNull(typeof(ThermalNetwork));
    }
}
