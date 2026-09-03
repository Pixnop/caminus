using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Caminus;

public class CaminusModSystem : ModSystem
{
    public override void Start(ICoreAPI api)
    {
        api.Logger.Notification("Caminus {0} loaded ({1})", Mod.Info.Version, api.Side);
        // Both sides, like SurvivalCoreSystem registers the vanilla one (SurvivalCoreSystem.cs:865):
        // the client half of the behavior drives the frost shader. The entity picks it up through
        // assets/caminus/patches/player-bodytemperature.json.
        api.RegisterEntityBehaviorClass("caminusbodytemperature", typeof(EntityBehaviorCaminusBodyTemperature));
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        api.ChatCommands.GetOrCreate("caminus")
            .WithDescription("Caminus: building thermal simulation")
            .RequiresPrivilege(Privilege.chat)
            .BeginSubCommand("version")
                .WithDescription("Mod version")
                .HandleWith(_ => TextCommandResult.Success($"Caminus {Mod.Info.Version}"))
            .EndSubCommand()
            .BeginSubCommand("temp")
                .WithDescription("Thermal report of the room you are in (or at the given position)")
                .WithArgs(api.ChatCommands.Parsers.OptionalWorldPosition("pos"))
                .HandleWith(args =>
                {
                    BlockPos? pos = (args[0] as Vec3d)?.AsBlockPos ?? (args.Caller.Entity == null ? null : RoomThermalSystem.EyeBlockPos(args.Caller.Entity));
                    if (pos == null) return TextCommandResult.Error("Position required from the console: /caminus temp x y z");
                    string body = args.Caller.Entity?.GetBehavior<EntityBehaviorCaminusBodyTemperature>()?.Describe() ?? "";
                    if (!api.ModLoader.GetModSystem<RoomThermalSystem>().TryGetReport(pos, out string report))
                        return TextCommandResult.Success(body.Length == 0 ? "No enclosed room here." : "No enclosed room here.\n" + body);
                    return TextCommandResult.Success(body.Length == 0 ? report : report + "\n" + body);
                })
            .EndSubCommand();
    }
}
