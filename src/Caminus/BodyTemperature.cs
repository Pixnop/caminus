using System.Globalization;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Caminus;

/// <summary>
/// Vanilla body temperature with the room's real air temperature in place of the flat +1 °C per
/// hour every enclosed room used to grant.
///
/// <para><c>EntityBehaviorBodyTemperature.updateBodyTemperature</c> is protected but NOT virtual and
/// almost all of its state is private, so there is nothing to override: the whole update is ported
/// here and <see cref="OnGameTick"/> replaces the vanilla one without calling it. Every block below
/// carries the line number it was ported from in
/// <c>VSSurvivalMod/Vintagestory.GameContent/EntityBehaviorBodyTemperature.cs</c> (1.22.7 decompile),
/// so the next game version can be diffed line by line.</para>
///
/// <para>Only two things differ from vanilla, both on the l.266 formula:
/// the air temperature is the room node's when Caminus tracks the room, and the
/// <c>inEnclosedRoom ? 1f : ...</c> flat bonus is gone. Indoors the player now gets the same comfort
/// term vanilla applies outdoors, minus the wind. Nothing else is retuned.</para>
///
/// <para>The class registry uses <c>Dictionary.Add</c> (ClassRegistry.cs:442), so "bodytemperature"
/// cannot be re-registered with our type: the swap happens in
/// <c>assets/caminus/patches/player-bodytemperature.json</c>.</para>
/// </summary>
public class EntityBehaviorCaminusBodyTemperature : EntityBehaviorBodyTemperature
{
    private const string ColdIdle = "coldidle";
    private const string ColdIdleHeld = "coldidleheld";

    // Vanilla keeps all of this private (l.17-54). Re-declared, same names, same initial values.
    private ICoreAPI api = null!;
    private readonly EntityAgent? eagent;
    private float accum;
    private float slowaccum;
    private float veryslowaccum;
    private readonly BlockPos plrpos = new(0);
    private readonly BlockPos tmpPos = new(0);
    private bool inEnclosedRoom;
    private float tempChange;
    private float clothingBonus;
    private float damagingFreezeHours;
    private int sprinterCounter;
    private double lastWearableHoursTotalUpdate;
    private float bodyTemperatureResistance;
    private ICachingBlockAccessor? blockAccess;
    private bool firstTick;
    private long lastMoveMs;

    /// <summary>Null client side: <see cref="RoomThermalSystem"/> is a server-only mod system.</summary>
    private RoomThermalSystem? thermal;

    /// <summary>The air temperature the last update actually used, room node or climate.</summary>
    private float airTemperature;

    public EntityBehaviorCaminusBodyTemperature(Entity entity) : base(entity) => eagent = entity as EntityAgent;

    public override string PropertyName() => "caminusbodytemperature";

    /// <summary>One line for <c>/caminus temp</c> and the overlay HUD.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"Body: {CurBodyTemperature:0.0} °C (air {airTemperature:0.0} °C, radiant {nearHeatSourceStrength:0.0}, clothing {clothingBonus:+0.0;-0.0;+0.0})");

    // vanilla l.125-144. Base still sets NormalBodyTemperature and creates the bodyTemp tree, which
    // the public properties read; only the private state has to be duplicated here.
    public override void Initialize(EntityProperties properties, JsonObject typeAttributes)
    {
        // vanilla l.131-143: the world config is read ONLY on the branch where the tree already
        // existed, so a brand new entity runs its first session with a resistance of 0. Kept as is.
        bool hadTree = entity.WatchedAttributes.GetTreeAttribute("bodyTemp") != null;
        base.Initialize(properties, typeAttributes);
        api = entity.World.Api;
        blockAccess = api.World.GetCachingBlockAccessor(synchronize: false, relight: false);
        thermal = api.ModLoader.GetModSystem<RoomThermalSystem>();
        if (hadTree) bodyTemperatureResistance = entity.World.Config.GetString("bodyTemperatureResistance").ToFloat();
    }

    // vanilla l.146-150. Base disposes its own accessor, ours is separate.
    public override void OnEntityDespawn(EntityDespawnData despawn)
    {
        base.OnEntityDespawn(despawn);
        blockAccess?.Dispose();
        blockAccess = null;
    }

