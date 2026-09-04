using System.Diagnostics;
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

    // chimneycourse.json declares variantgroups courses/type/orientation with a single course state,
    // so the placeable block is claybrickchimney-four-<type>-<orientation>. It is the one carrying the
    // Chimney behavior; the legacy claybrickchimney-<type>-<state>-<orientation> carries none.
    private const string Chimney = "game:claybrickchimney-four-red-ns";

    /// <summary>5×5×5 box: solid shell, interior air volume 3×3×3 (27 blocks, 54 faces).</summary>
    private const int Inner = 3;

    [GeneratedRegex(@"Room: (-?\d+(?:\.\d+)?) °C")]
    private static partial Regex RoomTemp();

    [GeneratedRegex(@"outside (-?\d+(?:\.\d+)?) °C")]
    private static partial Regex OutsideTemp();

    [GeneratedRegex(@"Losses: (-?\d+(?:\.\d+)?) W/K")]
    private static partial Regex Losses();

    [GeneratedRegex(@"Losses: -?\d+(?:\.\d+)? W/K, i\.e\. (-?\d+) W")]
    private static partial Regex LossWatts();

    [GeneratedRegex(@"Ground node: (-?\d+(?:\.\d+)?) °C")]
    private static partial Regex GroundTemp();

    [GeneratedRegex(@"Perish rate: (\d+(?:\.\d+)?)x")]
    private static partial Regex PerishRate();

    [GeneratedRegex(@"Wind: (\d+(?:\.\d+)?) ")]
    private static partial Regex WindSpeed();

    [GeneratedRegex(@"\(\+(\d+(?:\.\d+)?) % losses")]
    private static partial Regex WindLosses();

    [GeneratedRegex(@"Sun: \d+(?:\.\d+)? \(forest shade \d+(?:\.\d+)?, \+(-?\d+) W\)")]
    private static partial Regex SunWatts();

    [GeneratedRegex(@"Sources: -?\d+ W \(nearby (-?\d+) W\)")]
    private static partial Regex NearbyWatts();

    [GeneratedRegex(@"draft (\d+\.\d+) m³/s")]
    private static partial Regex DraftFlow();

    [GeneratedRegex(@"floor (-?\d+(?:\.\d+)?) °C")]
    private static partial Regex FloorTemp();

    [GeneratedRegex(@"ceiling (-?\d+(?:\.\d+)?) °C")]
    private static partial Regex CeilingTemp();

    [GeneratedRegex(@"eyes (-?\d+(?:\.\d+)?) °C")]
    private static partial Regex EyesTemp();

    [GeneratedRegex(@"Body: (-?\d+(?:\.\d+)?) °C")]
    private static partial Regex BodyTemp();

    [GeneratedRegex(@"\(air (-?\d+(?:\.\d+)?) °C")]
    private static partial Regex BodyAir();

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
        (ITestPlayer player, BlockPos inside) = await RoomAndPlayer("CaminusWarm", 40, Stone);
        try
        {
            LightFirepit(inside.Offset(0, -1, 0));
            await World.Until(() => Firepit(inside.Offset(0, -1, 0))?.IsBurning == true);

            // Waiting on the source line, not just on the room: the room can already be tracked from an
            // earlier tick and answer instantly with the scan that ran before the firepit was placed.
            string before = await WaitForRoomReport(inside, r => r.Contains("Sources: 4000 W")); // 10 units × 400 W

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
        finally { await Leave(player); }
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Glass_pane_replaces_a_stone_face()
    {
        (ITestPlayer player, BlockPos inside) = await RoomAndPlayer("CaminusPane", 80, Stone);
        try
        {
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
        finally { await Leave(player); }
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Open_air_is_not_a_room()
    {
        ITestPlayer player = await World.JoinPlayer("CaminusOutside");
        try
        {
            await player.TeleportTo(World.Spawn.Offset(200, 0, 40));
            await World.Ticks(90); // three mod ticks
            Assert.Contains("No enclosed room here.", await ReportAt(player.Position));
        }
        finally { await Leave(player); }
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Wood_walls_leak_less_than_stone()
    {
        (ITestPlayer stonePlayer, BlockPos stone) = await RoomAndPlayer("CaminusStone", 120, Stone);
        (ITestPlayer woodPlayer, BlockPos wood) = await RoomAndPlayer("CaminusWood", 160, Wood);
        try
        {
            string stoneReport = await WaitForRoomReport(stone);
            string woodReport = await WaitForRoomReport(wood);

            Assert.Contains("Stone: 54 faces", stoneReport);
            Assert.Contains("Wood: 54 faces", woodReport);
            Assert.True(Read(Losses(), woodReport) < Read(Losses(), stoneReport),
                $"stone:\n{stoneReport}\nwood:\n{woodReport}");
        }
        finally
        {
            await Leave(stonePlayer);
            await Leave(woodPlayer);
        }
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Buried_room_has_a_ground_node()
    {
        (ITestPlayer player, BlockPos inside) = await Cellar("CaminusCellar", 240);
        try
        {
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
        finally { await Leave(player); }
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Perish_rate_follows_room_temperature()
    {
        (ITestPlayer player, BlockPos inside) = await RoomAndPlayer("CaminusPerish", 280, Stone);
        try
        {
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
        finally { await Leave(player); }
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Stratification_makes_the_ceiling_warmer_than_the_floor()
    {
        (ITestPlayer player, BlockPos inside) = await RoomAndPlayer("CaminusStrat", 320, Stone);
        try
        {
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
        finally { await Leave(player); }
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
        (ITestPlayer player, BlockPos inside) = await RoomAndPlayer("CaminusWind", 360, Stone);
        try
        {
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
        finally { await Leave(player); }
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Room_keeps_living_without_a_player()
    {
        (ITestPlayer player, BlockPos inside) = await RoomAndPlayer("CaminusAlone", 400, Stone);
        try
        {
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
        finally { await Leave(player); }
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Unvisited_room_is_discovered_by_its_container()
    {
        ITestPlayer player = await World.JoinPlayer("CaminusCrock");
        try
        {
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
        finally { await Leave(player); }
    }

    /// <summary>
    /// The server half of the overlay. Atlas has no client, so the highlight cubes and the HUD text
    /// are checked where they are built, and the per-face flows are checked against the same room's
    /// /caminus temp report: the overlay and the report must not tell two different stories.
    /// </summary>
    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Overlay_describes_the_room()
    {
        (ITestPlayer player, BlockPos inside) = await RoomAndPlayer("CaminusOverlay", 480, Stone);
        try
        {
            LightFirepit(inside.Offset(0, -1, 0));
            await World.Until(() => Firepit(inside.Offset(0, -1, 0))?.IsBurning == true);
            await WaitForRoomReport(inside, r => r.Contains("Sources: 4000 W"));

            // About three time constants (27 m³ x 6030 J/K/m³ over 162 W/K), so the room settles near
            // its 4000 W / 162 W/K = +25 K equilibrium and every face, floor included, loses heat.
            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 540f);
            await World.Ticks(300);
            // The Losses line is the calm envelope only, the faces also carry the wind: compare the two
            // on a still day, where the wind is worth under 2 % of a 162 W/K box.
            string report = await WindReport(inside, "still", r => WindBelow(r, 0.05));

            RoomThermalSystem thermal = World.Api.ModLoader.GetModSystem<RoomThermalSystem>();
            Assert.True(thermal.TryGetFaceFlows(inside, out RoomFlows? flows), report);
            Assert.Equal(54, flows!.Faces.Count);
            Assert.True(flows.Temperature > flows.OutsideTemperature + 5,
                $"the firepit barely warmed the room, nothing to look at\n{report}");
            Assert.DoesNotContain(flows.Faces, f => f.Watts <= 0);

            // The Losses line is the fabric against the outside air; each face also sees whatever sun falls
            // on it, which is the same Sun line the report prints, so the two still have to add up.
            double sum = flows.Faces.Sum(f => f.Watts), losses = Read(LossWatts(), report) - SunOf(report);
            Assert.True(Math.Abs(sum - losses) < 0.05 * losses,
                $"the faces add up to {sum:0} W, the report says {losses:0} W\n{report}");

            (List<BlockPos> blocks, List<int> colors) = OverlayServer.Highlights(flows);
            Assert.Equal(54, blocks.Count);
            Assert.Equal(blocks.Count, colors.Count);
            Assert.StartsWith("Room ", OverlayServer.Describe(flows, inside.Y));
        }
        finally { await Leave(player); }
    }

    /// <summary>
    /// A lava pocket walled into the rock under the floor. The room's air bbox starts one block above
    /// the shell, so lava 5 blocks below the interior floor sits 3 blocks of rock past the first wall
    /// layer: 12 heat units x 400 W, attenuated by 1/(1+3), i.e. 1200 W that a bbox+1 scan would miss.
    /// Geology and forest density cannot be driven in Atlas's superflat world (one climate map, no
    /// hot springs): their formulas are covered by Caminus.Core.Tests.ExposureTests instead.
    /// </summary>
    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Lava_under_the_floor_heats_the_cellar_through_rock()
    {
        (ITestPlayer controlPlayer, BlockPos control) = await RoomAndPlayer("CaminusNoLava", 520, Stone);
        (ITestPlayer heatedPlayer, BlockPos heated) = await RoomAndPlayer("CaminusLava", 560, Stone);
        try
        {
            await WaitForRoomReport(control);
            await WaitForRoomReport(heated);

            // The pocket crosses a chunk boundary below the box: wait for that chunk too, or SetBlock is a no-op.
            BlockPos lava = heated.Offset(0, -5, 0);
            await World.Until(() => World.Api.World.BlockAccessor.GetChunkAtBlockPos(lava.Offset(0, -1, 0)) != null, 1200);
            // Lava spreads: it only stays put walled in on all six sides.
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                        World.SetBlock(Stone, lava.Offset(x, y, z));
            World.SetBlock("game:lava-still-7", lava);

            string report = await WaitForRoomReport(heated, r => NearbyWatts().IsMatch(r));
            Assert.Contains("Sources: 1200 W (nearby 1200 W)", report);

            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 540f);
            await World.Ticks(300);

            string warm = await ReportAt(heated), cold = await ReportAt(control);
            // 1200 W over a 162 W/K stone box is +7.4 K at equilibrium; the two boxes are 40 blocks apart
            // at the same height, so they share their climate, their wind and their sun.
            double gain = Read(RoomTemp(), warm) - Read(RoomTemp(), cold);
            Assert.True(gain > 1.0, $"the lava is worth {gain:0.00} K\nlava:\n{warm}\ncontrol:\n{cold}");
            Assert.DoesNotContain("nearby", cold);
        }
        finally
        {
            await Leave(controlPlayer);
            await Leave(heatedPlayer);
        }
    }

    /// <summary>
    /// Sol-air gain: the same box at noon and at midnight. Atlas's superflat sky is open, so every
    /// outside face reads full sunlight and only the sun's height above the horizon changes.
    /// </summary>
    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Sun_warms_the_roof_by_day()
    {
        (ITestPlayer player, BlockPos inside) = await RoomAndPlayer("CaminusSun", 600, Stone);
        try
        {
            await WaitForRoomReport(inside);
            // Back to real time: the polling below must not drag the clock across sunrise.
            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 0f);

            // The 9 roof faces take the sun's noon height and the walls it shines on take the horizontal
            // cosine of incidence, so the exact wattage moves with the season and the latitude.
            string noon = await ReportAtHour(inside, 12, r => SunOf(r) > 0);
            Assert.True(SunOf(noon) > 0, $"no sun at noon\n{noon}");

            string night = await ReportAtHour(inside, 0, r => SunOf(r) == 0);
            Assert.Equal(0, SunOf(night));
            Assert.Contains("Sun: 0.0 (", night);
        }
        finally { await Leave(player); }
    }

    /// <summary>Winds the calendar forward to the next occurrence of that hour, then polls the report.</summary>
    private async Task<string> ReportAtHour(BlockPos pos, double hour, Func<string, bool> ready)
    {
        Vintagestory.API.Common.IGameCalendar cal = World.Api.World.Calendar;
        cal.Add((float)(((hour - cal.HourOfDay) % cal.HoursPerDay + cal.HoursPerDay) % cal.HoursPerDay));
        return await WaitForRoomReport(pos, ready);
    }

    /// <summary>Solar watts of a report, -1 when the room is not reporting yet.</summary>
    private static double SunOf(string report)
    {
        Match m = SunWatts().Match(report);
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
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
    private async Task<(ITestPlayer Player, BlockPos Pos)> Cellar(string playerName, int dx)
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
        return (player, center);
    }

    private double ContainerPerishRate(BlockPos pos)
    {
        BlockEntityContainer be = World.BlockEntityAt<BlockEntityContainer>(pos) ?? throw new XunitException($"no container at {pos}");
        return ((InWorldContainer)ContainerField.GetValue(be)!).GetPerishRate();
    }

    /// <summary>
    /// Joins a player, builds a hollow box far from spawn and 30 blocks above ground, and teleports
    /// the player into it (a player standing in a room is how the mod finds it). Returns both: the
    /// caller owes the player a <see cref="Leave"/>, the box only needs its center interior position.
    /// </summary>
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
    private async Task WaitForShellChunks(BlockPos min, int sx = Inner, int sy = Inner, int sz = Inner)
    {
        Vintagestory.API.Common.IBlockAccessor acc = World.Api.World.BlockAccessor;
        List<BlockPos> corners = [];
        foreach (int x in (int[])[-1, sx])
            foreach (int y in (int[])[-1, sy])
                foreach (int z in (int[])[-1, sz])
                    corners.Add(min.Offset(x, y, z));
        await World.Until(() => corners.TrueForAll(p => acc.GetChunkAtBlockPos(p) != null), 1200);
    }

    /// <summary>Hollow shell around the interior whose bottom corner is <paramref name="min"/>.</summary>
    private void Build(BlockPos min, string wall, int sx = Inner, int sy = Inner, int sz = Inner)
    {
        for (int x = -1; x <= sx; x++)
            for (int y = -1; y <= sy; y++)
                for (int z = -1; z <= sz; z++)
                {
                    bool shell = x < 0 || y < 0 || z < 0 || x == sx || y == sy || z == sz;
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

    // --- Milestone 3: body temperature -------------------------------------------------------

    /// <summary>
    /// The JSON patch swapped the behavior on the entity, and the player reads the room's own air
    /// rather than the climate. A firepit is what separates the two: an unheated room settles on
    /// the outside temperature and the assertion would prove nothing.
    /// </summary>
    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Player_body_temperature_uses_our_behavior()
    {
        (ITestPlayer player, BlockPos inside) = await RoomAndPlayer("CaminusBody", 640, Stone);
        try
        {
            await Survival("CaminusBody");
            LightFirepit(inside.Offset(0, -1, 0));
            await World.Until(() => Firepit(inside.Offset(0, -1, 0))?.IsBurning == true);

            // Two body temperature behaviors on one entity would fight over the same bodyTemp tree, so
            // the patch has to REPLACE the vanilla entry, not add ours next to it.
            EntityBehaviorBodyTemperature? behavior = player.Entity.GetBehavior<EntityBehaviorBodyTemperature>();
            Assert.IsType<EntityBehaviorCaminusBodyTemperature>(behavior);
            Assert.Equal(1, player.Entity.SidedProperties.Behaviors.Count(b => b is EntityBehaviorBodyTemperature));

            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 540f);
            await World.Ticks(300);

            var ours = (EntityBehaviorCaminusBodyTemperature)behavior!;
            string report = await WaitForRoomReport(inside, r => Read(RoomTemp(), r) > Read(OutsideTemp(), r) + 5);
            double outside = Read(OutsideTemp(), report);
            string line = "";
            for (int i = 0; i < 40 && !(BodyAir().IsMatch(line) && Read(BodyAir(), line) > outside + 5); i++)
            {
                await World.Ticks(30);
                line = ours.Describe();
            }
            Assert.True(BodyAir().IsMatch(line) && Read(BodyAir(), line) > outside + 5,
                $"the body never saw the warm room air (outside {outside:0.0} °C)\n{line}\n{await ReportAt(inside)}");

            // The air the body sees is the room node at eye height, not the climate outside. The two are
            // read a moment apart while the room is still warming, hence the 2 K window.
            report = await ReportAt(inside);
            Assert.True(Math.Abs(Read(EyesTemp(), report) - Read(BodyAir(), line)) < 2.0,
                $"the body reads {Read(BodyAir(), line):0.0} °C, the room says {Read(EyesTemp(), report):0.0} °C\n{report}\n{line}");
            Assert.Contains("radiant", line);
            Assert.InRange(Read(BodyTemp(), line), 31, 45);

            // Same line through the command, this time with a player as the caller.
            string full = PlayerReport(player, "/caminus temp");
            Assert.Contains("Room:", full);
            Assert.Contains("Body:", full);

            // And through the overlay HUD, where it is a suffix on the third line.
            Assert.True(World.Api.ModLoader.GetModSystem<RoomThermalSystem>().TryGetFaceFlows(inside, out RoomFlows? flows), report);
            Assert.EndsWith(" °C", OverlayServer.Describe(flows!, inside.Y, ours.CurBodyTemperature));
            Assert.Contains("body ", OverlayServer.Describe(flows!, inside.Y, ours.CurBodyTemperature));
        }
        finally { await Leave(player); }
    }

    /// <summary>
    /// Vanilla gives a flat +1 °C per hour in ANY enclosed room, whatever the weather. With that
    /// gone, a room whose air is below the <c>bodyTemperatureResistance</c> world config (default
    /// 0 °C) has to cool the player, while an identical room in the normal climate must not.
    /// </summary>
    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task Unheated_cold_room_cools_the_player()
    {
        (ITestPlayer control, BlockPos mild) = await RoomAndPlayer("CaminusBodyMild", 720, Stone);
        (ITestPlayer chilled, BlockPos cold) = await RoomAndPlayer("CaminusBodyCold", 780, Stone);
        await Survival("CaminusBodyMild");
        await Survival("CaminusBodyCold");
        SetWindPattern("still"); // another scenario may have left a storm blowing warm air in
        await WaitForRoomReport(mild);
        string before = await WaitForRoomReport(cold);

        // Atlas's superflat world has one climate map and no season worth overriding
        // (SetSeasonOverride moves the worldgen season, not this flat map), and the comfort term
        // only starts cooling below bodyTemperatureResistance. So the cold room is made cold at the
        // source: OnGetClimate is the engine's own hook for the instantaneous temperature, the same
        // one ModTemperature uses, and Caminus reads the outside air through GetClimateAt every
        // tick. The X slice keeps every other room of this class on its own weather.
        int gate = World.Spawn.X + 760;
        void Freeze(ref Vintagestory.API.Common.ClimateCondition climate, BlockPos pos, Vintagestory.API.Common.EnumGetClimateMode mode, double totalDays)
        {
            if (climate != null && pos.X >= gate) climate.Temperature = -25f;
        }

        World.Api.Event.OnGetClimate += Freeze;
        try
        {
            var mildBody = Assert.IsType<EntityBehaviorCaminusBodyTemperature>(control.Entity.GetBehavior<EntityBehaviorBodyTemperature>());
            var coldBody = Assert.IsType<EntityBehaviorCaminusBodyTemperature>(chilled.Entity.GetBehavior<EntityBehaviorBodyTemperature>());
            float mildStart = mildBody.CurBodyTemperature, coldStart = coldBody.CurBodyTemperature;

            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 540f);
            // The stone box has a time constant of about 1000 game seconds and x10 time buys 300 of
            // them per real second, so 900 ticks is roughly nine of them: the room really reaches
            // the forced -25 °C instead of drifting halfway there.
            await World.Ticks(900);

            string after = await ReportAt(cold);
            Assert.True(Read(RoomTemp(), after) < -10,
                $"the forced climate never reached the room\nbefore:\n{before}\nafter:\n{after}");

            double coldDrop = coldStart - coldBody.CurBodyTemperature;
            double mildDrop = mildStart - mildBody.CurBodyTemperature;
            Assert.True(coldDrop > 2.0,
                $"the player only lost {coldDrop:0.00} K in a room at {Read(RoomTemp(), after):0.0} °C\n{after}\n{coldBody.Describe()}");
            Assert.True(coldDrop > mildDrop + 2.0,
                $"cold room {coldDrop:0.00} K vs control {mildDrop:0.00} K\ncold: {coldBody.Describe()}\ncontrol: {mildBody.Describe()}");
        }
        finally
        {
            World.Api.Event.OnGetClimate -= Freeze;
            await Leave(control);
            await Leave(chilled);
        }
    }

    /// <summary>
    /// Frees the client slot. The embedded server accepts 16 clients and no queue, test players never
    /// leave on their own, and xUnit gives no intra-class ordering: every scenario here joins its
    /// players inside a try and hands the seats back in the finally, so the peak is what the busiest
    /// single scenario needs (two) rather than the sum of the whole class.
    /// </summary>
    private async Task Leave(ITestPlayer player)
    {
        // Printed before the disconnect, so the run log carries how close each scenario came to the cap.
        Console.WriteLine($"[Caminus] {player.Player.PlayerName} leaves, " +
                          $"{World.Api.World.AllOnlinePlayers.Length} players online");
        ((Vintagestory.API.Server.IServerPlayer)player.Player).Disconnect("scenario over");
        await World.Until(() => !player.IsConnected, 600);
    }

    /// <summary>
    /// Atlas players join in creative, and vanilla pins a creative player's body temperature at
    /// 37 °C and returns before anything else runs (l.228).
    /// </summary>
    private async Task Survival(string playerName)
    {
        CommandResult result = await World.ExecuteCommand($"/gamemode {playerName} survival");
        Assert.True(result.Ok, result.Message);
    }

    /// <summary>
    /// Runs a command with a player as the caller. Atlas's own ExecuteCommand always runs as the
    /// console, and the Body line only exists when the caller has an entity.
    /// </summary>
    private string PlayerReport(ITestPlayer player, string command)
    {
        string message = "";
        World.Api.ChatCommands.ExecuteUnparsed(command, new Vintagestory.API.Common.TextCommandCallingArgs
        {
            LanguageCode = "en",
            Caller = new Vintagestory.API.Common.Caller { Player = player.Player, CallerPrivileges = ["*"] }
        }, result => message = result.StatusMessage ?? "");
        return message;
    }

    // --- Milestone 4: chimney -----------------------------------------------------------------

    /// <summary>
    /// Stacks <paramref name="blocks"/> chimney blocks straight up from the roof over
    /// <paramref name="firepit"/>. +3 is the roof itself: the firepit sits on the interior floor
    /// layer (Room's y=0), the roof shell is 3 blocks above that (Room's y=3, see <see cref="Build"/>),
    /// and the first chimney block replaces it.
    /// </summary>
    private void BuildChimney(BlockPos firepit, int blocks)
    {
        for (int i = 0; i < blocks; i++)
            World.SetBlock(Chimney, firepit.Offset(0, 3 + i, 0));
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Chimney_is_detected_with_its_height()
    {
        (ITestPlayer player, BlockPos inside) = await RoomAndPlayer("CaminusChimney4", 860, Stone);
        try
        {
            BlockPos firepit = inside.Offset(0, -1, 0);
            LightFirepit(firepit);
            await World.Until(() => Firepit(firepit)?.IsBurning == true);
            BuildChimney(firepit, 4);
            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 540f);

            await WaitForRoomReport(inside, r => r.Contains("Flue: 4 m, 1 column"));
            await World.Ticks(300); // ≈ 10 real seconds, ≈ 1.7 game hours at ×10: time for the draft to settle the haze
            string report = await ReportAt(inside);

            Assert.Contains("Flue: 4 m, 1 column", report);
            Assert.Contains("(flue takes", report);
            Assert.DoesNotContain("Smoke: heavy", report);
        }
        finally { await Leave(player); }
    }

    /// <summary>
    /// Compares draft/√dT rather than the raw flow: Q ∝ √(dH·dT), so dividing the temperature
    /// difference back out leaves only what the flue's own height is worth, robust to the two
    /// rooms not settling on quite the same temperature.
    /// </summary>
    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Taller_chimney_draws_more()
    {
        (ITestPlayer shortPlayer, BlockPos shortRoom) = await RoomAndPlayer("CaminusChimShort", 940, Stone);
        (ITestPlayer tallPlayer, BlockPos tallRoom) = await RoomAndPlayer("CaminusChimTall", 1020, Stone);
        try
        {
            SetWindPattern("still"); // this comparison reads instantaneous draft: keep the wind out of it
            BlockPos shortFire = shortRoom.Offset(0, -1, 0), tallFire = tallRoom.Offset(0, -1, 0);
            LightFirepit(shortFire);
            LightFirepit(tallFire);
            await World.Until(() => Firepit(shortFire)?.IsBurning == true && Firepit(tallFire)?.IsBurning == true);
            BuildChimney(shortFire, 4);
            BuildChimney(tallFire, 8);
            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 540f);

            await WaitForRoomReport(shortRoom, r => r.Contains("Flue: 4 m, 1 column"));
            await WaitForRoomReport(tallRoom, r => r.Contains("Flue: 8 m, 1 column"));
            await World.Ticks(300);

            string shortReport = await ReportAt(shortRoom), tallReport = await ReportAt(tallRoom);
            double shortDt = Read(CeilingTemp(), shortReport) - Read(OutsideTemp(), shortReport);
            double tallDt = Read(CeilingTemp(), tallReport) - Read(OutsideTemp(), tallReport);
            Assert.True(shortDt > 0.5 && tallDt > 0.5,
                $"not enough of a draft signal yet\nshort:\n{shortReport}\ntall:\n{tallReport}");

            double shortNorm = Read(DraftFlow(), shortReport) / Math.Sqrt(shortDt);
            double tallNorm = Read(DraftFlow(), tallReport) / Math.Sqrt(tallDt);
            Assert.True(tallNorm > shortNorm,
                $"8-block flue: {tallNorm:0.0000} vs 4-block: {shortNorm:0.0000}\nshort:\n{shortReport}\ntall:\n{tallReport}");
        }
        finally
        {
            await Leave(shortPlayer);
            await Leave(tallPlayer);
        }
    }

    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task Hearth_without_chimney_fills_the_room_with_smoke()
    {
        (ITestPlayer player, BlockPos inside) = await RoomAndPlayer("CaminusSmoky", 1100, Stone);
        try
        {
            BlockPos firepit = inside.Offset(0, -1, 0);
            LightFirepit(firepit);
            await World.Until(() => Firepit(firepit)?.IsBurning == true);
            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 540f);

            await WaitForRoomReport(inside, r => r.Contains("Sources: 4000 W"));
            // No flue: air changes stay at the envelope's own leakage alone, so the haze needs
            // longer than the other scenarios to reach its (clamped) equilibrium.
            await World.Ticks(600); // ≈ 20 real seconds, ≈ 3.3 game hours at ×10
            string report = await ReportAt(inside);

            Assert.Contains("Flue: none", report);
            Assert.Contains("Smoke: heavy", report);
        }
        finally { await Leave(player); }
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Blocked_chimney_counts_as_none()
    {
        (ITestPlayer player, BlockPos inside) = await RoomAndPlayer("CaminusChimBlock", 1180, Stone);
        try
        {
            BlockPos firepit = inside.Offset(0, -1, 0);
            LightFirepit(firepit);
            await World.Until(() => Firepit(firepit)?.IsBurning == true);
            BuildChimney(firepit, 4);
            World.SetBlock(Stone, firepit.Offset(0, 7, 0)); // caps the stack: no sky above it

            string report = await WaitForRoomReport(inside, r => r.Contains("Flue: blocked"));
            Assert.Contains("Flue: blocked (roof over the stack)", report);
            Assert.DoesNotContain("flue takes", report);
        }
        finally { await Leave(player); }
    }

    // --- Milestone 6: our own room detection ---------------------------------------------------

    /// <summary>
    /// A 20x4x20 hall: past the vanilla registry's 14-block limit on two axes, so the game itself
    /// calls it an open volume, and Caminus's own flood fill keeps it. Also where one scan is timed.
    /// </summary>
    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task Hall_wider_than_the_vanilla_limit_is_a_room()
    {
        ITestPlayer player = await World.JoinPlayer("CaminusHall");
        try
        {
            BlockPos min = World.Spawn.Offset(1260, 30, 40);
            await player.TeleportTo(World.Spawn.Offset(1260, 0, 40));
            await WaitForShellChunks(min, 20, 4, 20);
            Build(min, Stone, 20, 4, 20);
            BlockPos inside = min.Offset(10, 1, 10);
            await player.TeleportTo(inside);

            Assert.True(World.Api.ModLoader.GetModSystem<RoomRegistry>().GetRoomForPosition(inside).ExitCount > 0,
                "the vanilla flood fill was expected to give up on a hall this wide");

            string report = await WaitForRoomReport(inside);
            Assert.Contains("Volume 1600 blocks", report);
            // Floor and ceiling are 20x20 each, the four walls 20x4: 800 + 320 = 1120 faces.
            Assert.Contains("Stone: 1120 faces", report);
            Assert.DoesNotContain("Openings", report);

            using var scanner = new RoomScanner(World.Api, 4096, 48);
            Stopwatch clock = Stopwatch.StartNew();
            RoomVolume? volume = scanner.Scan(inside);
            clock.Stop();
            Assert.NotNull(volume);
            Console.WriteLine($"[Caminus] one flood fill of the 20x4x20 hall: {clock.Elapsed.TotalMilliseconds:0.00} ms");
        }
        finally { await Leave(player); }
    }

    /// <summary>
    /// A hole in a wall is an opening the model prices, not an exit that costs the room its
    /// existence. The same box twice, one of them missing a wall block at floor level: it stays a
    /// room, the opening shows up in the report, and it gives the chimney a real inlet instead of the
    /// envelope's cracks, so the same 4-block stack draws harder.
    /// </summary>
    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task Window_is_an_opening_not_an_exit()
    {
        (ITestPlayer shutPlayer, BlockPos shutRoom) = await RoomAndPlayer("CaminusNoWindow", 1340, Stone);
        (ITestPlayer openPlayer, BlockPos openRoom) = await RoomAndPlayer("CaminusWindow", 1400, Stone);
        try
        {
            SetWindPattern("still"); // this comparison reads instantaneous draft: keep the wind out of it
            // The middle block of the north wall, at interior floor level. What replaces it is real
            // air whose own column is still under the roof, so it joins the room, and the block
            // beyond it sees the sky, which is where the room ends.
            World.SetBlock(Air, openRoom.Offset(0, -1, -2));

            string open = await WaitForRoomReport(openRoom, r => r.Contains("Openings: 1 faces"));
            Assert.Contains("Room:", open);
            Assert.Contains("Volume 28 blocks", open); // the 27 interior blocks plus the hole itself
            Assert.Contains("Stone: 57 faces", open);  // 54 of the box, minus the one the hole opened, plus its own 4

            BlockPos shutFire = shutRoom.Offset(0, -1, 0), openFire = openRoom.Offset(0, -1, 0);
            LightFirepit(shutFire);
            LightFirepit(openFire);
            await World.Until(() => Firepit(shutFire)?.IsBurning == true && Firepit(openFire)?.IsBurning == true);
            BuildChimney(shutFire, 4);
            BuildChimney(openFire, 4);
            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 540f);

            await WaitForRoomReport(shutRoom, r => r.Contains("Flue: 4 m, 1 column"));
            await WaitForRoomReport(openRoom, r => r.Contains("Flue: 4 m, 1 column"));
            await World.Ticks(300);

            open = await ReportAt(openRoom);
            string shut = await ReportAt(shutRoom);
            Assert.Contains("(1 opening)", open);
            Assert.Contains("inlet leakage", shut);
            Assert.True(Read(DraftFlow(), open) > Read(DraftFlow(), shut),
                $"the window drew no better than the cracks\nwindow:\n{open}\nsealed:\n{shut}");
        }
        finally
        {
            await Leave(shutPlayer);
            await Leave(openPlayer);
        }
    }

    // --- Milestone 5: the overlay as the client receives it -------------------------------------

    /// <summary>
    /// The other half of <see cref="Overlay_describes_the_room"/>: not what the server builds, but what
    /// leaves it. Atlas 0.12 drains the test player's own connection, so the highlight packet, the
    /// particles and the OverlayPacket are read where a real client reads them, and
    /// <c>OverlayServer.Highlights</c> is checked against the cubes that actually travelled.
    /// </summary>
    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task Overlay_reaches_the_player()
    {
        (ITestPlayer player, BlockPos inside) = await RoomAndPlayer("CaminusOvNet", 1460, Stone);
        try
        {
            BlockPos firepit = inside.Offset(0, -1, 0);
            LightFirepit(firepit);
            await World.Until(() => Firepit(firepit)?.IsBurning == true);
            await WaitForRoomReport(inside, r => r.Contains("Sources: 4000 W"));
            // Same warm-up as Overlay_describes_the_room: every face has to be losing heat before the
            // colours below mean anything (red is "heat leaving", blue would be heat coming in).
            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 540f);
            await World.Ticks(300);
            // Back to the normal clock before anything is compared: the assertions below read the
            // packet and the model a moment apart, and a sky racing across the map moves the sun-warmed
            // faces between the two.
            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 0f);

            player.Client.Clear();
            // The command's answer never becomes a chat line here: a real client's message goes through
            // the server's chat handler, which supplies the result callback, and Atlas has no "player
            // types this" entry point, so the answer only exists in the callback we pass ourselves.
            // Said through the real client chat packet path (Atlas 0.12.0-rc.2): the command answer
            // reaches this client's chat lines, which is what a real player reads.
            await player.Say("/caminus overlay");
            Assert.Contains(player.Client.ChatLines(), line => line.Contains("Thermal overlay on."));

            // The overlay rides the mod's own 1 s tick, i.e. 30 server ticks.
            await World.Until(() => player.Client.Highlights(OverlayServer.HighlightSlot).Count > 0, 300);

            IReadOnlyList<HighlightedBlock> cubes = player.Client.Highlights(OverlayServer.HighlightSlot);
            RoomThermalSystem thermal = World.Api.ModLoader.GetModSystem<RoomThermalSystem>();
            Assert.True(thermal.TryGetFaceFlows(inside, out RoomFlows? flows), "the room stopped being tracked");
            (List<BlockPos> blocks, _) = OverlayServer.Highlights(flows!);
            // A wall block can carry two faces (an inner corner), and the highlight is one cube per
            // block: on this box the 54 faces sit on 54 distinct blocks (a 3x3 plate per wall, no
            // block shared between two walls), so the count survives the dedupe.
            Assert.Equal(54, blocks.Count);
            Assert.Equal(blocks.ToHashSet(), cubes.Select(c => c.Pos).ToHashSet());
            // Stone wall, no ground, no opening, heat leaving: (255, 50, 40) shaded by alpha.
            Assert.All(cubes, c => Assert.Equal(255, c.Rgba.R));
            Assert.All(cubes, c => Assert.True(c.Rgba.R > c.Rgba.G && c.Rgba.R > c.Rgba.B,
                $"cube at {c.Pos} is not red: {c.Rgba}"));

            IReadOnlyList<OverlayPacket> texts = player.Client.Packets<OverlayPacket>("caminus");
            Assert.NotEmpty(texts);
            Assert.StartsWith("Room ", texts[^1].Text);
            Assert.Contains("outside", texts[^1].Text);

            // The puffs are spawned by OverlayClient now, so no particle packet travels at all: they
            // ride the overlay packet as faces, on this player's connection only. Client.Particles()
            // is therefore empty, and the assertion moved onto what the packet asks the client to draw.
            Assert.Empty(player.Client.Particles());
            List<ParticleFace> expected = OverlayServer.ParticleFaces(flows!);
            Assert.Equal(16, expected.Count); // the loudest 16 of the 54 faces; no chimney on this box
            Assert.Equal(expected.Count, texts[^1].Faces.Count);
            // WHICH sixteen is deliberately not compared: the cut falls inside a group of twelve wall
            // faces that the wind separates by a fraction of a watt, and the packet is always a mod
            // tick older than the flows read above, so the last few swap places between the two. What
            // has to hold is that every puff is a real face of this very room, and each of them once.
            HashSet<(int, int, int, int)> envelope = [.. flows!.Faces
                .Select(f => (f.Face.Pos.X, f.Face.Pos.Y, f.Face.Pos.Z, f.Face.Facing.Index))];
            List<(int, int, int, int)> sent = [.. texts[^1].Faces.Select(f => (f.X, f.Y, f.Z, f.Facing))];
            Assert.Equal(sent.Count, sent.Distinct().Count());
            Assert.All(sent, f => Assert.Contains(f, envelope));
            // Every face of this room loses heat, so every puff flies out of it.
            Assert.All(texts[^1].Faces, f => Assert.True(f.Watts > 0 && f.Speed > 0,
                $"the face at {f.X},{f.Y},{f.Z} carries {f.Watts:0} W at speed {f.Speed}"));

            // Two more mod ticks with nobody reading, so the timed read below pays for a real drain
            // (every read decodes whatever arrived since the previous one, so a read right after a
            // World.Until poll would be measuring nothing at all).
            await World.Ticks(60);
            Stopwatch clock = Stopwatch.StartNew();
            int seen = player.Client.Highlights(OverlayServer.HighlightSlot).Count;
            double highlightMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            int packets = player.Client.Packets<OverlayPacket>("caminus").Count;
            Console.WriteLine($"[Caminus] client observations: Highlights({seen} cubes) {highlightMs:0.00} ms, " +
                              $"Packets<OverlayPacket>({packets}) {clock.Elapsed.TotalMilliseconds:0.00} ms, " +
                              $"{expected.Count} puffs per packet, {player.Client.ChatLines().Count} chat lines");

            // Off: the mod clears the slot and sends an empty text, and the empty highlight packet is
            // exactly how a slot is cleared client side.
            await player.Say("/caminus overlay");
            Assert.Contains(player.Client.ChatLines(), line => line.Contains("Thermal overlay off."));
            await World.Until(() => player.Client.Highlights(OverlayServer.HighlightSlot).Count == 0, 300);
            OverlayPacket last = player.Client.Packets<OverlayPacket>("caminus")[^1];
            Assert.Equal("", last.Text);
            Assert.Empty(last.Faces); // nothing left for the client to draw either

            player.Client.Clear();
            Assert.Empty(player.Client.Highlights(OverlayServer.HighlightSlot));
            Assert.Empty(player.Client.Packets<OverlayPacket>("caminus"));
            Assert.Empty(player.Client.Particles());
        }
        finally { await Leave(player); }
    }

    /// <summary>
    /// The whole palette on one room, read off the cubes the player actually received. A half-buried
    /// cellar is where every colour shows up at once: the floor and the bottom course of walls are
    /// ground faces, the two courses above the worldgen surface are outside walls, and one of them is
    /// knocked out for an opening. The hole has to be in the TOP course: everything below the surface
    /// has natural terrain behind it, so a hole down there is still a wall, only a wall made of soil.
    /// </summary>
    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task Cellar_overlay_paints_ground_brown_and_openings_cyan()
    {
        (ITestPlayer player, BlockPos inside) = await Cellar("CaminusCellarOv", 1540);
        try
        {
            // The middle block of the north wall, top interior course. What replaces it is real air
            // still under the roof, so it joins the room, and the block beyond it sees the sky.
            World.SetBlock(Air, inside.Offset(0, 1, -2));
            BlockPos firepit = inside.Offset(0, -1, 0);
            LightFirepit(firepit);
            await World.Until(() => Firepit(firepit)?.IsBurning == true);
            await WaitForRoomReport(inside, r => r.Contains("Openings: 1 faces") && r.Contains("Sources: 4000 W"));

            // Warm enough that every face loses heat, ground included: the sign of the flow is what
            // picks between the two colours of each pair, and a lukewarm cellar would flicker.
            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 540f);
            await World.Ticks(300);
            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 0f);

            player.Client.Clear();
            await player.Say("/caminus overlay");
            await World.Until(() => player.Client.Highlights(OverlayServer.HighlightSlot).Count > 0, 300);

            RoomThermalSystem thermal = World.Api.ModLoader.GetModSystem<RoomThermalSystem>();
            Assert.True(thermal.TryGetFaceFlows(inside, out RoomFlows? flows), "the cellar stopped being tracked");
            Assert.DoesNotContain(flows!.Faces, f => f.Watts <= 0);

            Dictionary<BlockPos, Rgba> cubes = player.Client.Highlights(OverlayServer.HighlightSlot)
                .ToDictionary(c => c.Pos, c => c.Rgba);

            // The nine floor blocks, two metres under the surface: brown, and the brown of a face
            // losing heat rather than the darker one of a face taking it back.
            List<FaceFlow> floor = [.. flows.Faces.Where(f => f.Face.Ground && f.Face.Facing == BlockFacing.DOWN)];
            Assert.Equal(9, floor.Count);
            foreach (FaceFlow f in floor)
            {
                Rgba c = cubes[f.Face.Pos];
                Assert.Equal(OverlayServer.GroundLoss, (c.R, c.G, c.B));
            }

            // The hole: an opening is priced as one whatever the ground around it is doing.
            FaceFlow opening = Assert.Single(flows.Faces, f => f.Face.Opening);
            Rgba cyan = cubes[opening.Face.Pos];
            Assert.Equal(OverlayServer.OpeningLoss, (cyan.R, cyan.G, cyan.B));

            // An outside wall, above the surface: red or blue, and which one has to agree with the
            // sign the model reports for that same face.
            FaceFlow wall = flows.Faces.First(f => !f.Face.Ground && !f.Face.Opening);
            Rgba hot = cubes[wall.Face.Pos];
            Assert.Equal(wall.Watts >= 0 ? OverlayServer.WallLoss : OverlayServer.WallGain, (hot.R, hot.G, hot.B));

            // And the puffs the packet asks the client to draw: watts and speed say the same thing
            // about which way the heat goes, face by face, as the model does.
            List<ParticleFace> puffs = player.Client.Packets<OverlayPacket>("caminus")[^1].Faces;
            Assert.Equal(OverlayServer.ParticleFaces(flows).Count, puffs.Count);
            foreach (ParticleFace p in puffs)
            {
                FaceFlow flow = flows.Faces.Single(f => f.Face.Pos.X == p.X && f.Face.Pos.Y == p.Y
                                                        && f.Face.Pos.Z == p.Z && f.Face.Facing.Index == p.Facing);
                Assert.Equal(flow.Watts >= 0, p.Watts >= 0);
                Assert.Equal(p.Watts >= 0, p.Speed >= 0); // losing blows outward, gaining blows back in
            }
            Assert.Contains(puffs, p => p.Watts > 0 && p.Speed > 0);
        }
        finally { await Leave(player); }
    }

    /// <summary>
    /// Two players in one heated room. The overlay is per player from end to end: the cubes go out
    /// with <c>HighlightBlocks(player, ...)</c>, the text and the puffs ride a packet addressed to
    /// that player, and nothing at all is spawned into the shared world any more, so the one who
    /// never asked for the overlay sees an empty world and an empty channel.
    /// </summary>
    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task Second_player_sees_its_own_overlay_only()
    {
        (ITestPlayer first, BlockPos inside) = await RoomAndPlayer("CaminusPairOne", 1620, Stone);
        ITestPlayer second = await World.JoinPlayer("CaminusPairTwo");
        try
        {
            // The two players read the room one course apart: the third HUD line prints the air at
            // the eyes, and the firepit puts 1.6 K/m between them. The first is put on the floor
            // rather than left to fall there, the second gets a block to stand on.
            await first.TeleportTo(inside.Offset(-1, -1, -1));
            World.SetBlock(Stone, inside.Offset(1, -1, 1));
            await second.TeleportTo(inside.Offset(1, 0, 1));

            BlockPos firepit = inside.Offset(0, -1, 0);
            LightFirepit(firepit);
            await World.Until(() => Firepit(firepit)?.IsBurning == true);
            await WaitForRoomReport(inside, r => r.Contains("Stratification: 1.6 K/m"));
            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 540f);
            await World.Ticks(300);
            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 0f);

            first.Client.Clear();
            second.Client.Clear();
            await first.Say("/caminus overlay");
            Assert.Contains(first.Client.ChatLines(), l => l.Contains("Thermal overlay on."));
            await World.Until(() => first.Client.Highlights(OverlayServer.HighlightSlot).Count > 0, 300);

            Assert.NotEmpty(first.Client.Highlights(OverlayServer.HighlightSlot));
            Assert.NotEmpty(first.Client.Packets<OverlayPacket>("caminus"));
            // The other player asked for nothing and receives nothing: no cubes, no packet, and no
            // particle either, on either connection, since the puffs are spawned client side now.
            Assert.Empty(second.Client.Highlights(OverlayServer.HighlightSlot));
            Assert.Empty(second.Client.Packets<OverlayPacket>("caminus"));
            Assert.Empty(first.Client.Particles());
            Assert.Empty(second.Client.Particles());

            second.Client.Clear();
            await second.Say("/caminus overlay");
            Assert.Contains(second.Client.ChatLines(), l => l.Contains("Thermal overlay on."));
            await World.Until(() => second.Client.Highlights(OverlayServer.HighlightSlot).Count > 0, 300);
            Assert.NotEmpty(second.Client.Highlights(OverlayServer.HighlightSlot));

            // Both now get a packet of their own, built at their own eye height: one course apart in
            // a room the fire keeps warmer at the top.
            Assert.Equal(RoomThermalSystem.EyeBlockPos(first.Entity).Y + 1,
                         RoomThermalSystem.EyeBlockPos(second.Entity).Y);
            string firstText = first.Client.Packets<OverlayPacket>("caminus")[^1].Text;
            string secondText = second.Client.Packets<OverlayPacket>("caminus")[^1].Text;
            Assert.StartsWith("Room ", firstText);
            Assert.StartsWith("Room ", secondText);
            Assert.True(Read(EyesTemp(), secondText) > Read(EyesTemp(), firstText) + 1.0,
                $"the two players read the same air\nfirst:\n{firstText}\nsecond:\n{secondText}");
        }
        finally
        {
            await Leave(first);
            await Leave(second);
        }
    }

    /// <summary>
    /// The commands as a player runs them: said on the chat channel, answered in that player's own
    /// chat lines. Atlas's own <c>ExecuteCommand</c> runs as the console, a caller with no entity and
    /// no position, so this is the only place the player path is exercised end to end.
    /// </summary>
    [AtlasScenario(TimeoutMs = 240_000)]
    public async Task Commands_answer_in_the_players_chat()
    {
        (ITestPlayer player, BlockPos inside) = await RoomAndPlayer("CaminusChat", 1700, Stone);
        try
        {
            await WaitForRoomReport(inside);

            player.Client.Clear();
            await player.Say("/caminus version");
            Assert.Contains(player.Client.ChatLines(), l => l.Contains("Caminus 0.1.0"));

            player.Client.Clear();
            await player.Say("/caminus temp");
            Assert.Contains(player.Client.ChatLines(), l => l.Contains("Room:"));

            // Back down on the ground under the box, where the command has to say there is no room
            // rather than answer with the last one it knows.
            await player.TeleportTo(World.Spawn.Offset(1700, 0, 40));
            await World.Ticks(90);
            player.Client.Clear();
            await player.Say("/caminus temp");
            Assert.Contains(player.Client.ChatLines(), l => l.Contains("No enclosed room here."));

            player.Client.Clear();
            await player.Say("/caminus overlay");
            Assert.Contains(player.Client.ChatLines(), l => l.Contains("Thermal overlay on."));

            player.Client.Clear();
            await player.Say("/caminus overlay");
            Assert.Contains(player.Client.ChatLines(), l => l.Contains("Thermal overlay off."));
        }
        finally { await Leave(player); }
    }
}
