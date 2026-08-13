using System;
using System.Collections.Generic;
using System.Linq;
using NuclearOption.Networking;
using UnityEngine;

namespace NuclearOptionCommander;

internal sealed class CommanderBuildingPlacementService
{
    private const float RefreshIntervalSeconds = 10f;

    private readonly CommanderMapClickTracker placementClickTracker = new();
    private readonly List<BuildingDefinition> buildingDefinitions = new();
    private bool awaitingPlacementSelection;
    private int selectedIndex;
    private float nextRefreshAt;
    private string statusText = string.Empty;

    internal static CommanderBuildingPlacementService? Instance { get; private set; }

    internal CommanderBuildingPlacementService()
    {
        Instance = this;
    }

    internal bool AwaitingPlacementSelection => awaitingPlacementSelection;

    internal IReadOnlyList<BuildingDefinition> BuildingDefinitions => buildingDefinitions;

    internal BuildingDefinition? SelectedDefinition => buildingDefinitions.Count == 0
        ? null
        : buildingDefinitions[Mathf.Clamp(selectedIndex, 0, buildingDefinitions.Count - 1)];

    internal string StatusText => statusText;

    internal string PreviewLabel => SelectedDefinition?.unitName ?? "BUILD";

    internal void Activate()
    {
        RefreshBuildingDefinitions();
        nextRefreshAt = CommanderScheduler.Stagger("building-placement", RefreshIntervalSeconds, 0.4f);
    }

    internal void Deactivate()
    {
        CancelPlacementSelection(showStatus: false);
    }

    internal void ResetSession()
    {
        awaitingPlacementSelection = false;
        placementClickTracker.Reset();
        buildingDefinitions.Clear();
        selectedIndex = 0;
        nextRefreshAt = 0f;
        statusText = string.Empty;
    }

    internal void TickActive()
    {
        if (!awaitingPlacementSelection)
        {
            return;
        }

        if (CommanderGameInput.CancelDown)
        {
            CancelPlacementSelection(showStatus: true);
            return;
        }

        if (!DynamicMap.mapMaximized)
        {
            CancelPlacementSelection(showStatus: true);
            return;
        }

        DynamicMap? map = SceneSingleton<DynamicMap>.i;
        if (map == null)
        {
            CancelPlacementSelection(showStatus: true);
            SetStatus("Could not open the tactical map.", warning: true);
            return;
        }

        if (placementClickTracker.Tick(map, out GlobalPosition target))
        {
            CompletePlacementSelection(target);
        }
    }

    internal void TickPersistent()
    {
        if (!CommanderScheduler.IsDue(ref nextRefreshAt, RefreshIntervalSeconds))
        {
            return;
        }

        RefreshBuildingDefinitions();
    }

    internal string GetDefinitionLabel(BuildingDefinition definition)
    {
        string cost = definition.value.ToString("F0");
        return $"{definition.unitName}\n{definition.jsonKey}  |  {cost}";
    }

    internal void SelectDefinition(int index)
    {
        if (index >= 0 && index < buildingDefinitions.Count)
        {
            selectedIndex = index;
        }
    }

    internal void BeginPlacementSelection()
    {
        BuildingDefinition? definition = SelectedDefinition;
        if (definition == null)
        {
            SetStatus("No building blueprints are available.", warning: true);
            return;
        }

        if (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            SetStatus("Building placement is host-only.", warning: true);
            return;
        }

        if (!OpenMap())
        {
            SetStatus("Could not open the tactical map.", warning: true);
            return;
        }

        awaitingPlacementSelection = true;
        placementClickTracker.Reset();
        SetStatus($"Select a location for {definition.unitName}.");
    }

