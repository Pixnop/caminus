using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Xunit;
using Xunit.Sdk;

namespace Caminus.Scenarios;

/// <summary>
/// Batch 2: the mod on a REAL world. Every other scenario class runs on Atlas's default superflat
/// world, where there is one climate map, no relief, no forest and no geology; this class boots the
/// game's own "standard" terrain under the "surviveandbuild" play style (the pair the game itself
/// uses for a normal survival world: <c>VSCreativeMod</c> declares
/// <c>creativebuilding → superflat</c>, <c>VSSurvivalMod</c> declares
/// <c>surviveandbuild → standard</c> with the realistic climate, so latitudes, seasons, forests,
/// hills and geologic activity all exist here). The seed is fixed, so the terrain is the same on
/// every run; the exact spots the survey below picks can still shift a little, because worldgen
/// finishes a column in several passes, hence assertions on contrast rather than on coordinates.
///
/// <para>Worldgen is the slow part: the class loads a 9×9 chunk grid around spawn once, keeps it
/// loaded, and every scenario picks its site out of that grid. Hence the generous timeouts.</para>
/// </summary>
[AtlasWorld(WorldType = "standard", PlayStyle = "surviveandbuild", Seed = 1234)]
public partial class BiomeScenarios : AtlasScenarioBase
{
    /// <summary>Chunk columns loaded around spawn, per axis. 9 × 32 = 288 blocks of surveyed ground.</summary>
    private const int SurveyChunks = 9;

    /// <summary>Step of the climate grid, in blocks. The forest and climate maps carry one value per
    /// 32 blocks (<c>TerraGenConfig.forestMapScale</c>), lerped in between: 16 oversamples slightly.</summary>
    private const int ClimateStep = 16;

    /// <summary>Fluid layer, where water and lava live. <c>GetBlock(pos)</c> only reads the solid one.</summary>
    private const int FluidLayer = 2;

    private const string Stone = "game:cobblestone-granite";
    private const string Air = "game:air";

    /// <summary>3×3×3 interior, like the boxes in <see cref="ThermalScenarios"/>.</summary>
    private const int Inner = 3;

    [GeneratedRegex(@"Room: (-?\d+(?:\.\d+)?) °C")]
    private static partial Regex RoomTemp();

    [GeneratedRegex(@"outside (-?\d+(?:\.\d+)?) °C")]
    private static partial Regex OutsideTemp();

    [GeneratedRegex(@"forest shelter (\d+(?:\.\d+)?) %")]
    private static partial Regex ForestShelter();

    [GeneratedRegex(@"forest shade (\d+(?:\.\d+)?),")]
    private static partial Regex ForestShade();

    [GeneratedRegex(@"Sun: \d+(?:\.\d+)? \(forest shade \d+(?:\.\d+)?, \+(-?\d+) W\)")]
    private static partial Regex SunWatts();

    [GeneratedRegex(@"Ground walls:\n(?:  \w+: (\d+) faces)")]
    private static partial Regex GroundFaces();

    [GeneratedRegex(@"\(geology (\d+(?:\.\d+)?), ")]
    private static partial Regex Geology();

    /// <summary>One surveyed spot: what worldgen decided there, and where the ground is.</summary>
    private sealed record Spot(BlockPos Pos, double Forest, double Geologic);

    /// <summary>The survey is the expensive part and the world is shared by the whole class: run it once.</summary>
    private static List<Spot>? surveyed;

    /// <summary>Block bounds of the pinned chunk grid (Y unused): everything is built inside it.</summary>
    private static Cuboidi bounds = new();

    // --- Seasons -------------------------------------------------------------------------------

