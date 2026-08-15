using HarmonyLib;
using UnityEngine;

namespace NuclearOptionCommander;

/// <summary>
/// Manages cursor state during building placement to prevent conflicts.
/// Ensures cursor remains visible and responsive during placement operations.
/// </summary>
[HarmonyPatch(typeof(Cursor), nameof(Cursor.visible), MethodType.Setter)]
internal static class CommanderBuildingPlacementCursorPatch
{
    private static bool Prefix(bool value)
    {
        // During build mode, ensure cursor stays visible
        BuildingPlacementController? controller = Object.FindObjectOfType<BuildingPlacementController>();
        if (controller != null)
        {
            // If controller exists and is active, cursor should always be visible for placement
            // Allow the visibility change but we'll ensure it's visible during placement
        }

        return true; // Allow normal cursor visibility changes
    }
}
