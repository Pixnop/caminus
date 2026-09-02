using System.Globalization;
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

    [GeneratedRegex(@"Losses: (-?\d+(?:\.\d+)?) W/K")]
    private static partial Regex Losses();

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
    public async Task Opening_a_wall_adds_losses()
    {
        BlockPos inside = await Room("CaminusOpen", 80, Stone);
        string closed = await WaitForRoomReport(inside);
        Assert.DoesNotContain("Openings", closed);

        // One wall face replaced with a glass pane: Caminus counts it as an opening (30 W/K)
        // instead of a stone wall (3 W/K), i.e. +27 W/K.
        World.SetBlock(Pane, inside.Offset(0, 0, -2));
        string opened = await WaitForRoomReport(inside, r => r.Contains("Openings"));

        Assert.Contains("Openings: 1 faces", opened);
        double gain = Read(Losses(), opened) - Read(Losses(), closed);
        Assert.True(Math.Abs(gain - 27.0) < 0.1, $"conductance +{gain:0.0} W/K instead of +27\nclosed:\n{closed}\nopen:\n{opened}");
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

    /// <summary>
    /// Joins a player, builds a hollow box far from spawn and 30 blocks above ground,
    /// and teleports the player into it (the mod only tracks occupied rooms). Returns the
    /// center interior position.
    /// </summary>
    private async Task<BlockPos> Room(string playerName, int dx, string wall)
    {
        ITestPlayer player = await World.JoinPlayer(playerName);
        BlockPos min = World.Spawn.Offset(dx, 30, 40); // bottom interior corner
        // The server only loads chunks around players: the player is first placed on the ground
        // below the future box. 30 blocks lower, the open-air "room" it occupies cannot
        // contain the box (RoomRegistry's flood fill is bounded to ±14 blocks).
        await player.TeleportTo(World.Spawn.Offset(dx, 0, 40));
        await World.Until(() => World.Api.World.BlockAccessor.GetChunkAtBlockPos(min) != null, 1200);

        for (int x = -1; x <= Inner; x++)
            for (int y = -1; y <= Inner; y++)
                for (int z = -1; z <= Inner; z++)
                {
                    bool shell = x < 0 || y < 0 || z < 0 || x == Inner || y == Inner || z == Inner;
                    World.SetBlock(shell ? wall : Air, min.Offset(x, y, z));
                }

        BlockPos center = min.Offset(1, 1, 1);
        await player.TeleportTo(center);
        return center;
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
