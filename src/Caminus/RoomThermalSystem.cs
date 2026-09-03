using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Caminus.Core;
using HarmonyLib;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Caminus;

/// <summary>assets/caminus/config/thermal.json. SI units.</summary>
public class ThermalConfig
{
    public int TickMs { get; set; } = 1000;
    public double WattsPerHeatStrength { get; set; } = 400;
    /// <summary>Capacity = volume × 1.2 kg/m³ × 1005 J/kg/K × this factor (furniture and interior walls).</summary>
    public double AirCapacityFactor { get; set; } = 5;
    /// <summary>W/K per m² of open face (door, window, hole).</summary>
    public double OpeningConductance { get; set; } = 30;
    /// <summary>W/K per m² of wall face buried below the world-generated surface, whatever the material.</summary>
    public double GroundContactU { get; set; } = 1.0;
    /// <summary>Extra wall conductance per unit of wind blowing straight at the face (0 = wind ignored).</summary>
    public double WindWallFactor { get; set; } = 2.0;
    /// <summary>Same for an opening, which leaks with any wind, not only a head-on one.</summary>
    public double WindOpeningFactor { get; set; } = 4.0;
    /// <summary>Blocks upwind where the incoming air's temperature is sampled, at full wind.</summary>
    public double WindSampleDistance { get; set; } = 64;
    public double StratificationKPerMPerKW { get; set; } = 0.4;
    public double StratificationMaxKPerM { get; set; } = 2.0;
    /// <summary>Ground node warming per unit of <c>ClimateCondition.GeologicActivity</c> (0..1), K.</summary>
    public double GeothermalKPerActivity { get; set; } = 20;
    /// <summary>How many blocks of rock beyond the envelope the heat-source scan reaches into.</summary>
    public int SourceScanMargin { get; set; } = 4;
    /// <summary>Sol-air excess of a face in full sun, straight at the sun, K.</summary>
    public double SolAirMaxK { get; set; } = 12;
    /// <summary>Share of the sun a fully forested position keeps off the walls.</summary>
    public double ForestShade { get; set; } = 0.7;
    /// <summary>Share of the wind a fully forested position keeps off the walls.</summary>
    public double ForestShelter { get; set; } = 0.7;
    /// <summary>Cd of the stack-effect flow, 0..1 (ASHRAE gives 0.6 for a plain opening).</summary>
    public double DischargeCoefficient { get; set; } = 0.6;
    /// <summary>Cracks around one square metre of envelope, m². This is what the draft pulls air through.</summary>
    public double LeakageAreaPerFace { get; set; } = 0.003;
    /// <summary>Share of a smoking source's power that goes straight up the flue instead of heating the room.</summary>
    public double FlueLossFraction { get; set; } = 0.4;
    /// <summary>Haze one smoking source adds per hour, in a room whose air is never renewed.</summary>
    public double SmokePerSourcePerHour { get; set; } = 2.0;
    /// <summary>Whether heavy smoke hurts. Off by default: the server owner decides after playing.</summary>
    public bool SmokeDamage { get; set; }
    public Dictionary<EnumBlockMaterial, double> WallU { get; set; } = [];

    public double UFor(EnumBlockMaterial mat) => WallU.TryGetValue(mat, out double u) ? u : 3.0;
}

/// <summary>One square metre of the room's envelope.</summary>
/// <param name="Pos">The wall block, outside the room.</param>
/// <param name="Facing">From the room toward the wall, so <c>Facing.Normali</c> is the outward normal.</param>
/// <param name="UA">Conductance of the face in calm air, W/K.</param>
/// <param name="Y">Height of the room-side air block, the air actually touching the face: same as
/// <paramref name="Pos"/>.Y except for the floor and ceiling faces.</param>
/// <param name="Sun">How much sky the block just outside the face sees, 0..1, measured once at
/// geometry time and independent of the time of day. 0 on a buried face.</param>
public readonly record struct Face(BlockPos Pos, BlockFacing Facing, EnumBlockMaterial Material, double UA, bool Ground, bool Opening, int Y, double Sun);

/// <summary>A face with what it is doing right now. Positive <paramref name="Watts"/> = heat leaving the room.</summary>
public readonly record struct FaceFlow(Face Face, double Conductance, double Watts);

/// <summary>
/// One chimney stack rising from the room's ceiling. <paramref name="Columns"/> is its cross section
/// in square metres (one block wide = 1 m²) and <paramref name="Height"/> the mean number of chimney
/// blocks of its columns. Blocked means the stack ends under a roof, which draws nothing.
/// </summary>
public readonly record struct Flue(BlockPos Start, int Height, int Columns, int TopY, bool Blocked);

/// <summary>What the room's chimneys are doing right now. Watts is negative: the draft takes heat out.</summary>
public sealed record DraftState(int Height, int Columns, bool Blocked, double Flow, double Watts,
    double InletArea, IReadOnlyList<BlockPos> Blocks);

/// <summary>Everything a client overlay needs to draw one room. GroundTemperature is NaN if the room touches no ground.</summary>
public sealed record RoomFlows(
    double Temperature, double OutsideTemperature, double GroundTemperature, double WindTemperature,
    Vec3d Wind, double Gradient, double YMid, double StratificationWatts, IReadOnlyList<FaceFlow> Faces,
    double SolarWatts, double GeologicActivity, double ForestDensity,
    DraftState? Draft, double Smoke, int SmokeSources);

/// <summary>
/// Thermal simulation of the rooms players are in. Server side only: RoomRegistry
/// is not thread-safe, everything runs on the main thread.
/// Each room owns its own <see cref="ThermalNetwork"/> of at most four nodes, so a tick costs
/// O(1) per room and a base with fifty of them is fifty tiny solves, not one 200x200 matrix.
/// </summary>
public class RoomThermalSystem : ModSystem
{
    private const double AirVolumetricCapacity = 1.2 * 1005; // J/K per m³ of air
    private const string ModDataKey = "caminus:rooms";
    /// <summary>How long a position that is not in a room is trusted not to have become one, in game hours.</summary>
    private const double NoRoomTtlHours = 60.0 / 3600;
    /// <summary>Ticks between two heat-source scans of a room nobody is standing in.</summary>
    private const int UnoccupiedSourceScanTicks = 10;
    /// <summary>Ticks between two doses of smoke damage, at the 1 s tick.</summary>
    private const int SmokeDamageTicks = 10;
    private const float SmokeDamagePerHit = 0.5f;
    /// <summary>Smoke above which the air is called heavy, and hurts when the config lets it.</summary>
    private const double HeavySmoke = 0.4;
    /// <summary>Air changes per hour an envelope leaks on its own, with no draft at all.</summary>
    private const double BaseAirChanges = 0.3;
    /// <summary>Blocks a flue may rise before we stop believing it is one.</summary>
    private const int MaxFlueHeight = 64;

