namespace Caminus.Core;

/// <summary>
/// Kusuda and Achenbach (1965) ground temperature model: the seasonal surface wave is damped
/// exponentially with depth and delayed in time. Units: °C, metres, days.
/// </summary>
public static class GroundTemperature
{
    /// <summary>Thermal diffusivity of soil, m²/day (about 5.8e-7 m²/s).</summary>
    public const double SoilDiffusivity = 0.05;

    /// <param name="annualMean">Annual mean surface temperature.</param>
    /// <param name="annualAmplitude">Half of the annual surface swing (mean to peak).</param>
    /// <param name="coldestDay">Day of the year when the surface is coldest.</param>
    /// <param name="depth">Depth below the surface, metres (0 or less gives the surface wave).</param>
    /// <param name="day">Day of the year to evaluate.</param>
    /// <param name="daysPerYear">Length of the year in days.</param>
    /// <param name="diffusivity">Thermal diffusivity, m²/day.</param>
    public static double At(double annualMean, double annualAmplitude, double coldestDay, double depth, double day, double daysPerYear,
        double diffusivity = SoilDiffusivity)
    {
        double z = Math.Max(0, depth);
        double damping = Math.Exp(-z * Math.Sqrt(Math.PI / (daysPerYear * diffusivity)));
        double lag = z / 2 * Math.Sqrt(daysPerYear / (Math.PI * diffusivity));
        return annualMean - annualAmplitude * damping * Math.Cos(2 * Math.PI / daysPerYear * (day - coldestDay - lag));
    }
}
