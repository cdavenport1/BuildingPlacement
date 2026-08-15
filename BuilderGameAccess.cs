using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;

namespace NuclearOptionBuilder;

internal static class BuilderGameAccess
{
    internal static FactionHQ? GetLocalHq()
    {
        return GameManager.GetLocalHQ(out FactionHQ localHq) ? localHq : null;
    }

    internal static bool TryRaycastWorldPosition(Vector2 screenPosition, out GlobalPosition position)
    {
        position = default;
        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null)
        {
            return false;
        }

        Ray ray = camera.ScreenPointToRay(screenPosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, 500000f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        position = GlobalPositionExtensions.ToGlobalPosition(hit.point);
        return true;
    }
}

