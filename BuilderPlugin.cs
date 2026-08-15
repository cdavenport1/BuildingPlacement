using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NuclearOptionBuilder;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
public sealed class BuilderPlugin : BaseUnityPlugin
{
    internal static BuilderPlugin? Instance { get; private set; }
    internal static ManualLogSource Log => Instance!.Logger;

    private Harmony? harmony;

    private void Awake()
    {
        Instance = this;
        BuilderSettings.Initialize(Config);
        if (!BuilderSettings.ModEnabled)
        {
            Logger.LogInfo($"{PluginInfo.Name} {PluginInfo.Version} is disabled in configuration.");
            return;
        }

        harmony = new Harmony(PluginInfo.Guid);
        harmony.PatchAll();
        gameObject.AddComponent<BuildingPlacementController>();
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

