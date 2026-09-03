using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Caminus;

/// <summary>
/// The air of one room: the box that holds it, and which blocks of that box belong to it.
/// Caminus's answer to the vanilla <c>Room</c>, which stops at 14 blocks per axis and hands back an
/// open volume as soon as the walls have a hole in them.
/// </summary>
public sealed class RoomVolume(Cuboidi bounds, BlockPos seed, bool[] air, int sizeX, int sizeY, int sizeZ)
{
    /// <summary>Bounding box, both ends inclusive, like <c>Room.Location</c>.</summary>
    public Cuboidi Bounds { get; } = bounds;
    /// <summary>An air block of the room: where a rescan starts from.</summary>
    public BlockPos Seed { get; } = seed;

    public bool Contains(BlockPos pos)
    {
        int x = pos.X - Bounds.MinX, y = pos.Y - Bounds.MinY, z = pos.Z - Bounds.MinZ;
        return x >= 0 && y >= 0 && z >= 0 && x < sizeX && y < sizeY && z < sizeZ
               && air[(x * sizeY + y) * sizeZ + z];
    }
}

/// <summary>
/// Caminus's own room flood fill, replacing the vanilla one for the thermal model (the vanilla
/// <c>RoomRegistry</c> is untouched and keeps deciding greenhouses and cellars).
/// Two rules differ from vanilla and they are the whole point of this class:
/// a hall wider than 14 blocks is still a room, and a hole in a wall is an opening the model can
/// price rather than a reason to give up on the room.
/// Server main thread only, like vanilla: the visited set and the caching accessor are reused
/// between scans and nothing here is locked.
/// </summary>
public sealed class RoomScanner(ICoreServerAPI api, int maxBlocks, int maxExtent) : IDisposable
{
    /// <summary>Room-relative positions of the current fill, packed by <see cref="Index"/>.</summary>
    private readonly HashSet<int> visited = [];
    private readonly Queue<int> queue = new();
    /// <summary>Half-width of the packing box. Clamped so that its cube still fits an int.</summary>
    private readonly int extent = Math.Clamp(maxExtent, 1, 256);
    private ICachingBlockAccessor? cache;
    // State of the fill in progress, kept out of the call chain like the queue and the visited set.
    private BlockPos scanSeed = new(0);
    private BlockPos scanNb = new(0);
    private Cuboidi scanBox = new(0, 0, 0, 0, 0, 0);

    private int Span => 2 * extent + 1;

    /// <summary>
    /// True when nothing above this position stops rain. <c>RainHeightMap</c> holds the Y of the
    /// topmost rain-impermeable block of the column and is rewritten on every block placement
    /// (<c>BlockAccessorBase.UpdateRainHeightMap</c>), so a roof a player just put up counts and the
    /// air under it stops seeing the sky. An unloaded map chunk answers 0, i.e. "open to the sky",
    /// which stops the fill instead of letting it run into blocks nobody has loaded.
    /// </summary>
    public static bool SeesSky(IBlockAccessor acc, BlockPos pos) => acc.GetRainMapHeightAt(pos) <= pos.Y;

    /// <summary>
    /// Flood fill of the air around <paramref name="seed"/>. null when this is not a room: the seed
    /// itself is open to the sky, or the fill ran past <c>maxRoomBlocks</c> or <c>maxRoomExtent</c>,
    /// which is what "outdoors" looks like from the inside of a flood fill.
    /// </summary>
    public RoomVolume? Scan(BlockPos seed)
    {
        ICachingBlockAccessor acc = cache ??= api.World.GetCachingBlockAccessor(synchronize: false, relight: false);
        acc.Begin();
        if (!acc.IsValidPos(seed) || SeesSky(acc, seed)) return null;

        visited.Clear();
        queue.Clear();
        visited.Add(Index(0, 0, 0));
        queue.Enqueue(Index(0, 0, 0));
        scanSeed = seed;
        scanBox = new Cuboidi(0, 0, 0, 0, 0, 0);
        scanNb = new BlockPos(seed.dimension);
        var cur = new BlockPos(seed.dimension);

        while (queue.Count > 0)
        {
            Decode(queue.Dequeue(), out int dx, out int dy, out int dz);
            Block here = acc.GetBlock(cur.Set(seed.X + dx, seed.Y + dy, seed.Z + dz));
            foreach (BlockFacing face in BlockFacing.ALLFACES)
            {
                // Vanilla (RoomRegistry.cs:398) first asks the block we are leaving whether its own
                // face retains heat: a slab floor stops the fill even though its top is walkable air.
                if (here.Id != 0 && here.GetRetention(cur, face, EnumRetentionType.Heat) != 0) continue;
                if (!Visit(acc, dx + face.Normali.X, dy + face.Normali.Y, dz + face.Normali.Z, face)) return null;
            }
        }
        return Build(seed, scanBox);
    }

