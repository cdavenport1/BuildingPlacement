using HarmonyLib;
using UnityEngine;

namespace NuclearOptionBuilder;

[HarmonyPatch(typeof(DynamicMap), "MapControls")]
internal static class BuilderBuildingPlacementMapControlsPatch
{
    private static bool Prefix(DynamicMap __instance)
    {
        // Get the map service to check if tactical map is active
        BuilderBuildingPlacementMapService? mapService = BuilderBuildingPlacementMapService.Instance;
        
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

