using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NuclearOptionCommander;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
public sealed class CommanderPlugin : BaseUnityPlugin
{
    internal static CommanderPlugin? Instance { get; private set; }
    internal static ManualLogSource Log => Instance!.Logger;

    private Harmony? harmony;

    private void Awake()
    {
        Instance = this;
        CommanderSettings.Initialize(Config);
        if (!CommanderSettings.ModEnabled)
        {
            Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} is disabled in configuration.");
            return;
        }

        harmony = new Harmony(PluginInfo.Guid);
        harmony.PatchAll();
        gameObject.AddComponent<StandaloneBuildingPlacementController>();
        Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} loaded");
    }

    private void OnDestroy()
    {
        if (harmony != null)
        {
            harmony.UnpatchSelf();
            harmony = null;
        }

        Instance = null;
    }
}
