using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Xunit;
using Xunit.Sdk;

namespace Caminus.Scenarios;

/// <summary>
/// Batch 1 integration scenarios: the mod runs inside a real embedded 1.22.7 server (Atlas),
/// rooms are built by hand and the <c>/caminus temp</c> report is read.
/// </summary>
public partial class ThermalScenarios : AtlasScenarioBase
{
    // Stone without BreakIfFloating or UnstableRock (rock-* has it): a box in mid-air does not collapse.
    private const string Stone = "game:cobblestone-granite";
    private const string Wood = "game:planks-oak-ud";
    private const string Air = "game:air";

    // A glass pane: sidesolid = false everywhere (so it's an "opening" for Caminus) but
    // BlockGlassPane.GetRetention returns 1 across its plane, so RoomRegistry keeps the
    // room closed. A plain air block would let the flood fill leak into the sky and the room
    // would become an open 29³ volume.
    private const string Pane = "game:glasspane-leaded-aged-ns";

    /// <summary>5×5×5 box: solid shell, interior air volume 3×3×3 (27 blocks, 54 faces).</summary>
    private const int Inner = 3;

    [GeneratedRegex(@"Room: (-?\d+(?:\.\d+)?) °C")]
    private static partial Regex RoomTemp();

    [GeneratedRegex(@"outside (-?\d+(?:\.\d+)?) °C")]
    private static partial Regex OutsideTemp();

    [GeneratedRegex(@"Losses: (-?\d+(?:\.\d+)?) W/K")]
    private static partial Regex Losses();

    [GeneratedRegex(@"Ground node: (-?\d+(?:\.\d+)?) °C")]
    private static partial Regex GroundTemp();

    [GeneratedRegex(@"Perish rate: (\d+(?:\.\d+)?)x")]
    private static partial Regex PerishRate();

    [GeneratedRegex(@"Wind: (\d+(?:\.\d+)?) ")]
    private static partial Regex WindSpeed();

    [GeneratedRegex(@"\(\+(\d+(?:\.\d+)?) % losses\)")]
    private static partial Regex WindLosses();

    [GeneratedRegex(@"floor (-?\d+(?:\.\d+)?) °C")]
    private static partial Regex FloorTemp();

    [GeneratedRegex(@"ceiling (-?\d+(?:\.\d+)?) °C")]
    private static partial Regex CeilingTemp();

