using System.IO;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Xunit;
using Xunit.Sdk;

namespace Caminus.Scenarios;

/// <summary>
/// Generator for the ready-made houses in <c>templates/</c>. Each scenario builds one house in the
/// air of a scratch world, exports it with the game's own <see cref="BlockSchematic"/> serializer
/// (the same one behind <c>/we export</c>) and re-imports it 30 blocks higher, checking a few
/// blocks: that round trip is the proof that the file WorldEdit will read is correct.
/// </summary>
public class TemplateScenarios : AtlasScenarioBase
{
    private const string Stone = "game:cobblestone-granite";
    private const string Wood = "game:planks-oak-ud";
    private const string Air = "game:air";

    // The chimney block carrying the Chimney behavior: chimneycourse.json declares a single
    // "four" course state, so the placeable code is claybrickchimney-four-<type>-<orientation>.
    private const string Chimney = "game:claybrickchimney-four-red-ns";

    // A door is a 1×2 multiblock: the block itself plus multiblock-monolithic-<dx>-<dy>-<dz> above
    // (BlockBehaviorDoor.placeMultiblockParts). SetBlock does not run placement logic, so the
    // schematic has to carry both halves, and the facing lives in the block entity, not the code.
    private const string Door = "game:door-solid-oak";
    private const string DoorTop = "game:multiblock-monolithic-0-p1-0";

    private const string Ladder = "game:ladder-wood-oak-north";
    private const string Trapdoor = "game:trapdoor-solid-oak-1"; // airtightByType: trapdoor-solid-*-1
    private const string Chest = "game:chest-east";
    private const string Bread = "game:bread-spelt-perfect";
    private const string Lava = "game:lava-still-7";

    // A full glass block: solid, so the room stays enclosed, and material Glass, so Caminus reads it
    // as a poorly insulating wall the sun shines on. Panes have no horizontal plane (GetRetention
    // returns 0 up and down), which is why the glass house's roof is this and not a pane.
    private const string Glass = "game:glass-plain";