    private sealed class Geometry
    {
        public int Volume;
        /// <summary>Every face of the envelope, flat: walls, openings and buried faces alike.</summary>
        public readonly List<Face> Faces = [];
        public int GroundFaces;
        public int GroundDepthSum;
        /// <summary>Calm-air conductance toward the outside air, W/K.</summary>
        public double Conductance;
        public double GroundConductance;
        /// <summary>
        /// Area the draft can pull air in through, m²: cracks over the whole envelope, plus any real
        /// opening at one square metre a face. An enclosed room has no opening, so today this is the
        /// leakage alone; milestone 6's flood fill is what lets a room keep a hole and stay a room.
        /// </summary>
        public double InletArea;
        /// <summary>Chimney blocks resting on the ceiling: the bottom of each column, before grouping.</summary>
        public readonly List<BlockPos> ChimneyStarts = [];
        /// <summary>One entry per chimney stack on the ceiling, blocked ones included.</summary>
        public readonly List<Flue> Flues = [];
        /// <summary>Every chimney block of every stack, for the overlay.</summary>
        public readonly List<BlockPos> ChimneyBlocks = [];

        /// <summary>Cross section that actually draws, m², i.e. the columns of the unblocked stacks.</summary>
        public int FlueColumns;
        /// <summary>Mean height of the drawing stacks, weighted by their section, m.</summary>
        public double FlueHeight;
        /// <summary>Top of the highest drawing stack.</summary>
        public int FlueTopY;

        /// <summary>Mean burial depth of the ground faces, in metres (1 block = 1 m).</summary>
        public double GroundDepth => GroundFaces == 0 ? 0 : (double)GroundDepthSum / GroundFaces;
    }

    private sealed class RoomEntry
    {
        public Room Room = null!;
        public Geometry Geom = null!;
        public double Temperature;
        public double OutsideTemperature;
        public double GroundTemp;
        /// <summary>Sources inside the room or in the wall touching it, W.</summary>
        public double SourceWatts;
        /// <summary>Sources further out, already attenuated by the rock between them and the room, W.</summary>
        public double NearbyWatts;
        /// <summary>Share of <see cref="SourceWatts"/> that comes from a source making smoke, W.</summary>
        public double SmokeWatts;
        /// <summary>How many blocks in the room are emitting smoke right now.</summary>
        public int SmokeSources;
        /// <summary>Haze in the room, 0..1.</summary>
        public double Smoke;
        /// <summary>Stack-effect volume flow up the flue, m³/s.</summary>
        public double DraftFlow;
        /// <summary>What that flow costs the room, W/K, toward the outside air the inlet pulls in.</summary>
        public double DraftConductance;
        /// <summary>Correction for the draft taking ceiling air rather than mean air, W (negative).</summary>
        public double DraftWatts;
        /// <summary>Wind above the roof, game units. Vanilla only ever fills X (see <see cref="WindAt"/>).</summary>
        public Vec3d Wind = new();
        /// <summary>Temperature of the air the wind brings in, sampled upwind.</summary>
        public double WindTemperature;
        /// <summary>Extra envelope conductance caused by the wind, W/K, toward <see cref="WindTemperature"/>.</summary>
        public double WindConductance;
        public double Gradient;
        /// <summary>Power the vertical gradient costs the room, W (negative: the ceiling loses more than the floor gains).</summary>
        public double StratificationWatts;
        /// <summary>Power the sun-warmed envelope pushes into the room, W.</summary>
        public double SolarWatts;
        /// <summary>Height of the sun above the horizon, 0 at night, 1 at the zenith.</summary>
        public double Daylight;
        /// <summary>What each facing catches of the sun right now, indexed by <c>BlockFacing.Index</c>.</summary>
        public readonly double[] SunFactor = new double[6];
        /// <summary>What the forest lets through: 1 in the open, <c>1 − forestShade</c> under a full canopy.</summary>
        public double Shade = 1;
        /// <summary>Same for the wind, with <c>forestShelter</c>.</summary>
        public double Shelter = 1;
        /// <summary>Geologic activity and forest density here, both 0..1, fixed at worldgen and sampled once.</summary>
        public (double Geologic, double Forest)? Land;
        public bool HasPlayer;
        public long Ticks;
        /// <summary>Game hours at the last simulated step: the base for offline relaxation.</summary>
        public double TotalHours;
        public int Dimension;
        /// <summary>Surface climate at this room, sampled once (annual mean, half-swing, coldest day).</summary>
        public (double Mean, double Amplitude, double ColdestDay)? Climate;
        /// <summary>This room alone: room node, outside, wind, and the ground when it touches any.</summary>
        public ThermalNetwork Net = null!;
        public int Node = -1;
        public int OutsideNode = -1;
        public int Edge = -1;
        public int WindNode = -1;
        public int WindEdge = -1;
        public int DraftEdge = -1;
        public int GroundNode = -1;
        public int GroundEdge = -1;
    }

    /// <summary>What survives an unload: enough to relax the room forward when it comes back.</summary>
    private sealed record Saved(double Temperature, double TotalHours);

    private ICoreServerAPI sapi = null!;
    private RoomRegistry rooms = null!;
    private ThermalConfig config = null!;
    private readonly Dictionary<string, RoomEntry> entries = [];
    /// <summary>Chunks where a container asked for a room and there was none, with the game hour it expires.</summary>
    private readonly Dictionary<(int X, int Y, int Z), double> noRoom = [];
    private Harmony? harmony;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartPre(ICoreAPI api)
    {
        // Singleplayer runs the client and the server in one process: patch once.
        if (Harmony.HasAnyPatches("caminus")) return;
        harmony = new Harmony("caminus");
        harmony.PatchAll();
    }

    public override void Dispose()
    {
        harmony?.UnpatchAll("caminus");
        harmony = null;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        config = api.Assets.Get("caminus:config/thermal.json").ToObject<ThermalConfig>();
        api.Logger.Notification("[Caminus] config: tick {0} ms, {1} W per heat unit, {2} wall materials",
            config.TickMs, config.WattsPerHeatStrength, config.WallU.Count);
        rooms = api.ModLoader.GetModSystem<RoomRegistry>();
        api.Event.RegisterGameTickListener(OnTick, ex => api.Logger.Error("[Caminus] thermal tick: {0}", ex), config.TickMs);
        // ChunkColumnUnloaded fires BEFORE the unload loop (ServerSystemUnloadChunks.cs:287), so the
        // chunk is still writable. GameWorldSave covers the autosave and the save-on-shutdown
        // (ServerSystemLoadAndSaveGame.cs:204), which is fired before the chunks are written out.
        api.Event.ChunkColumnUnloaded += OnChunkColumnUnloaded;
        api.Event.GameWorldSave += () => { foreach (RoomEntry e in entries.Values) Save(e); };
    }

