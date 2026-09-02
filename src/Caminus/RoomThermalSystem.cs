using System.Text;
using Caminus.Core;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Caminus;

/// <summary>assets/caminus/config/thermal.json. Unités SI.</summary>
public class ThermalConfig
{
    public int TickMs { get; set; } = 1000;
    public double WattsPerHeatStrength { get; set; } = 400;
    /// <summary>Capacité = volume × 1,2 kg/m³ × 1005 J/kg/K × ce facteur (mobilier et parois intérieures).</summary>
    public double AirCapacityFactor { get; set; } = 5;
    /// <summary>W/K par m² de face ouverte (porte, fenêtre, trou).</summary>
    public double OpeningConductance { get; set; } = 30;
    public double RoomForgetSeconds { get; set; } = 300;
    public Dictionary<EnumBlockMaterial, double> WallU { get; set; } = [];

    public double UFor(EnumBlockMaterial mat) => WallU.TryGetValue(mat, out double u) ? u : 3.0;
}

/// <summary>
/// Simulation thermique des pièces où se trouvent des joueurs. Serveur uniquement : RoomRegistry
/// n'est pas thread-safe, tout se fait sur le thread principal.
/// </summary>
public class RoomThermalSystem : ModSystem
{
    private const double AirVolumetricCapacity = 1.2 * 1005; // J/K par m³ d'air

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
        api.Logger.Notification("[Caminus] config : tick {0} ms, {1} W par unité de chaleur, {2} matériaux de paroi",
            config.TickMs, config.WattsPerHeatStrength, config.WallU.Count);
        rooms = api.ModLoader.GetModSystem<RoomRegistry>();
        api.Event.RegisterGameTickListener(OnTick, ex => api.Logger.Error("[Caminus] tick thermique : {0}", ex), config.TickMs);
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

        // Le pas est en secondes de JEU : la chaleur d'un bâtiment évolue à l'échelle des journées
        // de jeu, pas des minutes réelles. SpeedOfTime × CalendarSpeedMul = 60 × 0,5 par défaut,
        // soit 30 s de jeu par seconde réelle (GameCalendar.cs:303).
        double dt = dtRealSeconds * sapi.World.Calendar.SpeedOfTime * sapi.World.Calendar.CalendarSpeedMul;
        if (dt <= 0) return;
        network!.Step(dt);

        foreach (RoomEntry e in entries.Values) e.Temperature = network.GetTemperature(e.Node);
    }

    private void TrackPlayerRooms(long nowMs)
    {
        foreach (IPlayer player in sapi.World.AllOnlinePlayers)
        {
            if (player is not IServerPlayer { ConnectionState: EnumClientState.Playing } sp || sp.Entity == null) continue;
            BlockPos pos = sp.Entity.Pos.AsBlockPos;
            Room room = rooms.GetRoomForPosition(pos);
            if (room?.Location == null || room.PosInRoom == null) continue;
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
            // Même bbox mais autre instance : le registre a été invalidé par un ChunkDirty, donc un
            // bloc a bougé. La géométrie est refaite, la température conservée.
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
            Temperature = ClimateTemperature(c, dim) ?? 10, // une pièce neuve démarre à la température extérieure
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
            // Lot 1 : toutes les pertes vont vers l'extérieur, pas d'arête pièce-pièce.
            e.Edge = net.AddEdge(e.Node, e.OutsideNode, e.Geom.Conductance);
        }
        network = net;
        dirty = false;
    }

    /// <summary>Parcours de la bbox : volume, parois par matériau et ouvertures. 1 m² par face.</summary>
    // ponytail : O(bbox × 6) avec un GetBlock par face, refait seulement quand la pièce change.
    private Geometry Measure(Room room, int dim)
    {
        var geom = new Geometry();
        Cuboidi c = room.Location;
        IBlockAccessor acc = sapi.World.BlockAccessor;
        // Room.Location contient des coordonnées brutes (RoomRegistry.cs:359) : dimension explicite.
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

    /// <summary>Face d'un bloc d'air vers l'extérieur de la pièce : paroi (bloc solide côté pièce) ou ouverture.</summary>
    private void AddFace(Geometry geom, IBlockAccessor acc, BlockPos nb, BlockFacing face)
    {
        Block block = acc.GetBlock(nb);
        if (block == null || !block.SideSolid[face.Opposite.Index]) { geom.Openings++; return; }
        EnumBlockMaterial mat = block.GetBlockMaterial(acc, nb);
        var prev = geom.Walls.GetValueOrDefault(mat);
        geom.Walls[mat] = (prev.Faces + 1, prev.WPerK + config.UFor(mat));
    }

    /// <summary>Sources de chaleur de la bbox élargie d'un bloc. Réévalué à chaque tick : un foyer s'éteint.</summary>
    // ponytail : jusqu'à 16³ GetBlock par pièce et par seconde ; passer par un cache de positions de sources invalidé sur ChunkDirty si ça pèse.
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

    /// <summary>null si la région n'est pas chargée : l'appelant garde la valeur précédente.</summary>
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
        var sb = new StringBuilder();
        sb.AppendLine($"Pièce : {e.Temperature:0.0} °C, extérieur {e.OutsideTemperature:0.0} °C");
        sb.AppendLine($"Volume {g.Volume} blocs, capacité {g.Volume * AirVolumetricCapacity * config.AirCapacityFactor / 1000:0} kJ/K");
        sb.AppendLine($"Sources : {e.SourceWatts:0} W");
        sb.AppendLine($"Pertes : {g.Conductance:0.0} W/K, soit {g.Conductance * dT:0} W à l'écart actuel");
        foreach (var (mat, w) in g.Walls.OrderByDescending(w => w.Value.WPerK))
            sb.AppendLine($"  {mat} : {w.Faces} faces, {w.WPerK:0.0} W/K, {w.WPerK * dT:0} W");
        if (g.Openings > 0)
        {
            double w = g.Openings * config.OpeningConductance;
            sb.AppendLine($"  Ouvertures : {g.Openings} faces, {w:0.0} W/K, {w * dT:0} W");
        }
        report = sb.ToString().TrimEnd();
        return true;
    }
}
