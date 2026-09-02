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
    public double RoomForgetSeconds { get; set; } = 300;
    public Dictionary<EnumBlockMaterial, double> WallU { get; set; } = [];

    public double UFor(EnumBlockMaterial mat) => WallU.TryGetValue(mat, out double u) ? u : 3.0;
}

/// <summary>
/// Thermal simulation of the rooms players are in. Server side only: RoomRegistry
/// is not thread-safe, everything runs on the main thread.
/// </summary>
public class RoomThermalSystem : ModSystem
{
    private const double AirVolumetricCapacity = 1.2 * 1005; // J/K per m³ of air
    private const string ModDataKey = "caminus:rooms";

    private sealed class Geometry
    {
        public int Volume;
        public int Openings;
        /// <summary>Faces toward the open air, by material.</summary>
        public readonly Dictionary<EnumBlockMaterial, (int Faces, double WPerK)> Walls = [];
        /// <summary>Faces buried below the world-generated surface, by material.</summary>
        public readonly Dictionary<EnumBlockMaterial, (int Faces, double WPerK)> GroundWalls = [];
        public int GroundFaces;
        public int GroundDepthSum;
        public double Conductance;
        public double GroundConductance;

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
        public double SourceWatts;
        public long LastSeenMs;
        /// <summary>Game hours at the last simulated step: the base for offline relaxation.</summary>
        public double TotalHours;
        public int Dimension;
        /// <summary>Surface climate at this room, sampled once (annual mean, half-swing, coldest day).</summary>
        public (double Mean, double Amplitude, double ColdestDay)? Climate;
        public int Node = -1;
        public int OutsideNode = -1;
        public int Edge = -1;
        public int GroundNode = -1;
        public int GroundEdge = -1;
    }

    /// <summary>What survives an unload: enough to relax the room forward when it comes back.</summary>
    private sealed record Saved(double Temperature, double TotalHours);

    private ICoreServerAPI sapi = null!;
    private RoomRegistry rooms = null!;
    private ThermalConfig config = null!;
    private readonly Dictionary<string, RoomEntry> entries = [];
    private ThermalNetwork? network;
    private Harmony? harmony;
    private bool dirty;

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
        PerishRatePatch.System = null;
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
        PerishRatePatch.System = this;
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
        long nowMs = sapi.World.ElapsedMilliseconds;
        TrackPlayerRooms(nowMs);
        ForgetStaleRooms(nowMs);
        if (entries.Count == 0) { network = null; dirty = false; return; }

        foreach (RoomEntry e in entries.Values)
        {
            e.OutsideTemperature = OutsideTemperature(e);
            e.GroundTemp = GroundTemperatureOf(e);
            e.SourceWatts = SourceWatts(e.Room, e.Dimension);
        }

        if (dirty || network == null) Rebuild();

        foreach (RoomEntry e in entries.Values)
        {
            network!.SetTemperature(e.OutsideNode, e.OutsideTemperature);
            network.SetSourcePower(e.Node, e.SourceWatts);
            network.SetEdgeConductance(e.Edge, e.Geom.Conductance);
            if (e.GroundNode < 0) continue;
            network.SetTemperature(e.GroundNode, e.GroundTemp);
            network.SetEdgeConductance(e.GroundEdge, e.Geom.GroundConductance);
        }

        // The step is in GAME seconds: a building's heat evolves on the scale of game days,
        // not real-world minutes. SpeedOfTime × CalendarSpeedMul = 60 × 0.5 by default,
        // i.e. 30 game seconds per real second (GameCalendar.cs:303).
        double dt = dtRealSeconds * sapi.World.Calendar.SpeedOfTime * sapi.World.Calendar.CalendarSpeedMul;
        if (dt <= 0) return;
        network!.Step(dt);