    private void OnTick(float dtRealSeconds)
    {
        if (dtRealSeconds <= 0) return;
        TrackPlayerRooms();
        // A room is kept as long as its chunk column is loaded, player or not: a cellar has to keep
        // cooling while nobody watches, and OnChunkColumnUnloaded is what ends its life.

        // The step is in GAME seconds: a building's heat evolves on the scale of game days,
        // not real-world minutes. SpeedOfTime × CalendarSpeedMul = 60 × 0.5 by default,
        // i.e. 30 game seconds per real second (GameCalendar.cs:303).
        double dt = dtRealSeconds * sapi.World.Calendar.SpeedOfTime * sapi.World.Calendar.CalendarSpeedMul;
        if (dt <= 0) return;

        foreach (RoomEntry e in entries.Values)
        {
            e.OutsideTemperature = OutsideTemperature(e);
            e.GroundTemp = GroundTemperatureOf(e);
            // The block scan is the expensive part of the tick; an empty room can afford to see
            // its firepit go out 10 s late, and the rock around it never changes on its own.
            bool outer = e.Ticks % UnoccupiedSourceScanTicks == 0;
            if (e.HasPlayer || outer) ScanSources(e, outer);
            e.Ticks++;
            UpdateWindSunAndStratification(e);
            UpdateDraftAndSmoke(e, dt);

            e.Net.SetTemperature(e.OutsideNode, e.OutsideTemperature);
            e.Net.SetSourcePower(e.Node, e.SourceWatts - FlueWatts(e) + e.NearbyWatts
                                         + e.StratificationWatts + e.SolarWatts + e.DraftWatts);
            e.Net.SetEdgeConductance(e.Edge, e.Geom.Conductance);
            e.Net.SetTemperature(e.WindNode, e.WindTemperature);
            e.Net.SetEdgeConductance(e.WindEdge, e.WindConductance);
            e.Net.SetEdgeConductance(e.DraftEdge, e.DraftConductance);
            if (e.GroundNode >= 0)
            {
                e.Net.SetTemperature(e.GroundNode, e.GroundTemp);
                e.Net.SetEdgeConductance(e.GroundEdge, e.Geom.GroundConductance);
            }

            e.Net.Step(dt);
            e.Temperature = e.Net.GetTemperature(e.Node);
            e.TotalHours = sapi.World.Calendar.TotalHours;
        }
    }

    /// <summary>
    /// Block at eye height. The feet position lands inside the slab, stair or ground block the
    /// player stands on, and the vanilla registry then returns a one-block "room" inside it.
    /// </summary>
    public static BlockPos EyeBlockPos(Entity entity)
    {
        BlockPos pos = entity.Pos.AsBlockPos;
        pos.Y = (int)Math.Floor(entity.Pos.Y + entity.LocalEyePos.Y);
        return pos;
    }

    private void TrackPlayerRooms()
    {
        foreach (RoomEntry e in entries.Values) e.HasPlayer = false;
        foreach (IPlayer player in sapi.World.AllOnlinePlayers)
        {
            if (player is not IServerPlayer { ConnectionState: EnumClientState.Playing } sp || sp.Entity == null) continue;
            BlockPos pos = EyeBlockPos(sp.Entity);
            Room room = rooms.GetRoomForPosition(pos);
            if (!Enclosed(room)) continue;
            RoomEntry? e = Track(room, pos.dimension);
            if (e == null) continue;
            e.HasPlayer = true;
            if (config.SmokeDamage && e.Smoke >= HeavySmoke && e.Ticks % SmokeDamageTicks == 0)
                sp.Entity.ReceiveDamage(new DamageSource { Source = EnumDamageSource.Block, Type = EnumDamageType.Suffocation },
                    SmokeDamagePerHit);
        }
    }

    // ponytail: an open volume (a doorway to the outside, a hall wider than the vanilla 14-block
    // limit) is treated as outside; milestone 6 brings the homemade flood fill.
    private static bool Enclosed([NotNullWhen(true)] Room? room) =>
        room?.Location != null && room.PosInRoom != null && room.ExitCount == 0;

    private RoomEntry? Track(Room room, int dim)
    {
        string key = Key(room.Location, dim);
        if (entries.TryGetValue(key, out RoomEntry? e))
        {
            // Same bbox but a different instance: the registry was invalidated by a ChunkDirty, so a
            // block moved. The geometry is rebuilt, the temperature is kept.
            if (ReferenceEquals(e.Room, room)) return e;
            e.Room = room;
            e.Geom = Measure(room, dim);
            Build(e);
            return e;
        }

        Geometry geom = Measure(room, dim);
        if (geom.Volume == 0) return null;
        double outside = ClimateTemperature(room.Location, dim) ?? 10;
        e = new RoomEntry
        {
            Room = room,
            Geom = geom,
            Dimension = dim,
            OutsideTemperature = outside,
            WindTemperature = outside,
            Temperature = outside, // a room we have never seen starts at the outside temperature
            TotalHours = sapi.World.Calendar.TotalHours,
        };
        e.GroundTemp = GroundTemperatureOf(e);
        Restore(key, e);
        Build(e);
        entries[key] = e;
        return e;
    }

    /// <summary>
    /// The room's own network, from its current temperature and geometry. Rebuilt when the entry is
    /// created and whenever a block moved in its envelope.
    /// </summary>
    // ponytail: one network per room, because nothing links two rooms yet. Milestone 4 (a vertical
    // opening between a cellar and the room above) needs an edge between two room nodes: those two
    // rooms then have to share one network, which is where this splits into groups of rooms.
    private void Build(RoomEntry e)
    {
        var net = new ThermalNetwork();
        e.Node = net.AddNode(Capacity(e.Geom), e.Temperature);
        e.OutsideNode = net.AddFixedNode(e.OutsideTemperature);
        e.Edge = net.AddEdge(e.Node, e.OutsideNode, e.Geom.Conductance);
        // The wind blows air in from somewhere else, so it gets its own fixed node at the
        // temperature it comes from; its edge carries only the extra conductance it causes.
        e.WindNode = net.AddFixedNode(e.WindTemperature);
        e.WindEdge = net.AddEdge(e.Node, e.WindNode, e.WindConductance);
        // The draft pulls its make-up air in through the envelope, so it comes from the local outside
        // air, not from upwind: same node as the fabric, a second edge whose conductance is the flow.
        e.DraftEdge = net.AddEdge(e.Node, e.OutsideNode, e.DraftConductance);
        e.GroundNode = e.GroundEdge = -1;
        if (e.Geom.GroundFaces > 0)
        {
            e.GroundNode = net.AddFixedNode(e.GroundTemp);
            e.GroundEdge = net.AddEdge(e.Node, e.GroundNode, e.Geom.GroundConductance);
        }
        e.Net = net;
    }

    private double Capacity(Geometry g) => Math.Max(1, g.Volume * AirVolumetricCapacity * config.AirCapacityFactor);

    /// <summary>Walks the bbox: volume and one <see cref="Face"/> per square metre of envelope.</summary>
    // ponytail: O(bbox × 6) with one GetBlock per face, redone only when the room changes.
    private Geometry Measure(Room room, int dim)
    {
        var geom = new Geometry();
        Cuboidi c = room.Location;
        IBlockAccessor acc = sapi.World.BlockAccessor;
        // Room.Location holds raw coordinates (RoomRegistry.cs:359): explicit dimension.
        var pos = new BlockPos(c.MinX, c.MinY, c.MinZ, dim);
        BlockPos nb = pos.Copy();

        for (int x = c.MinX; x <= c.MaxX; x++)
            for (int y = c.MinY; y <= c.MaxY; y++)
                for (int z = c.MinZ; z <= c.MaxZ; z++)
                    MeasureAirBlock(geom, room, acc, pos.Set(x, y, z), nb);

        foreach (Face f in geom.Faces)
        {
            if (f.Ground) { geom.GroundConductance += f.UA; continue; }
            geom.Conductance += f.UA;
            // A face open to the air is a whole square metre of inlet; a solid one only its cracks.
            geom.InletArea += f.Opening ? 1 : config.LeakageAreaPerFace;
        }
        BuildFlues(geom, acc);
        return geom;
    }

