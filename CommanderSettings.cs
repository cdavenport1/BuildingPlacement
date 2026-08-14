using BepInEx.Configuration;
using System.Collections.Generic;
using UnityEngine;

namespace NuclearOptionCommander;

internal static class CommanderSettings
{
    private static ConfigFile? config;
    private static readonly Dictionary<string, ConfigEntryBase> entries = new();

    internal static float UiScale { get; set; } = 1.5f;
    internal static bool ModEnabled { get => Get("General", "Enabled", true); set => Set("General", "Enabled", value); }
    internal static bool LinkToExternalCommanderMode { get => Get("General", "LinkToExternalCommanderMode", true); set => Set("General", "LinkToExternalCommanderMode", value); }
    internal static string ExternalCommanderPluginGuid { get => Get("General", "ExternalCommanderPluginGuid", "com.nuclearoption.commander"); set => Set("General", "ExternalCommanderPluginGuid", value); }
    internal static string ExternalCommanderTypeName { get => Get("General", "ExternalCommanderTypeName", string.Empty); set => Set("General", "ExternalCommanderTypeName", value); }
    internal static string ExternalCommanderStateMemberName { get => Get("General", "ExternalCommanderStateMemberName", "IsCommanderModeActive"); set => Set("General", "ExternalCommanderStateMemberName", value); }
    internal static string ExternalCommanderInstanceMemberName { get => Get("General", "ExternalCommanderInstanceMemberName", string.Empty); set => Set("General", "ExternalCommanderInstanceMemberName", value); }
    internal static string ExternalCommanderExpectedValue { get => Get("General", "ExternalCommanderExpectedValue", string.Empty); set => Set("General", "ExternalCommanderExpectedValue", value); }
    internal static string ExternalCommanderButtonTypeName { get => Get("General", "ExternalCommanderButtonTypeName", "NuclearOptionCommander.CommanderFeatureGate"); set => Set("General", "ExternalCommanderButtonTypeName", value); }
    internal static string ExternalCommanderButtonInstanceMemberName { get => Get("General", "ExternalCommanderButtonInstanceMemberName", string.Empty); set => Set("General", "ExternalCommanderButtonInstanceMemberName", value); }
    internal static string ExternalCommanderButtonMemberName { get => Get("General", "ExternalCommanderButtonMemberName", "manuallyUnlocked"); set => Set("General", "ExternalCommanderButtonMemberName", value); }
    internal static string ExternalCommanderButtonExpectedValue { get => Get("General", "ExternalCommanderButtonExpectedValue", string.Empty); set => Set("General", "ExternalCommanderButtonExpectedValue", value); }
    internal static bool PlacementCalibrationInitialized { get => Get("Placement", "CalibrationInitialized", false); set => Set("Placement", "CalibrationInitialized", value); }
    internal static float PlacementOrientationOffsetDegrees { get => Get("Placement", "OrientationOffsetDegrees", 0f); set => Set("Placement", "OrientationOffsetDegrees", value); }
    internal static bool PlacementReverseScrollDirection { get => Get("Placement", "ReverseScrollDirection", false); set => Set("Placement", "ReverseScrollDirection", value); }
    internal static KeyboardShortcut PrimaryAction { get => GetShortcut("PrimaryAction", KeyCode.Mouse0, "Place a building or interact with the UI."); set => Set("Keybinds", "PrimaryAction", value); }
    internal static KeyboardShortcut CancelAction { get => GetShortcut("CancelAction", KeyCode.Escape, "Cancel building placement."); set => Set("Keybinds", "CancelAction", value); }

    internal static void Initialize(ConfigFile configFile)
    {
        config = configFile;
        _ = ModEnabled;
        _ = LinkToExternalCommanderMode;
        _ = ExternalCommanderPluginGuid;
        _ = ExternalCommanderTypeName;
        _ = ExternalCommanderStateMemberName;
        _ = ExternalCommanderInstanceMemberName;
        _ = ExternalCommanderExpectedValue;
        _ = ExternalCommanderButtonTypeName;
        _ = ExternalCommanderButtonInstanceMemberName;
        _ = ExternalCommanderButtonMemberName;
        _ = ExternalCommanderButtonExpectedValue;
        _ = PlacementCalibrationInitialized;
        _ = PlacementOrientationOffsetDegrees;
        _ = PlacementReverseScrollDirection;
        _ = PrimaryAction;
        _ = CancelAction;
    }

    private static KeyboardShortcut GetShortcut(string key, KeyCode defaultKey, string description)
    {
        if (config == null)
        {
            return new KeyboardShortcut(defaultKey);
        }

        string lookup = "Keybinds/" + key;
        if (entries.TryGetValue(lookup, out ConfigEntryBase existing))
        {
            return ((ConfigEntry<KeyboardShortcut>)existing).Value;
        }

        ConfigEntry<KeyboardShortcut> created = config.Bind(
            "Keybinds",
            key,
            new KeyboardShortcut(defaultKey),
            new ConfigDescription(description + " Set the main key to None to disable it."));
        entries.Add(lookup, created);
        return created.Value;
    }

    private static T Get<T>(string section, string key, T defaultValue)
    {
        ConfigEntry<T>? entry = GetEntry(section, key, defaultValue);
        return entry == null ? defaultValue : entry.Value;
    }

    private static void Set<T>(string section, string key, T value)
    {
        ConfigEntry<T>? entry = GetEntry(section, key, value);
        if (entry != null)
        {
            entry.Value = value;
        }
    }

    private static ConfigEntry<T>? GetEntry<T>(string section, string key, T defaultValue)
    {
        if (config == null)
        {
            return null;
        }

        string lookup = section + "/" + key;
        if (entries.TryGetValue(lookup, out ConfigEntryBase existing))
        {
            return (ConfigEntry<T>)existing;
        }

        ConfigEntry<T> created = config.Bind(section, key, defaultValue);
        entries.Add(lookup, created);
        return created;
    }
}
