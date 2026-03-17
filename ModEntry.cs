using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace FirstMod;

[ModInitializer("ModLoaded")]
public static class ModEntry
{
    public static void ModLoaded()
    {
        Log.Warn("MOD FINISHED LOADING");
        Bridge.BridgeRuntime.Initialize();
    }
}