    /// <summary>
    /// Turns the chimney blocks sitting on the ceiling into flues. Two stacks side by side are one
    /// flue of twice the section, not two flues: what matters to the draft is the total area.
    /// </summary>
    private void BuildFlues(Geometry geom, IBlockAccessor acc)
    {
        var pending = new HashSet<BlockPos>(geom.ChimneyStarts);
        while (pending.Count > 0)
        {
            BlockPos seed = pending.First();
            pending.Remove(seed);
            List<BlockPos> group = [seed];
            for (int i = 0; i < group.Count; i++)
                foreach (BlockFacing side in BlockFacing.HORIZONTALS)
                {
                    BlockPos n = group[i].AddCopy(side);
                    if (pending.Remove(n)) group.Add(n);
                }

            int heightSum = 0, top = 0, open = 0;
            foreach (BlockPos start in group)
            {
                (int height, int topY, bool sky) = WalkFlue(acc, start, geom.ChimneyBlocks);
                heightSum += height;
                if (!sky) continue;
                open++;
                top = Math.Max(top, topY);
            }
            geom.Flues.Add(new Flue(seed, (int)Math.Round((double)heightSum / group.Count), group.Count, top, open == 0));
        }

        foreach (Flue f in geom.Flues)
        {
            if (f.Blocked) continue;
            geom.FlueColumns += f.Columns;
            geom.FlueHeight += (double)f.Height * f.Columns;
            geom.FlueTopY = Math.Max(geom.FlueTopY, f.TopY);
        }
        if (geom.FlueColumns > 0) geom.FlueHeight /= geom.FlueColumns;
    }

    /// <summary>
    /// Walks one column of chimney blocks upward. It draws only if what sits on top is not solid and
    /// sees the sky: <c>GetRainMapHeightAt</c> is the topmost block that stops rain at that x/z, so a
    /// stack whose own top block is the highest thing there answers exactly its own Y, and a stack
    /// under a roof answers the roof's. A cap laid straight on the stack fails the solidity test first.
    /// </summary>
    private static (int Height, int TopY, bool Sky) WalkFlue(IBlockAccessor acc, BlockPos start, List<BlockPos> blocks)
    {
        BlockPos p = start.Copy();
        int height = 0;
        while (height < MaxFlueHeight && acc.GetBlock(p)?.HasBehavior<BlockBehaviorChimney>(withInheritance: true) == true)
        {
            blocks.Add(p.Copy());
            height++;
            p.Up();
        }
        Block? above = acc.GetBlock(p);
        bool sky = above != null && above.GetRetention(p, BlockFacing.DOWN, EnumRetentionType.Heat) == 0
                   && acc.GetRainMapHeightAt(p) <= p.Y;
        return (height, p.Y - 1, sky);
    }