    // vanilla l.152-214, verbatim.
    public override void OnGameTick(float deltaTime)
    {
        if (!firstTick && api.Side == EnumAppSide.Client && entity.Properties.Client.Renderer is EntityShapeRenderer renderer)
        {
            renderer.getFrostAlpha = () =>
            {
                float temperature = api.World.BlockAccessor.GetClimateAt(entity.Pos.AsBlockPos,
                    EnumGetClimateMode.ForSuppliedDate_TemperatureOnly, api.World.Calendar.TotalDays).Temperature;
                float num = GameMath.Clamp((NormalBodyTemperature - CurBodyTemperature) / 4f - 0.5f, 0f, 1f);
                return GameMath.Clamp((Math.Max(0f, 0f - temperature) - 5f) / 5f, 0f, 1f) * num;
            };
        }
        firstTick = true;
        updateFreezingAnimState();
        accum += deltaTime;
        slowaccum += deltaTime;
        veryslowaccum += deltaTime;
        plrpos.Set((int)entity.Pos.X, (int)entity.Pos.Y, (int)entity.Pos.Z);
        plrpos.SetDimension(entity.Pos.Dimension);
        if (veryslowaccum > 10f && damagingFreezeHours > 3f)
        {
            if (api.World.Config.GetString("harshWinters").ToBool(defaultValue: true))
            {
                entity.ReceiveDamage(new DamageSource
                {
                    DamageTier = 0,
                    Source = EnumDamageSource.Weather,
                    Type = EnumDamageType.Frost
                }, 0.2f);
            }
            veryslowaccum = 0f;
            if (eagent!.Controls.Sprint) sprinterCounter = GameMath.Clamp(sprinterCounter + 1, 0, 10);
            else sprinterCounter = GameMath.Clamp(sprinterCounter - 1, 0, 10);
        }
        if (slowaccum > 3f)
        {
            if (api.Side == EnumAppSide.Server)
            {
                try
                {
                    nearHeatSourceStrength = getNearHeatSourceStrength();
                }
                catch (Exception e)
                {
                    api.Logger.Warning("Exception thrown while calculating near heat source strength for {0} at {1}:", entity.GetName(), entity.Pos?.XYZ);
                    api.Logger.Error(e);
                    return;
                }
            }
            updateWearableConditions();
            entity.WatchedAttributes.MarkPathDirty("bodyTemp");
            slowaccum = 0f;
        }
        if (accum > 1f && api.Side == EnumAppSide.Server) updateBodyTemperature();
    }