    /// <summary>Crosses into one neighbour if the fill may. False when a budget is exceeded.</summary>
    private bool Visit(ICachingBlockAccessor acc, int nx, int ny, int nz, BlockFacing face)
    {
        if (!Traversable(acc, scanNb.Set(scanSeed.X + nx, scanSeed.Y + ny, scanSeed.Z + nz), face)) return true;
        if (!Grow(scanBox, nx, ny, nz)) return false;
        int index = Index(nx, ny, nz);
        if (!visited.Add(index)) return true;
        if (visited.Count > maxBlocks) return false;
        queue.Enqueue(index);
        return true;
    }

    /// <summary>
    /// Whether the fill crosses into that neighbour. The retention test is the vanilla criterion
    /// (RoomRegistry.cs:418), so a closed door, a glass pane or a chiseled block holds the room shut
    /// exactly as it does for the game's own rooms; air open to the sky is where a room ends and the
    /// weather begins, and the caller reads that face back as an opening.
    /// </summary>
    private static bool Traversable(ICachingBlockAccessor acc, BlockPos nb, BlockFacing face) =>
        acc.IsValidPos(nb)
        && acc.GetBlock(nb).GetRetention(nb, face.Opposite, EnumRetentionType.Heat) == 0
        && !SeesSky(acc, nb);

    /// <summary>Stretches the box over one more position. False once an axis is past the budget.</summary>
    private bool Grow(Cuboidi box, int x, int y, int z)
    {
        box.X1 = Math.Min(box.X1, x); box.X2 = Math.Max(box.X2, x);
        box.Y1 = Math.Min(box.Y1, y); box.Y2 = Math.Max(box.Y2, y);
        box.Z1 = Math.Min(box.Z1, z); box.Z2 = Math.Max(box.Z2, z);
        // Cuboidi.SizeX is MaxX - MinX with no +1, so a run of n blocks measures n - 1. Staying
        // under `extent` blocks per axis is also what keeps every offset inside the packing box.
        return box.SizeX < extent && box.SizeY < extent && box.SizeZ < extent;
    }

    private RoomVolume Build(BlockPos seed, Cuboidi box)
    {
        int sizeX = box.SizeX + 1, sizeY = box.SizeY + 1, sizeZ = box.SizeZ + 1;
        var air = new bool[sizeX * sizeY * sizeZ];
        foreach (int index in visited)
        {
            Decode(index, out int dx, out int dy, out int dz);
            air[((dx - box.X1) * sizeY + (dy - box.Y1)) * sizeZ + (dz - box.Z1)] = true;
        }
        var bounds = new Cuboidi(seed.X + box.X1, seed.Y + box.Y1, seed.Z + box.Z1,
                                 seed.X + box.X2, seed.Y + box.Y2, seed.Z + box.Z2);
        return new RoomVolume(bounds, seed.Copy(), air, sizeX, sizeY, sizeZ);
    }

    private int Index(int x, int y, int z) => ((x + extent) * Span + y + extent) * Span + z + extent;

    private void Decode(int index, out int x, out int y, out int z)
    {
        z = index % Span - extent;
        y = index / Span % Span - extent;
        x = index / Span / Span - extent;
    }

    public void Dispose()
    {
        cache?.Dispose();
        cache = null;
    }
}
