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
    private const float FinalMaximumBuildDelayMinutes = 15f;
    private const float MinimumBuildTimeMultiplier = 0f;
    private const float MaximumBuildTimeMultiplier = 4f;
    private const float BuildTimeMultiplierStep = 0.25f;
    private const float OrientationStepDegrees = 5f;  // 5-degree intervals for granular control
    private const float RefreshIntervalSeconds = 10f;

    private readonly CommanderMapClickTracker placementClickTracker = new();
    private readonly CommanderPlacementCursorState placementCursorState = new();
    private readonly List<PlaceableDefinition> placeableDefinitions = new();
    private readonly List<QueuedPlacement> pendingPlacements = new();
    private bool awaitingPlacementSelection;
    private bool awaitingOrientationConfirmation;
    private GlobalPosition pendingPlacementTarget;
    private float pendingYawDegrees;
    private int selectedIndex;
    private float nextRefreshAt;
    private string statusText = string.Empty;
    private float buildTimeMultiplier = 1f;
    private BuildingDefinition? ammoDumpDefinition;
    private Unit? previewAmmoDump;

    internal bool AwaitingPlacementSelection => awaitingPlacementSelection;

    internal IReadOnlyList<PlaceableDefinition> PlaceableDefinitions => placeableDefinitions;

    internal IReadOnlyList<PlaceableDefinition> VisibleDefinitions
    {
        get
        {
            if (CommanderSettings.PlacementShowShips)
            {
                return placeableDefinitions;
            }

            // Filter out ships
            return placeableDefinitions.Where(d => !d.IsShip).ToList();
        }
    }

    internal PlaceableDefinition? SelectedDefinition => placeableDefinitions.Count == 0
        ? null
        : placeableDefinitions[Mathf.Clamp(selectedIndex, 0, placeableDefinitions.Count - 1)];

    internal string StatusText => statusText;

    internal string PreviewLabel => SelectedDefinition?.UnitName ?? "BUILD";

    internal string BuildTimeSettingLabel => $"BUILD TIME x{buildTimeMultiplier:0.##}";

    internal string BuildTimePreviewLabel
    {
        get
        {
            PlaceableDefinition? definition = SelectedDefinition;
            if (definition == null)
            {
                return "No building selected";
            }

            float baseMinutes = GetBuildDelayMinutes(definition.Value);
            float adjustedMinutes = GetBuildDelayMinutesWithSetting(definition.Value);
            return $"{baseMinutes:0}m -> {adjustedMinutes:0}m";
        }
    }

    internal string OrientationOffsetLabel => $"ORIENTATION OFFSET {Mathf.RoundToInt(NormalizeYawDegrees(CommanderSettings.PlacementOrientationOffsetDegrees)):000}";

    internal string OrientationScrollLabel => CommanderSettings.PlacementReverseScrollDirection
        ? "SCROLL DIRECTION REVERSED"
        : "SCROLL DIRECTION NORMAL";

    internal bool TryGetPlacementOrientationPreview(out PlacementOrientationPreview preview)
    {
        preview = default;
        if (!awaitingPlacementSelection || !awaitingOrientationConfirmation)
        {
            return false;
        }

        preview = new PlacementOrientationPreview(pendingPlacementTarget, GetCalibratedPlacementYawDegrees(pendingYawDegrees));
        return true;
    }

    internal bool TryGetStructureOrientationPreview(
        out PlaceableDefinition definition,
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

        PlaceableDefinition? selected = SelectedDefinition;
        if (selected == null || selected.UnitPrefab == null)
        {
            return false;
        }

        definition = selected;
        localPosition = pendingPlacementTarget.ToLocalPosition() + selected.SpawnOffset;
        rotation = BuildPlacementRotation(pendingYawDegrees);
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
            float remainingSeconds = queued.timerStarted
                ? Mathf.Max(0f, queued.completeAt - Time.unscaledTime)
                : -1f;
            float radius = GetJackknifeActivationRadius(queued.definition);
            statuses[i] = new PendingPlacementStatus(
                queued.definition.UnitName,
                queued.target,
                remainingSeconds,
                radius);
        }

        return statuses;
    }

    internal void Activate()
    {
        EnsurePlacementCalibrationDefaults();
        RefreshBuildingDefinitions();
        nextRefreshAt = CommanderScheduler.Stagger("building-placement", RefreshIntervalSeconds, 0.4f);
    }

    internal void Deactivate()
    {
        placementCursorState.Deactivate();
        CancelPlacementSelection(showStatus: false);
    }

    internal void ResetSession()
    {
        DestroyPreviewAmmoDump();
        RefundPendingPlacements();
        awaitingPlacementSelection = false;
        awaitingOrientationConfirmation = false;
        pendingPlacementTarget = default;
        pendingYawDegrees = 0f;
        placementClickTracker.Reset();
        placementCursorState.Deactivate();
        placeableDefinitions.Clear();
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

        placementCursorState.Tick();

        if (CommanderGameInput.CancelDown)
        {
            CancelPlacementSelection(showStatus: true);
            return;
        }

        DynamicMap? map = SceneSingleton<DynamicMap>.i;

        if (awaitingOrientationConfirmation)
        {
            float scrollDelta = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scrollDelta) > Mathf.Epsilon)
            {
                float signedScrollDelta = CommanderSettings.PlacementReverseScrollDirection ? -scrollDelta : scrollDelta;
                pendingYawDegrees = NormalizeYawDegrees(pendingYawDegrees + signedScrollDelta * OrientationStepDegrees);
                UpdatePreviewAmmoDumpRotation();
            }

            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                CompletePlacementSelection(pendingPlacementTarget, pendingYawDegrees);
            }

            return;
        }

        if (map != null && placementClickTracker.Tick(map, out GlobalPosition target))
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

    internal string GetDefinitionLabel(PlaceableDefinition definition)
    {
        string cost = FormatCostMillions(definition.Value);
        string type = definition.IsShip ? "Ship" : definition.BuildingDefinition?.jsonKey ?? "Building";
        return $"{definition.UnitName}\n{type}  |  {cost}";
    }

    internal bool CanAffordDefinition(PlaceableDefinition definition)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        return hq == null || hq.factionFunds >= definition.Value;
    }

    internal void AdjustBuildTimeMultiplier(bool increase)
    {
        float delta = increase ? BuildTimeMultiplierStep : -BuildTimeMultiplierStep;
        buildTimeMultiplier = Mathf.Clamp(
            buildTimeMultiplier + delta,
            MinimumBuildTimeMultiplier,
            MaximumBuildTimeMultiplier);
    }

    internal void AdjustOrientationOffset(float deltaDegrees)
    {
        CommanderSettings.PlacementOrientationOffsetDegrees = NormalizeYawDegrees(
            CommanderSettings.PlacementOrientationOffsetDegrees + deltaDegrees);
    }

    internal void SetOrientationOffset(float absoluteDegrees)
    {
        CommanderSettings.PlacementOrientationOffsetDegrees = NormalizeYawDegrees(absoluteDegrees);
    }

    internal void ToggleOrientationScrollDirection()
    {
        CommanderSettings.PlacementReverseScrollDirection = !CommanderSettings.PlacementReverseScrollDirection;
    }

    internal void ResetOrientationCalibration()
    {
        CommanderSettings.PlacementOrientationOffsetDegrees = 0f;
        CommanderSettings.PlacementReverseScrollDirection = false;
    }

    internal void SelectDefinition(int index)
    {
        if (index >= 0 && index < placeableDefinitions.Count)
        {
            selectedIndex = index;
        }
    }

    internal void BeginPlacementSelection()
    {
        PlaceableDefinition? definition = SelectedDefinition;
        if (definition == null)
        {
            SetStatus("No building or ship blueprints are available.", warning: true);
            return;
        }

        if (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            SetStatus("Building placement is host-only.", warning: true);
            return;
        }

        awaitingPlacementSelection = true;
        placementClickTracker.Reset();
        placementCursorState.Activate();
        SetStatus($"Select a location for {definition.UnitName} on the tactical map or in the 3D world.");
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

        DestroyPreviewAmmoDump();
        awaitingPlacementSelection = false;
        awaitingOrientationConfirmation = false;
        pendingPlacementTarget = default;
        pendingYawDegrees = 0f;
        placementClickTracker.Reset();
        placementCursorState.Deactivate();
        if (showStatus)
        {
            SetStatus("Building placement cancelled.");
        }
    }

    private void RefreshBuildingDefinitions()
    {
        placeableDefinitions.Clear();
        
        // Load buildings
        BuildingDefinition[] buildings = Resources.FindObjectsOfTypeAll<BuildingDefinition>()
            .Where(definition => definition != null && definition.unitPrefab != null)
            .Distinct()
            .OrderBy(definition => definition.unitName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.jsonKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        
        foreach (BuildingDefinition building in buildings)
        {
            placeableDefinitions.Add(new PlaceableDefinition(building));
        }
        
        // Load ships
        Encyclopedia? encyclopedia = Encyclopedia.i;
        if (encyclopedia?.ships != null)
        {
            for (int i = 0; i < encyclopedia.ships.Count; i++)
            {
                ShipDefinition ship = encyclopedia.ships[i];
                if (ship != null && ship.unitPrefab != null && ship.unitPrefab.GetComponent<Ship>() != null)
                {
                    placeableDefinitions.Add(new PlaceableDefinition(ship));
                }
            }
        }
        
        // Sort all by name
        placeableDefinitions.Sort((left, right) => 
            string.Compare(left.UnitName, right.UnitName, StringComparison.OrdinalIgnoreCase));

        if (placeableDefinitions.Count == 0)
        {
            selectedIndex = 0;
            if (string.IsNullOrWhiteSpace(statusText))
            {
                statusText = "No building or ship blueprints were found.";
            }
            return;
        }

        // Cache ammo dump definition for preview spawning
        ammoDumpDefinition = buildings.FirstOrDefault(d => 
            d.unitName.IndexOf("Ammo", StringComparison.OrdinalIgnoreCase) >= 0 || 
            d.jsonKey.IndexOf("ammo", StringComparison.OrdinalIgnoreCase) >= 0);

        selectedIndex = Mathf.Clamp(selectedIndex, 0, placeableDefinitions.Count - 1);
    }

    private void BeginOrientationConfirmation(GlobalPosition target)
    {
        awaitingOrientationConfirmation = true;
        pendingPlacementTarget = SnapConstructionTargetToTerrain(target);
        pendingYawDegrees = 0f;
        SpawnPreviewAmmoDump(pendingPlacementTarget, BuildPlacementRotation(pendingYawDegrees));
        SetStatus("Scroll mouse wheel to rotate. Press Enter to confirm placement.");
    }

    private void CompletePlacementSelection(GlobalPosition target, float yawDegrees)
    {
        PlaceableDefinition? definition = SelectedDefinition;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (definition == null || hq == null)
        {
            DestroyPreviewAmmoDump();
            CancelPlacementSelection(showStatus: false);
            SetStatus("The building could not be spawned.", warning: true);
            return;
        }

        float cost = definition.Value;
        if (hq.factionFunds < cost)
        {
            awaitingPlacementSelection = false;
            awaitingOrientationConfirmation = false;
            pendingPlacementTarget = default;
            pendingYawDegrees = 0f;
            placementClickTracker.Reset();
            placementCursorState.Deactivate();
            SetStatus($"Insufficient faction funds. {definition.UnitName} costs {FormatCostMillions(cost)}.", warning: true);
            return;
        }

        float buildDelayMinutes = GetBuildDelayMinutesWithSetting(cost);
        float buildDelaySeconds = buildDelayMinutes * 60f;
        float calibratedYaw = GetCalibratedPlacementYawDegrees(yawDegrees);
        Quaternion rotation = BuildPlacementRotation(yawDegrees);

        hq.AddFunds(-cost);
        float jackknifeRadius = GetJackknifeActivationRadius(definition);
        QueuedPlacement placement = new QueuedPlacement(
            definition,
            target,
            cost,
            buildDelaySeconds,
            $"NOC_BUILD_{definition.UnitName}_{Time.frameCount}",
            rotation);
        
        // Transfer preview ammo dump ownership to the queued placement
        placement.previewAmmoDump = previewAmmoDump;
        previewAmmoDump = null;
        
        pendingPlacements.Add(placement);

        awaitingPlacementSelection = false;
        awaitingOrientationConfirmation = false;
        pendingPlacementTarget = default;
        pendingYawDegrees = 0f;
        placementClickTracker.Reset();
        placementCursorState.Deactivate();
        SetStatus($"{definition.UnitName} queued ({FormatCostMillions(cost)}). Waiting for Jackknife within {jackknifeRadius:0}m. Heading {Mathf.RoundToInt(calibratedYaw):000}.");
    }

    private static Quaternion BuildPlacementRotation(float yawDegrees)
    {
        float calibratedYaw = GetCalibratedPlacementYawDegrees(yawDegrees);
        return Quaternion.Euler(0f, calibratedYaw, 0f);
    }

    private static void EnsurePlacementCalibrationDefaults()
    {
        if (CommanderSettings.PlacementCalibrationInitialized)
        {
            return;
        }

        CommanderSettings.PlacementOrientationOffsetDegrees = 0f;
        CommanderSettings.PlacementReverseScrollDirection = false;
        CommanderSettings.PlacementCalibrationInitialized = true;
    }

    private static float GetCalibratedPlacementYawDegrees(float rawYawDegrees)
    {
        float offset = CommanderSettings.PlacementOrientationOffsetDegrees;
        return NormalizeYawDegrees(rawYawDegrees + offset);
    }

    private void ProcessPendingPlacements()
    {
        if (pendingPlacements.Count == 0)
        {
            return;
        }

        for (int i = pendingPlacements.Count - 1; i >= 0; i--)
        {
            QueuedPlacement queued = pendingPlacements[i];
            if (!queued.timerStarted)
            {
                if (!TryStartQueuedPlacementTimer(queued))
                {
                    continue;
                }

                pendingPlacements[i] = queued;
            }

            // Remove preview 5 seconds before build completes to avoid spawn conflicts
            float timeRemaining = queued.completeAt - Time.unscaledTime;
            if (queued.timerStarted && timeRemaining <= 5f && queued.previewAmmoDump != null)
            {
                DestroyQueuedPlacementPreview(queued);
            }

            if (Time.unscaledTime < queued.completeAt)
            {
                continue;
            }

            pendingPlacements.RemoveAt(i);
            TryCompleteQueuedPlacement(queued);
        }
    }

    private bool TryStartQueuedPlacementTimer(QueuedPlacement queued)
    {
        if (queued.timerStarted)
        {
            return true;
        }

        Vector3 buildingPosition = queued.target.ToLocalPosition() + queued.definition.SpawnOffset;
        float radius = GetJackknifeActivationRadius(queued.definition);
        if (!TryFindNearbyJackknife(buildingPosition, radius, out _))
        {
            return false;
        }

        queued.timerStarted = true;
        queued.startedAt = Time.unscaledTime;
        queued.completeAt = queued.startedAt + queued.buildDelaySeconds;
        return true;
    }

    private static bool TryFindNearbyJackknife(Vector3 buildingPosition, float radius, out Unit? jackknife)
    {
        jackknife = null;
        Unit[] units = UnityEngine.Object.FindObjectsOfType<Unit>();
        for (int i = 0; i < units.Length; i++)
        {
            Unit unit = units[i];
            if (unit == null || !IsJackknifeUnit(unit))
            {
                continue;
            }

            if (Vector3.Distance(unit.transform.position, buildingPosition) <= radius)
            {
                jackknife = unit;
                return true;
            }
        }

        return false;
    }

    private static bool IsJackknifeUnit(Unit unit)
    {
        Type type = unit.GetType();
        string normalizedName = new string(type.Name
            .Where(ch => char.IsLetterOrDigit(ch))
            .ToArray())
            .ToLowerInvariant();
        string normalizedFullName = new string((type.FullName ?? string.Empty)
            .Where(ch => char.IsLetterOrDigit(ch))
            .ToArray())
            .ToLowerInvariant();
        string normalizedObjectName = new string((unit.gameObject.name ?? string.Empty)
            .Where(ch => char.IsLetterOrDigit(ch))
            .ToArray())
            .ToLowerInvariant();

        if (normalizedName.Contains("jackknife")
            || normalizedFullName.Contains("jackknife")
            || normalizedObjectName.Contains("jackknife"))
        {
            return true;
        }

        // Some missions/mod setups expose the construction vehicle as UGVDozer1.
        return normalizedName.Contains("ugvdozer")
            || normalizedFullName.Contains("ugvdozer")
            || normalizedObjectName.Contains("ugvdozer")
            || normalizedObjectName.Contains("dozer1");
    }

    private static float GetJackknifeActivationRadius(PlaceableDefinition definition)
    {
        // Ships require a minimum 160m Jackknife activation radius with size-based scaling
        if (definition.IsShip)
        {
            ShipDefinition? ship = definition.ShipDefinition;
            if (ship == null)
            {
                return 160f;
            }

            // Scale up based on ship size dimensions
            float shipSize = Mathf.Max(ship.width, ship.height, ship.length);
            float shipSizeMultiplier = Mathf.Max(0f, (shipSize - 20f) / 30f);
            float shipRadiusFromSize = 160f + shipSizeMultiplier * 90f;
            return Mathf.Clamp(shipRadiusFromSize, 160f, 340f);
        }

        BuildingDefinition? building = definition.BuildingDefinition;
        if (building == null)
        {
            return 25f;
        }

        string typeKey = (building.unitName ?? string.Empty) + " " + (building.jsonKey ?? string.Empty);
        string normalizedKey = typeKey.ToLowerInvariant();

        if (normalizedKey.Contains("hq") || normalizedKey.Contains("headquarters") || normalizedKey.Contains("command") || normalizedKey.Contains("base"))
        {
            return 90f;
        }

        if (normalizedKey.Contains("power") || normalizedKey.Contains("reactor") || normalizedKey.Contains("factory") || normalizedKey.Contains("workshop") || normalizedKey.Contains("shipyard") || normalizedKey.Contains("hangar") || normalizedKey.Contains("fort") || normalizedKey.Contains("outpost") || normalizedKey.Contains("research") || normalizedKey.Contains("lab") || normalizedKey.Contains("barracks") || normalizedKey.Contains("airfield") || normalizedKey.Contains("terminal"))
        {
            return 70f;
        }

        if (normalizedKey.Contains("radar") || normalizedKey.Contains("tower") || normalizedKey.Contains("turret") || normalizedKey.Contains("gun") || normalizedKey.Contains("battery") || normalizedKey.Contains("wall") || normalizedKey.Contains("gate") || normalizedKey.Contains("dome"))
        {
            return 45f;
        }

        if (normalizedKey.Contains("mine") || normalizedKey.Contains("depot") || normalizedKey.Contains("storage") || normalizedKey.Contains("warehouse") || normalizedKey.Contains("refinery") || normalizedKey.Contains("harvester") || normalizedKey.Contains("extractor") || normalizedKey.Contains("dock"))
        {
            return 55f;
        }

        float baseRadius = 25f;
        if (building.unitPrefab == null)
        {
            return baseRadius;
        }

        Renderer[] renderers = building.unitPrefab.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            return baseRadius;
        }

        float maxExtent = 0f;
        Transform root = building.unitPrefab.transform;
        for (int i = 0; i < renderers.Length; i++)
        {
            Bounds bounds = renderers[i].bounds;
            Vector3 localExtents = root.InverseTransformVector(bounds.extents);
            maxExtent = Mathf.Max(maxExtent, localExtents.magnitude);
        }

        float sizeMultiplier = Mathf.Max(0f, (maxExtent - 6f) / 18f);
        float radiusFromSize = baseRadius + sizeMultiplier * 18f;
        return Mathf.Clamp(radiusFromSize, baseRadius, 120f);
    }

    private void TryCompleteQueuedPlacement(QueuedPlacement queued)
    {
        PlaceableDefinition? definition = queued.definition;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (definition == null || hq == null || spawner == null)
        {
            RefundQueuedPlacement(queued);
            SetStatus($"Build failed because game services are unavailable. Refunded {FormatCostMillions(queued.cost)}.", warning: true);
            return;
        }

        Vector3 localPosition = queued.target.ToLocalPosition() + definition.SpawnOffset;

        try
        {
            Unit unit;
            
            if (definition.IsShip && definition.ShipDefinition != null)
            {
                // Spawn ship immediately
                unit = spawner.SpawnShip(
                    definition.ShipDefinition.unitPrefab,
                    localPosition.ToGlobalPosition(),
                    queued.rotation,
                    hq,
                    null,
                    1f,
                    holdPosition: false);
            }
            else if (!definition.IsShip && definition.BuildingDefinition != null)
            {
                // Spawn building
                unit = spawner.SpawnFromUnitDefinitionInEditor(
                    definition.BuildingDefinition,
                    localPosition.ToGlobalPosition(),
                    queued.rotation,
                    hq,
                    queued.spawnName);
            }
            else
            {
                throw new InvalidOperationException($"Unknown placeable type: {definition.UnitName}");
            }
            
            if (unit == null)
            {
                throw new InvalidOperationException($"Spawner returned null for {definition.UnitName}.");
            }

            if (unit is GroundVehicle groundVehicle)
            {
                groundVehicle.SetHoldPosition(true);
            }
            else if (unit is Ship ship)
            {
                ship.SetHoldPosition(true);
            }

            SetStatus($"{definition.UnitName} completed for {FormatCostMillions(queued.cost)}.");
        }
        catch (Exception exception)
        {
            DestroyQueuedPlacementPreview(queued);
            RefundQueuedPlacement(queued);
            CommanderPlugin.Log.LogError($"Building/ship placement failed: {exception}");
            SetStatus($"{definition.UnitName} placement failed: {exception.Message}. Refunded {FormatCostMillions(queued.cost)}.", warning: true);
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
            DestroyQueuedPlacementPreview(pendingPlacements[i]);
            RefundQueuedPlacement(pendingPlacements[i]);
        }

        pendingPlacements.Clear();
    }

    private void DestroyQueuedPlacementPreview(QueuedPlacement queued)
    {
        if (queued?.previewAmmoDump != null)
        {
            try
            {
                UnityEngine.Object.Destroy(queued.previewAmmoDump.gameObject);
            }
            catch (Exception exception)
            {
                CommanderPlugin.Log.LogWarning($"Failed to destroy queued placement preview: {exception.Message}");
            }
            finally
            {
                queued.previewAmmoDump = null;
            }
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

    private static float NormalizeYawDegrees(float yawDegrees)
    {
        yawDegrees %= 360f;
        return yawDegrees < 0f ? yawDegrees + 360f : yawDegrees;
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

    private void SpawnPreviewAmmoDump(GlobalPosition target, Quaternion rotation)
    {
        try
        {
            DestroyPreviewAmmoDump();
            
            if (ammoDumpDefinition == null)
            {
                return;
            }

            FactionHQ? hq = CommanderGameAccess.GetLocalHq();
            Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
            if (hq == null || spawner == null)
            {
                return;
            }

            Vector3 localPosition = target.ToLocalPosition() + ammoDumpDefinition.spawnOffset;
            previewAmmoDump = spawner.SpawnFromUnitDefinitionInEditor(
                ammoDumpDefinition,
                localPosition.ToGlobalPosition(),
                rotation,
                hq,
                $"PREVIEW_AMMODUMP_{Time.frameCount}");
        }
        catch (Exception exception)
        {
            CommanderPlugin.Log.LogWarning($"Failed to spawn preview ammo dump: {exception.Message}");
        }
    }

    private void DestroyPreviewAmmoDump()
    {
        if (previewAmmoDump != null)
        {
            try
            {
                UnityEngine.Object.Destroy(previewAmmoDump.gameObject);
            }
            catch (Exception exception)
            {
                CommanderPlugin.Log.LogWarning($"Failed to destroy preview ammo dump: {exception.Message}");
            }
            finally
            {
                previewAmmoDump = null;
            }
        }
    }

    private void UpdatePreviewAmmoDumpRotation()
    {
        if (previewAmmoDump != null)
        {
            try
            {
                previewAmmoDump.transform.rotation = BuildPlacementRotation(pendingYawDegrees);
            }
            catch (Exception exception)
            {
                CommanderPlugin.Log.LogWarning($"Failed to update preview ammo dump rotation: {exception.Message}");
            }
        }
    }

    private sealed class QueuedPlacement
    {
        internal readonly PlaceableDefinition definition;
        internal readonly GlobalPosition target;
        internal readonly float cost;
        internal readonly float buildDelaySeconds;
        internal readonly string spawnName;
        internal readonly Quaternion rotation;
        internal bool timerStarted;
        internal float startedAt;
        internal float completeAt;
        internal Unit? previewAmmoDump;

        internal QueuedPlacement(
            PlaceableDefinition definition,
            GlobalPosition target,
            float cost,
            float buildDelaySeconds,
            string spawnName,
            Quaternion rotation)
        {
            this.definition = definition;
            this.target = target;
            this.cost = cost;
            this.buildDelaySeconds = buildDelaySeconds;
            this.spawnName = spawnName;
            this.rotation = rotation;
            this.startedAt = 0f;
            this.completeAt = float.PositiveInfinity;
            this.timerStarted = false;
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
        internal readonly float jackknifeRadius;

        internal PendingPlacementStatus(string name, GlobalPosition target, float remainingSeconds, float jackknifeRadius)
        {
            this.name = name;
            this.target = target;
            this.remainingSeconds = remainingSeconds;
            this.jackknifeRadius = jackknifeRadius;
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

internal sealed class PlaceableDefinition
{
    internal readonly BuildingDefinition? BuildingDefinition;
    internal readonly ShipDefinition? ShipDefinition;
    internal readonly bool IsShip;
    internal readonly string UnitName;
    internal readonly float Value;
    internal readonly GameObject? UnitPrefab;
    internal readonly Vector3 SpawnOffset;

    internal PlaceableDefinition(BuildingDefinition definition)
    {
        BuildingDefinition = definition;
        ShipDefinition = null;
        IsShip = false;
        UnitName = definition.unitName;
        Value = Mathf.Max(definition.value, 5f);
        UnitPrefab = definition.unitPrefab;
        SpawnOffset = definition.spawnOffset;
    }

    internal PlaceableDefinition(ShipDefinition definition)
    {
        BuildingDefinition = null;
        ShipDefinition = definition;
        IsShip = true;
        UnitName = definition.unitName;
        Value = definition.value;
        UnitPrefab = definition.unitPrefab;
        // Add vertical offset for ships so they don't clip into terrain
        float shipVerticalOffset = Mathf.Max(10f, definition.height * 0.5f);
        SpawnOffset = new Vector3(0f, shipVerticalOffset, 0f);
    }
}