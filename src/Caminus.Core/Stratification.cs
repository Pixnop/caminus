namespace Caminus.Core;

/// <summary>
/// Warm air rises: instead of stacking air nodes, the room keeps one mean temperature and a
/// linear vertical gradient around its mid height. Units: °C, metres, watts.
/// </summary>
public static class Stratification
{
    /// <summary>Gradient in K/m, proportional to the heating power and capped.</summary>
    public static double Gradient(double sourceWatts, double kPerMPerKW, double maxKPerM) =>
        Math.Min(maxKPerM, kPerMPerKW * Math.Max(0, sourceWatts) / 1000);

    /// <summary>Air temperature at the centre of block <paramref name="y"/>, mean at <paramref name="yMid"/>.</summary>
    public static double At(double mean, double gradient, double y, double yMid) =>
        mean + gradient * (y + 0.5 - yMid);
}
