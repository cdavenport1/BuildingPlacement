using System;
using System.Reflection;
using UnityEngine;
using HarmonyLib;

namespace NuclearOptionCommander;

internal sealed class CommanderBuildingPlacementMapService
{
    private const float TacticalMapSize = 675f;
    private const float TacticalMapMargin = 12f;
    private const float HeaderHeight = 30f;

    private static readonly MethodInfo? JumpCameraToMethod = AccessTools.Method(typeof(DynamicMap), "JumpCameraTo");

    internal static CommanderBuildingPlacementMapService? Instance { get; private set; }

    private readonly CommanderCameraFollowService cameraFollowService;
    private DynamicMap? activeMap;
    private bool positionInitialized;
    private bool helpVisible;
    private Rect mapWindowRect;
    private float lastAppliedUiScale = -1f;
    private Vector2 lastAppliedMapPosition = new(float.NaN, float.NaN);
    private Vector3[] mapCorners = new Vector3[4];
    private GameObject? hiddenVirtualMfd;
    private bool virtualMfdWasActive;

    internal CommanderBuildingPlacementMapService(CommanderCameraFollowService cameraFollowService)
    {
        this.cameraFollowService = cameraFollowService;
        Instance = this;
    }

    internal bool IsActive => activeMap != null && DynamicMap.mapMaximized;

    internal void Open()
    {
        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap == null) return;

        DynamicMap.AllowedToOpen = true;

