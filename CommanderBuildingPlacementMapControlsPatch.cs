using HarmonyLib;
using UnityEngine;

namespace NuclearOptionCommander;

[HarmonyPatch(typeof(DynamicMap), "MapControls")]
internal static class CommanderBuildingPlacementMapControlsPatch
{
    private static bool Prefix(DynamicMap __instance)
    {
        // Get the map service to check if tactical map is active
        CommanderBuildingPlacementMapService? mapService = CommanderBuildingPlacementMapService.Instance;
        
        // If tactical map is NOT active, allow normal map controls
        if (mapService?.IsActive != true)
        {
            return true;
        }

        // Tactical map is active - only allow map controls if cursor is inside the tactical map
        if (__instance.IsCursorInMapRectangle())
        {
            return true; // Cursor in map, allow normal controls
        }

        // Cursor outside map - block map controls
        return false;
    }
}
