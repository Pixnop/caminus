namespace Caminus.Core;

/// <summary>
/// What the world outside the walls does to a face: the sun that falls on it, and the heat that
/// crawls to it through the rock. Units: K, blocks (1 block = 1 m).
/// </summary>
public static class Exposure
{
    /// <summary>What a vertical wall catches of a sun it faces head on, against a roof's 1.</summary>
    public const double WallShare = 0.7;

    /// <summary>
    /// Sol-air excess, K: how much warmer than the outside air a sunlit face behaves.
    /// </summary>
    /// <param name="maxK">Excess of a face in full sun, facing straight at it.</param>
    /// <param name="sun">How much sky the face sees, 0..1.</param>
    /// <param name="incidence">What the face's own direction catches of the sun, from <see cref="Incidence"/>.</param>
    /// <param name="shade">What the forest lets through, 0..1.</param>
    public static double SolAir(double maxK, double sun, double incidence, double shade) =>
        maxK * Math.Max(0, sun) * Math.Max(0, incidence) * Math.Max(0, shade);

    /// <summary>
    /// How much of the sun a face pointing along <c>n</c> catches, 0..1. The sun is given as the unit
    /// vector pointing at it, so <c>sunY</c> is the sine of its height above the horizon and
    /// (<c>sunX</c>, <c>sunZ</c>) its compass direction, of length cos(height).
    /// A roof takes the height itself, the sun straight through it. A wall takes the horizontal
    /// cosine of incidence, so the wall facing the sun gets up to <see cref="WallShare"/> (a wall is
    /// never square to a sun that is overhead) and the opposite wall nothing. A floor never sees the
    /// sun, and nor does anything once the sun has set.
    /// </summary>
    public static double Incidence(double sunX, double sunY, double sunZ, int nX, int nY, int nZ) =>
        sunY <= 0 ? 0
        : nY > 0 ? sunY
        : nY < 0 ? 0
        : WallShare * Math.Max(0, sunX * nX + sunZ * nZ);

    /// <summary>
    /// Distance from <paramref name="v"/> to the closed interval, minus the first block outside it:
    /// 0 inside the room and in the wall that touches it, 1 for the next layer of rock, and so on.
    /// </summary>
    public static int Beyond(int v, int min, int max) => Math.Max(0, Math.Max(min - v, v - max) - 1);

    /// <summary>Share of a source that still reaches the room through <paramref name="distance"/> blocks of rock.</summary>
    public static double Reach(int distance) => 1.0 / (1 + Math.Max(0, distance));
}