    // vanilla l.216-307. The two Caminus changes are marked inline; everything else is the port.
    private new void updateBodyTemperature()
    {
        EntityPlayer? entityPlayer = entity as EntityPlayer;
        IPlayer? player = entityPlayer?.Player;
        if (api.Side == EnumAppSide.Server && player is not IServerPlayer { ConnectionState: EnumClientState.Playing }) return;
        if (player != null && player.WorldData.CurrentGameMode is EnumGameMode.Creative or EnumGameMode.Spectator)
        {
            CurBodyTemperature = NormalBodyTemperature;
            entity.WatchedAttributes.SetFloat("freezingEffectStrength", 0f);
            return;
        }
        if (player != null && (entityPlayer!.Controls.TriesToMove || entityPlayer.Controls.Jump
            || entityPlayer.Controls.LeftMouseDown || entityPlayer.Controls.RightMouseDown))
        {
            lastMoveMs = entity.World.ElapsedMilliseconds;
        }
        ClimateCondition climateAt = api.World.BlockAccessor.GetClimateAt(plrpos);
        if (climateAt == null) return;
        Vec3d windSpeedAt = api.World.BlockAccessor.GetWindSpeedAt(plrpos);
        bool flag = api.World.BlockAccessor.GetRainMapHeightAt(plrpos) <= plrpos.Y;

        // CAMINUS CHANGE 1 (vanilla l.259 reads climateAt.Temperature): inside a room the mod
        // tracks, the air is the room node's at the player's own eye height. Rain and wetness below
        // keep using the climate: they are about the sky, not the air.
        // comfortOffsetK shifts the room air the comfort formula below sees, so a server owner can
        // move vanilla's neutral point (bodyTemperatureResistance, 0 °C) without touching code.
        double roomAir = 0;
        bool tracked = thermal != null && thermal.TryGetLocalTemperature(RoomThermalSystem.EyeBlockPos(entity), out roomAir);
        airTemperature = tracked ? (float)(roomAir + thermal!.ComfortOffsetK) : climateAt.Temperature;

        // vanilla l.245-255: rain wetness, head slot protection, drying by nearby fire.
        float num = climateAt.Rainfall * (flag ? 0.06f : 0f) * ((climateAt.Temperature < -1f) ? 0.05f : 1f);
        if (num > 0f && entityPlayer != null)
        {
            ItemSlot? itemSlot = entityPlayer.Player.InventoryManager.GetOwnInventory("character")
                ?.FirstOrDefault(slot => (slot as ItemSlotCharacter)?.Type == EnumCharacterDressType.Head);
            if (itemSlot != null && !itemSlot.Empty)
                num *= GameMath.Clamp(1f - itemSlot.Itemstack.ItemAttributes["rainProtectionPerc"].AsFloat(), 0f, 1f);
        }
        Wetness = GameMath.Clamp(Wetness + num + (entity.Swimming ? 1 : 0)
            - (float)Math.Max(0.0, (api.World.Calendar.TotalHours - LastWetnessUpdateTotalHours) * GameMath.Clamp(nearHeatSourceStrength, 1f, 2f)), 0f, 1f);
        LastWetnessUpdateTotalHours = api.World.Calendar.TotalHours;
        accum = 0f;

        // vanilla l.257-265: sprint bonus, wetness penalty, comfort term against the resistance.
        float num2 = sprinterCounter / 2f;
        float num3 = (float)Math.Max(0.0, (double)Wetness - 0.1) * 15f;
        float num4 = airTemperature + clothingBonus + num2 - num3;
        float num5 = num4 - GameMath.Clamp(num4, bodyTemperatureResistance, 50f);
        if (num5 == 0f) num5 = Math.Max(num4 - bodyTemperatureResistance, 0f);
        float num6 = GameMath.Clamp(num5 / 6f, -6f, 6f);

        // CAMINUS CHANGE 2. Vanilla l.266 adds a flat 1 per hour in any enclosed room instead of the
        // comfort term, and subtracts the wind only outdoors. The flat +1/h is gone, so a room gets the same comfort term as the outdoors, computed on
        // its own air. The wind still only bites where vanilla let it: outside any enclosure.
        float wind = (float)Math.Max((windSpeedAt.Length() - 0.15) * 2.0, 0.0);
        tempChange = nearHeatSourceStrength + num6 - (inEnclosedRoom || tracked ? 0f : wind);

        // vanilla l.267-282, unchanged (sleeping and being on fire).
        EntityBehaviorTiredness? behavior = entity.GetBehavior<EntityBehaviorTiredness>();
        if (behavior != null && behavior.IsSleeping)
        {
            if (inEnclosedRoom) tempChange = GameMath.Clamp(NormalBodyTemperature - CurBodyTemperature, -0.15f, 0.15f);
            else if (!flag) tempChange += GameMath.Clamp(NormalBodyTemperature - CurBodyTemperature, 1f, 1f);
        }
        if (entity.IsOnFire) tempChange = Math.Max(25f, tempChange);

        // vanilla l.283-306: integrate over the game hours elapsed, then the freezing bookkeeping.
        float num7 = (float)(api.World.Calendar.TotalHours - BodyTempUpdateTotalHours);
        if (!((double)num7 > 0.01)) return;
        if ((double)tempChange < -0.5 || tempChange > 0f)
        {
            if ((double)tempChange > 0.5) tempChange *= 2f;
            CurBodyTemperature = GameMath.Clamp(CurBodyTemperature + tempChange * num7, 31f, 45f);
        }
        BodyTempUpdateTotalHours = api.World.Calendar.TotalHours;
        float value = GameMath.Clamp((NormalBodyTemperature - CurBodyTemperature) / 4f - 0.5f, 0f, 1f);
        entity.WatchedAttributes.SetFloat("freezingEffectStrength", value);
        if (NormalBodyTemperature - CurBodyTemperature > 4f) damagingFreezeHours += num7;
        else damagingFreezeHours = 0f;
    }

