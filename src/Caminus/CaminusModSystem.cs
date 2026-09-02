using Vintagestory.API.Common;
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
            .WithDescription("Caminus : simulation thermique du bâtiment")
            .RequiresPrivilege(Privilege.chat)
            .BeginSubCommand("version")
                .WithDescription("Version du mod")
                .HandleWith(_ => TextCommandResult.Success($"Caminus {Mod.Info.Version}"))
            .EndSubCommand()
            .BeginSubCommand("temp")
                .WithDescription("Bilan thermique de la pièce où vous êtes")
                .RequiresPlayer()
                .HandleWith(args =>
                    api.ModLoader.GetModSystem<RoomThermalSystem>().TryGetReport(args.Caller.Entity.Pos.AsBlockPos, out string report)
                        ? TextCommandResult.Success(report)
                        : TextCommandResult.Success("Pas de pièce détectée ici."))
            .EndSubCommand();
    }
}
