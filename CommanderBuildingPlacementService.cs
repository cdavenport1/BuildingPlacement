using System;
using System.Collections.Generic;
using System.Linq;
using NuclearOption.Networking;
using UnityEngine;

namespace NuclearOptionCommander;

internal sealed class CommanderBuildingPlacementService
{
    private const float MinimumBuildingCostMillions = 5f;
    private const float BuildDelayStepMinutes = 5f;
    private const float BaseMinimumBuildDelayMinutes = 5f;
    private const float BaseMaximumBuildDelayMinutes = 180f;
    private const float FinalMinimumBuildDelayMinutes = 0f;
    private const float FinalMaximumBuildDelayMinutes = 720f;
    private const float MinimumBuildTimeMultiplier = 0f;
    private const float MaximumBuildTimeMultiplier = 4f;
    private const float BuildTimeMultiplierStep = 0.25f;
    private const float OrientationStepDegrees = 5f;
    private const float RefreshIntervalSeconds = 10f;

    private readonly CommanderMapClickTracker placementClickTracker = new();
    private readonly List<BuildingDefinition> buildingDefinitions = new();
    private readonly List<QueuedPlacement> pendingPlacements = new();
    private bool awaitingPlacementSelection;
    private bool awaitingOrientationConfirmation;
    private GlobalPosition pendingPlacementTarget;
    private float pendingYawDegrees;
    private int selectedIndex;
    private float nextRefreshAt;
    private string statusText = string.Empty;
    private float buildTimeMultiplier = 1f;

    internal bool AwaitingPlacementSelection => awaitingPlacementSelection;

    internal IReadOnlyList<BuildingDefinition> BuildingDefinitions => buildingDefinitions;

    internal BuildingDefinition? SelectedDefinition => buildingDefinitions.Count == 0
        ? null
        : buildingDefinitions[Mathf.Clamp(selectedIndex, 0, buildingDefinitions.Count - 1)];

    internal string StatusText => statusText;

    internal string PreviewLabel => SelectedDefinition?.unitName ?? "BUILD";

    internal string BuildTimeSettingLabel => $"BUILD TIME x{buildTimeMultiplier:0.##}";

    internal string BuildTimePreviewLabel
    {
        get
        {
            BuildingDefinition? definition = SelectedDefinition;
            if (definition == null)
            {
                return "No building selected";
            }

            float baseMinutes = GetBuildDelayMinutes(GetDefinitionCost(definition));
            float adjustedMinutes = GetBuildDelayMinutesWithSetting(GetDefinitionCost(definition));
            return $"{baseMinutes:0}m -> {adjustedMinutes:0}m";
        }
    }

    internal bool TryGetPlacementOrientationPreview(out PlacementOrientationPreview preview)
    {
        preview = default;
        if (!awaitingPlacementSelection || !awaitingOrientationConfirmation)
        {
            return false;
        }

        preview = new PlacementOrientationPreview(pendingPlacementTarget, pendingYawDegrees);
        return true;
    }

    internal bool TryGetStructureOrientationPreview(
        out BuildingDefinition definition,
        out Vector3 localPosition,
        out Quaternion rotation)
    {
        definition = null!;
        localPosition = default;
        rotation = Quaternion.identity;
        if (!awaitingPlacementSelection || !awaitingOrientationConfirmation)
        {
            return false;
        }

        BuildingDefinition? selected = SelectedDefinition;
        if (selected == null || selected.unitPrefab == null)
        {
            return false;
        }

        definition = selected;
        localPosition = pendingPlacementTarget.ToLocalPosition() + selected.spawnOffset;
        rotation = Quaternion.Euler(0f, pendingYawDegrees, 0f);
        return true;
    }