    // BlockGlassPane.GetRetention returns 1 only across its own plane: "ns" seals travel along the
    // north/south axis, "ew" along east/west. The wall on the north side of a room is crossed
    // northwards, so it needs "ns"; the wall on the east side needs "ew".
    private const string PaneNS = "game:glasspane-leaded-aged-ns";
    private const string PaneEW = "game:glasspane-leaded-aged-ew";

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Cabin_stone()
    {
        BlockPos min = await Site("TplCabinStone", 2000, 5, 9, 5);
        Cabin(min, Stone, chimney: true);
        LightFirepit(min.Offset(2, 1, 2));
        Door_(min.Offset(2, 1, 4), BlockFacing.SOUTH);

        string path = await Export("cabin-stone", min, 5, 9, 5);
        BlockPos copy = min.Offset(0, 30, 0);
        RoundTrip(path, copy,
            (0, 0, 0, Stone), (2, 1, 2, "game:firepit-lit"), (2, 1, 4, Door), (2, 2, 4, DoorTop),
            (2, 4, 2, Chimney), (2, 8, 2, Chimney), (1, 1, 1, Air));
        // The block entity data made the round trip too, not just the block codes.
        Assert.True(World.BlockEntityAt<BlockEntityFirepit>(copy.Offset(2, 1, 2))?.IsBurning, "the imported firepit is out");
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Cabin_wood()
    {
        BlockPos min = await Site("TplCabinWood", 2200, 5, 5, 5);
        Cabin(min, Wood, chimney: false);
        LightFirepit(min.Offset(2, 1, 2));
        Door_(min.Offset(2, 1, 4), BlockFacing.SOUTH);

        string path = await Export("cabin-wood", min, 5, 5, 5);
        RoundTrip(path, min.Offset(0, 30, 0),
            (0, 0, 0, Wood), (2, 4, 2, Wood), (2, 1, 2, "game:firepit-lit"), (2, 1, 4, Door));
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Cabin_windows()
    {
        BlockPos min = await Site("TplCabinWindow", 2400, 5, 9, 5);
        Cabin(min, Stone, chimney: true);
        LightFirepit(min.Offset(2, 1, 2));
        Door_(min.Offset(2, 1, 4), BlockFacing.SOUTH);
        // Two holes at floor level in the north wall. Real air: Caminus counts them as openings and
        // the draft comes in through them, low and cold, while the chimney pulls out high and warm.
        World.SetBlock(Air, min.Offset(1, 1, 0));
        World.SetBlock(Air, min.Offset(3, 1, 0));

        string path = await Export("cabin-windows", min, 5, 9, 5);
        RoundTrip(path, min.Offset(0, 30, 0),
            (1, 1, 0, Air), (3, 1, 0, Air), (2, 1, 0, Stone), (2, 4, 2, Chimney));
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Glass_house()
    {
        BlockPos min = await Site("TplGlassHouse", 2600, 5, 5, 5);
        Clear(min, 5, 5, 5);
        for (int x = 0; x < 5; x++)
            for (int z = 0; z < 5; z++)
            {
                World.SetBlock(Stone, min.Offset(x, 0, z)); // floor slab
                World.SetBlock(Glass, min.Offset(x, 4, z)); // roof
                for (int y = 1; y <= 3; y++)
                {
                    // The corners are crossed by nothing (the flood fill only walks faces), so they
                    // take the north/south pane for looks.
                    if (z == 0 || z == 4) World.SetBlock(PaneNS, min.Offset(x, y, z));
                    else if (x == 0 || x == 4) World.SetBlock(PaneEW, min.Offset(x, y, z));
                    else World.SetBlock(Air, min.Offset(x, y, z));
                }
            }

        string path = await Export("glass-house", min, 5, 5, 5);
        RoundTrip(path, min.Offset(0, 30, 0),
            (2, 1, 0, PaneNS), (0, 1, 2, PaneEW), (2, 4, 2, Glass), (2, 0, 2, Stone), (2, 1, 2, Air));
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Cellar()
    {
        BlockPos min = await Site("TplCellar", 2800, 5, 10, 5);
        Cellar_(min, lava: false);

        string path = await Export("cellar", min, 5, 10, 5);
        BlockPos copy = min.Offset(0, 30, 0);
        RoundTrip(path, copy,
            (2, 1, 2, Air), (1, 1, 1, Chest), (2, 4, 2, Ladder), (2, 8, 2, Ladder), (2, 9, 2, Trapdoor),
            (0, 9, 0, Stone));
        Assert.Equal(Bread, World.BlockEntityAt<BlockEntityGenericTypedContainer>(copy.Offset(1, 1, 1))
            ?.Inventory[0].Itemstack?.Collectible.Code.ToString());
        // Hinged on the block below, so the hatch lies flat and seals the shaft when closed.
        Assert.Equal(BlockFacing.DOWN.Index, World.Api.World.BlockAccessor.GetBlockEntity(copy.Offset(2, 9, 2))
            ?.GetBehavior<BEBehaviorTrapDoor>()?.AttachedFace);
    }

    [AtlasScenario(TimeoutMs = 180_000)]
    public async Task Cellar_lava()
    {
        BlockPos min = await Site("TplCellarLava", 3000, 5, 14, 5);
        Cellar_(min, lava: true);

        string path = await Export("cellar-lava", min, 5, 14, 5);
        RoundTrip(path, min.Offset(0, 30, 0),
            (2, 5, 2, Air), (1, 5, 1, Chest), (2, 13, 2, Trapdoor), (2, 0, 2, Stone), (2, 3, 2, Stone));
        // Lava lives in the fluid layer, which BlockAt does not read.
        Assert.Equal(Lava, World.Api.World.BlockAccessor.GetBlock(min.Offset(2, 31, 2), 2).Code.ToString());
    }

    [AtlasScenario(TimeoutMs = 300_000)]
    public async Task Hall()
    {
        const int Side = 22; // 20×20 interior
        BlockPos min = await Site("TplHall", 3200, Side, 10, Side);
        Clear(min, Side, 10, Side);
        for (int x = 0; x < Side; x++)
            for (int z = 0; z < Side; z++)
            {
                World.SetBlock(Stone, min.Offset(x, 0, z));
                if (x != 10 || z != 10) World.SetBlock(Stone, min.Offset(x, 5, z)); // the chimney goes through
                for (int y = 1; y <= 4; y++)
                    if (x == 0 || x == Side - 1 || z == 0 || z == Side - 1)
                        World.SetBlock(Stone, min.Offset(x, y, z));
            }
        LightFirepit(min.Offset(10, 1, 10));
        for (int i = 0; i < 5; i++) World.SetBlock(Chimney, min.Offset(10, 5 + i, 10));
        Door_(min.Offset(10, 1, 0), BlockFacing.NORTH);
        Door_(min.Offset(11, 1, Side - 1), BlockFacing.SOUTH);

        string path = await Export("hall", min, Side, 10, Side);
        RoundTrip(path, min.Offset(0, 30, 0),
            (10, 1, 10, "game:firepit-lit"), (10, 9, 10, Chimney), (10, 1, 0, Door),
            (11, 2, Side - 1, DoorTop), (10, 3, 10, Air), (0, 0, 0, Stone));
    }

    // ---- building blocks ------------------------------------------------------------------

    /// <summary>
    /// Joins a player, drops it on the ground under the future house so the server loads the chunks,
    /// and returns the bottom north-west corner of the build box, 30 blocks up in clear air.
    /// </summary>
    private async Task<BlockPos> Site(string playerName, int dx, int sx, int sy, int sz)
    {
        ITestPlayer player = await World.JoinPlayer(playerName);
        await player.TeleportTo(World.Spawn.Offset(dx + sx / 2, 0, 40 + sz / 2));
        BlockPos min = World.Spawn.Offset(dx, 30, 40);

        // SetBlock into a chunk the server has not loaded does nothing at all, silently. The wait
        // covers the corners of the build box and of the copy the round trip places 30 blocks up.
        IBlockAccessor acc = World.Api.World.BlockAccessor;
        int[] xs = [-1, sx], zs = [-1, sz], ys = [-1, sy, sy + 30, sy + 30 + sy];
        await World.Until(() => xs.All(x => zs.All(z => ys.All(y => acc.GetChunkAtBlockPos(min.Offset(x, y, z)) != null))), 1800);
        return min;
    }

    /// <summary>
    /// Empties the whole build box. The export skips air, so whatever the box still holds when it is
    /// read (a tree on a hillside, a stray rock) would be baked into the template.
    /// </summary>
    private void Clear(BlockPos min, int sx, int sy, int sz)
    {
        for (int x = 0; x < sx; x++)
            for (int y = 0; y < sy; y++)
                for (int z = 0; z < sz; z++)
                    World.SetBlock(Air, min.Offset(x, y, z));
    }

    /// <summary>5×5 shell around a 3×3×3 interior: floor slab at y=0, walls y=1..3, roof y=4.</summary>
    private void Cabin(BlockPos min, string wall, bool chimney)
    {
        Clear(min, 5, chimney ? 9 : 5, 5);
        for (int x = 0; x < 5; x++)
            for (int z = 0; z < 5; z++)
            {
                World.SetBlock(wall, min.Offset(x, 0, z));
                if (!chimney || x != 2 || z != 2) World.SetBlock(wall, min.Offset(x, 4, z));
                for (int y = 1; y <= 3; y++)
                    if (x == 0 || x == 4 || z == 0 || z == 4) World.SetBlock(wall, min.Offset(x, y, z));
            }
        // Five courses starting in the roof hole, so four of them stand above the ridge: that is the
        // draw the mod reads from the stack height.
        if (chimney) for (int i = 0; i < 5; i++) World.SetBlock(Chimney, min.Offset(2, 4 + i, 2));
    }

    /// <summary>
    /// Cellar: 3×3×3 stone room, a 1×1 ladder shaft up to a trapdoor at the top layer, everything
    /// else solid rock so the box can be dropped straight into the ground. With <paramref
    /// name="lava"/>, four more layers underneath hold a sealed 3×3×1 lava pocket.
    /// </summary>
    private void Cellar_(BlockPos min, bool lava)
    {
        int b = lava ? 4 : 0; // height of the sub-basement, i.e. the Y of the cellar floor slab
        Clear(min, 5, b + 10, 5);
        if (lava)
        {
            // Sealed pocket: rock under it, a rock ring around it, two courses of rock over it, so
            // the lava sits exactly three blocks below the cellar floor slab.
            Fill(min, 0, 0, Stone);
            Fill(min, 2, 3, Stone);
            for (int x = 0; x < 5; x++)
                for (int z = 0; z < 5; z++)
                    if (x == 0 || x == 4 || z == 0 || z == 4) World.SetBlock(Stone, min.Offset(x, 1, z));
            for (int x = 1; x <= 3; x++)
                for (int z = 1; z <= 3; z++)
                    World.SetBlock(Lava, min.Offset(x, 1, z));
        }
        Fill(min, b, b, Stone);
        for (int x = 0; x < 5; x++)
            for (int z = 0; z < 5; z++)
                for (int y = b + 1; y <= b + 3; y++)
                    if (x == 0 || x == 4 || z == 0 || z == 4) World.SetBlock(Stone, min.Offset(x, y, z));
        // Ceiling and shaft in one go: solid rock but for the well at the centre. The trapdoor caps
        // the top layer, which is the one that has to end up level with the ground.
        for (int x = 0; x < 5; x++)
            for (int z = 0; z < 5; z++)
                for (int y = b + 4; y <= b + 9; y++)
                    World.SetBlock(x == 2 && z == 2 ? (y == b + 9 ? Trapdoor : Ladder) : Stone, min.Offset(x, y, z));

        FlatTrapdoor(min.Offset(2, b + 9, 2));
        StockedChest(min.Offset(1, b + 1, 1));
    }

    private void Fill(BlockPos min, int y0, int y1, string code)
    {
        for (int x = 0; x < 5; x++)
            for (int z = 0; z < 5; z++)
                for (int y = y0; y <= y1; y++)
                    World.SetBlock(code, min.Offset(x, y, z));
    }

    /// <summary>Lit firepit that will not go out: IsBurning only looks at fuelBurnTime.</summary>
    private void LightFirepit(BlockPos pos)
    {
        World.SetBlock("game:firepit-lit", pos);
        BlockEntityFirepit fire = World.BlockEntityAt<BlockEntityFirepit>(pos)
            ?? throw new XunitException($"no BlockEntityFirepit at {pos}");
        fire.maxFuelBurnTime = fire.fuelBurnTime = 1_000_000f;
        fire.MarkDirty(true);
    }

    /// <summary>Door plus its upper multiblock half, turned so its closed face looks outward.</summary>
    private void Door_(BlockPos pos, BlockFacing outward)
    {
        World.SetBlock(Door, pos);
        World.SetBlock(DoorTop, pos.Offset(0, 1, 0));
        BEBehaviorDoor door = World.Api.World.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorDoor>()
            ?? throw new XunitException($"no BEBehaviorDoor at {pos}");
        door.RotateYRad = YawFor(outward);
        door.Blockentity.MarkDirty(true);
    }

    /// <summary>
    /// BEBehaviorDoor stores its facing as a yaw and reads it back through
    /// BlockFacing.HorizontalFromYaw, so the yaw is looked up rather than guessed.
    /// </summary>
    private static float YawFor(BlockFacing facing)
    {
        for (int i = 0; i < 4; i++)
        {
            float yaw = i * (float)System.Math.PI / 2f;
            if (BlockFacing.HorizontalFromYaw(yaw) == facing) return yaw;
        }
        throw new XunitException($"no quarter turn faces {facing}");
    }

    /// <summary>
    /// Hatch lying flat, hinged on the block below. AttachedFace is what BEBehaviorTrapDoor turns
    /// into facingWhenClosed (the opposite face for a vertical one), and a closed airtight trapdoor
    /// retains heat, which is what keeps the cellar sealed.
    /// </summary>
    private void FlatTrapdoor(BlockPos pos)
    {
        BEBehaviorTrapDoor hatch = World.Api.World.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorTrapDoor>()
            ?? throw new XunitException($"no BEBehaviorTrapDoor at {pos}");
        hatch.AttachedFace = BlockFacing.DOWN.Index;
        hatch.Blockentity.MarkDirty(true);
    }

    private void StockedChest(BlockPos pos)
    {
        World.SetBlock(Chest, pos);
        BlockEntityGenericTypedContainer chest = World.BlockEntityAt<BlockEntityGenericTypedContainer>(pos)
            ?? throw new XunitException($"no chest block entity at {pos}");
        Item bread = World.Api.World.GetItem(new AssetLocation(Bread))
            ?? throw new XunitException($"no item {Bread}");
        chest.Inventory[0].Itemstack = new ItemStack(bread, 8);
        chest.Inventory[0].MarkDirty();
        chest.MarkDirty(true);
    }

    // ---- export and round trip ------------------------------------------------------------

    /// <summary>
    /// Serializes the build box with the engine's own exporter and writes it to <c>templates/</c>.
    /// The replace mode is forced to ReplaceAll: the format stores no air at all, so only a mode
    /// that blanks the whole cuboid first gives a hollow house when it lands in solid ground.
    /// </summary>
    private async Task<string> Export(string name, BlockPos min, int sx, int sy, int sz)
    {
        await World.Ticks(5); // block entities settle (door halves, chest inventory)

        BlockSchematic schematic = new BlockSchematic();
        schematic.AddArea(World.Api.World, min, min.Offset(sx, sy, sz)); // end corner is exclusive
        Assert.True(schematic.Pack(World.Api.World, min), $"{name} is too large to pack");
        schematic.ReplaceMode = EnumReplaceMode.ReplaceAll;
        Assert.Equal((sx, sy, sz), (schematic.SizeX, schematic.SizeY, schematic.SizeZ));

        string path = Path.Combine(TemplatesDir(), name + ".json");
        File.WriteAllText(path, schematic.ToJson());
        return path;
    }

    /// <summary>Places the file back into the world and checks it landed as it was built.</summary>
    private void RoundTrip(string path, BlockPos origin, params (int dx, int dy, int dz, string code)[] expected)
    {
        Assert.True(File.Exists(path), $"{path} was not written");
        Assert.True(World.PlaceSchematic(path, origin) > 0, $"{path} placed nothing");
        foreach ((int dx, int dy, int dz, string code) in expected)
        {
            BlockPos at = origin.Offset(dx, dy, dz);
            Assert.Equal(code, World.BlockAt(at).Code.ToString());
        }
    }

    /// <summary>
    /// templates/ at the repo root, found by walking up from this assembly. Not from
    /// AppContext.BaseDirectory: the embedded server sets that to the game folder.
    /// </summary>
    private static string TemplatesDir()
    {
        string from = Path.GetDirectoryName(typeof(TemplateScenarios).Assembly.Location)!;
        DirectoryInfo? dir = new DirectoryInfo(from);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Caminus.slnx"))) dir = dir.Parent;
        if (dir == null) throw new XunitException($"no Caminus.slnx above {from}");
        return Directory.CreateDirectory(Path.Combine(dir.FullName, "templates")).FullName;
    }
}