    /// <summary>BlockEntityContainer.container is protected: the only way to ask a real chest for its rate.</summary>
    private static readonly FieldInfo ContainerField =
        typeof(BlockEntityContainer).GetField("container", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [AtlasScenario]
    public async Task Version_command_answers()
    {
        CommandResult result = await World.ExecuteCommand("/caminus version");
        Assert.True(result.Ok, result.Message);
        Assert.Contains("Caminus 0.1.0", result.Message);
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Heated_stone_room_warms_up()
    {
        BlockPos inside = await Room("CaminusWarm", 40, Stone);
        LightFirepit(inside.Offset(0, -1, 0));
        await World.Until(() => Firepit(inside.Offset(0, -1, 0))?.IsBurning == true);

        string before = await WaitForRoomReport(inside);
        Assert.Contains("Sources: 4000 W", before); // lit firepit = 10 units × 400 W

        // The mod's step is in GAME seconds (realDt × SpeedOfTime × CalendarSpeedMul).
        // GameCalendar.CalculateCurrentTimeSpeed SUMS the modifiers (it does not multiply
        // them): the "baseGameSpeed" base is 60, +540 brings SpeedOfTime to 600, i.e. ×10.
        // Without this, 10 real seconds would only be worth 300 game seconds and the rise
        // would depend on Atlas's tick-pumping rate; with it, we have an order-of-magnitude margin.
        World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 540f);
        await World.Ticks(300); // ≈ 10 real seconds

        string after = await ReportAt(inside);
        double t0 = Read(RoomTemp(), before), t1 = Read(RoomTemp(), after);
        Assert.True(t1 - t0 >= 2.0, $"the room only gained {t1 - t0:0.00} K\nbefore:\n{before}\nafter:\n{after}");
        Assert.Contains("Sources: 4000 W", after);
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Glass_pane_replaces_a_stone_face()
    {
        BlockPos inside = await Room("CaminusPane", 80, Stone);
        string stone = await WaitForRoomReport(inside);
        Assert.DoesNotContain("Openings", stone);

        // One wall face replaced with a glass pane: still an enclosed room for the vanilla
        // registry (the pane retains heat across its plane) and a Glass wall for Caminus
        // (5 W/K instead of 3 W/K for stone, i.e. +2 W/K).
        World.SetBlock(Pane, inside.Offset(0, 0, -2));
        string glazed = await WaitForRoomReport(inside, r => r.Contains("Glass: 1 faces"));

        Assert.Contains("Stone: 53 faces", glazed);
        Assert.DoesNotContain("Openings", glazed);
        double gain = Read(Losses(), glazed) - Read(Losses(), stone);
        Assert.True(Math.Abs(gain - 2.0) < 0.1, $"conductance +{gain:0.0} W/K instead of +2\nstone:\n{stone}\nglazed:\n{glazed}");
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Open_air_is_not_a_room()
    {
        ITestPlayer player = await World.JoinPlayer("CaminusOutside");
        await player.TeleportTo(World.Spawn.Offset(200, 0, 40));
        await World.Ticks(90); // three mod ticks
        Assert.Contains("No enclosed room here.", await ReportAt(player.Position));
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Wood_walls_leak_less_than_stone()
    {
        BlockPos stone = await Room("CaminusStone", 120, Stone);
        BlockPos wood = await Room("CaminusWood", 160, Wood);

        string stoneReport = await WaitForRoomReport(stone);
        string woodReport = await WaitForRoomReport(wood);

        Assert.Contains("Stone: 54 faces", stoneReport);
        Assert.Contains("Wood: 54 faces", woodReport);
        Assert.True(Read(Losses(), woodReport) < Read(Losses(), stoneReport),
            $"stone:\n{stoneReport}\nwood:\n{woodReport}");
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Buried_room_has_a_ground_node()
    {
        BlockPos inside = await Cellar("CaminusCellar", 240);
        string report = await WaitForRoomReport(inside, r => r.Contains("Ground walls:"));

        // Interior at Y 1..3 with the worldgen surface at 2: the 9 floor faces (2 m down) and the
        // 12 side faces of the bottom layer (1 m down) are buried, the other 33 face the open air.
        Assert.Contains("Outside walls:\n  Stone: 33 faces", report.ReplaceLineEndings("\n"));
        Assert.Contains("Ground walls:\n  Stone: 21 faces", report.ReplaceLineEndings("\n"));
        Assert.Contains("at 1.4 m", report); // (9 × 2 + 12 × 1) / 21

        World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 540f);
        await World.Ticks(300); // about two time constants: 27 m³ × 6030 J/K/m³ over 120 W/K

        string after = await ReportAt(inside);
        double ground = Read(GroundTemp(), after), outside = Read(OutsideTemp(), after), t = Read(RoomTemp(), after);
        // The ground node ignores the diurnal swing entirely, so it is almost never equal to the
        // outside temperature; the room settles on the conductance-weighted mix of the two.
        Assert.True(Math.Abs(outside - ground) > 1, $"no signal to measure: outside and ground agree\n{after}");
        double equilibrium = (33 * 3.0 * outside + 21 * 1.0 * ground) / (33 * 3.0 + 21 * 1.0);
        Assert.True(Math.Abs(t - equilibrium) < 1, $"cellar at {t:0.0} °C, expected about {equilibrium:0.0} °C\n{after}");
        Assert.True(Math.Abs(t - outside) > 0.1, $"the ground node pulled the cellar nowhere\n{after}");
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Perish_rate_follows_room_temperature()
    {
        BlockPos inside = await Room("CaminusPerish", 280, Stone);
        BlockPos chest = inside.Offset(1, -1, 0);
        // A chest is sidesolid: false on every face, so the vanilla flood fill runs straight through
        // it and the block stays part of the room. No food needed: GetPerishRate ignores the contents.
        World.SetBlock("game:chest-east", chest);
        LightFirepit(inside.Offset(0, -1, 0));
        await World.Until(() => Firepit(inside.Offset(0, -1, 0))?.IsBurning == true);

        // The report is read AT THE CHEST, not at eye height: the perish rate now follows the air at
        // the container's own height, and the firepit puts 1.6 K/m between the two.
        string before = await WaitForRoomReport(chest, r => r.Contains("Perish rate:"));
        double reported = Read(PerishRate(), before);
        Assert.Equal(reported, ContainerPerishRate(chest), 2); // the postfix is live, not just our report

        World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 540f);
        await World.Ticks(300);

        string after = await ReportAt(chest);
        double heated = Read(PerishRate(), after);
        Assert.True(heated > reported, $"perish rate went {reported:0.000} -> {heated:0.000}\nbefore:\n{before}\nafter:\n{after}");
        Assert.Equal(heated, ContainerPerishRate(chest), 2);
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Stratification_makes_the_ceiling_warmer_than_the_floor()
    {
        BlockPos inside = await Room("CaminusStrat", 320, Stone);
        LightFirepit(inside.Offset(0, -1, 0));
        await World.Until(() => Firepit(inside.Offset(0, -1, 0))?.IsBurning == true);

        // 4000 W of firepit x 0.4 K/m/kW = 1.6 K/m, and the interior is 3 blocks tall, so the floor
        // and the ceiling sit 1 m either side of the mean: 3.2 K apart.
        string report = await WaitForRoomReport(inside, r => r.Contains("Stratification: 1.6 K/m"));
        double floor = Read(FloorTemp(), report), ceiling = Read(CeilingTemp(), report);
        Assert.True(ceiling - floor > 3.0, $"ceiling {ceiling:0.0} °C, floor {floor:0.0} °C\n{report}");
        Assert.True(Math.Abs(ceiling + floor - 2 * Read(RoomTemp(), report)) < 0.15,
            $"the mean should sit halfway between the two\n{report}");
    }

    /// <summary>
    /// Vanilla wind is one number per map region (WeatherSystemBase.Event_OnGetWindSpeed only ever
    /// fills the X component), so a scenario can drive it by swapping the wind pattern of every
    /// simulation. Sea level is 3 in Atlas's superflat world and these boxes are built 30 blocks up,
    /// so WeatherSimulationRegion.GetWindSpeed amplifies rather than damps: still reads about 0.04
    /// and a storm about 1.2 (pattern strength x max(1, 0.9 + height above sea level / 100), capped
    /// at 1.5), which puts +40 % on a stone box whose whole east wall faces the wind.
    /// </summary>
    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Wind_increases_losses()
    {
        BlockPos inside = await Room("CaminusWind", 360, Stone);
        await WaitForRoomReport(inside);

        string calm = await WindReport(inside, "still", r => WindBelow(r, 0.05));
        double calmSpeed = Read(WindSpeed(), calm);
        string windy = await WindReport(inside, "storm", r => !WindBelow(r, Math.Max(0.05, 3 * calmSpeed)));
        SetWindPattern("still"); // the world is shared with the other scenarios of this class

        Assert.True(Read(WindSpeed(), windy) > 0.5, $"a storm should blow hard\ncalm:\n{calm}\nwindy:\n{windy}");
        // Wind blows toward +X, i.e. from the west, onto the 9 faces of the east wall:
        // 9 x 3 W/K x windWallFactor 2 x speed, against 162 W/K of calm envelope, so about +40 %
        // at a storm's 1.2.
        Assert.Contains("from the west", windy);
        Assert.True(Read(WindLosses(), windy) > Read(WindLosses(), calm) + 5,
            $"the storm barely cost anything\ncalm:\n{calm}\nwindy:\n{windy}");
        // Losses stays the fabric of the building: the wind's share is on the Wind line only.
        Assert.Equal(Read(Losses(), calm), Read(Losses(), windy), 1);
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Room_keeps_living_without_a_player()
    {
        (ITestPlayer player, BlockPos inside) = await RoomAndPlayer("CaminusAlone", 400, Stone);
        LightFirepit(inside.Offset(0, -1, 0));
        await World.Until(() => Firepit(inside.Offset(0, -1, 0))?.IsBurning == true);
        string before = await WaitForRoomReport(inside, r => r.Contains("Sources: 4000 W"));

        // 60 blocks away and back on the ground: outside the room, but well within the server view
        // distance (the join log reports 128), so the chunk column holding the room stays loaded.
        await player.TeleportTo(World.Spawn.Offset(460, 0, 40));
        World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 540f);
        await World.Ticks(300);

        string after = await ReportAt(inside);
        Assert.Contains("Sources: 4000 W", after); // the 10-tick scan of an empty room still sees the fire
        double gain = Read(RoomTemp(), after) - Read(RoomTemp(), before);
        Assert.True(gain >= 2.0, $"the empty room only gained {gain:0.00} K\nbefore:\n{before}\nafter:\n{after}");
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Unvisited_room_is_discovered_by_its_container()
    {
        ITestPlayer player = await World.JoinPlayer("CaminusCrock");
        BlockPos min = World.Spawn.Offset(440, 30, 40);
        await player.TeleportTo(World.Spawn.Offset(440, 0, 40));
        await WaitForShellChunks(min);
        Build(min, Stone);

        BlockPos chest = min.Offset(1, 1, 1);
        World.SetBlock("game:chest-east", chest);
        await World.Ticks(90); // three mod ticks: no player ever set foot in there
        Assert.Contains("No enclosed room here.", await ReportAt(chest));

        // Exactly what the container calls on its own 10 s tick, through the Harmony postfix.
        // Calling it here instead of waiting for that tick keeps the scenario 10 s shorter.
        ContainerPerishRate(chest);
        Assert.Contains("Room:", await ReportAt(chest));
    }

    // Room_temperature_survives_a_restart is NOT an Atlas scenario. [AtlasScenario(RestartWorld = true)]
    // fails a class that has joined test players (their connections die with the host), and every room
    // here needs a player standing in it for the mod to track it; seeding the room before the restart
    // from another scenario is ruled out too, since xUnit gives no intra-class ordering. What the
    // restart would exercise is split in two and covered: the analytic catch-up by
    // ThermalNetworkTests.Relax_MatchesIntegratingTheSameSpan, and the write itself by the moddata
    // round trip through IServerChunk, which stays a manual in-game check (heat a cabin, quit, come
    // back a few game days later, /caminus temp).

    /// <summary>
    /// 3×3×3 stone-lined cellar dug as deep as the world allows under the world-generated surface,
    /// with the player inside. Atlas's superflat world only has terrain down to Y 0, so the cellar
    /// ends up half buried: exactly what makes it interesting, both wall groups show up at once.
    /// </summary>
    private async Task<BlockPos> Cellar(string playerName, int dx)
    {
        ITestPlayer player = await World.JoinPlayer(playerName);
        BlockPos surface = World.Spawn.Offset(dx, 0, 40);
        await player.TeleportTo(surface);
        await World.Until(() => World.Api.World.BlockAccessor.GetChunkAtBlockPos(surface) != null, 1200);

        int surfaceY = World.Api.World.BlockAccessor.GetTerrainMapheightAt(surface);
        Assert.True(surfaceY == 2, $"worldgen surface at Y={surfaceY}: the face counts asserted below assume 2");
        BlockPos min = new(surface.X, Math.Max(1, surfaceY - 8), surface.Z, surface.dimension);
        await WaitForShellChunks(min);
        Build(min, Stone);

        BlockPos center = min.Offset(1, 1, 1);
        await player.TeleportTo(center);
        return center;
    }

    private double ContainerPerishRate(BlockPos pos)
    {
        BlockEntityContainer be = World.BlockEntityAt<BlockEntityContainer>(pos) ?? throw new XunitException($"no container at {pos}");
        return ((InWorldContainer)ContainerField.GetValue(be)!).GetPerishRate();
    }

    /// <summary>
    /// Joins a player, builds a hollow box far from spawn and 30 blocks above ground,
    /// and teleports the player into it (a player standing in a room is how the mod finds it).
    /// Returns the center interior position.
    /// </summary>
    private async Task<BlockPos> Room(string playerName, int dx, string wall) =>
        (await RoomAndPlayer(playerName, dx, wall)).Pos;

    private async Task<(ITestPlayer Player, BlockPos Pos)> RoomAndPlayer(string playerName, int dx, string wall)
    {
        ITestPlayer player = await World.JoinPlayer(playerName);
        BlockPos min = World.Spawn.Offset(dx, 30, 40); // bottom interior corner
        // The server only loads chunks around players: the player is first placed on the ground
        // below the future box. 30 blocks lower, the open-air "room" it occupies cannot
        // contain the box (RoomRegistry's flood fill is bounded to ±14 blocks).
        await player.TeleportTo(World.Spawn.Offset(dx, 0, 40));
        await WaitForShellChunks(min);
        Build(min, wall);

        BlockPos center = min.Offset(1, 1, 1);
        await player.TeleportTo(center);
        return (player, center);
    }

    /// <summary>
    /// Waits for every chunk the shell touches. A box whose interior starts on a chunk boundary has
    /// one of its walls in the neighbouring chunk, and SetBlock into a chunk the server has not
    /// loaded yet does nothing at all: the wall would silently be missing and the room would leak.
    /// </summary>
    private async Task WaitForShellChunks(BlockPos min)
    {
        Vintagestory.API.Common.IBlockAccessor acc = World.Api.World.BlockAccessor;
        await World.Until(() => acc.GetChunkAtBlockPos(min.Offset(-1, -1, -1)) != null
                             && acc.GetChunkAtBlockPos(min.Offset(Inner, Inner, Inner)) != null, 1200);
    }

    /// <summary>Hollow shell around the interior whose bottom corner is <paramref name="min"/>.</summary>
    private void Build(BlockPos min, string wall)
    {
        for (int x = -1; x <= Inner; x++)
            for (int y = -1; y <= Inner; y++)
                for (int z = -1; z <= Inner; z++)
                {
                    bool shell = x < 0 || y < 0 || z < 0 || x == Inner || y == Inner || z == Inner;
                    World.SetBlock(shell ? wall : Air, min.Offset(x, y, z));
                }
    }

    /// <summary>
    /// Every weather simulation switched to one of the game's wind patterns (still, storm...).
    /// GetWindSpeedAt blends the four map-region sims around the position, and a region that is not
    /// loaded contributes the dummy sim, so all of them have to be switched at once.
    /// </summary>
    private void SetWindPattern(string code)
    {
        WeatherSystemServer weather = World.Api.ModLoader.GetModSystem<WeatherSystemServer>();
        Assert.NotEmpty(weather.weatherSimByMapRegion);
        foreach (WeatherSimulationRegion sim in weather.weatherSimByMapRegion.Values)
            Assert.True(sim.SetWindPattern(code, updateInstant: true), $"no wind pattern '{code}'");
        weather.dummySim?.SetWindPattern(code, updateInstant: true);
    }

    /// <summary>
    /// Drives the wind to a pattern and waits for the mod's report to agree. The pattern is set
    /// again on every attempt: the lookup creates the sims of the neighbouring regions lazily, and
    /// a sim born after the first call still carries its own random pattern.
    /// </summary>
    private async Task<string> WindReport(BlockPos pos, string pattern, System.Func<string, bool> ready)
    {
        string report = "";
        for (int i = 0; i < 20; i++)
        {
            SetWindPattern(pattern);
            await World.Ticks(20);
            report = await ReportAt(pos);
            if (report.Contains("Wind:") && ready(report)) return report;
        }
        throw new XunitException($"the wind never settled on '{pattern}', last report:\n{report}");
    }

    /// <summary>A report with no Wind line yet (the room is not tracked) counts as not windy.</summary>
    private static bool WindBelow(string report, double speed)
    {
        Match m = WindSpeed().Match(report);
        return !m.Success || double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) < speed;
    }

    /// <summary>Lit firepit that never goes out: IsBurning only looks at fuelBurnTime.</summary>
    private void LightFirepit(BlockPos pos)
    {
        World.SetBlock("game:firepit-lit", pos);
        BlockEntityFirepit fire = Firepit(pos) ?? throw new XunitException($"no BlockEntityFirepit at {pos}");
        fire.maxFuelBurnTime = fire.fuelBurnTime = 1_000_000f;
    }

    private BlockEntityFirepit? Firepit(BlockPos pos) => World.BlockEntityAt<BlockEntityFirepit>(pos);

    private async Task<string> ReportAt(BlockPos pos)
    {
        // From the console, WorldPositionArgParser reads coordinates relative to the middle of the
        // map: the "=" prefix forces absolute coordinates.
        CommandResult result = await World.ExecuteCommand($"/caminus temp ={pos.X} ={pos.Y} ={pos.Z}");
        Assert.True(result.Ok, result.Message);
        return result.Message;
    }

    /// <summary>
    /// Waits for the mod's tick (1 s) to have picked up the room. A polling loop
    /// rather than a <c>World.Until</c>: the predicate depends on the result of a command, which is
    /// asynchronous.
    /// </summary>
    private async Task<string> WaitForRoomReport(BlockPos pos, Func<string, bool>? ready = null)
    {
        ready ??= r => r.StartsWith("Room", StringComparison.Ordinal);
        string report = "";
        for (int i = 0; i < 90; i++)
        {
            report = await ReportAt(pos);
            if (ready(report)) return report;
            await World.Ticks(10);
        }
        throw new XunitException($"room never reported at {pos}, last report:\n{report}");
    }

    private static double Read(Regex what, string report)
    {
        Match m = what.Match(report);
        Assert.True(m.Success, $"'{what}' not found in:\n{report}");
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