    // vanilla l.309-342, verbatim. The scan already takes the whole room bbox when enclosed, which
    // is exactly the radiant term milestone 3 wants, so it is ported without a change.
    private float getNearHeatSourceStrength()
    {
        Room roomForPosition = api.ModLoader.GetModSystem<RoomRegistry>().GetRoomForPosition(plrpos);
        inEnclosedRoom = roomForPosition.ExitCount == 0 || roomForPosition.SkylightCount < roomForPosition.NonSkylightCount;
        float strength = 0f;
        double px = entity.Pos.X;
        double py = entity.Pos.Y + 0.9;
        double pz = entity.Pos.Z;
        double proximityPower = inEnclosedRoom ? 0.875 : 1.25;
        BlockPos minPos, maxPos;
        if (inEnclosedRoom && roomForPosition.Location.SizeX >= 1 && roomForPosition.Location.SizeY >= 1 && roomForPosition.Location.SizeZ >= 1)
        {
            minPos = new BlockPos(roomForPosition.Location.MinX, roomForPosition.Location.MinY, roomForPosition.Location.MinZ);
            maxPos = new BlockPos(roomForPosition.Location.MaxX, roomForPosition.Location.MaxY, roomForPosition.Location.MaxZ);
        }
        else
        {
            minPos = plrpos.AddCopy(-3, -3, -3);
            maxPos = plrpos.AddCopy(3, 3, 3);
        }
        tmpPos.SetDimension(plrpos.dimension);
        blockAccess!.Begin();
        blockAccess.WalkBlocks(minPos, maxPos, (block, x, y, z) =>
        {
            IHeatSource? heatSource = block.GetInterface<IHeatSource>(api.World, tmpPos.Set(x, y, z));
            if (heatSource != null)
            {
                float num = Math.Min(1f, 9f / (8f + (float)Math.Pow(tmpPos.DistanceSqToNearerEdge(px, py, pz), proximityPower)));
                strength += heatSource.GetHeatStrength(api.World, tmpPos, plrpos) * num;
            }
        });
        return strength;
    }

    // vanilla l.344-367, verbatim.
    private void updateFreezingAnimState()
    {
        float num = entity.WatchedAttributes.GetFloat("freezingEffectStrength");
        bool flag = (entity as EntityAgent)?.LeftHandItemSlot?.Itemstack != null || (entity as EntityAgent)?.RightHandItemSlot?.Itemstack != null;
        EnumGameMode? enumGameMode = (entity as EntityPlayer)?.Player?.WorldData?.CurrentGameMode;
        if ((damagingFreezeHours > 0f || (double)num > 0.4) && enumGameMode != EnumGameMode.Creative && enumGameMode != EnumGameMode.Spectator && entity.Alive)
        {
            if (flag)
            {
                entity.StartAnimation(ColdIdleHeld);
                entity.StopAnimation(ColdIdle);
            }
            else
            {
                entity.StartAnimation(ColdIdle);
                entity.StopAnimation(ColdIdleHeld);
            }
        }
        else if (entity.AnimManager.IsAnimationActive(ColdIdle) || entity.AnimManager.IsAnimationActive(ColdIdleHeld))
        {
            entity.StopAnimation(ColdIdle);
            entity.StopAnimation(ColdIdleHeld);
        }
    }

    // vanilla l.375-414, verbatim: warmth of the worn clothes and their wear from moving.
    private void updateWearableConditions()
    {
        double num = api.World.Calendar.TotalHours - lastWearableHoursTotalUpdate;
        if (num < -1.0)
        {
            lastWearableHoursTotalUpdate = api.World.Calendar.TotalHours;
            return;
        }
        if (num < 0.5) return;
        clothingBonus = 0f;
        float changeVal = 0f;
        if (entity.World.ElapsedMilliseconds - lastMoveMs <= 3000) changeVal = (0f - (float)num) / 1296f;
        EntityBehaviorPlayerInventory? inv = eagent?.GetBehavior<EntityBehaviorPlayerInventory>();
        if (inv?.Inventory != null)
        {
            foreach (ItemSlot item in inv.Inventory)
            {
                if (item.Empty) continue;
                IWearable? wearable = item.Itemstack.Collectible.GetCollectibleInterface<IWearable>();
                IWearableStatsSupplier? stats = item.Itemstack.Collectible.GetCollectibleInterface<IWearableStatsSupplier>();
                if (wearable != null && stats != null && !stats.IsArmorType(item))
                {
                    clothingBonus += wearable.GetWarmth(item);
                    wearable.ChangeCondition(item, changeVal);
                }
            }
        }
        lastWearableHoursTotalUpdate = api.World.Calendar.TotalHours;
    }
}
