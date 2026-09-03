namespace Caminus.Core;

/// <summary>
/// Stack effect: a column of warm air in a flue weighs less than the outside air of the same
/// height, and the pressure difference pulls air through the building. ASHRAE Fundamentals,
/// ventilation and infiltration. Units: m, m², m³/s, °C, W/K, hours.
/// </summary>
public static class Chimney
{
    private const double Gravity = 9.81;
    /// <summary>Density and specific heat of air: what one m³/s of draft carries away, W/K.</summary>
    private const double AirVolumetricCapacity = 1.2 * 1005;

    /// <summary>
    /// Two openings the same air has to pass through, one after the other: the smaller one rules.
    /// Always at most the smaller of the two, and half of it when both are equal.
    /// </summary>
    public static double SeriesArea(double a, double b) =>
        a <= 0 || b <= 0 ? 0 : 1 / Math.Sqrt(1 / (a * a) + 1 / (b * b));

    /// <summary>
    /// Draft volume flow, m³/s. <paramref name="dH"/> is the height from the inlet to the top of the
    /// flue, <paramref name="flue"/> the air temperature entering it (the ceiling of the room, which
    /// is where the stratification puts the warmest air). Zero as soon as the inside is not warmer:
    /// a cold flue reverses, and a reversed draft is not something this model has anything to say about.
    /// </summary>
    public static double Draft(double dischargeCoefficient, double flueArea, double inletArea,
                               double dH, double flue, double outside)
    {
        double dT = flue - outside, tAbs = outside + 273.15;
        if (dT <= 0 || dH <= 0 || tAbs <= 0) return 0;
        return dischargeCoefficient * SeriesArea(flueArea, inletArea) * Math.Sqrt(2 * Gravity * dH * dT / tAbs);
    }

    /// <summary>Conductance of a volume flow of air between the room and where the air comes from, W/K.</summary>
    public static double Conductance(double flow) => AirVolumetricCapacity * Math.Max(0, flow);

    /// <summary>Air changes per hour: what the draft renews, plus what leaks through the envelope anyway.</summary>
    public static double AirChanges(double flow, double volume, double baseAirChanges) =>
        baseAirChanges + (volume <= 0 ? 0 : flow * 3600 / volume);

    /// <summary>
    /// Smoke haze in the room, 0..1, after <paramref name="dtHours"/>: production fills it, air changes
    /// empty it. Integrated in closed form rather than stepped, because a well drawing flue renews the
    /// air dozens of times an hour and an explicit step of that size oscillates instead of settling.
    /// </summary>
    public static double Smoke(double smoke, double perHour, double airChangesPerHour, double dtHours)
    {
        if (dtHours <= 0) return Math.Clamp(smoke, 0, 1);
        if (airChangesPerHour <= 0) return Math.Clamp(smoke + perHour * dtHours, 0, 1);
        double equilibrium = perHour / airChangesPerHour;
        return Math.Clamp(equilibrium + (smoke - equilibrium) * Math.Exp(-airChangesPerHour * dtHours), 0, 1);
    }

    /// <summary>What the player is told: none, light or heavy.</summary>
    public static string Level(double smoke)
    {
        if (smoke < 0.1) return "none";
        return smoke < 0.4 ? "light" : "heavy";
    }
}
