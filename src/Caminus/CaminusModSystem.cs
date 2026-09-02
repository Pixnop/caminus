using Vintagestory.API.Common;

namespace Caminus;

public class CaminusModSystem : ModSystem
{
    public override void Start(ICoreAPI api)
    {
        api.Logger.Notification("Caminus {0} loaded ({1})", Mod.Info.Version, api.Side);
    }
}