    private void MeasureAirBlock(Geometry geom, Room room, IBlockAccessor acc, BlockPos pos, BlockPos nb)
    {
        if (!room.Contains(pos)) return;
        geom.Volume++;
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            nb.Set(pos.X + face.Normali.X, pos.Y + face.Normali.Y, pos.Z + face.Normali.Z);
            if (!room.Contains(nb)) AddFace(geom, acc, nb, face, pos.Y);
        }
    }

    /// <summary>Face of an air block toward the outside of the room: wall (solid block on the room side) or opening.</summary>
    private void AddFace(Geometry geom, IBlockAccessor acc, BlockPos nb, BlockFacing face, int airY)
    {
        Block block = acc.GetBlock(nb);
        // Same criterion as the vanilla RoomRegistry (RoomRegistry.cs:418): a closed door, a glass
        // pane or a chiseled block retains heat even though its faces are not "solid".
        if (block == null || block.GetRetention(nb, face.Opposite, EnumRetentionType.Heat) == 0)
        {
            geom.Faces.Add(new Face(nb.Copy(), face, EnumBlockMaterial.Air, config.OpeningConductance, false, true, airY, SkyExposure(acc, nb, face)));
            return;
        }
        EnumBlockMaterial mat = block.GetBlockMaterial(acc, nb);
        // The chimney block is sidesolid only downward, so the vanilla flood fill treats it as a
        // ceiling and the room stays enclosed with the stack outside it. The behavior, not the block
        // code: another mod's chimney draws just as well. Inheritance included, for subclasses of it.
        if (face == BlockFacing.UP && block.HasBehavior<BlockBehaviorChimney>(withInheritance: true))
            geom.ChimneyStarts.Add(nb.Copy());

        // GetTerrainMapheightAt, not GetRainMapHeightAt: both return the topmost solid Y at that
        // x/z, but the rain map is updated whenever a block is placed, so the roof of any building
        // becomes the "surface" and its own walls would read as buried. The worldgen map is the
        // natural ground level and never moves (IBlockAccessor.cs:474-488). The surface block
        // itself is exposed to the air, hence the strict comparison.
        int surfaceY = acc.GetTerrainMapheightAt(nb);
        bool ground = nb.Y < surfaceY;
        if (ground) { geom.GroundFaces++; geom.GroundDepthSum += surfaceY - nb.Y; }
        geom.Faces.Add(new Face(nb.Copy(), face, mat, ground ? config.GroundContactU : config.UFor(mat), ground, false, airY,
            ground ? 0 : SkyExposure(acc, nb, face)));
    }

    /// <summary>
    /// How much sky the block just outside the wall sees, 0..1: its stored sunlight level
    /// (<c>OnlySunLight</c>, 0..<c>SunBrightness</c> = 24 server side, BlockAccessorBase.cs:669-684)
    /// which is the daylight cycle taken out, exactly what a fixed geometric exposure needs. An
    /// unloaded chunk answers SunBrightness, i.e. full sky.
    /// </summary>
    private double SkyExposure(IBlockAccessor acc, BlockPos wall, BlockFacing face) =>
        Math.Clamp((double)acc.GetLightLevel(wall.AddCopy(face), EnumLightLevelType.OnlySunLight)
                   / Math.Max(1, sapi.World.SunBrightness), 0, 1);

    /// <summary>
    /// Heat sources in and around the room. What sits in the room or in the wall touching it counts in
    /// full and lands in <see cref="RoomEntry.SourceWatts"/>; a lava lake or a hot spring further out
    /// only reaches the room through the rock, so it is divided by 1 + its Chebyshev distance past
    /// that first wall and lands in <see cref="RoomEntry.NearbyWatts"/>.
    /// </summary>
    // ponytail: the first shell is walked every tick because a firepit goes out, the rock beyond it
    // only on the 10-tick beat, where the scan costs (bbox + 2×margin)³ GetBlock calls. Cache the
    // source positions and invalidate on ChunkDirty if that ever shows up in a profile.
    private void ScanSources(RoomEntry e, bool outer)
    {
        Cuboidi c = e.Room.Location;
        int m = outer ? Math.Max(1, config.SourceScanMargin) : 1;
        var scan = default(Scan);
        var pos = new BlockPos(c.MinX, c.MinY, c.MinZ, e.Dimension);
        for (int x = c.MinX - m; x <= c.MaxX + m; x++)
            for (int y = c.MinY - m; y <= c.MaxY + m; y++)
                for (int z = c.MinZ - m; z <= c.MaxZ + m; z++)
                    AddSource(pos.Set(x, y, z), c, ref scan);
        e.SourceWatts = scan.Inside;
        e.SmokeWatts = scan.SmokeWatts;
        e.SmokeSources = scan.SmokeSources;
        // A pass that stopped at the first shell has seen nothing of the rock: keep what the last full one found.
        if (outer) e.NearbyWatts = scan.Nearby;
    }

    /// <summary>Running totals of one <see cref="ScanSources"/> pass.</summary>
    private struct Scan
    {
        public double Inside, Nearby, SmokeWatts;
        public int SmokeSources;
    }

    private void AddSource(BlockPos pos, Cuboidi c, ref Scan scan)
    {
        Block? block = sapi.World.BlockAccessor.GetBlock(pos);
        if (block == null || block.Id == 0) return;
        int d = Math.Max(Exposure.Beyond(pos.X, c.MinX, c.MaxX),
                Math.Max(Exposure.Beyond(pos.Y, c.MinY, c.MaxY), Exposure.Beyond(pos.Z, c.MinZ, c.MaxZ)));
        // Only what burns inside the room smokes into it. BlockFirepit and BlockPitkiln implement
        // ISmokeEmitter, and both answer false while they are not lit.
        bool smoking = d == 0 && block.GetInterface<ISmokeEmitter>(sapi.World, pos)?.EmitsSmoke(pos) == true;
        if (smoking) scan.SmokeSources++;

        IHeatSource? src = block.GetInterface<IHeatSource>(sapi.World, pos);
        if (src == null) return;
        double watts = src.GetHeatStrength(sapi.World, pos, pos) * config.WattsPerHeatStrength;
        if (d > 0) { scan.Nearby += watts * Exposure.Reach(d); return; }
        scan.Inside += watts;
        if (smoking) scan.SmokeWatts += watts;
    }

    /// <summary>
    /// Power a drawing flue takes straight up the stack, W: the share of an open hearth's fire that
    /// leaves with the combustion gases instead of warming the room. Without a flue nothing is lost
    /// this way, and the smoke stays in the room instead.
    /// </summary>
    private double FlueWatts(RoomEntry e) => e.Geom.FlueColumns > 0 ? config.FlueLossFraction * e.SmokeWatts : 0;

    private double OutsideTemperature(RoomEntry e) => ClimateTemperature(e.Room.Location, e.Dimension) ?? e.OutsideTemperature;

    /// <summary>null if the region isn't loaded: the caller keeps the previous value.</summary>
    private double? ClimateTemperature(Cuboidi c, int dim) =>
        sapi.World.BlockAccessor.GetClimateAt(Center(c, dim), EnumGetClimateMode.NowValues)?.Temperature;

    private static BlockPos Center(Cuboidi c, int dim) => new((c.MinX + c.MaxX) / 2, (c.MinY + c.MaxY) / 2, (c.MinZ + c.MaxZ) / 2, dim);

    /// <summary>
    /// Kusuda wave at the mean burial depth of the room's ground faces, plus the geothermal offset of
    /// the region: the rock under a geologically active place is simply warmer.
    /// </summary>
    private double GroundTemperatureOf(RoomEntry e)
    {
        if (e.Geom.GroundFaces == 0) return e.OutsideTemperature;
        IGameCalendar cal = sapi.World.Calendar;
        (double mean, double amplitude, double coldestDay) = e.Climate ??= SampleSurfaceClimate(e);
        return GroundTemperature.At(mean, amplitude, coldestDay, e.Geom.GroundDepth, cal.DayOfYearf, cal.DaysPerYear)
               + config.GeothermalKPerActivity * Land(e).Geologic;
    }

    /// <summary>
    /// Geologic activity and forest density here, both normalized 0..1 and both fixed at world
    /// generation, so one sample per room is enough. Only the worldgen climate carries them:
    /// <c>ServerWorldMap.getWorldGenClimateAt</c> (l.610) skips both in the temperature-only modes,
    /// and <c>WorldGenValues</c> is also the mode that does not run the weather system on top.
    /// A null answer means the region was not loaded: try again next tick rather than cache a zero.
    /// </summary>
    private (double Geologic, double Forest) Land(RoomEntry e)
    {
        if (e.Land == null)
        {
            ClimateCondition? c = sapi.World.BlockAccessor.GetClimateAt(
                Center(e.Room.Location, e.Dimension), EnumGetClimateMode.WorldGenValues);
            if (c != null) e.Land = (c.GeologicActivity, c.ForestDensity);
        }
        return e.Land ?? (0, 0);
    }

    /// <summary>
    /// Annual mean, half-swing and coldest day of the surface above this room, read from the game's
    /// own climate at 12 evenly spaced days of the current year. Sampled at hour 10, where vanilla's
    /// diurnal term is exactly zero (ModTemperature.updateTemperature: it adds
    /// (smoothstep(|cyclicDistance(4, hour, 24)| / 12) − 0.5) × swing, and smoothstep(0.5) = 0.5),
    /// so the mean is not biased by whatever time of day we happen to ask about. A few dozen calls,
    /// once per room. ForSuppliedDate_TemperatureOnly is the only mode that never returns null.
    /// </summary>
    private (double Mean, double Amplitude, double ColdestDay) SampleSurfaceClimate(RoomEntry e)
    {
        IGameCalendar cal = sapi.World.Calendar;
        BlockPos pos = Center(e.Room.Location, e.Dimension);
        double yearStart = Math.Floor(cal.TotalDays / cal.DaysPerYear) * cal.DaysPerYear;
        double sum = 0, min = double.MaxValue, max = double.MinValue, coldestDay = 0;
        for (int i = 0; i < 12; i++)
        {
            double day = i * cal.DaysPerYear / 12.0;
            double t = sapi.World.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.ForSuppliedDate_TemperatureOnly,
                yearStart + day + 10.0 / cal.HoursPerDay).Temperature;
            sum += t;
            if (t < min) { min = t; coldestDay = day; }
            if (t > max) max = t;
        }
        return (sum / 12, (max - min) / 2, coldestDay);
    }

    // --- Wind and stratification ---------------------------------------------------------------

    private void UpdateWindSunAndStratification(RoomEntry e)
    {
        double forest = Land(e).Forest;
        e.Shade = 1 - config.ForestShade * forest;
        e.Shelter = 1 - config.ForestShelter * forest;
        e.Wind = WindAt(e);
        e.WindTemperature = UpwindTemperature(e);
        UpdateSunFactors(e);
        e.Gradient = Stratification.Gradient(e.SourceWatts - FlueWatts(e), config.StratificationKPerMPerKW, config.StratificationMaxKPerM);

        // A face at height Y sees T + gradient×(Y + 0.5 − yMid), not the mean T. Keeping the edges on
        // the mean and injecting −Σ G_f × gradient × (Y_f + 0.5 − yMid) into the room node is the
        // same set of flows, and it keeps the network linear: one number instead of one node per layer.
        // The sun is folded in the same way: a sunlit face sees air warmer by its sol-air excess, which
        // is +Σ UA_f × solAir_f into the room node. Only the fabric conductance carries it: the extra
        // the wind opens up leads to air that comes from upwind and never touched the warm wall.
        double yMid = YMid(e), wind = 0, strat = 0, sun = 0;
        foreach (Face f in e.Geom.Faces)
        {
            double extra = WindExtra(f, e.Wind, e.Shelter);
            wind += extra;
            strat -= (f.UA + extra) * e.Gradient * (f.Y + 0.5 - yMid);
            sun += f.UA * SolAir(e, f);
        }
        e.WindConductance = wind;
        e.StratificationWatts = strat;
        e.SolarWatts = sun;
    }

    /// <summary>
    /// The chimney, once the stratification gradient is known. The draft is a conductance toward the
    /// outside air recomputed from the current temperatures every tick: over one step it is linear,
    /// which is all the implicit integration needs, and the square root of the temperature difference
    /// is folded back in on the next one. The air that actually leaves is the ceiling's, not the mean,
    /// so the difference goes in as an extra power exactly the way the stratification does.
    /// </summary>
    private void UpdateDraftAndSmoke(RoomEntry e, double dtSeconds)
    {
        Geometry g = e.Geom;
        double ceiling = LocalTemperature(e, e.Room.Location.MaxY);
        // The inlet is half a metre above the floor: the cracks the draft pulls through are spread
        // over the whole envelope, and milestone 6 will put the real openings' mean height here.
        e.DraftFlow = Chimney.Draft(config.DischargeCoefficient, g.FlueColumns, g.InletArea,
            g.FlueTopY - (e.Room.Location.MinY + 0.5), ceiling, e.OutsideTemperature);
        e.DraftConductance = Chimney.Conductance(e.DraftFlow);
        e.DraftWatts = -e.DraftConductance * (ceiling - e.Temperature);
        e.Smoke = Chimney.Smoke(e.Smoke, e.SmokeSources * config.SmokePerSourcePerHour,
            Chimney.AirChanges(e.DraftFlow, g.Volume, BaseAirChanges), dtSeconds / 3600);
    }

    private double SolAir(RoomEntry e, in Face f) =>
        Exposure.SolAir(config.SolAirMaxK, f.Sun, e.SunFactor[f.Facing.Index], e.Shade);

    /// <summary>
    /// Where the sun is, once per room per tick, turned into what each of the six facings catches of
    /// it (<see cref="Exposure.Incidence"/>): the roof takes its height, the wall it shines on takes
    /// the horizontal cosine of incidence and the wall behind the building takes nothing.
    /// <c>Calendar.GetSunPosition(pos, totalDays)</c> is the unit vector pointing at the sun, built
    /// from cos(zenith) and the azimuth (GameCalendar.cs:215-222); the survival mod fills the real
    /// spherical coordinates (SurvivalCoreSystem.cs:131, latitude and axial tilt included).
    /// <c>GetDayLightStrength</c> would be the obvious call for the day/night part, but it never
    /// reaches 0: it keeps the moon (up to 0.33) and a 0.06 floor from the sunlight texture
    /// (GameCalendar.cs:404-413), and moonlight has no business warming a wall.
    /// </summary>
    private void UpdateSunFactors(RoomEntry e)
    {
        Cuboidi c = e.Room.Location;
        IGameCalendar cal = sapi.World.Calendar;
        Vec3f sun = cal.GetSunPosition(new Vec3d((c.MinX + c.MaxX) / 2.0, c.MaxY, (c.MinZ + c.MaxZ) / 2.0), cal.TotalDays);
        e.Daylight = Math.Max(0, sun.Y);
        // Face.Facing points from the room toward the wall, so its normal is the one pointing at the sky.
        foreach (BlockFacing face in BlockFacing.ALLFACES)
            e.SunFactor[face.Index] = Exposure.Incidence(sun.X, sun.Y, sun.Z, face.Normali.X, face.Normali.Y, face.Normali.Z);
    }

    /// <summary>
    /// Wind two blocks above the roof. Vanilla does not damp it indoors: the survival weather system
    /// only fills the X component (WeatherSystemBase.Event_OnGetWindSpeed) from the region's wind
    /// pattern strength, scaled by height alone (WeatherSimulationRegion.GetWindSpeed: amplified up
    /// to 1.5 above sea level, divided by 1 + depth/4 below it). Sampling above the roof is therefore
    /// about the exposure of the building, not about being inside or outside.
    /// </summary>
    private Vec3d WindAt(RoomEntry e)
    {
        Cuboidi c = e.Room.Location;
        return sapi.World.BlockAccessor.GetWindSpeedAt(
            new BlockPos((c.MinX + c.MaxX) / 2, c.MaxY + 2, (c.MinZ + c.MaxZ) / 2, e.Dimension));
    }

    /// <summary>
    /// The game has no wind temperature, so the incoming air takes the climate of where it comes
    /// from: up to windSampleDistance blocks upwind (scaled down when the wind is weak, so a calm
    /// day samples the building itself), at the height of the room or of the upwind terrain,
    /// whichever is higher. Vanilla's climate cools with altitude, so sampling at the ground would
    /// hand a tower on a plain a warm wind it never feels; taking the terrain height only when it
    /// is above the room keeps what matters, air that came over a mountain arriving colder.
    /// </summary>
    private double UpwindTemperature(RoomEntry e)
    {
        double speed = Horizontal(e.Wind);
        if (speed <= 0) return e.OutsideTemperature;
        Cuboidi c = e.Room.Location;
        double d = config.WindSampleDistance * Math.Clamp(speed / 0.5, 0, 1) / speed;
        var pos = new BlockPos((int)((c.MinX + c.MaxX) / 2.0 - e.Wind.X * d), 0,
                               (int)((c.MinZ + c.MaxZ) / 2.0 - e.Wind.Z * d), e.Dimension);
        pos.Y = Math.Max(sapi.World.BlockAccessor.GetTerrainMapheightAt(pos) + 1, (c.MinY + c.MaxY) / 2);
        return sapi.World.BlockAccessor.GetClimateAt(pos, EnumGetClimateMode.NowValues)?.Temperature ?? e.OutsideTemperature;
    }

    /// <summary>
    /// Extra conductance of one face, W/K. A wall only feels the wind that blows into it
    /// (UA × factor × max(0, wind·n)); an opening leaks with any wind, a head-on one twice as much.
    /// A buried face is shielded by the ground, and so, in part, is a building in a forest.
    /// </summary>
    private double WindExtra(in Face f, Vec3d wind, double shelter)
    {
        if (f.Ground) return 0;
        double headOn = Math.Max(0, wind.X * f.Facing.Normali.X + wind.Z * f.Facing.Normali.Z);
        return shelter * (f.Opening
            ? f.UA * config.WindOpeningFactor * (Horizontal(wind) + headOn) / 2
            : f.UA * config.WindWallFactor * headOn);
    }

    private static double Horizontal(Vec3d v) => Math.Sqrt(v.X * v.X + v.Z * v.Z);

    /// <summary>Mid height of the room, in block coordinates (block y spans y..y+1).</summary>
    private static double YMid(RoomEntry e) => (e.Room.Location.MinY + e.Room.Location.MaxY + 1) / 2.0;

    private static double LocalTemperature(RoomEntry e, int y) => Stratification.At(e.Temperature, e.Gradient, y, YMid(e));

    /// <summary>Air temperature at that height in the room. False if the position is not in a room Caminus tracks.</summary>
    public bool TryGetLocalTemperature(BlockPos pos, out double temperature)
    {
        RoomEntry? e = Find(pos);
        temperature = e == null ? 0 : LocalTemperature(e, pos.Y);
        return e != null;
    }

    /// <summary>Per-face state of the room containing <paramref name="pos"/>, for the client overlay.</summary>
    public bool TryGetFaceFlows(BlockPos pos, [NotNullWhen(true)] out RoomFlows? flows)
    {
        flows = null;
        RoomEntry? e = Find(pos);
        if (e == null) return false;

        double yMid = YMid(e);
        var faces = new List<FaceFlow>(e.Geom.Faces.Count);
        foreach (Face f in e.Geom.Faces)
        {
            double local = Stratification.At(e.Temperature, e.Gradient, f.Y, yMid);
            double extra = WindExtra(f, e.Wind, e.Shelter);
            // The sun does not warm the ground, and it only lifts the outside air the fabric sees:
            // what blows in through the wind's extra conductance comes from upwind, off a cold wall.
            double sol = f.Ground ? 0 : SolAir(e, f);
            double node = f.Ground ? e.GroundTemp : e.OutsideTemperature;
            faces.Add(new FaceFlow(f, f.UA + extra,
                f.UA * (local - node - sol) + extra * (local - e.WindTemperature)));
        }
        (double geologic, double forest) = Land(e);
        flows = new RoomFlows(e.Temperature, e.OutsideTemperature,
            e.Geom.GroundFaces == 0 ? double.NaN : e.GroundTemp, e.WindTemperature,
            e.Wind, e.Gradient, yMid, e.StratificationWatts, faces, e.SolarWatts, geologic, forest,
            DraftOf(e), e.Smoke, e.SmokeSources);
        return true;
    }

    /// <summary>null when the room has no chimney block on its ceiling at all.</summary>
    private static DraftState? DraftOf(RoomEntry e)
    {
        Geometry g = e.Geom;
        if (g.Flues.Count == 0) return null;
        return new DraftState((int)Math.Round(g.FlueHeight), g.FlueColumns, g.FlueColumns == 0,
            e.DraftFlow, e.DraftConductance * (e.OutsideTemperature - LocalTemperature(e, e.Room.Location.MaxY)),
            g.InletArea, g.ChimneyBlocks);
    }

    // --- Persistence -------------------------------------------------------------------------

    private static string Key(Cuboidi c, int dim) =>
        string.Create(CultureInfo.InvariantCulture, $"{dim}/{c.MinX}/{c.MinY}/{c.MinZ}/{c.SizeX}/{c.SizeY}/{c.SizeZ}");

    /// <summary>Server chunk holding the room's min corner. World coordinates are never negative.</summary>
    private IServerChunk? ChunkOf(RoomEntry e)
    {
        Cuboidi c = e.Room.Location;
        return sapi.World.BlockAccessor.GetChunkAtBlockPos(new BlockPos(c.MinX, c.MinY, c.MinZ, e.Dimension)) as IServerChunk;
    }

    private void Save(RoomEntry e)
    {
        IServerChunk? chunk = ChunkOf(e);
        if (chunk == null) return;
        Dictionary<string, Saved> all = Read(chunk);
        all[Key(e.Room.Location, e.Dimension)] = new Saved(e.Temperature, e.TotalHours);
        Write(chunk, all);
    }

    private static Dictionary<string, Saved> Read(IServerChunk chunk)
    {
        byte[]? data = chunk.GetServerModdata(ModDataKey);
        return (data == null ? null : JsonConvert.DeserializeObject<Dictionary<string, Saved>>(Encoding.UTF8.GetString(data))) ?? [];
    }

    private static void Write(IServerChunk chunk, Dictionary<string, Saved> all)
    {
        chunk.SetServerModdata(ModDataKey, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(all)));
        // SetServerModdata only writes the dictionary (ServerChunk.cs:659). Unlike SetModdata it does
        // NOT call MarkModified, and both TryUnloadChunk and the autosave only write chunks whose
        // DirtyForSaving is set, so without this the entry would never reach the save file.
        chunk.MarkModified();
    }

    /// <summary>
    /// Catches a room up to now from what was stored when it was last unloaded, then drops the entry.
    /// A single node with fixed neighbours obeys C·dT/dt = G·(Teq − T), whose exact solution is
    /// T = Teq + (T0 − Teq)·e^(−dt/tau) with tau = C/G: nothing is approximated in the integration,
    /// however long the room stayed away. What IS approximate is Teq: the outside temperature, the
    /// ground and the fire moved while we were not looking, and we relax toward the equilibrium as
    /// it stands now rather than replaying the history we did not record. The wind is left out of
    /// that equilibrium: it has not been sampled yet, and it averages out over the days this covers.
    /// </summary>
    private void Restore(string key, RoomEntry e)
    {
        IServerChunk? chunk = ChunkOf(e);
        if (chunk == null) return;
        Dictionary<string, Saved> all = Read(chunk);
        if (!all.Remove(key, out Saved? saved)) return;
        Write(chunk, all);

        double g = e.Geom.Conductance + e.Geom.GroundConductance;
        if (g <= 0) { e.Temperature = saved.Temperature; return; }
        ScanSources(e, outer: true);
        double teq = (e.Geom.Conductance * e.OutsideTemperature + e.Geom.GroundConductance * e.GroundTemp
                      + e.SourceWatts - FlueWatts(e) + e.NearbyWatts) / g;
        double dt = Math.Max(0, sapi.World.Calendar.TotalHours - saved.TotalHours) * 3600;
        e.Temperature = ThermalNetwork.Relax(saved.Temperature, teq, dt, Capacity(e.Geom) / g);
    }

    private void OnChunkColumnUnloaded(Vec3i chunkCoord)
    {
        foreach (var (key, e) in entries.Where(kv => InColumn(kv.Value, chunkCoord)).ToList())
        {
            Save(e);
            entries.Remove(key);
        }
    }

    private static bool InColumn(RoomEntry e, Vec3i c) =>
        e.Room.Location.MinX / GlobalConstants.ChunkSize == c.X && e.Room.Location.MinZ / GlobalConstants.ChunkSize == c.Z;

    // --- Spoilage ----------------------------------------------------------------------------

    /// <summary>
    /// The tail of the vanilla curve (InWorldContainer.GetPerishRate, VSEssentials) with the room's
    /// real temperature in place of vanilla's sea-level-climate proxy: same balance, same Q10 of
    /// about 1.8, but a cellar that is genuinely cold now behaves like one.
    /// </summary>
    public static double PerishRate(double temperature) =>
        Math.Clamp(Math.Pow(3, temperature / 19 - 1.2) - 0.1, 0.1, 2.4);

    /// <summary>
    /// False if the position is not inside a room Caminus tracks: the caller keeps vanilla's answer.
    /// The rate follows the air at the container's own height, so a crock on the floor keeps better
    /// than one on a shelf under the ceiling.
    /// </summary>
    public bool TryGetPerishRate(BlockPos pos, out float rate)
    {
        RoomEntry? e = Find(pos) ?? Discover(pos);
        rate = (float)PerishRate(e == null ? 0 : LocalTemperature(e, pos.Y));
        return e != null;
    }

    /// <summary>
    /// A container asking for its perish rate in a room no player has ever entered: track it, so a
    /// cellar is discovered by its own crocks. Called from the container's 10 s tick, on the server
    /// main thread, which is the only place RoomRegistry may be used. Positions that turn out not to
    /// be in a room are remembered per chunk for a minute, so an outdoor chest costs one flood fill
    /// per minute instead of one per tick.
    /// </summary>
    private RoomEntry? Discover(BlockPos pos)
    {
        double now = sapi.World.Calendar.TotalHours;
        var chunk = (pos.X / GlobalConstants.ChunkSize, pos.Y / GlobalConstants.ChunkSize, pos.Z / GlobalConstants.ChunkSize);
        if (noRoom.TryGetValue(chunk, out double until) && now < until) return null;

        Room room = rooms.GetRoomForPosition(pos);
        if (!Enclosed(room))
        {
            if (noRoom.Count > 256)
                foreach (var stale in noRoom.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) noRoom.Remove(stale);
            noRoom[chunk] = now + NoRoomTtlHours;
            return null;
        }
        noRoom.Remove(chunk);
        return Track(room, pos.dimension);
    }

    private RoomEntry? Find(BlockPos pos) =>
        entries.Values.FirstOrDefault(x => x.Dimension == pos.dimension && x.Room.Contains(pos));

    // --- Report ------------------------------------------------------------------------------

    public bool TryGetReport(BlockPos pos, out string report)
    {
        report = "";
        RoomEntry? e = Find(pos);
        if (e == null) return false;

        Geometry g = e.Geom;
        double dT = e.Temperature - e.OutsideTemperature;
        double calm = g.Conductance + g.GroundConductance;
        double local = LocalTemperature(e, pos.Y);
        // Invariant culture: the report is also parsed by integration scenarios, a decimal
        // separator that changes with the server locale would make the format unstable.
        CultureInfo c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        double geologic = Land(e).Geologic;
        sb.Append(c, $"Room: {e.Temperature:0.0} °C, outside {e.OutsideTemperature:0.0} °C").AppendLine();
        if (g.GroundFaces > 0)
        {
            sb.Append(c, $"Ground node: {e.GroundTemp:0.0} °C at {g.GroundDepth:0.0} m");
            if (geologic > 0) sb.Append(c, $" (geology {geologic:0.00}, +{config.GeothermalKPerActivity * geologic:0.0} K)");
            sb.AppendLine();
        }
        sb.Append(c, $"Volume {g.Volume} blocks, capacity {Capacity(g) / 1000:0} kJ/K").AppendLine();
        sb.Append(c, $"Sources: {e.SourceWatts + e.NearbyWatts:0} W");
        if (e.NearbyWatts != 0) sb.Append(c, $" (nearby {e.NearbyWatts:0} W)");
        if (FlueWatts(e) > 0) sb.Append(c, $" (flue takes {FlueWatts(e):0} W)");
        sb.AppendLine();
        AppendFlue(sb, c, e);
        sb.Append(c, $"Wind: {Horizontal(e.Wind):0.00} from the {ComesFrom(e.Wind)} at {e.WindTemperature:0.0} °C " +
                     $"(+{(calm <= 0 ? 0 : 100 * e.WindConductance / calm):0.#} % losses, " +
                     $"forest shelter {100 * (1 - e.Shelter):0.#} %)").AppendLine();
        sb.Append(c, $"Sun: {e.Daylight:0.0#} (forest shade {1 - e.Shade:0.0#}, +{e.SolarWatts:0} W)").AppendLine();
        sb.Append(c, $"Stratification: {e.Gradient:0.0} K/m (floor {LocalTemperature(e, e.Room.Location.MinY):0.0} °C, " +
                     $"eyes {local:0.0} °C, ceiling {LocalTemperature(e, e.Room.Location.MaxY):0.0} °C)").AppendLine();
        sb.Append(c, $"Perish rate: {PerishRate(local):0.00}x (vanilla here: {PerishRate(VanillaClimate(e)):0.00}x)").AppendLine();
        // The wind's share is on the Wind line: this one is the fabric of the building, calm air.
        sb.Append(c, $"Losses: {calm:0.0} W/K, i.e. " +
                     $"{g.Conductance * dT + g.GroundConductance * (e.Temperature - e.GroundTemp):0} W at the current delta").AppendLine();
        AppendWalls(sb, c, "Outside walls", g.Faces.Where(f => !f.Ground && !f.Opening), dT);
        AppendOpenings(sb, c, g.Faces.Where(f => f.Opening), dT);
        AppendWalls(sb, c, "Ground walls", g.Faces.Where(f => f.Ground), e.Temperature - e.GroundTemp);
        report = sb.ToString().TrimEnd();
        return true;
    }

    /// <summary>The chimney and what it is doing to the air, two lines at most.</summary>
    private void AppendFlue(StringBuilder sb, CultureInfo c, RoomEntry e)
    {
        Geometry g = e.Geom;
        if (g.Flues.Count == 0) sb.Append("Flue: none").AppendLine();
        else if (g.FlueColumns == 0) sb.Append("Flue: blocked (roof over the stack)").AppendLine();
        else
            sb.Append(c, $"Flue: {g.FlueHeight:0.#} m, {g.FlueColumns} column{(g.FlueColumns == 1 ? "" : "s")}, " +
                         $"draft {e.DraftFlow:0.00} m³/s ({-e.DraftConductance * (LocalTemperature(e, e.Room.Location.MaxY) - e.OutsideTemperature):0} W), " +
                         $"inlet leakage {g.InletArea:0.00} m²").AppendLine();
        if (e.Smoke > 0.1 || e.SmokeSources > 0)
            sb.Append(c, $"Smoke: {Chimney.Level(e.Smoke)} ({e.Smoke:0.00}), {e.SmokeSources} source{(e.SmokeSources == 1 ? "" : "s")}").AppendLine();
    }

    private static void AppendWalls(StringBuilder sb, CultureInfo c, string title, IEnumerable<Face> faces, double dT)
    {
        var byMaterial = faces.GroupBy(f => f.Material)
            .Select(gr => (Material: gr.Key, Faces: gr.Count(), WPerK: gr.Sum(f => f.UA)))
            .OrderByDescending(w => w.WPerK).ToList();
        if (byMaterial.Count == 0) return;
        sb.Append(title).Append(':').AppendLine();
        foreach (var (mat, count, wPerK) in byMaterial)
            sb.Append(c, $"  {mat}: {count} faces, {wPerK:0.0} W/K, {wPerK * dT:0} W").AppendLine();
    }

    private static void AppendOpenings(StringBuilder sb, CultureInfo c, IEnumerable<Face> faces, double dT)
    {
        int count = 0;
        double wPerK = 0;
        foreach (Face f in faces) { count++; wPerK += f.UA; }
        if (count == 0) return;
        sb.Append(c, $"  Openings: {count} faces, {wPerK:0.0} W/K, {wPerK * dT:0} W").AppendLine();
    }

    /// <summary>
    /// Compass direction the wind blows FROM. Vintage Story axes: +X east, +Z south.
    /// </summary>
    public static string ComesFrom(Vec3d wind)
    {
        string[] names = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"];
        return names[(int)Math.Round(Math.Atan2(-wind.X, wind.Z) / (Math.PI / 4)) & 7];
    }

    /// <summary>
    /// What vanilla would read here: the climate at sea level, like InWorldContainer.GetPerishRate.
    /// The sunlight and small-room corrections are left out, so this is the base number, not the
    /// exact vanilla rate for every container shape.
    /// </summary>
    private double VanillaClimate(RoomEntry e)
    {
        Cuboidi c = e.Room.Location;
        return sapi.World.BlockAccessor.GetClimateAt(new BlockPos((c.MinX + c.MaxX) / 2, sapi.World.SeaLevel, (c.MinZ + c.MaxZ) / 2, e.Dimension),
            EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, sapi.World.Calendar.TotalDays).Temperature;
    }
}
