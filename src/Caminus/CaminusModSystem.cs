using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Caminus;

public class CaminusModSystem : ModSystem
{
    public override void Start(ICoreAPI api)
    {
        api.Logger.Notification("Caminus {0} loaded ({1})", Mod.Info.Version, api.Side);
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
                    return api.ModLoader.GetModSystem<RoomThermalSystem>().TryGetReport(pos, out string report)
                        ? TextCommandResult.Success(report)
                        : TextCommandResult.Success("No enclosed room here.");
                })
            .EndSubCommand();
    }
}
