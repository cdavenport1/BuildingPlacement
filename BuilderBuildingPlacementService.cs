using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NuclearOption.Networking;
using UnityEngine;

namespace NuclearOptionBuilder;

internal enum BuildCategory
{
    Structures,
    Naval,
    Vehicles
}

internal sealed class BuilderBuildingPlacementService
{
    private const float MinimumBuildingCostMillions = 5f;
    private const float BuildDelayStepMinutes = 1f;
    private const float BaseMinimumBuildDelayMinutes = 0.5f;
    private const float BaseMaximumBuildDelayMinutes = 180f;
    private const float FinalMinimumBuildDelayMinutes = 0.1667f;  // 10 seconds minimum
    private const float FinalMaximumBuildDelayMinutes = 10f;
    private const float MaximumBuildDelayShipsMinutes = 5f;
    private const float MaximumBuildDelayVehiclesMinutes = 3f;
    private const float MaximumBuildDelayLargeCarrierMinutes = 10f;
    private const float BuildTimeMultiplierFor10Seconds = 0f;  // 0x multiplier = 10 seconds instant placement
    private const float MinimumBuildTimeMultiplier = 0f;
    private const float MaximumBuildTimeMultiplier = 4f;
    private const float BuildTimeMultiplierStep = 0.25f;
    private const float OrientationStepDegrees = 5f;  // 5-degree intervals for granular control
    private const float RefreshIntervalSeconds = 10f;
    private const float ShipLargeFactorySearchTimeoutSeconds = 5f;  // Ship placements timeout after 5 seconds if no Large Factory found
    private const float VehicleDepotSearchTimeoutSeconds = 5f;  // Vehicle placements timeout after 5 seconds if no depot found
    private const float PreviewDestructionGraceSeconds = 1.5f;  // Ignore destruction checks briefly after (re)spawning a preview, since newly spawned units can start disabled for a frame or two

    // No public API clears these once a player-attributed order sets them; without resetting them the AI's own
    // objective/nearest-enemy loop stays gated off forever after a single hold order.
    private static readonly FieldInfo? GroundVehicleCommandedDestinationField = typeof(GroundVehicle).GetField("commandedDestination", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? GroundVehicleDestinationField = typeof(GroundVehicle).GetField("destination", BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly BuilderMapClickTracker placementClickTracker = new();
    private readonly BuilderPlacementCursorState placementCursorState = new();
    private readonly List<PlaceableDefinition> placeableDefinitions = new();
    private readonly List<PlaceableDefinition> visibleDefinitionsCache = new();
    private readonly List<QueuedPlacement> pendingPlacements = new();
    private readonly Dictionary<BuildCategory, bool> categoryExpandedState = new()
    {
        { BuildCategory.Structures, true },
        { BuildCategory.Naval, true },
        { BuildCategory.Vehicles, true }
    };
    
    internal void LoadCategoryStates()
    {
        categoryExpandedState[BuildCategory.Structures] = BuilderSettings.StructuresExpanded;
        categoryExpandedState[BuildCategory.Naval] = BuilderSettings.NavalExpanded;
        categoryExpandedState[BuildCategory.Vehicles] = BuilderSettings.VehiclesExpanded;
    }
    
    private void SaveCategoryState(BuildCategory category)
    {
        // All categories are seeded in the constructor, so the key always exists
        bool isExpanded = categoryExpandedState[category];
        switch (category)
        {
            case BuildCategory.Structures:
                BuilderSettings.StructuresExpanded = isExpanded;
                break;
            case BuildCategory.Naval:
                BuilderSettings.NavalExpanded = isExpanded;
                break;
            case BuildCategory.Vehicles:
                BuilderSettings.VehiclesExpanded = isExpanded;
                break;
        }
    }
    private bool awaitingPlacementSelection;
    private bool awaitingOrientationConfirmation;
    private GlobalPosition pendingPlacementTarget;
    private float pendingYawDegrees;
    private int selectedIndex;
    private float nextRefreshAt;
    private string statusText = string.Empty;
    private float buildTimeMultiplier = 1f;
    private UnitDefinition? fuelContainerDefinition;
    private bool fuelContainerMissingWarningShown;
    private BuildingDefinition? ammoDumpDefinition;
    private bool ammoDumpMissingWarningShown;
    private Unit? previewFuelContainer;

    internal bool AwaitingPlacementSelection => awaitingPlacementSelection;

    internal IReadOnlyList<PlaceableDefinition> PlaceableDefinitions => placeableDefinitions;

    internal IReadOnlyList<PlaceableDefinition> VisibleDefinitions => visibleDefinitionsCache;

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

            float baseMinutes = GetBuildDelayMinutes(definition.Value, definition);
            float adjustedMinutes = GetBuildDelayMinutesWithSetting(definition.Value, definition);
            return $"{baseMinutes:0}m -> {adjustedMinutes:0}m";
        }
    }

    internal string OrientationOffsetLabel => $"ORIENTATION OFFSET {Mathf.RoundToInt(NormalizeYawDegrees(BuilderSettings.PlacementOrientationOffsetDegrees)):000}";