    /// <summary>
    /// The same cabin in the coldest and the warmest month. <c>SetSeasonOverride</c> pins the term
    /// <c>ModTemperature.updateTemperature</c> adds to the worldgen temperature: the override is
    /// mapped through <c>Smootherstep(|CyclicValueDistance(0.5, s×12, 12)| / 6)</c>, which is ≈0 at
    /// s = 0 (the whole |latitude| × 65 K swing subtracted: deep winter) and ≈1 at s = 0.5 (the
    /// swing added back: high summer). It reaches <c>GetClimateAt(NowValues)</c>, the call Caminus
    /// reads every tick, because the hook skips only <c>WorldGenValues</c>.
    /// </summary>
    [AtlasScenario(TimeoutMs = 900_000)]
    public async Task Seasons_change_the_room_temperature()
    {
        List<Spot> spots = await Survey();
        ITestPlayer player = await Scout("CaminusSeason");
        try
        {
            BlockPos inside = await Cabin(player, Open(spots));
            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 540f); // ×10 game time

            (double winterOut, double winterRoom) = await Season(inside, 0f);
            (double summerOut, double summerRoom) = await Season(inside, 0.5f);
            Console.WriteLine($"[Caminus] winter outside {winterOut:0.0} °C, room {winterRoom:0.0} °C; " +
                              $"summer outside {summerOut:0.0} °C, room {summerRoom:0.0} °C");

            Assert.True(summerOut > winterOut + 5,
                $"the season override moved the outside air by {summerOut - winterOut:0.0} K only " +
                $"(winter {winterOut:0.0} °C, summer {summerOut:0.0} °C): too close to the equator to measure?");
            // The cabin's firepit puts the same constant offset on both, so the room has to follow.
            Assert.True(summerRoom > winterRoom + 3,
                $"the room did not follow: winter {winterRoom:0.0} °C, summer {summerRoom:0.0} °C");
            Assert.True(winterRoom > winterOut, $"an unlit cabin? winter room {winterRoom:0.0} °C, outside {winterOut:0.0} °C");
        }
        finally
        {
            World.Api.World.Calendar.SetSeasonOverride(null);
            await Leave(player);
        }
    }

    /// <summary>Pins the season, lets the room settle, and reads the outside and room temperatures.</summary>
    private async Task<(double Outside, double Room)> Season(BlockPos inside, float season)
    {
        World.Api.World.Calendar.SetSeasonOverride(season);
        // The stone cabin's time constant is about 1000 game seconds and ×10 buys 300 of them per
        // real second: 600 ticks (≈20 s) is roughly six of them.
        await World.Ticks(600);
        string report = await WaitForRoomReport(inside);
        return (Read(OutsideTemp(), report), Read(RoomTemp(), report));
    }

    // --- Forest --------------------------------------------------------------------------------

    /// <summary>
    /// Forest density is a worldgen map, fixed for good: the same cabin under a canopy and in the
    /// open must read a bigger wind shelter (<c>forestShelter</c> × density) and a bigger sun shade
    /// (<c>forestShade</c> × density). Only a standard world has the map at all, which is why this
    /// could never be checked on the superflat one.
    /// </summary>
    [AtlasScenario(TimeoutMs = 900_000)]
    public async Task Forest_shelters_and_shades()
    {
        List<Spot> spots = await Survey();
        Spot dense = spots.MaxBy(s => s.Forest)!, sparse = spots.MinBy(s => s.Forest)!;
        Console.WriteLine($"[Caminus] forest density: densest {dense.Forest:0.000} at {dense.Pos}, " +
                          $"sparsest {sparse.Forest:0.000} at {sparse.Pos}");
        Assert.True(dense.Forest - sparse.Forest > 0.05,
            $"this seed has no forest contrast within {SurveyChunks * 16} blocks of spawn: " +
            $"{sparse.Forest:0.000} to {dense.Forest:0.000}");

        // Two players, one per cabin: each cabin needs a player standing in it once for the mod to
        // find the room, and they are too far apart for one player to be in both.
        ITestPlayer inWood = await Scout("CaminusForest");
        ITestPlayer inOpen = await Scout("CaminusClearing");
        try
        {
            BlockPos wooded = await Cabin(inWood, dense.Pos);
            BlockPos clearing = await Cabin(inOpen, sparse.Pos);
            string woodedReport = await WaitForRoomReport(wooded, r => r.Contains("Wind:"));
            string clearingReport = await WaitForRoomReport(clearing, r => r.Contains("Wind:"));

            double woodedShelter = Read(ForestShelter(), woodedReport), clearingShelter = Read(ForestShelter(), clearingReport);
            double woodedShade = Read(ForestShade(), woodedReport), clearingShade = Read(ForestShade(), clearingReport);
            Console.WriteLine($"[Caminus] wooded: shelter {woodedShelter:0.#} %, shade {woodedShade:0.00}; " +
                              $"clearing: shelter {clearingShelter:0.#} %, shade {clearingShade:0.00}");

            Assert.True(woodedShelter > clearingShelter,
                $"shelter {woodedShelter:0.#} % under the canopy vs {clearingShelter:0.#} % in the open\n" +
                $"wooded:\n{woodedReport}\nclearing:\n{clearingReport}");
            Assert.True(woodedShade > clearingShade,
                $"shade {woodedShade:0.00} under the canopy vs {clearingShade:0.00} in the open\n" +
                $"wooded:\n{woodedReport}\nclearing:\n{clearingReport}");
        }
        finally
        {
            await Leave(inWood);
            await Leave(inOpen);
        }
    }

    // --- Relief --------------------------------------------------------------------------------

    /// <summary>
    /// A cabin cut into a slope, floor at the downhill level: the uphill wall then sits below the
    /// worldgen terrain height, which is exactly what <c>AddWall</c> calls a buried face. The
    /// superflat world has no relief, so the ground node could only ever be exercised there by
    /// digging a cellar by hand.
    /// </summary>
    [AtlasScenario(TimeoutMs = 900_000)]
    public async Task Hillside_cabin_has_ground_faces()
    {
        await Survey();
        (BlockPos low, int drop) = FindSlope();
        Console.WriteLine($"[Caminus] slope at {low}: {drop} blocks over the cabin's 4 m footprint");
        Assert.True(drop >= 3, $"no 3-block slope over 4 m anywhere in the surveyed grid (best: {drop})");

        ITestPlayer player = await Scout("CaminusSlope");
        try
        {
            BlockPos inside = await Cabin(player, low);
            string report = (await WaitForRoomReport(inside, r => r.Contains("Ground walls:"))).ReplaceLineEndings("\n");
            double faces = Read(GroundFaces(), report);
            Console.WriteLine($"[Caminus] hillside cabin: {faces:0} buried faces");

            Assert.True(faces > 0, $"no buried face on a {drop}-block slope\n{report}");
            Assert.Contains("Ground node:", report);
            Assert.Contains("Outside walls:", report); // the downhill side is still in the open air
        }
        finally { await Leave(player); }
    }

    /// <summary>
    /// First spot in the surveyed grid whose terrain climbs at least 3 blocks over the cabin's own
    /// 4 m footprint, on solid ground at both ends. Returns the downhill corner and the climb.
    /// </summary>
    private (BlockPos Low, int Drop) FindSlope()
    {
        IBlockAccessor acc = World.Api.World.BlockAccessor;
        (BlockPos Low, int Drop) best = (World.Spawn, 0);
        for (int x = bounds.MinX; x <= bounds.MaxX - 4; x += 2)
            for (int z = bounds.MinZ; z <= bounds.MaxZ - 4; z += 2)
            {
                var low = new BlockPos(x, 0, z, World.Spawn.dimension);
                if (!IsLand(acc, low) || !IsLand(acc, low.Offset(4, 0, 0))) continue;
                int drop = acc.GetTerrainMapheightAt(low.Offset(4, 0, 0)) - acc.GetTerrainMapheightAt(low);
                if (drop > best.Drop) best = (low, drop);
                if (best.Drop >= 3) return best;
            }
        return best;
    }

    // --- Sun -----------------------------------------------------------------------------------

    /// <summary>
    /// Sol-air gain on a real sky: watts at noon, nothing at midnight. Same check as
    /// <c>ThermalScenarios.Sun_warms_the_roof_by_day</c>, but here the sun's height comes from the
    /// world's own latitude and axial tilt rather than a flat creative sky.
    /// </summary>
    [AtlasScenario(TimeoutMs = 900_000)]
    public async Task Day_and_night_move_the_sun_line()
    {
        List<Spot> spots = await Survey();
        ITestPlayer player = await Scout("CaminusDaylight");
        try
        {
            BlockPos inside = await Cabin(player, Open(spots));
            await WaitForRoomReport(inside);
            // Back to real time: the polling below must not drag the clock across sunrise.
            World.Api.World.Calendar.SetTimeSpeedModifier("caminus-test", 0f);

            string noon = await ReportAtHour(inside, 12, r => SunOf(r) > 0);
            string night = await ReportAtHour(inside, 0, r => SunOf(r) == 0);
            Console.WriteLine($"[Caminus] sun at noon: {SunOf(noon):0} W, at midnight: {SunOf(night):0} W");

            Assert.True(SunOf(noon) > 0, $"no sun at noon\n{noon}");
            Assert.Equal(0, SunOf(night));
            Assert.Contains("Sun: 0.0 (", night);
        }
        finally { await Leave(player); }
    }

    // --- Geology -------------------------------------------------------------------------------

    /// <summary>
    /// Geologic activity warms the ground node by <c>geothermalKPerActivity</c> per unit, and the
    /// report says so on the ground line. The map is sparse (the standard play style leaves
    /// <c>geologicActivity</c> at 0.05, i.e. most of the map reads zero), so this scenario passes
    /// with a log line when the surveyed grid holds nothing hot: the formula itself is covered by
    /// <c>Caminus.Core.Tests.ExposureTests</c>, what is checked here is the plumbing when it exists.
    /// </summary>
    [AtlasScenario(TimeoutMs = 900_000)]
    public async Task Geology_if_found()
    {
        List<Spot> spots = await Survey();
        Spot hottest = spots.MaxBy(s => s.Geologic)!;
        Console.WriteLine($"[Caminus] highest geologic activity in the grid: {hottest.Geologic:0.000} at {hottest.Pos}");
        if (hottest.Geologic <= 0.1)
        {
            Console.WriteLine("[Caminus] no geologic activity near spawn: nothing to assert, scenario skipped on purpose");
            return;
        }

        ITestPlayer player = await Scout("CaminusGeology");
        try
        {
            // Buried, so the room has ground faces at all: without one there is no ground line to read.
            BlockPos inside = await BuriedRoom(player, hottest.Pos);
            string report = (await WaitForRoomReport(inside, r => r.Contains("Ground node:"))).ReplaceLineEndings("\n");
            Console.WriteLine($"[Caminus] geologic room report:\n{report}");

            Assert.Contains("(geology ", report);
            Assert.True(Read(Geology(), report) > 0.1, $"the room read a colder rock than the survey did\n{report}");
        }
        finally { await Leave(player); }
    }

    // --- Performance ---------------------------------------------------------------------------

    /// <summary>
    /// Fifty rooms simulated at once, which is what a large base or a small server looks like. The
    /// rooms are plain 3×3×3 boxes floating over the surveyed grid, each with a chest inside: a
    /// container asking for its perish rate is what makes the mod discover a room nobody ever
    /// entered, and the flood fill budget (<c>maxScansPerTick</c>, 2) means that takes a couple of
    /// dozen mod ticks. Then the mod's own tick is timed for 30 of its beats.
    /// </summary>
    [AtlasScenario(TimeoutMs = 900_000)]
    public async Task Performance_fifty_rooms_tick_under_budget()
    {
        const int Rooms = 50, Pitch = 8, Columns = 10;
        await Survey();
        RoomThermalSystem thermal = World.Api.ModLoader.GetModSystem<RoomThermalSystem>();

        // Well clear of the highest ground and of the cabins' chimneys, which reach 8 blocks up.
        IBlockAccessor acc = World.Api.World.BlockAccessor;
        int top = World.Spawn.Y;
        for (int x = bounds.MinX; x <= bounds.MaxX; x += 16)
            for (int z = bounds.MinZ; z <= bounds.MaxZ; z += 16)
                top = Math.Max(top, acc.GetTerrainMapheightAt(new BlockPos(x, 0, z, World.Spawn.dimension)));

        List<BlockPos> chests = [], centers = [];
        for (int i = 0; i < Rooms; i++)
        {
            var min = new BlockPos(bounds.MinX + 8 + i % Columns * Pitch, top + 12,
                                   bounds.MinZ + 8 + i / Columns * Pitch, World.Spawn.dimension);
            Build(min, Stone);
            World.SetBlock("game:chest-east", min);
            chests.Add(min);
            centers.Add(min.Offset(1, 1, 1));
        }

        int tracked = 0;
        for (int round = 0; round < 80 && tracked < Rooms; round++)
        {
            foreach (BlockPos chest in chests) thermal.TryGetPerishRate(chest, out _);
            await World.Ticks(35); // one mod tick, which resets the flood fill budget
            tracked = centers.Count(p => thermal.TryGetLocalTemperature(p, out _));
        }
        Assert.Equal(Rooms, tracked);

        var samples = new List<double>();
        for (int i = 0; i < 30; i++)
        {
            await World.Ticks(35);
            samples.Add(thermal.LastTickMilliseconds);
        }
        double average = samples.Average();
        Console.WriteLine($"[Caminus] {thermal.TrackedRooms} rooms tracked ({tracked} of them built here), " +
                          $"mod tick {average:0.00} ms average over {samples.Count} ticks, worst {samples.Max():0.00} ms, " +
                          $"on {RuntimeInformation.OSDescription} / {Environment.ProcessorCount} logical cores / " +
                          $"{RuntimeInformation.ProcessArchitecture}");
        Assert.True(average < 5.0, $"the mod tick averages {average:0.00} ms with {thermal.TrackedRooms} rooms, " +
                                   $"budget is 5 ms (samples: {string.Join(", ", samples.Select(s => s.ToString("0.00", CultureInfo.InvariantCulture)))})");
    }

    // --- Survey --------------------------------------------------------------------------------

    /// <summary>
    /// Generates and pins a <see cref="SurveyChunks"/>² chunk grid around spawn, then reads the
    /// worldgen climate every <see cref="ClimateStep"/> blocks over it. Pinned rather than walked by
    /// a player: <c>LoadChunkColumn(keepLoaded: true)</c> generates exactly the columns wanted, where
    /// walking a player in loads its whole view distance around every step, and pinned columns also
    /// keep the rooms of the earlier scenarios alive for the performance one.
    /// </summary>
    private async Task<List<Spot>> Survey()
    {
        if (surveyed != null) return surveyed;
        int size = GlobalConstants.ChunkSize;
        IWorldManagerAPI wm = World.Api.WorldManager;
        int cx = World.Spawn.X / size - SurveyChunks / 2, cz = World.Spawn.Z / size - SurveyChunks / 2;
        for (int i = 0; i < SurveyChunks; i++)
            for (int j = 0; j < SurveyChunks; j++)
                wm.LoadChunkColumn(cx + i, cz + j, keepLoaded: true);

        IBlockAccessor acc = World.Api.World.BlockAccessor;
        var clock = Stopwatch.StartNew();
        // Both maps: the map chunk carries the terrain heights and shows up early, the block chunk is
        // what SetBlock and PlaceSchematic need (writing into a column still in the generation queue
        // does nothing at all, silently).
        await World.Until(() => Enumerable.Range(0, SurveyChunks).All(i =>
            Enumerable.Range(0, SurveyChunks).All(j =>
                wm.GetMapChunk(cx + i, cz + j) != null
                && acc.GetChunkAtBlockPos(new BlockPos((cx + i) * size, World.Spawn.Y, (cz + j) * size,
                    World.Spawn.dimension)) != null)), 18_000);
        // A column answers as loaded before its late worldgen passes (water, vegetation) have run,
        // and IsLand reads the fluid layer: let the generation queue drain before sampling, or which
        // spots count as land drifts from run to run.
        await World.Ticks(120);
        clock.Stop();

        // The grid's own block bounds, with a margin for the cabin's 5×5 footprint. Not spawn ± half:
        // spawn sits anywhere inside its chunk, so a window centred on it would run off the pinned
        // columns on one side and every write there would be silently dropped.
        bounds = new Cuboidi(cx * size + 8, 0, cz * size + 8,
                             (cx + SurveyChunks) * size - 9, 0, (cz + SurveyChunks) * size - 9);
        var spots = new List<Spot>();
        for (int x = bounds.MinX; x <= bounds.MaxX; x += ClimateStep)
            for (int z = bounds.MinZ; z <= bounds.MaxZ; z += ClimateStep)
            {
                var p = new BlockPos(x, 0, z, World.Spawn.dimension);
                if (!IsLand(acc, p)) continue;
                // WorldGenValues is the only mode carrying ForestDensity and GeologicActivity, and it
                // is the one the mod itself samples: both are fixed at world generation.
                ClimateCondition? c = acc.GetClimateAt(p, EnumGetClimateMode.WorldGenValues);
                if (c != null) spots.Add(new Spot(p, c.ForestDensity, c.GeologicActivity));
            }

        Console.WriteLine($"[Caminus] surveyed {SurveyChunks * SurveyChunks} chunk columns in " +
                          $"{clock.Elapsed.TotalSeconds:0.0} s, {spots.Count} land spots, " +
                          $"forest {spots.Min(s => s.Forest):0.000}..{spots.Max(s => s.Forest):0.000}, " +
                          $"geology {spots.Min(s => s.Geologic):0.000}..{spots.Max(s => s.Geologic):0.000}");
        Assert.NotEmpty(spots);
        surveyed = spots;
        Console.WriteLine($"[Caminus] steepest 4 m slope found: {FindSlope().Drop} blocks");
        return spots;
    }

    /// <summary>Dry land: the terrain top block carries no water or lava just above it.</summary>
    private static bool IsLand(IBlockAccessor acc, BlockPos pos)
    {
        int y = acc.GetTerrainMapheightAt(pos);
        if (y <= 0) return false;
        var top = new BlockPos(pos.X, y + 1, pos.Z, pos.dimension);
        return acc.GetBlock(top, FluidLayer).Id == 0;
    }

    /// <summary>The least wooded spot of the survey: an open sky for the sun, no canopy for the wind.</summary>
    private static BlockPos Open(List<Spot> spots) => spots.MinBy(s => s.Forest)!.Pos;

    // --- Building ------------------------------------------------------------------------------

    /// <summary>
    /// Stamps <c>templates/cabin-stone.json</c> with its floor on the terrain, relights its firepit,
    /// and puts the player inside so the mod finds the room. <see cref="EnumReplaceMode.ReplaceAll"/>
    /// rather than the schematic's own mode: on real terrain the house has to carve its own hole,
    /// and the mode stored in the file skips the schematic's air blocks.
    /// </summary>
    private async Task<BlockPos> Cabin(ITestPlayer player, BlockPos site)
    {
        IBlockAccessor acc = World.Api.World.BlockAccessor;
        var origin = new BlockPos(site.X, acc.GetTerrainMapheightAt(site), site.Z, site.dimension);
        Assert.True(World.PlaceSchematic(Template("cabin-stone"), origin, EnumReplaceMode.ReplaceAll) > 0,
            $"nothing placed at {origin}");
        LightFirepit(origin.Offset(2, 1, 2));
        BlockPos inside = origin.Offset(2, 2, 2);
        await player.TeleportTo(inside);
        return inside;
    }

    /// <summary>
    /// A 3×3×3 stone box sunk into the ground, roof flush with the surface: every wall but the roof
    /// reads as buried, which is what gives the report its ground line.
    /// </summary>
    private async Task<BlockPos> BuriedRoom(ITestPlayer player, BlockPos site)
    {
        IBlockAccessor acc = World.Api.World.BlockAccessor;
        var min = new BlockPos(site.X, Math.Max(1, acc.GetTerrainMapheightAt(site) - Inner - 1), site.Z, site.dimension);
        Build(min, Stone);
        BlockPos inside = min.Offset(1, 1, 1);
        await player.TeleportTo(inside);
        return inside;
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

    /// <summary>Lit firepit that never goes out: IsBurning only looks at fuelBurnTime.</summary>
    private void LightFirepit(BlockPos pos)
    {
        World.SetBlock("game:firepit-lit", pos);
        BlockEntityFirepit fire = World.BlockEntityAt<BlockEntityFirepit>(pos)
                                  ?? throw new XunitException($"no BlockEntityFirepit at {pos}");
        fire.maxFuelBurnTime = fire.fuelBurnTime = 1_000_000f;
    }

    /// <summary>
    /// The schematics live in the repository, not in the test output: walk up from the test
    /// assembly until the <c>templates</c> folder shows up. The assembly's own path, not
    /// <c>AppContext.BaseDirectory</c>: Atlas repoints that one at the game installation before boot
    /// (the engine derives its asset paths from it), so it does not lead back to the repository.
    /// </summary>
    private static string Template(string name)
    {
        string start = Path.GetDirectoryName(typeof(BiomeScenarios).Assembly.Location)!;
        DirectoryInfo? dir = new(start);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "templates"))) dir = dir.Parent;
        Assert.True(dir != null, $"no templates/ folder above {start}");
        return Path.Combine(dir!.FullName, "templates", name);
    }

    // --- Players and reports -------------------------------------------------------------------

    /// <summary>
    /// A player in creative: this world runs the survival play style, and a survival test player
    /// left standing in a cabin for a few game days starves, freezes or drowns while we measure.
    /// </summary>
    private async Task<ITestPlayer> Scout(string name)
    {
        ITestPlayer player = await World.JoinPlayer(name);
        CommandResult result = await World.ExecuteCommand($"/gamemode {name} creative");
        Assert.True(result.Ok, result.Message);
        return player;
    }

    /// <summary>Frees the client slot: the embedded server accepts 16 clients and no queue.</summary>
    private async Task Leave(ITestPlayer player)
    {
        ((IServerPlayer)player.Player).Disconnect("scenario over");
        await World.Until(() => !player.IsConnected, 600);
    }

    private async Task<string> ReportAt(BlockPos pos)
    {
        // "=" forces absolute coordinates: from the console, WorldPositionArgParser reads them
        // relative to the middle of the map.
        CommandResult result = await World.ExecuteCommand($"/caminus temp ={pos.X} ={pos.Y} ={pos.Z}");
        Assert.True(result.Ok, result.Message);
        return result.Message;
    }

    /// <summary>Polls the report until the mod's 1 s tick has picked the room up.</summary>
    private async Task<string> WaitForRoomReport(BlockPos pos, System.Func<string, bool>? ready = null)
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

    /// <summary>Winds the calendar forward to the next occurrence of that hour, then polls the report.</summary>
    private async Task<string> ReportAtHour(BlockPos pos, double hour, System.Func<string, bool> ready)
    {
        IGameCalendar cal = World.Api.World.Calendar;
        cal.Add((float)(((hour - cal.HourOfDay) % cal.HoursPerDay + cal.HoursPerDay) % cal.HoursPerDay));
        return await WaitForRoomReport(pos, ready);
    }

    /// <summary>Solar watts of a report, -1 when the room is not reporting yet.</summary>
    private static double SunOf(string report)
    {
        Match m = SunWatts().Match(report);
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
    }

    private static double Read(Regex what, string report)
    {
        Match m = what.Match(report);
        Assert.True(m.Success, $"'{what}' not found in:\n{report}");
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
