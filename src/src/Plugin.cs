using BepInEx;
using BepInEx.Unity.Mono;

namespace HvrChinese;

/// <summary>
/// BepInEx entry point. Everything interesting happens in
/// <see cref="ChineseLocalization"/>, which is driven from here.
/// </summary>
[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
public class Plugin : BaseUnityPlugin
{
    private void Awake()
    {
        ChineseLocalization.Initialise(Logger, Config);
        ChineseFont.Initialise(Logger);
        GameTextLocalization.Initialise(Logger);
        SpriteOverride.Initialise(Logger);
    }

    private void Update()
    {
        ChineseLocalization.Tick();
        ChineseFont.Tick();
        GameTextLocalization.Tick();
        SpriteOverride.Tick();
    }
}

internal static class PluginInfo
{
    public const string Guid = "dev.holovsrobo.chineselocalization";
    public const string Name = "Holo vs Robo — Chinese Localization";
    public const string Version = "1.2.0";
}