    internal string OrientationScrollLabel => BuilderSettings.PlacementReverseScrollDirection
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
                ? Mathf.Max(0f, queued.buildDelaySeconds - queued.elapsedBuildSeconds)
                : -1f;
            float radius;
            if (queued.definition.IsShip)
            {
                radius = GetLargeFactoryActivationRadius(queued.definition);
            }
            else if (queued.definition.IsVehicle)
            {
                radius = GetVehicleDepotActivationRadius();
            }
            else
            {
                radius = GetJackknifeActivationRadius(queued.definition);
            }
            statuses[i] = new PendingPlacementStatus(
                queued.definition.UnitName,
                queued.target,
                remainingSeconds,
                radius,
                queued.definition.IsShip,
                queued.definition.IsVehicle);
        }

        return statuses;
    }

    internal void CancelPendingPlacement(int index)
    {
        if (index < 0 || index >= pendingPlacements.Count)
        {
            return;
        }

        QueuedPlacement queued = pendingPlacements[index];
        DestroyQueuedPlacementPreview(queued);
        RefundQueuedPlacement(queued);
        pendingPlacements.RemoveAt(index);
        SetStatus($"{queued.definition.UnitName} placement cancelled. Refunded {FormatCostMillions(queued.cost)}.");
    }

    internal void ToggleCategoryExpanded(BuildCategory category)
    {
        if (categoryExpandedState.ContainsKey(category))
        {
            categoryExpandedState[category] = !categoryExpandedState[category];
            SaveCategoryState(category);
        }
    }

    internal bool IsCategoryExpanded(BuildCategory category)
    {
        return categoryExpandedState.TryGetValue(category, out bool expanded) && expanded;
    }

    internal void Activate()
    {
        EnsurePlacementCalibrationDefaults();
        LoadCategoryStates();
        RefreshBuildingDefinitions();
        nextRefreshAt = BuilderScheduler.Stagger("building-placement", RefreshIntervalSeconds, 0.4f);
    }

    internal void Deactivate()
    {
        placementCursorState.Deactivate();
        CancelPlacementSelection(showStatus: false);
    }

    internal void ResetSession()
    {
        DestroyPreviewFuelContainer();
        RefundPendingPlacements();
        awaitingPlacementSelection = false;
        awaitingOrientationConfirmation = false;
        pendingPlacementTarget = default;
        pendingYawDegrees = 0f;
        placementClickTracker.Reset();
        placementCursorState.Deactivate();
        placeableDefinitions.Clear();
        visibleDefinitionsCache.Clear();
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

        if (BuilderGameInput.CancelDown)
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
                float signedScrollDelta = BuilderSettings.PlacementReverseScrollDirection ? -scrollDelta : scrollDelta;
                pendingYawDegrees = NormalizeYawDegrees(pendingYawDegrees + signedScrollDelta * OrientationStepDegrees);
                UpdatePreviewFuelContainerRotation();
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

        if (!BuilderScheduler.IsDue(ref nextRefreshAt, RefreshIntervalSeconds))
        {
            return;
        }

        RefreshBuildingDefinitions();
    }

    internal string GetDefinitionLabel(PlaceableDefinition definition)
    {
        string cost = FormatCostMillions(definition.Value);
        string type;
        if (definition.IsShip)
            type = "Ship";
        else if (definition.IsVehicle)
            type = "Vehicle";
        else
            type = definition.BuildingDefinition?.jsonKey ?? "Building";
        return $"{definition.UnitName}\n{type}  |  {cost}";
    }

    internal bool CanAffordDefinition(PlaceableDefinition definition)
    {
        return CanAffordDefinition(definition, BuilderGameAccess.GetLocalHq());
    }

    internal bool CanAffordDefinition(PlaceableDefinition definition, FactionHQ? hq)
    {
        return hq == null || hq.factionFunds >= definition.Value;
    }

    // First selected ground vehicle or ship on the tactical map, since only those two support hold position
    internal bool TryGetSelectedCommandableUnit(out Unit? unit, out bool holdPosition)
    {
        unit = null;
        holdPosition = false;

        DynamicMap? map = SceneSingleton<DynamicMap>.i;
        if (map == null)
        {
            return false;
        }

        foreach (MapIcon icon in map.selectedIcons)
        {
            if (icon is not UnitMapIcon { unit: { } candidate })
            {
                continue;
            }

            if (candidate is GroundVehicle groundVehicle)
            {
                unit = groundVehicle;
                holdPosition = groundVehicle.GetHoldPosition();
                return true;
            }

            if (candidate is Ship ship)
            {
                unit = ship;
                holdPosition = ship.holdPosition;
                return true;
            }
        }

        return false;
    }

    internal void ToggleSelectedUnitHoldPosition()
    {
        if (!TryGetSelectedCommandableUnit(out Unit? unit, out bool holdPosition))
        {
            return;
        }

        bool willHold = !holdPosition;

        if (unit is GroundVehicle groundVehicle)
        {
            groundVehicle.SetHoldPosition(willHold);

            if (willHold)
            {
                // Attribute a stay-in-place order to the player so the map's Order tooltip shows Player Control
                if (unit is ICommandable { UnitCommand: { } holdCommand })
                {
                    holdCommand.SetDestination(unit.GlobalPosition(), playerCommand: true);
                }
            }
            else
            {
                // Don't call SetDestination ourselves here - it always re-pins "destination" to whatever position
                // is passed in and re-paths to it, which would undo this reset in the same call by targeting this
                // exact spot again. Just open the gate and let the AI's own loop issue its own next destination.
                GroundVehicleCommandedDestinationField?.SetValue(groundVehicle, false);
                GroundVehicleDestinationField?.SetValue(groundVehicle, default(GlobalPosition));
            }
        }
        else if (unit is Ship ship)
        {
            ship.SetHoldPosition(willHold);

            if (unit is ICommandable { UnitCommand: { } shipCommand })
            {
                if (willHold)
                {
                    // Attribute a stay-in-place order to the player so the map's Order tooltip shows Player Control
                    shipCommand.SetDestination(unit.GlobalPosition(), playerCommand: true);
                }
                else
                {
                    // Ships have no autonomous objective-seeking of their own (unlike GroundVehicle) - pick the
                    // nearest active faction objective ourselves, same source the vehicle AI itself relies on
                    GlobalPosition resumeDestination = MissionPosition.TryGetClosestPosition(ship, out GlobalPosition closestObjective)
                        ? closestObjective
                        : unit.GlobalPosition();
                    shipCommand.SetDestination(resumeDestination, playerCommand: false);
                }
            }
        }
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
        BuilderSettings.PlacementOrientationOffsetDegrees = NormalizeYawDegrees(
            BuilderSettings.PlacementOrientationOffsetDegrees + deltaDegrees);
    }

    internal void SetOrientationOffset(float absoluteDegrees)
    {
        BuilderSettings.PlacementOrientationOffsetDegrees = NormalizeYawDegrees(absoluteDegrees);
    }

    internal void ToggleOrientationScrollDirection()
    {
        BuilderSettings.PlacementReverseScrollDirection = !BuilderSettings.PlacementReverseScrollDirection;
    }

    internal void ResetOrientationCalibration()
    {
        BuilderSettings.PlacementOrientationOffsetDegrees = 0f;
        BuilderSettings.PlacementReverseScrollDirection = false;
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

        PlaceableDefinition? definition = SelectedDefinition;
        
        // Ships skip validation - use fixed sea level plane intersection
        if (definition?.IsShip == true)
        {
            Ray ray = UnityEngine.Camera.main.ScreenPointToRay(screenPosition);
            // Find intersection with sea level plane
            // Ray equation: P = ray.origin + t * ray.direction
            // Plane equation: y = Datum.LocalSeaY
            // Solve: ray.origin.y + t * ray.direction.y = Datum.LocalSeaY
            
            float rayOriginY = ray.origin.y;
            float rayDirectionY = ray.direction.y;
            
            // Avoid division by zero if ray is parallel to plane
            if (Mathf.Abs(rayDirectionY) > 0.001f)
            {
                float t = (Datum.LocalSeaY - rayOriginY) / rayDirectionY;
                if (t > 0f)  // Only accept intersections in front of camera
                {
                    Vector3 intersectionPoint = ray.origin + ray.direction * t;
                    GlobalPosition shipPosition = intersectionPoint.ToGlobalPosition();
                    
                    BeginOrientationConfirmation(shipPosition);
                    return true;
                }
            }
            
            // Fallback if ray doesn't intersect plane properly
            SetStatus("Could not determine water placement position.");
            return true;
        }
        
        // For non-ships, require terrain raycast
        if (!BuilderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition target))
        {
            SetStatus("No valid terrain position was found.");
            return true;
        }

        BeginOrientationConfirmation(target);
        return true;
    }

    // showStatus: true for user-initiated cancels, false when cleanup is silent or a more specific status follows immediately
    internal void CancelPlacementSelection(bool showStatus = true)
    {
        if (!awaitingPlacementSelection)
        {
            return;
        }

        DestroyPreviewFuelContainer();
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
        // Preserve the current selection across the rebuild below (list order/count can change between refreshes)
        string? previouslySelectedUnitName = SelectedDefinition?.UnitName;
        BuildCategory? previouslySelectedCategory = SelectedDefinition?.Category;

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
            PlaceableDefinition def = new PlaceableDefinition(building);
            placeableDefinitions.Add(def);
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
                    PlaceableDefinition def = new PlaceableDefinition(ship);
                    placeableDefinitions.Add(def);
                }
            }
        }
        
        // Load vehicles
        VehicleDefinition[] vehicles = Resources.FindObjectsOfTypeAll<VehicleDefinition>();
        foreach (VehicleDefinition vehicle in vehicles)
        {
            if (vehicle != null && vehicle.unitPrefab != null && vehicle.unitPrefab.GetComponent<GroundVehicle>() != null)
            {
                PlaceableDefinition def = new PlaceableDefinition(vehicle);
                placeableDefinitions.Add(def);
            }
        }
        
        // Sort all by category, then by name
        placeableDefinitions.Sort((left, right) =>
        {
            int categoryCompare = left.Category.CompareTo(right.Category);
            return categoryCompare != 0 ? categoryCompare : string.Compare(left.UnitName, right.UnitName, StringComparison.OrdinalIgnoreCase);
        });

        // Rebuild the cached grouped/sorted view once here, instead of on every VisibleDefinitions access
        visibleDefinitionsCache.Clear();
        visibleDefinitionsCache.AddRange(placeableDefinitions
            .GroupBy(d => d.Category)
            .OrderBy(g => g.Key)
            .SelectMany(g => g.OrderBy(d => d.UnitName, StringComparer.OrdinalIgnoreCase)));

        if (placeableDefinitions.Count == 0)
        {
            selectedIndex = 0;
            if (string.IsNullOrWhiteSpace(statusText))
            {
                statusText = "No building or ship blueprints were found.";
            }
            return;
        }

        // Cache fuel container definition, used as the placement preview.
        // The 10,000L variant is jsonKey "FuelContainer3" (jsonKeys are just sequential, not capacity-based),
        // in Encyclopedia.otherUnits ("Other Units" category in-game) - matched here on its unitName instead.
        if (fuelContainerDefinition == null && encyclopedia?.otherUnits != null)
        {
            fuelContainerDefinition = encyclopedia.otherUnits.FirstOrDefault(d =>
                d != null && d.unitPrefab != null && d.unitName != null &&
                d.unitName.IndexOf("Fuel Container", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (d.unitName.IndexOf("10,000", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 d.unitName.IndexOf("10000", StringComparison.OrdinalIgnoreCase) >= 0));
        }

        if (fuelContainerDefinition == null)
        {
            if (!fuelContainerMissingWarningShown)
            {
                fuelContainerMissingWarningShown = true;
                SetStatus("No fuel container blueprint found; placement previews will be unavailable.", warning: true);
            }
        }
        else
        {
            fuelContainerMissingWarningShown = false;
        }

        // Cache ammo dump definition, used as the placement preview for ships
        if (ammoDumpDefinition == null)
        {
            ammoDumpDefinition = buildings.FirstOrDefault(d =>
                d.unitName.IndexOf("Ammo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                d.jsonKey.IndexOf("ammo", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        if (ammoDumpDefinition == null)
        {
            if (!ammoDumpMissingWarningShown)
            {
                ammoDumpMissingWarningShown = true;
                SetStatus("No ammo dump blueprint found; ship placement previews will be unavailable.", warning: true);
            }
        }
        else
        {
            ammoDumpMissingWarningShown = false;
        }

        // Keep the same selected building across the refresh instead of silently clamping to a different one
        if (!awaitingPlacementSelection && previouslySelectedUnitName != null)
        {
            int matchIndex = placeableDefinitions.FindIndex(d =>
                d.Category == previouslySelectedCategory && string.Equals(d.UnitName, previouslySelectedUnitName, StringComparison.OrdinalIgnoreCase));
            if (matchIndex >= 0)
            {
                selectedIndex = matchIndex;
                return;
            }
        }

        selectedIndex = Mathf.Clamp(selectedIndex, 0, placeableDefinitions.Count - 1);
    }

    private void BeginOrientationConfirmation(GlobalPosition target)
    {
        awaitingOrientationConfirmation = true;
        // Ships should not be snapped to terrain - use their position as-is with default offset
        pendingPlacementTarget = SelectedDefinition?.IsShip == true ? target : SnapConstructionTargetToTerrain(target);
        pendingYawDegrees = 0f;
        // SpawnPreviewFuelContainer clears any existing preview and no-ops if there's no preview definition for the selected type
        SpawnPreviewFuelContainer(pendingPlacementTarget, BuildPlacementRotation(pendingYawDegrees));
        SetStatus("Scroll mouse wheel to rotate. Press Enter to confirm placement.");
    }

    // Structures preview as a fuel container; ships preview as an ammo dump; vehicles get no preview
    private UnitDefinition? GetPreviewDefinition(PlaceableDefinition? definition)
    {
        if (definition == null || definition.IsVehicle)
        {
            return null;
        }
        return definition.IsShip ? ammoDumpDefinition : fuelContainerDefinition;
    }

    private void CompletePlacementSelection(GlobalPosition target, float yawDegrees)
    {
        PlaceableDefinition? definition = SelectedDefinition;
        FactionHQ? hq = BuilderGameAccess.GetLocalHq();
        if (definition == null || hq == null)
        {
            DestroyPreviewFuelContainer();
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

        float buildDelayMinutes = GetBuildDelayMinutesWithSetting(cost, definition);
        float buildDelaySeconds = buildDelayMinutes * 60f;
        float calibratedYaw = GetCalibratedPlacementYawDegrees(yawDegrees);
        Quaternion rotation = BuildPlacementRotation(yawDegrees);

        // Ships should not be snapped to terrain - use their position as-is with default offset
        GlobalPosition finalTarget = definition.IsShip ? target : SnapConstructionTargetToTerrain(target);

        hq.AddFunds(-cost);
        float largeFactoryRadius = GetLargeFactoryActivationRadius(definition);
        float vehicleDepotRadius = GetVehicleDepotActivationRadius();
        float jackknifeRadius = GetJackknifeActivationRadius(definition);
        QueuedPlacement placement = new QueuedPlacement(
            definition,
            finalTarget,
            cost,
            buildDelaySeconds,
            definition.UnitName,
            rotation);
        
        // Transfer preview ownership so it stays visible through the waiting-for-activation period, not just after
        placement.previewFuelContainer = previewFuelContainer;
        placement.previewSpawnedAt = Time.unscaledTime;
        previewFuelContainer = null;
        
        pendingPlacements.Add(placement);

        awaitingPlacementSelection = false;
        awaitingOrientationConfirmation = false;
        pendingPlacementTarget = default;
        pendingYawDegrees = 0f;
        placementClickTracker.Reset();
        placementCursorState.Deactivate();
        
        // Display appropriate status message based on unit type
        if (definition.IsShip)
        {
            SetStatus($"{definition.UnitName} queued ({FormatCostMillions(cost)}). Waiting for Large Factory within {largeFactoryRadius:0}m. Heading {Mathf.RoundToInt(calibratedYaw):000}.");
        }
        else if (definition.IsVehicle)
        {
            SetStatus($"{definition.UnitName} queued ({FormatCostMillions(cost)}). Waiting for Vehicle Depot within {vehicleDepotRadius:0}m. Heading {Mathf.RoundToInt(calibratedYaw):000}.");
        }
        else
        {
            SetStatus($"{definition.UnitName} queued ({FormatCostMillions(cost)}). Waiting for Jackknife within {jackknifeRadius:0}m. Heading {Mathf.RoundToInt(calibratedYaw):000}.");
        }
    }

    private static Quaternion BuildPlacementRotation(float yawDegrees)
    {
        float calibratedYaw = GetCalibratedPlacementYawDegrees(yawDegrees);
        return Quaternion.Euler(0f, calibratedYaw, 0f);
    }

    private static void EnsurePlacementCalibrationDefaults()
    {
        if (BuilderSettings.PlacementCalibrationInitialized)
        {
            return;
        }

        BuilderSettings.PlacementOrientationOffsetDegrees = 0f;
        BuilderSettings.PlacementReverseScrollDirection = false;
        BuilderSettings.PlacementCalibrationInitialized = true;
    }

    private static float GetCalibratedPlacementYawDegrees(float rawYawDegrees)
    {
        float offset = BuilderSettings.PlacementOrientationOffsetDegrees;
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
            
            // Check for ship large factory search timeout
            if (queued.definition.IsShip && !queued.timerStarted)
            {
                float factorySearchElapsed = Time.unscaledTime - queued.createdAt;
                if (factorySearchElapsed > ShipLargeFactorySearchTimeoutSeconds)
                {
                    // Timeout: refund and remove placement
                    FactionHQ? hq = BuilderGameAccess.GetLocalHq();
                    if (hq != null)
                    {
                        hq.AddFunds(queued.cost);
                    }
                    DestroyQueuedPlacementPreview(queued);
                    pendingPlacements.RemoveAt(i);
                    SetStatus($"{queued.definition.UnitName} placement cancelled: no Large Factory found within {ShipLargeFactorySearchTimeoutSeconds:0}s. Refunded {FormatCostMillions(queued.cost)}.", warning: true);
                    continue;
                }
            }
            
            // Check for vehicle depot search timeout
            if (queued.definition.IsVehicle && !queued.timerStarted)
            {
                float depotSearchElapsed = Time.unscaledTime - queued.createdAt;
                if (depotSearchElapsed > VehicleDepotSearchTimeoutSeconds)
                {
                    // Timeout: refund and remove placement
                    FactionHQ? hq = BuilderGameAccess.GetLocalHq();
                    if (hq != null)
                    {
                        hq.AddFunds(queued.cost);
                    }
                    DestroyQueuedPlacementPreview(queued);
                    pendingPlacements.RemoveAt(i);
                    SetStatus($"{queued.definition.UnitName} placement cancelled: no Vehicle Depot found within {VehicleDepotSearchTimeoutSeconds:0}s. Refunded {FormatCostMillions(queued.cost)}.", warning: true);
                    continue;
                }
            }
            
            // Combat destruction sets NetworkunitState.Destroyed; manual deletion just deactivates/disables the object without that state
            bool fuelContainerDeleted = false;
            bool fuelContainerDestroyedInCombat = false;
            if (queued.previewFuelContainer != null)
            {
                // Re-check == null here: Unity's fake-null can flip between the outer check and this line once Destroy() has been called
                if (queued.previewFuelContainer == null || queued.previewFuelContainer.gameObject == null)
                {
                    fuelContainerDestroyedInCombat = true;
                }
                else if (queued.previewFuelContainer.NetworkunitState == Unit.UnitState.Destroyed)
                {
                    fuelContainerDestroyedInCombat = true;
                }
                // Skip the inactive check briefly after (re)spawning - newly spawned units can take a frame or two to activate.
                // Only activeInHierarchy is checked (not `disabled`, which can be legitimately true for an unpowered preview).
                else if (Time.unscaledTime - queued.previewSpawnedAt >= PreviewDestructionGraceSeconds
                    && !queued.previewFuelContainer.gameObject.activeInHierarchy)
                {
                    fuelContainerDeleted = true;
                }
            }
            
            if (fuelContainerDestroyedInCombat)
            {
                DestroyQueuedPlacementPreview(queued);
                pendingPlacements.RemoveAt(i);
                SetStatus($"{queued.definition.UnitName} placement cancelled: preview structure was destroyed in combat (no refund).", warning: true);
                continue;
            }
            
            if (fuelContainerDeleted)
            {
                DestroyQueuedPlacementPreview(queued);
                RefundQueuedPlacement(queued);
                pendingPlacements.RemoveAt(i);
                SetStatus($"{queued.definition.UnitName} placement cancelled: preview structure was deleted. Refunded {FormatCostMillions(queued.cost)}.", warning: true);
                continue;
            }
            
            if (!queued.timerStarted)
            {
                if (!TryStartQueuedPlacementTimer(queued))
                {
                    continue;
                }

                pendingPlacements[i] = queued;
            }

            // Accumulate build time (Time.deltaTime is 0 when game is paused)
            if (queued.timerStarted)
            {
                queued.elapsedBuildSeconds += Time.deltaTime;
            }

            // Remove preview 5 seconds before build completes to avoid spawn conflicts
            float timeRemaining = queued.buildDelaySeconds - queued.elapsedBuildSeconds;
            if (queued.timerStarted && timeRemaining <= 5f && queued.previewFuelContainer != null)
            {
                DestroyQueuedPlacementPreview(queued);
            }

            if (queued.elapsedBuildSeconds < queued.buildDelaySeconds)
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
        
        // Check if this is a ship - requires nearby Large Factory and minimum 350m distance
        if (queued.definition.IsShip)
        {
            float factoryRadius = GetLargeFactoryActivationRadius(queued.definition);
            // Ship path needs the factory reference below for the distance check, so the null check is kept explicit here
            if (!TryFindNearbyLargeFactory(buildingPosition, factoryRadius, out Unit? factory) || factory == null)
            {
                return false;
            }

            // Check minimum distance from factory (must be at least 350m away)
            float distanceToFactory = Vector3.Distance(factory.transform.position, buildingPosition);
            if (distanceToFactory < 350f)
            {
                SetStatus($"Ship placed too close to Large Factory. Minimum distance 350m required. Current distance: {distanceToFactory:0}m", warning: true);
                return false;
            }
        }
        // Check if this is a vehicle - requires nearby vehicle depot
        else if (queued.definition.IsVehicle)
        {
            float depotRadius = GetVehicleDepotActivationRadius();
            if (!TryFindNearbyVehicleDepot(buildingPosition, depotRadius, out _))
            {
                return false;
            }
        }
        else
        {
            // Buildings require nearby Jackknife
            float radius = GetJackknifeActivationRadius(queued.definition);
            if (!TryFindNearbyJackknife(buildingPosition, radius, out _))
            {
                return false;
            }
        }

        queued.timerStarted = true;
        queued.startedAt = Time.unscaledTime;
        queued.elapsedBuildSeconds = 0f;
        // Fallback only: the preview normally already exists (transferred from selection in CompletePlacementSelection)
        if (queued.previewFuelContainer == null && GetPreviewDefinition(queued.definition) != null)
        {
            queued.previewFuelContainer = SpawnQueuedPlacementPreviewFuelContainer(queued);
            queued.previewSpawnedAt = Time.unscaledTime;
        }
        return true;
    }

    private static BuildCategory DetermineBuildCategory(PlaceableDefinition definition)
    {
        // Ships are Naval
        if (definition.IsShip)
        {
            return BuildCategory.Naval;
        }

        // Vehicles are Vehicles
        if (definition.IsVehicle)
        {
            return BuildCategory.Vehicles;
        }

        // Everything else is Structures
        return BuildCategory.Structures;
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

    private static bool TryFindNearbyLargeFactory(Vector3 shipPosition, float radius, out Unit? factory)
    {
        factory = null;
        Unit[] units = UnityEngine.Object.FindObjectsOfType<Unit>();
        for (int i = 0; i < units.Length; i++)
        {
            Unit unit = units[i];
            if (unit == null || !IsLargeFactoryUnit(unit))
            {
                continue;
            }

            if (Vector3.Distance(unit.transform.position, shipPosition) <= radius)
            {
                factory = unit;
                return true;
            }
        }

        return false;
    }

    private static bool IsLargeFactoryUnit(Unit unit)
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

        return normalizedName.Contains("factorylarge")
            || normalizedFullName.Contains("factorylarge")
            || normalizedObjectName.Contains("factorylarge");
    }

    private static bool TryFindNearbyVehicleDepot(Vector3 vehiclePosition, float radius, out VehicleDepot? depot)
    {
        depot = null;
        VehicleDepot[] depots = UnityEngine.Object.FindObjectsOfType<VehicleDepot>();
        for (int i = 0; i < depots.Length; i++)
        {
            VehicleDepot vehicleDepot = depots[i];
            if (vehicleDepot == null)
            {
                continue;
            }

            if (Vector3.Distance(vehicleDepot.transform.position, vehiclePosition) <= radius)
            {
                depot = vehicleDepot;
                return true;
            }
        }

        return false;
    }

    private static float GetVehicleDepotActivationRadius()
    {
        // Vehicles require a fixed radius to be near a depot
        // Smaller than Jackknife since depots are more common than construction vehicles
        return 150f;
    }

    private static float GetLargeFactoryActivationRadius(PlaceableDefinition definition)
    {
        // Ships require a fixed 900m Large Factory activation radius
        if (definition.IsShip)
        {
            return 900f;
        }

        return 160f;
    }

    private static float GetJackknifeActivationRadius(PlaceableDefinition definition)
    {
        // Ships now use Large Factory instead (see GetLargeFactoryActivationRadius)
        // This method is only used for buildings
        BuildingDefinition? building = definition.BuildingDefinition;
        if (building == null)
        {
            return 50f;
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

        float baseRadius = 50f;
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
        FactionHQ? hq = BuilderGameAccess.GetLocalHq();
        Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
        if (definition == null || hq == null || spawner == null)
        {
            RefundQueuedPlacement(queued);
            SetStatus($"Build failed because game services are unavailable. Refunded {FormatCostMillions(queued.cost)}.", warning: true);
            return;
        }

        // Check if preview fuel container was removed before spawning - only refund for manual deletion, not combat destruction
        if (queued.previewFuelContainer != null)
        {
            Unit fuelContainer = queued.previewFuelContainer;
            bool destroyedInCombat = fuelContainer == null
                || fuelContainer.gameObject == null
                || fuelContainer.NetworkunitState == Unit.UnitState.Destroyed;
            bool deleted = !destroyedInCombat
                && Time.unscaledTime - queued.previewSpawnedAt >= PreviewDestructionGraceSeconds
                && !fuelContainer!.gameObject!.activeInHierarchy;

            if (destroyedInCombat)
            {
                SetStatus($"Build failed: preview structure was destroyed in combat (no refund).", warning: true);
                return;
            }

            if (deleted)
            {
                RefundQueuedPlacement(queued);
                SetStatus($"Build failed: preview structure was deleted. Refunded {FormatCostMillions(queued.cost)}.", warning: true);
                return;
            }
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
            else if (definition.IsVehicle && definition.VehicleDefinition != null)
            {
                // Spawn ground vehicle (same as building)
                unit = spawner.SpawnFromUnitDefinitionInEditor(
                    definition.VehicleDefinition,
                    localPosition.ToGlobalPosition(),
                    queued.rotation,
                    hq,
                    queued.spawnName);
                
                // Enable AI for ground vehicles
                if (unit is GroundVehicle groundVehicle)
                {
                    groundVehicle.SetHoldPosition(false);
                }
            }
            else if (definition.BuildingDefinition != null)
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

            // Enable AI for ships (they already spawn with holdPosition: false from SpawnShip)
            // Enable AI for buildings (not applicable, but ship was already enabled above)
            if (unit is Ship ship && definition.IsShip)
            {
                // Ship AI is already enabled via holdPosition: false parameter in SpawnShip
            }

            SetStatus($"{definition.UnitName} completed for {FormatCostMillions(queued.cost)}.");
        }
        catch (Exception exception)
        {
            DestroyQueuedPlacementPreview(queued);
            RefundQueuedPlacement(queued);
            BuilderPlugin.Log.LogError($"Building/ship placement failed: {exception}");
            SetStatus($"{definition.UnitName} placement failed: {exception.Message}. Refunded {FormatCostMillions(queued.cost)}.", warning: true);
        }
    }

    private static void RefundQueuedPlacement(QueuedPlacement queued)
    {
        FactionHQ? hq = BuilderGameAccess.GetLocalHq();
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
        if (queued?.previewFuelContainer != null)
        {
            try
            {
                UnityEngine.Object.Destroy(queued.previewFuelContainer.gameObject);
            }
            catch (Exception exception)
            {
                BuilderPlugin.Log.LogWarning($"Failed to destroy queued placement preview: {exception.Message}");
            }
            finally
            {
                queued.previewFuelContainer = null;
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

    internal static string FormatCostMillions(float value)
    {
        return "$" + value.ToString("0.##") + "m";
    }

    private bool IsLargeCarrier(PlaceableDefinition definition)
    {
        if (!definition.IsShip || definition.ShipDefinition == null)
        {
            return false;
        }
        
        string shipName = definition.ShipDefinition.unitName;
        return shipName.IndexOf("Large", StringComparison.OrdinalIgnoreCase) >= 0 ||
               shipName.IndexOf("Carrier", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private float GetMaximumBuildDelayForDefinition(PlaceableDefinition definition)
    {
        if (definition.IsVehicle)
        {
            return MaximumBuildDelayVehiclesMinutes;
        }
        
        if (definition.IsShip)
        {
            return IsLargeCarrier(definition) 
                ? MaximumBuildDelayLargeCarrierMinutes 
                : MaximumBuildDelayShipsMinutes;
        }
        
        // Buildings use the default max
        return FinalMaximumBuildDelayMinutes;
    }

    private float GetBuildDelayMinutes(float costMillions, PlaceableDefinition definition)
    {
        // Ships use a separate cost-based linear scale
        if (definition.IsShip)
        {
            // Scale ships from $26M (2 min) to $3500M (10 min) linearly
            const float shipMinCost = 26f;
            const float shipMaxCost = 3500f;
            const float shipMinMinutes = 2f;
            const float shipMaxMinutes = 10f;
            
            float baseMinutes = shipMinMinutes + (costMillions - shipMinCost) / (shipMaxCost - shipMinCost) * (shipMaxMinutes - shipMinMinutes);
            return Mathf.Clamp(baseMinutes, shipMinMinutes, shipMaxMinutes);
        }
        
        // Non-ships use the standard step-based formula
        float steppedMinutes = Mathf.Ceil(costMillions / BuildDelayStepMinutes) * BuildDelayStepMinutes;
        return Mathf.Clamp(steppedMinutes, BaseMinimumBuildDelayMinutes, BaseMaximumBuildDelayMinutes);
    }

    private float GetBuildDelayMinutesWithSetting(float costMillions, PlaceableDefinition definition)
    {
        float baseMinutes = GetBuildDelayMinutes(costMillions, definition);
        float scaledMinutes = baseMinutes * buildTimeMultiplier;
        float steppedScaledMinutes = Mathf.Ceil(scaledMinutes);
        float maxDelay = GetMaximumBuildDelayForDefinition(definition);
        return Mathf.Clamp(steppedScaledMinutes, FinalMinimumBuildDelayMinutes, maxDelay);
    }

    private void SpawnPreviewFuelContainer(GlobalPosition target, Quaternion rotation)
    {
        try
        {
            DestroyPreviewFuelContainer();

            UnitDefinition? previewDefinition = GetPreviewDefinition(SelectedDefinition);
            if (previewDefinition == null)
            {
                return;
            }

            FactionHQ? hq = BuilderGameAccess.GetLocalHq();
            Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
            if (hq == null || spawner == null)
            {
                return;
            }

            Vector3 selectedUnitOffset = SelectedDefinition?.SpawnOffset ?? Vector3.zero;
            Vector3 localPosition = target.ToLocalPosition() + previewDefinition.spawnOffset + selectedUnitOffset;
            previewFuelContainer = spawner.SpawnFromUnitDefinitionInEditor(
                previewDefinition,
                localPosition.ToGlobalPosition(),
                rotation,
                hq,
                $"PREVIEW_{Time.frameCount}");
        }
        catch (Exception exception)
        {
            BuilderPlugin.Log.LogWarning($"Failed to spawn placement preview: {exception.Message}");
        }
    }

    private Unit? SpawnQueuedPlacementPreviewFuelContainer(QueuedPlacement queued)
    {
        try
        {
            UnitDefinition? previewDefinition = GetPreviewDefinition(queued.definition);
            if (previewDefinition == null)
            {
                return null;
            }

            FactionHQ? hq = BuilderGameAccess.GetLocalHq();
            Spawner? spawner = NetworkSceneSingleton<Spawner>.i;
            if (hq == null || spawner == null)
            {
                return null;
            }

            Vector3 localPosition = queued.target.ToLocalPosition() + previewDefinition.spawnOffset + queued.definition.SpawnOffset;
            return spawner.SpawnFromUnitDefinitionInEditor(
                previewDefinition,
                localPosition.ToGlobalPosition(),
                queued.rotation,
                hq,
                $"PREVIEW_{Time.frameCount}");
        }
        catch (Exception exception)
        {
            BuilderPlugin.Log.LogWarning($"Failed to spawn queued placement preview: {exception.Message}");
            return null;
        }
    }

    private void DestroyPreviewFuelContainer()
    {
        if (previewFuelContainer != null)
        {
            try
            {
                UnityEngine.Object.Destroy(previewFuelContainer.gameObject);
            }
            catch (Exception exception)
            {
                BuilderPlugin.Log.LogWarning($"Failed to destroy preview fuel container: {exception.Message}");
            }
            finally
            {
                previewFuelContainer = null;
            }
        }
    }

    private void UpdatePreviewFuelContainerRotation()
    {
        if (previewFuelContainer != null)
        {
            try
            {
                previewFuelContainer.transform.rotation = BuildPlacementRotation(pendingYawDegrees);
            }
            catch (Exception exception)
            {
                BuilderPlugin.Log.LogWarning($"Failed to update preview fuel container rotation: {exception.Message}");
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
        internal float createdAt;
        internal float elapsedBuildSeconds;
        internal Unit? previewFuelContainer;
        internal float previewSpawnedAt;

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
            this.createdAt = Time.unscaledTime;
            this.elapsedBuildSeconds = 0f;
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
        internal readonly bool isShip;
        internal readonly bool isVehicle;

        internal PendingPlacementStatus(string name, GlobalPosition target, float remainingSeconds, float jackknifeRadius, bool isShip = false, bool isVehicle = false)
        {
            this.name = name;
            this.target = target;
            this.remainingSeconds = remainingSeconds;
            this.jackknifeRadius = jackknifeRadius;
            this.isShip = isShip;
            this.isVehicle = isVehicle;
        }
    }

    private void SetStatus(string value, bool warning = false)
    {
        statusText = value;
        if (warning)
        {
            BuilderPlugin.Log.LogWarning(value);
        }
        else
        {
            BuilderPlugin.Log.LogInfo(value);
        }
    }
}

internal sealed class PlaceableDefinition
{
    internal readonly BuildingDefinition? BuildingDefinition;
    internal readonly ShipDefinition? ShipDefinition;
    internal readonly VehicleDefinition? VehicleDefinition;
    internal readonly bool IsShip;
    internal readonly bool IsVehicle;
    internal readonly string UnitName;
    internal readonly float Value;
    internal readonly GameObject? UnitPrefab;
    internal readonly Vector3 SpawnOffset;
    internal BuildCategory Category { get; set; }

    internal PlaceableDefinition(BuildingDefinition definition)
    {
        BuildingDefinition = definition;
        ShipDefinition = null;
        VehicleDefinition = null;
        IsShip = false;
        IsVehicle = false;
        UnitName = definition.unitName;
        Value = Mathf.Max(definition.value, 5f);
        UnitPrefab = definition.unitPrefab;
        SpawnOffset = definition.spawnOffset;
        Category = BuildCategory.Structures;
    }

    internal PlaceableDefinition(ShipDefinition definition)
    {
        BuildingDefinition = null;
        ShipDefinition = definition;
        VehicleDefinition = null;
        IsShip = true;
        IsVehicle = false;
        UnitName = definition.unitName;
        Value = definition.value;
        UnitPrefab = definition.unitPrefab;
        float shipVerticalOffset = Mathf.Max(2.0f, definition.height * 0.1f);
        SpawnOffset = new Vector3(0f, shipVerticalOffset, 0f);
        Category = BuildCategory.Naval;
    }

    internal PlaceableDefinition(VehicleDefinition definition)
    {
        BuildingDefinition = null;
        ShipDefinition = null;
        VehicleDefinition = definition;
        IsShip = false;
        IsVehicle = true;
        UnitName = definition.unitName;
        Value = definition.value;
        UnitPrefab = definition.unitPrefab;
        float vehicleVerticalOffset = Mathf.Max(2f, definition.height * 0.15f);
        SpawnOffset = new Vector3(0f, vehicleVerticalOffset, 0f);
        Category = BuildCategory.Vehicles;
    }
}