        activeMap = dynamicMap;
        helpVisible = false;
        EnsureInitialPosition();
        ApplyLayout();
        HideFullMapPanels();
    }

    internal void Close()
    {
        // Restore the MFD to its previous state
        RestoreVirtualMfd();
        
        // Minimize the game map
        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap != null && DynamicMap.mapMaximized)
        {
            dynamicMap.Minimize();
        }
        
        // Restore GameplayUI panels
        RestoreFullMapPanels();
        
        activeMap = null;
        helpVisible = false;
    }

    internal void Tick()
    {
        if (!IsActive) return;

        mapWindowRect.width = TacticalMapSize;
        mapWindowRect.height = TacticalMapSize + HeaderHeight;
        mapWindowRect = CommanderUiTheme.ClampWindow(mapWindowRect, TacticalMapMargin);
        
        if (!Mathf.Approximately(lastAppliedUiScale, CommanderUiScale.Scale)
            || lastAppliedMapPosition != mapWindowRect.position)
        {
            ApplyLayout();
        }

        cameraFollowService.Tick();
    }

    internal void DrawControls()
    {
        CommanderUiTheme.Ensure();
        
        // Don't render if NOCommander mode is active
        if (IsNoCommanderActive())
            return;
        
        if (!IsActive)
            return;
        
        Rect header = new(mapWindowRect.x, mapWindowRect.y, mapWindowRect.width, HeaderHeight);
        GUI.Box(header, string.Empty, CommanderUiTheme.Panel);
        GUI.Label(new Rect(header.x + 10f, header.y + 3f, 42f, 24f), "MAP", CommanderUiTheme.Header);

        bool oldEnabled = GUI.enabled;
        Rect cameraGroup = new(header.xMax - 380f, header.y + 2f, 312f, 26f);
        CommanderUiTheme.DrawSubtleBorder(cameraGroup, 1f);
        
        GUI.Label(new Rect(cameraGroup.x + 4f, cameraGroup.y + 1f, 38f, 24f), "CAM", CommanderUiTheme.MutedLabel);
        GUI.enabled = oldEnabled && cameraFollowService.CanFollow;
        
        if (GUI.Button(new Rect(cameraGroup.x + 42f, cameraGroup.y + 2f, 80f, 22f),
            cameraFollowService.Enabled ? "FOLLOW POS" : "FOLLOW",
            cameraFollowService.Enabled ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            cameraFollowService.Toggle();
        }
        
        if (GUI.Button(new Rect(cameraGroup.x + 126f, cameraGroup.y + 2f, 80f, 22f), "CENTER", CommanderUiTheme.Button))
        {
            cameraFollowService.CenterOnSelection();
        }
        
        if (GUI.Button(new Rect(cameraGroup.x + 210f, cameraGroup.y + 2f, 80f, 22f),
            cameraFollowService.PovMode ? "POV ON" : "POV",
            cameraFollowService.PovMode ? CommanderUiTheme.SelectedButton : CommanderUiTheme.Button))
        {
            cameraFollowService.TogglePov();
        }
        
        GUI.enabled = oldEnabled;
        
        if (GUI.Button(new Rect(header.xMax - 60f, header.y + 3f, 26f, 24f), "?", CommanderUiTheme.HelpButton))
        {
            helpVisible = !helpVisible;
        }
        
        if (GUI.Button(new Rect(header.xMax - 30f, header.y + 3f, 26f, 24f), "X", CommanderUiTheme.DangerButton))
        {
            Close();
            return;
        }

        Rect mapRect = GetMapGuiRect();
        CommanderUiTheme.DrawSubtleBorder(new Rect(mapRect.x - 2f, mapRect.y - 2f, mapRect.width + 4f, mapRect.height + 4f), 1f);
        
        if (helpVisible)
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(mapRect.x + 10f, mapRect.y + 10f, mapRect.width - 20f, 82f),
                "LMB selects map icons; RMB issues Basegame orders. FOLLOW tracks position, CENTER jumps once, and POV provides a movable view. Map navigation features.");
        }
    }

    internal bool ContainsScreenPoint(Vector2 screenPoint)
    {
        Vector2 guiPoint = CommanderUiScale.ScreenToGui(screenPoint);
        return IsActive && mapWindowRect.Contains(guiPoint);
    }

    internal void ResetSession()
    {
        activeMap = null;
        helpVisible = false;
        positionInitialized = false;
        lastAppliedUiScale = -1f;
        lastAppliedMapPosition = new Vector2(float.NaN, float.NaN);
    }

    private void EnsureInitialPosition()
    {
        if (positionInitialized) return;

        mapWindowRect = new Rect(
            CommanderUiScale.Width - TacticalMapMargin - TacticalMapSize,
            TacticalMapMargin,
            TacticalMapSize,
            TacticalMapSize + HeaderHeight);
        positionInitialized = true;
    }

    private Rect GetMapGuiRect()
    {
        return new Rect(mapWindowRect.x, mapWindowRect.y + HeaderHeight, TacticalMapSize, TacticalMapSize);
    }

    private void ApplyLayout()
    {
        if (activeMap == null) return;

        Rect mapRect = GetMapGuiRect();
        RectTransform rectTransform = activeMap.GetComponent<RectTransform>();
        rectTransform.localScale = Vector3.one;
        
        RectTransform measuredTransform = activeMap.mapBackground != null
            ? activeMap.mapBackground.rectTransform
            : rectTransform;
        
        measuredTransform.GetWorldCorners(mapCorners);
        Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(null, mapCorners[0]);
        Vector2 bottomRight = RectTransformUtility.WorldToScreenPoint(null, mapCorners[3]);
        float sourcePixelSize = Mathf.Max(Vector2.Distance(bottomLeft, bottomRight), 1f);
        float scale = TacticalMapSize * CommanderUiScale.Scale / sourcePixelSize;
        rectTransform.localScale = Vector3.one * scale;
        
        Vector2 screenCenter = CommanderUiScale.GuiToScreen(mapRect.center);
        rectTransform.position = new Vector3(screenCenter.x, screenCenter.y, rectTransform.position.z);
        
        lastAppliedUiScale = CommanderUiScale.Scale;
        lastAppliedMapPosition = mapWindowRect.position;
    }

    private static void HideFullMapPanels()
    {
        GameplayUI? gameplayUi = SceneSingleton<GameplayUI>.i;
        gameplayUi?.HideSelectAirbase();
        gameplayUi?.HideSpectatorPanel();
        CommanderBuildingPlacementMapService.Instance?.HideVirtualMfd();
    }

    private void HideVirtualMfd()
    {
        GameObject? virtualMfd = SceneSingleton<MapOptions>.i?.screen?.virtualMFD?.gameObject;
        if (virtualMfd == null)
        {
            return;
        }

        if (hiddenVirtualMfd != virtualMfd)
        {
            RestoreVirtualMfd();
            hiddenVirtualMfd = virtualMfd;
            virtualMfdWasActive = virtualMfd.activeSelf;
            virtualMfd.SetActive(false);
        }
    }

    private void RestoreVirtualMfd()
    {
        if (hiddenVirtualMfd != null)
        {
            hiddenVirtualMfd.SetActive(virtualMfdWasActive);
        }
        hiddenVirtualMfd = null;
        virtualMfdWasActive = false;
        RestoreFullMapPanels();
    }

    private static void RestoreFullMapPanels()
    {
        // Restore GameplayUI panels that were hidden during build mode
        GameplayUI? gameplayUi = SceneSingleton<GameplayUI>.i;
        if (gameplayUi != null)
        {
            try
            {
                // Try to restore panels if methods exist
                var showSelectAirbaseMethod = gameplayUi.GetType().GetMethod("ShowSelectAirbase");
                var showSpectatorPanelMethod = gameplayUi.GetType().GetMethod("ShowSpectatorPanel");
                showSelectAirbaseMethod?.Invoke(gameplayUi, null);
                showSpectatorPanelMethod?.Invoke(gameplayUi, null);
            }
            catch
            {
                // If methods don't exist, that's okay
            }
        }
    }

    private bool IsNoCommanderActive()
    {
        try
        {
            var commanderPluginType = Type.GetType("NuclearOptionCommander.CommanderPlugin, NuclearOptionCommander");
            if (commanderPluginType == null) return false;

            var instanceProp = commanderPluginType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (instanceProp == null) return false;

            var instance = instanceProp.GetValue(null);
            if (instance == null) return false;

            var isCommanderActiveProp = commanderPluginType.GetProperty("IsCommanderModeActive", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (isCommanderActiveProp == null) return false;

            return (bool?)isCommanderActiveProp.GetValue(instance) ?? false;
        }
        catch
        {
            return false;
        }
    }
}
