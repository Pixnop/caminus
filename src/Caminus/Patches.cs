using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Caminus;

/// <summary>
/// The only Harmony patch in Caminus. Harmony 2.4.2 ships with the game (Lib folder); the
/// instance is created in RoomThermalSystem.StartPre and unpatched in its Dispose.
/// The ModDB mod "Fix Perish Rate" patches this same method (it corrects the sea-level line).
/// A postfix composes with it: whatever prefix or transpiler ran first, the returned value is
/// only overwritten for containers standing in a room Caminus actually tracks, and every other
/// container keeps the result the rest of the chain produced.
/// </summary>
[HarmonyPatch(typeof(InWorldContainer), nameof(InWorldContainer.GetPerishRate))]
public static class PerishRatePatch
{
    // positionProvider is protected and no property exposes it. InventoryBase.Pos exists but is not
    // set by every container, so read the field itself: one reflected accessor, resolved once.
    // The name is qualified because Vintagestory.API.Common declares a different delegate of the
    // same name (returning Vec3d instead of BlockPos).
    private static readonly AccessTools.FieldRef<InWorldContainer, Vintagestory.GameContent.PositionProviderDelegate> Position =
        AccessTools.FieldRefAccess<InWorldContainer, Vintagestory.GameContent.PositionProviderDelegate>("positionProvider");

    public static void Postfix(InWorldContainer __instance, ref float __result)
    {
        // Singleplayer runs both sides in one process and statics are shared: only the server may
        // read the room table, which the server main thread mutates.
        ICoreAPI? api = __instance.Inventory?.Api;
        if (api?.Side != EnumAppSide.Server) return;
        BlockPos? pos = Position(__instance)?.Invoke();
        if (pos != null && api.ModLoader.GetModSystem<RoomThermalSystem>().TryGetPerishRate(pos, out float rate)) __result = rate;
    }
}
