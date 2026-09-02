using System.Globalization;
using System.Text;
using Caminus.Core;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
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

    private sealed class Geometry
    {
        public int Volume;
        public int Openings;
        public readonly Dictionary<EnumBlockMaterial, (int Faces, double WPerK)> Walls = [];
        public double Conductance;
    }

    private sealed class RoomEntry
    {
        public Room Room = null!;
        public Geometry Geom = null!;
        public double Temperature;
        public double OutsideTemperature;
        public double SourceWatts;
        public long LastSeenMs;
        public int Dimension;
        public int Node = -1;
        public int OutsideNode = -1;
        public int Edge = -1;
    }

    private ICoreServerAPI sapi = null!;
    private RoomRegistry rooms = null!;
    private ThermalConfig config = null!;
    private readonly Dictionary<(int, int, int, int, int, int, int), RoomEntry> entries = [];
    private ThermalNetwork? network;
    private bool dirty;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        config = api.Assets.Get("caminus:config/thermal.json").ToObject<ThermalConfig>();
        api.Logger.Notification("[Caminus] config: tick {0} ms, {1} W per heat unit, {2} wall materials",
            config.TickMs, config.WattsPerHeatStrength, config.WallU.Count);
        rooms = api.ModLoader.GetModSystem<RoomRegistry>();
        api.Event.RegisterGameTickListener(OnTick, ex => api.Logger.Error("[Caminus] thermal tick: {0}", ex), config.TickMs);
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
            e.SourceWatts = SourceWatts(e.Room, e.Dimension);
        }

        if (dirty || network == null) Rebuild();

        foreach (RoomEntry e in entries.Values)
        {
            network!.SetTemperature(e.OutsideNode, e.OutsideTemperature);
            network.SetSourcePower(e.Node, e.SourceWatts);
            network.SetEdgeConductance(e.Edge, e.Geom.Conductance);
        }

        // The step is in GAME seconds: a building's heat evolves on the scale of game days,
        // not real-world minutes. SpeedOfTime × CalendarSpeedMul = 60 × 0.5 by default,
        // i.e. 30 game seconds per real second (GameCalendar.cs:303).
        double dt = dtRealSeconds * sapi.World.Calendar.SpeedOfTime * sapi.World.Calendar.CalendarSpeedMul;
        if (dt <= 0) return;
        network!.Step(dt);

        foreach (RoomEntry e in entries.Values) e.Temperature = network.GetTemperature(e.Node);
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
            entries.Remove(key);
            dirty = true;
        }
    }

    private void Track(Room room, int dim, long nowMs)
    {
        Cuboidi c = room.Location;
        var key = (dim, c.MinX, c.MinY, c.MinZ, c.SizeX, c.SizeY, c.SizeZ);
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
        entries[key] = new RoomEntry
        {
            Room = room,
            Geom = geom,
            LastSeenMs = nowMs,
            Dimension = dim,
            OutsideTemperature = ClimateTemperature(c, dim) ?? 10,
            Temperature = ClimateTemperature(c, dim) ?? 10, // a new room starts at the outside temperature
        };
        dirty = true;
    }

    private void Rebuild()
    {
        var net = new ThermalNetwork();
        foreach (RoomEntry e in entries.Values)
        {
            e.Node = net.AddNode(Math.Max(1, e.Geom.Volume * AirVolumetricCapacity * config.AirCapacityFactor), e.Temperature);
            e.OutsideNode = net.AddFixedNode(e.OutsideTemperature);
            // Batch 1: all losses go outside, no room-to-room edge.
            e.Edge = net.AddEdge(e.Node, e.OutsideNode, e.Geom.Conductance);
        }
        network = net;
        dirty = false;
    }

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
        var prev = geom.Walls.GetValueOrDefault(mat);
        geom.Walls[mat] = (prev.Faces + 1, prev.WPerK + config.UFor(mat));
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
        sapi.World.BlockAccessor.GetClimateAt(new BlockPos((c.MinX + c.MaxX) / 2, (c.MinY + c.MaxY) / 2, (c.MinZ + c.MaxZ) / 2, dim),
            EnumGetClimateMode.NowValues)?.Temperature;

    public bool TryGetReport(BlockPos pos, out string report)
    {
        report = "";
        RoomEntry? e = entries.Values.FirstOrDefault(x => x.Dimension == pos.dimension && x.Room.Contains(pos));
        if (e == null) return false;

        Geometry g = e.Geom;
        double dT = e.Temperature - e.OutsideTemperature;
        // Invariant culture: the report is also parsed by integration scenarios, a decimal
        // separator that changes with the server locale would make the format unstable.
        CultureInfo c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(c, $"Room: {e.Temperature:0.0} °C, outside {e.OutsideTemperature:0.0} °C").AppendLine();
        sb.Append(c, $"Volume {g.Volume} blocks, capacity {g.Volume * AirVolumetricCapacity * config.AirCapacityFactor / 1000:0} kJ/K").AppendLine();
        sb.Append(c, $"Sources: {e.SourceWatts:0} W").AppendLine();
        sb.Append(c, $"Losses: {g.Conductance:0.0} W/K, i.e. {g.Conductance * dT:0} W at the current delta").AppendLine();
        foreach (var (mat, w) in g.Walls.OrderByDescending(w => w.Value.WPerK))
            sb.Append(c, $"  {mat}: {w.Faces} faces, {w.WPerK:0.0} W/K, {w.WPerK * dT:0} W").AppendLine();
        if (g.Openings > 0)
        {
            double w = g.Openings * config.OpeningConductance;
            sb.Append(c, $"  Openings: {g.Openings} faces, {w:0.0} W/K, {w * dT:0} W").AppendLine();
        }
        report = sb.ToString().TrimEnd();
        return true;
    }
}
