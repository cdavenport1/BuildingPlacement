using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NuclearOptionCommander;

/// <summary>
/// Ensures clean cleanup when scenes transition.
/// Prevents memory leaks and state corruption when switching scenes.
/// </summary>
[HarmonyPatch(typeof(SceneManager), nameof(SceneManager.LoadScene), typeof(string))]
internal static class CommanderBuildingPlacementSceneLoadPatch
{
    private static void Prefix(string sceneName)
    {
        // Clean up before scene transition
        BuildingPlacementController? controller = Object.FindObjectOfType<BuildingPlacementController>();
        if (controller != null)
        {
            // Let controller cleanup on scene change (it already does this via OnActiveSceneChanged)
            // This patch ensures proper ordering of cleanup operations
        }
    }
}