    internal bool TrySetPlacementFromWorld(Vector2 screenPosition)
    {
        if (!awaitingPlacementSelection)
        {
            return false;
        }

        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition target))
        {
            SetStatus("No valid terrain position was found.");
            return true;
        }

        CompletePlacementSelection(target);
        return true;
    }

    internal void CancelPlacementSelection(bool showStatus = true)
    {
        if (!awaitingPlacementSelection)
        {
            return;
        }

        awaitingPlacementSelection = false;
        placementClickTracker.Reset();
        CloseMap();
        if (showStatus)
        {
            SetStatus("Building placement cancelled.");
        }
    }

    private static bool OpenMap()
    {
        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap == null)
        {
            return false;
        }

        DynamicMap.AllowedToOpen = true;
        if (!DynamicMap.mapMaximized)
        {
            dynamicMap.Maximize();
        }

        return DynamicMap.mapMaximized;
    }

    private static void CloseMap()
    {
        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap != null && DynamicMap.mapMaximized)
        {
            dynamicMap.Minimize();
        }
    }

    private void RefreshBuildingDefinitions()
    {
        buildingDefinitions.Clear();
        BuildingDefinition[] available = Resources.FindObjectsOfTypeAll<BuildingDefinition>()
            .Where(definition => definition != null && definition.unitPrefab != null)
            .Distinct()
            .OrderBy(definition => definition.unitName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.jsonKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        buildingDefinitions.AddRange(available);

        if (buildingDefinitions.Count == 0)
        {
            selectedIndex = 0;
            if (string.IsNullOrWhiteSpace(statusText))
            {
                statusText = "No building blueprints were found.";
            }
            return;
        }

        selectedIndex = Mathf.Clamp(selectedIndex, 0, buildingDefinitions.Count - 1);
    }

    private void CompletePlacementSelection(GlobalPosition target)
    {
        BuildingDefinition? definition = SelectedDefinition;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (definition == null || hq == null || spawner == null)
        {
            CancelPlacementSelection(showStatus: false);
            SetStatus("The building could not be spawned.", warning: true);
            return;
        }

        GlobalPosition snappedTarget = SnapConstructionTargetToTerrain(target);
        Vector3 localPosition = snappedTarget.ToLocalPosition() + definition.spawnOffset;

        try
        {
            Unit unit = spawner.SpawnFromUnitDefinitionInEditor(
                definition,
                localPosition.ToGlobalPosition(),
                Quaternion.identity,
                hq,
                $"NOC_BUILD_{definition.unitName}_{Time.frameCount}");
            if (unit == null)
            {
                throw new InvalidOperationException($"Spawner returned null for {definition.unitName}.");
            }

            if (unit is GroundVehicle groundVehicle)
            {
                groundVehicle.SetHoldPosition(true);
            }
            else if (unit is Ship ship)
            {
                ship.SetHoldPosition(true);
            }

            SetStatus($"{definition.unitName} placed at the selected location.");
        }
        catch (Exception exception)
        {
            CommanderPlugin.Log.LogError($"Building placement failed: {exception}");
            SetStatus($"{definition.unitName} placement failed: {exception.Message}", warning: true);
        }
        finally
        {
            awaitingPlacementSelection = false;
            placementClickTracker.Reset();
            CloseMap();
        }
    }

    private static GlobalPosition SnapConstructionTargetToTerrain(GlobalPosition target)
    {
        Vector2[] offsets =
        {
            Vector2.zero,
            new(3f, 0f),
            new(-3f, 0f),
            new(0f, 3f),
            new(0f, -3f),
            new(6f, 0f),
            new(-6f, 0f),
            new(0f, 6f),
            new(0f, -6f),
            new(8f, 8f),
            new(-8f, 8f),
            new(8f, -8f),
            new(-8f, -8f)
        };

        for (int i = 0; i < offsets.Length; i++)
        {
            Vector3 local = new GlobalPosition(
                target.x + offsets[i].x,
                target.y,
                target.z + offsets[i].y).ToLocalPosition();
            Vector3 origin = new(local.x, Datum.LocalSeaY + 10000f, local.z);
            if (GameAssets.i == null)
            {
                continue;
            }

            RaycastHit[] hits = Physics.RaycastAll(
                origin,
                Vector3.down,
                20000f,
                PhysicsLayers.StaticsMask,
                QueryTriggerInteraction.Ignore);
            float highestTerrainY = float.MinValue;
            Vector3 terrainPoint = default;
            for (int hitIndex = 0; hitIndex < hits.Length; hitIndex++)
            {
                RaycastHit hit = hits[hitIndex];
                if (hit.collider != null
                    && hit.collider.sharedMaterial == GameAssets.i.terrainMaterial
                    && hit.point.y > highestTerrainY)
                {
                    highestTerrainY = hit.point.y;
                    terrainPoint = hit.point;
                }
            }

            if (highestTerrainY > float.MinValue)
            {
                return terrainPoint.ToGlobalPosition();
            }
        }

        return target;
    }

    private void SetStatus(string value, bool warning = false)
    {
        statusText = value;
        if (warning)
        {
            CommanderPlugin.Log.LogWarning(value);
        }
        else
        {
            CommanderPlugin.Log.LogInfo(value);
        }
    }
}