    internal PendingPlacementStatus[] GetPendingPlacementStatuses()
    {
        if (pendingPlacements.Count == 0)
        {
            return Array.Empty<PendingPlacementStatus>();
        }

        PendingPlacementStatus[] statuses = new PendingPlacementStatus[pendingPlacements.Count];
        for (int i = 0; i < pendingPlacements.Count; i++)
        {
            QueuedPlacement queued = pendingPlacements[i];
            statuses[i] = new PendingPlacementStatus(
                queued.definition.unitName,
                queued.target,
                Mathf.Max(0f, queued.completeAt - Time.unscaledTime));
        }

        return statuses;
    }

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
        RefundPendingPlacements();
        awaitingPlacementSelection = false;
        awaitingOrientationConfirmation = false;
        pendingPlacementTarget = default;
        pendingYawDegrees = 0f;
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
            return;
        }

        DynamicMap? map = SceneSingleton<DynamicMap>.i;
        if (map == null)
        {
            return;
        }

        if (awaitingOrientationConfirmation)
        {
            float scrollDelta = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scrollDelta) > Mathf.Epsilon)
            {
                pendingYawDegrees = NormalizeYawDegrees(pendingYawDegrees + scrollDelta * OrientationStepDegrees);
            }

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                CompletePlacementSelection(pendingPlacementTarget, pendingYawDegrees);
            }

            return;
        }

        if (placementClickTracker.Tick(map, out GlobalPosition target))
        {
            BeginOrientationConfirmation(target);
        }
    }

    internal void TickPersistent()
    {
        ProcessPendingPlacements();

        if (!CommanderScheduler.IsDue(ref nextRefreshAt, RefreshIntervalSeconds))
        {
            return;
        }

        RefreshBuildingDefinitions();
    }

    internal string GetDefinitionLabel(BuildingDefinition definition)
    {
        string cost = FormatCostMillions(GetDefinitionCost(definition));
        return $"{definition.unitName}\n{definition.jsonKey}  |  {cost}";
    }

    internal bool CanAffordDefinition(BuildingDefinition definition)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        return hq == null || hq.factionFunds >= GetDefinitionCost(definition);
    }

    internal void AdjustBuildTimeMultiplier(bool increase)
    {
        float delta = increase ? BuildTimeMultiplierStep : -BuildTimeMultiplierStep;
        buildTimeMultiplier = Mathf.Clamp(
            buildTimeMultiplier + delta,
            MinimumBuildTimeMultiplier,
            MaximumBuildTimeMultiplier);
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

        awaitingPlacementSelection = true;
        placementClickTracker.Reset();
        SetStatus($"Select a location for {definition.unitName} on the tactical map or in the 3D world.");
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

        BeginOrientationConfirmation(target);
        return true;
    }

    internal void CancelPlacementSelection(bool showStatus = true)
    {
        if (!awaitingPlacementSelection)
        {
            return;
        }

        awaitingPlacementSelection = false;
        awaitingOrientationConfirmation = false;
        pendingPlacementTarget = default;
        pendingYawDegrees = 0f;
        placementClickTracker.Reset();
        CloseMap();
        if (showStatus)
        {
            SetStatus("Building placement cancelled.");
        }
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

    private void BeginOrientationConfirmation(GlobalPosition target)
    {
        awaitingOrientationConfirmation = true;
        pendingPlacementTarget = SnapConstructionTargetToTerrain(target);
        pendingYawDegrees = 0f;
        SetStatus("Scroll mouse wheel to rotate. Press Enter to confirm placement.");
    }

    private void CompletePlacementSelection(GlobalPosition target, float yawDegrees)
    {
        BuildingDefinition? definition = SelectedDefinition;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (definition == null || hq == null)
        {
            CancelPlacementSelection(showStatus: false);
            SetStatus("The building could not be spawned.", warning: true);
            return;
        }

        float cost = GetDefinitionCost(definition);
        if (hq.factionFunds < cost)
        {
            awaitingPlacementSelection = false;
            awaitingOrientationConfirmation = false;
            pendingPlacementTarget = default;
            pendingYawDegrees = 0f;
            placementClickTracker.Reset();
            SetStatus($"Insufficient faction funds. {definition.unitName} costs {FormatCostMillions(cost)}.", warning: true);
            return;
        }

        float buildDelayMinutes = GetBuildDelayMinutesWithSetting(cost);
        float buildDelaySeconds = buildDelayMinutes * 60f;
        float normalizedYaw = NormalizeYawDegrees(yawDegrees);
        Quaternion rotation = Quaternion.Euler(0f, normalizedYaw, 0f);

        hq.AddFunds(-cost);
        pendingPlacements.Add(new QueuedPlacement(
            definition,
            target,
            cost,
            Time.unscaledTime + buildDelaySeconds,
            $"NOC_BUILD_{definition.unitName}_{Time.frameCount}",
            rotation));

        awaitingPlacementSelection = false;
        awaitingOrientationConfirmation = false;
        pendingPlacementTarget = default;
        pendingYawDegrees = 0f;
        placementClickTracker.Reset();
        SetStatus($"{definition.unitName} queued ({FormatCostMillions(cost)}). Build time {buildDelayMinutes:0}m. Heading {Mathf.RoundToInt(normalizedYaw):000}.");
    }

    private void ProcessPendingPlacements()
    {
        if (pendingPlacements.Count == 0)
        {
            return;
        }

        for (int i = pendingPlacements.Count - 1; i >= 0; i--)
        {
            if (Time.unscaledTime < pendingPlacements[i].completeAt)
            {
                continue;
            }

            QueuedPlacement queued = pendingPlacements[i];
            pendingPlacements.RemoveAt(i);
            TryCompleteQueuedPlacement(queued);
        }
    }

    private void TryCompleteQueuedPlacement(QueuedPlacement queued)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (hq == null || spawner == null)
        {
            RefundQueuedPlacement(queued);
            SetStatus($"{queued.definition.unitName} build failed because game services are unavailable. Refunded {FormatCostMillions(queued.cost)}.", warning: true);
            return;
        }

        Vector3 localPosition = queued.target.ToLocalPosition() + queued.definition.spawnOffset;

        try
        {
            Unit unit = spawner.SpawnFromUnitDefinitionInEditor(
                queued.definition,
                localPosition.ToGlobalPosition(),
                queued.rotation,
                hq,
                queued.spawnName);
            if (unit == null)
            {
                throw new InvalidOperationException($"Spawner returned null for {queued.definition.unitName}.");
            }

            if (unit is GroundVehicle groundVehicle)
            {
                groundVehicle.SetHoldPosition(true);
            }
            else if (unit is Ship ship)
            {
                ship.SetHoldPosition(true);
            }

            SetStatus($"{queued.definition.unitName} completed for {FormatCostMillions(queued.cost)}.");
        }
        catch (Exception exception)
        {
            RefundQueuedPlacement(queued);
            CommanderPlugin.Log.LogError($"Building placement failed: {exception}");
            SetStatus($"{queued.definition.unitName} placement failed: {exception.Message}. Refunded {FormatCostMillions(queued.cost)}.", warning: true);
        }
    }

    private static void RefundQueuedPlacement(QueuedPlacement queued)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        hq?.AddFunds(queued.cost);
    }

    private void RefundPendingPlacements()
    {
        for (int i = 0; i < pendingPlacements.Count; i++)
        {
            RefundQueuedPlacement(pendingPlacements[i]);
        }

        pendingPlacements.Clear();
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

    private static float GetDefinitionCost(BuildingDefinition definition)
    {
        return definition.value <= 0f
            ? MinimumBuildingCostMillions
            : definition.value;
    }

    private static string FormatCostMillions(float value)
    {
        return "$" + value.ToString("0.##") + "m";
    }

    private static float GetBuildDelayMinutes(float costMillions)
    {
        float steppedMinutes = Mathf.Ceil(costMillions / BuildDelayStepMinutes) * BuildDelayStepMinutes;
        return Mathf.Clamp(steppedMinutes, BaseMinimumBuildDelayMinutes, BaseMaximumBuildDelayMinutes);
    }

    private float GetBuildDelayMinutesWithSetting(float costMillions)
    {
        float baseMinutes = GetBuildDelayMinutes(costMillions);
        float scaledMinutes = baseMinutes * buildTimeMultiplier;
        float steppedScaledMinutes = Mathf.Ceil(scaledMinutes);
        return Mathf.Clamp(steppedScaledMinutes, FinalMinimumBuildDelayMinutes, FinalMaximumBuildDelayMinutes);
    }

    private static float NormalizeYawDegrees(float yawDegrees)
    {
        yawDegrees %= 360f;
        return yawDegrees < 0f ? yawDegrees + 360f : yawDegrees;
    }

    private readonly struct QueuedPlacement
    {
        internal readonly BuildingDefinition definition;
        internal readonly GlobalPosition target;
        internal readonly float cost;
        internal readonly float completeAt;
        internal readonly string spawnName;
        internal readonly Quaternion rotation;

        internal QueuedPlacement(
            BuildingDefinition definition,
            GlobalPosition target,
            float cost,
            float completeAt,
            string spawnName,
            Quaternion rotation)
        {
            this.definition = definition;
            this.target = target;
            this.cost = cost;
            this.completeAt = completeAt;
            this.spawnName = spawnName;
            this.rotation = rotation;
        }
    }

    internal readonly struct PlacementOrientationPreview
    {
        internal readonly GlobalPosition target;
        internal readonly float yawDegrees;
        internal readonly bool hasDirection;

        internal PlacementOrientationPreview(GlobalPosition target, float yawDegrees)
        {
            this.target = target;
            this.yawDegrees = yawDegrees;
            hasDirection = true;
        }
    }

    internal readonly struct PendingPlacementStatus
    {
        internal readonly string name;
        internal readonly GlobalPosition target;
        internal readonly float remainingSeconds;

        internal PendingPlacementStatus(string name, GlobalPosition target, float remainingSeconds)
        {
            this.name = name;
            this.target = target;
            this.remainingSeconds = remainingSeconds;
        }
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