        foreach (RoomEntry e in entries.Values)
        {
            e.Temperature = network.GetTemperature(e.Node);
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

    private void TrackPlayerRooms(long nowMs)
    {
        foreach (IPlayer player in sapi.World.AllOnlinePlayers)
        {
            if (player is not IServerPlayer { ConnectionState: EnumClientState.Playing } sp || sp.Entity == null) continue;
            BlockPos pos = EyeBlockPos(sp.Entity);
            Room room = rooms.GetRoomForPosition(pos);
            // ponytail: an open volume (a doorway to the outside, a hall wider than the vanilla
            // 14-block limit) is treated as outside; milestone 6 brings the homemade flood fill.
            if (room?.Location == null || room.PosInRoom == null || room.ExitCount > 0) continue;
            Track(room, pos.dimension, nowMs);
        }
    }

    private void ForgetStaleRooms(long nowMs)
    {
        foreach (var key in entries.Where(e => (nowMs - e.Value.LastSeenMs) / 1000.0 > config.RoomForgetSeconds).Select(e => e.Key).ToList())
        {
            Save(entries[key]);
            entries.Remove(key);
            dirty = true;
        }
    }

    private void Track(Room room, int dim, long nowMs)
    {
        string key = Key(room.Location, dim);
        if (entries.TryGetValue(key, out RoomEntry? e))
        {
            e.LastSeenMs = nowMs;
            // Same bbox but a different instance: the registry was invalidated by a ChunkDirty, so a
            // block moved. The geometry is rebuilt, the temperature is kept.
            if (ReferenceEquals(e.Room, room)) return;
            e.Room = room;
            e.Geom = Measure(room, dim);
            dirty = true;
            return;
        }

        Geometry geom = Measure(room, dim);
        if (geom.Volume == 0) return;
        double outside = ClimateTemperature(room.Location, dim) ?? 10;
        e = new RoomEntry
        {
            Room = room,
            Geom = geom,
            LastSeenMs = nowMs,
            Dimension = dim,
            OutsideTemperature = outside,
            Temperature = outside, // a room we have never seen starts at the outside temperature
            TotalHours = sapi.World.Calendar.TotalHours,
        };
        e.GroundTemp = GroundTemperatureOf(e);
        Restore(key, e);
        entries[key] = e;
        dirty = true;
    }

    private void Rebuild()
    {
        var net = new ThermalNetwork();
        foreach (RoomEntry e in entries.Values)
        {
            e.Node = net.AddNode(Capacity(e.Geom), e.Temperature);
            e.OutsideNode = net.AddFixedNode(e.OutsideTemperature);
            // Batch 1: all losses go outside or into the ground, no room-to-room edge.
            e.Edge = net.AddEdge(e.Node, e.OutsideNode, e.Geom.Conductance);
            e.GroundNode = e.GroundEdge = -1;
            if (e.Geom.GroundFaces == 0) continue;
            e.GroundNode = net.AddFixedNode(e.GroundTemp);
            e.GroundEdge = net.AddEdge(e.Node, e.GroundNode, e.Geom.GroundConductance);
        }
        network = net;
        dirty = false;
    }

    private double Capacity(Geometry g) => Math.Max(1, g.Volume * AirVolumetricCapacity * config.AirCapacityFactor);

    /// <summary>Walks the bbox: volume, walls by material, and openings. 1 m² per face.</summary>
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

        geom.Conductance = geom.Walls.Values.Sum(w => w.WPerK) + geom.Openings * config.OpeningConductance;
        geom.GroundConductance = geom.GroundWalls.Values.Sum(w => w.WPerK);
        return geom;
    }

    private void MeasureAirBlock(Geometry geom, Room room, IBlockAccessor acc, BlockPos pos, BlockPos nb)
    {
        if (!room.Contains(pos)) return;
        geom.Volume++;
        foreach (BlockFacing face in BlockFacing.ALLFACES)
        {
            nb.Set(pos.X + face.Normali.X, pos.Y + face.Normali.Y, pos.Z + face.Normali.Z);
            if (!room.Contains(nb)) AddFace(geom, acc, nb, face);
        }
    }

    /// <summary>Face of an air block toward the outside of the room: wall (solid block on the room side) or opening.</summary>
    private void AddFace(Geometry geom, IBlockAccessor acc, BlockPos nb, BlockFacing face)
    {
        Block block = acc.GetBlock(nb);
        // Same criterion as the vanilla RoomRegistry (RoomRegistry.cs:418): a closed door, a glass
        // pane or a chiseled block retains heat even though its faces are not "solid".
        if (block == null || block.GetRetention(nb, face.Opposite, EnumRetentionType.Heat) == 0) { geom.Openings++; return; }
        EnumBlockMaterial mat = block.GetBlockMaterial(acc, nb);

        // GetTerrainMapheightAt, not GetRainMapHeightAt: both return the topmost solid Y at that
        // x/z, but the rain map is updated whenever a block is placed, so the roof of any building
        // becomes the "surface" and its own walls would read as buried. The worldgen map is the
        // natural ground level and never moves (IBlockAccessor.cs:474-488). The surface block
        // itself is exposed to the air, hence the strict comparison.
        int surfaceY = acc.GetTerrainMapheightAt(nb);
        var walls = nb.Y < surfaceY ? geom.GroundWalls : geom.Walls;
        double u = config.GroundContactU;
        if (nb.Y < surfaceY) { geom.GroundFaces++; geom.GroundDepthSum += surfaceY - nb.Y; }
        else u = config.UFor(mat);
        var prev = walls.GetValueOrDefault(mat);
        walls[mat] = (prev.Faces + 1, prev.WPerK + u);
    }

    /// <summary>Heat sources in the bbox expanded by one block. Re-evaluated every tick: a firepit can go out.</summary>
    // ponytail: up to 16³ GetBlock calls per room per second; switch to a cache of source positions invalidated on ChunkDirty if this gets heavy.
    private double SourceWatts(Room room, int dim)
    {
        Cuboidi c = room.Location;
        double strength = 0;
        var pos = new BlockPos(c.MinX, c.MinY, c.MinZ, dim);
        for (int x = c.MinX - 1; x <= c.MaxX + 1; x++)
            for (int y = c.MinY - 1; y <= c.MaxY + 1; y++)
                for (int z = c.MinZ - 1; z <= c.MaxZ + 1; z++)
                {
                    pos.Set(x, y, z);
                    IHeatSource? src = sapi.World.BlockAccessor.GetBlock(pos)?.GetInterface<IHeatSource>(sapi.World, pos);
                    if (src != null) strength += src.GetHeatStrength(sapi.World, pos, pos);
                }
        return strength * config.WattsPerHeatStrength;
    }

    private double OutsideTemperature(RoomEntry e) => ClimateTemperature(e.Room.Location, e.Dimension) ?? e.OutsideTemperature;

    /// <summary>null if the region isn't loaded: the caller keeps the previous value.</summary>
    private double? ClimateTemperature(Cuboidi c, int dim) =>
        sapi.World.BlockAccessor.GetClimateAt(Center(c, dim), EnumGetClimateMode.NowValues)?.Temperature;

    private static BlockPos Center(Cuboidi c, int dim) => new((c.MinX + c.MaxX) / 2, (c.MinY + c.MaxY) / 2, (c.MinZ + c.MaxZ) / 2, dim);

    /// <summary>Kusuda wave at the mean burial depth of the room's ground faces.</summary>
    private double GroundTemperatureOf(RoomEntry e)
    {
        if (e.Geom.GroundFaces == 0) return e.OutsideTemperature;
        IGameCalendar cal = sapi.World.Calendar;
        (double mean, double amplitude, double coldestDay) = e.Climate ??= SampleSurfaceClimate(e);
        return GroundTemperature.At(mean, amplitude, coldestDay, e.Geom.GroundDepth, cal.DayOfYearf, cal.DaysPerYear);
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
    /// it stands now rather than replaying the history we did not record.
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
        double teq = (e.Geom.Conductance * e.OutsideTemperature + e.Geom.GroundConductance * e.GroundTemp
                      + SourceWatts(e.Room, e.Dimension)) / g;
        double dt = Math.Max(0, sapi.World.Calendar.TotalHours - saved.TotalHours) * 3600;
        e.Temperature = ThermalNetwork.Relax(saved.Temperature, teq, dt, Capacity(e.Geom) / g);
    }

    private void OnChunkColumnUnloaded(Vec3i chunkCoord)
    {
        foreach (var (key, e) in entries.Where(kv => InColumn(kv.Value, chunkCoord)).ToList())
        {
            Save(e);
            entries.Remove(key);
            dirty = true;
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

    /// <summary>False if the position is not inside a room Caminus tracks: the caller keeps vanilla's answer.</summary>
    public bool TryGetPerishRate(BlockPos pos, out float rate)
    {
        RoomEntry? e = Find(pos);
        rate = (float)PerishRate(e?.Temperature ?? 0);
        return e != null;
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
        // Invariant culture: the report is also parsed by integration scenarios, a decimal
        // separator that changes with the server locale would make the format unstable.
        CultureInfo c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(c, $"Room: {e.Temperature:0.0} °C, outside {e.OutsideTemperature:0.0} °C").AppendLine();
        if (g.GroundFaces > 0)
            sb.Append(c, $"Ground node: {e.GroundTemp:0.0} °C at {g.GroundDepth:0.0} m").AppendLine();
        sb.Append(c, $"Volume {g.Volume} blocks, capacity {Capacity(g) / 1000:0} kJ/K").AppendLine();
        sb.Append(c, $"Sources: {e.SourceWatts:0} W").AppendLine();
        sb.Append(c, $"Perish rate: {PerishRate(e.Temperature):0.00}x (vanilla here: {PerishRate(VanillaClimate(e)):0.00}x)").AppendLine();
        sb.Append(c, $"Losses: {g.Conductance + g.GroundConductance:0.0} W/K, i.e. " +
                     $"{g.Conductance * dT + g.GroundConductance * (e.Temperature - e.GroundTemp):0} W at the current delta").AppendLine();
        AppendWalls(sb, c, "Outside walls", g.Walls, dT);
        if (g.Openings > 0)
        {
            double w = g.Openings * config.OpeningConductance;
            sb.Append(c, $"  Openings: {g.Openings} faces, {w:0.0} W/K, {w * dT:0} W").AppendLine();
        }
        AppendWalls(sb, c, "Ground walls", g.GroundWalls, e.Temperature - e.GroundTemp);
        report = sb.ToString().TrimEnd();
        return true;
    }

    private static void AppendWalls(StringBuilder sb, CultureInfo c, string title,
        Dictionary<EnumBlockMaterial, (int Faces, double WPerK)> walls, double dT)
    {
        if (walls.Count == 0) return;
        sb.Append(title).Append(':').AppendLine();
        foreach (var (mat, w) in walls.OrderByDescending(w => w.Value.WPerK))
            sb.Append(c, $"  {mat}: {w.Faces} faces, {w.WPerK:0.0} W/K, {w.WPerK * dT:0} W").AppendLine();
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
