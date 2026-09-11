using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace NuclearOptionBuilder;

internal sealed class BuildingPlacementController : MonoBehaviour
{
    private const float PendingPlacementUiMaxDistance = 2000f;

    private static GUIStyle? arrowStyle;

    private BuilderBuildingPlacementService? service;
    private BuilderBuildingPlacementUi? ui;
    private BuilderCameraFollowService? cameraFollowService;
    private BuilderBuildingPlacementMapService? mapService;
    private Rect launcherRect;
    private bool launcherInitialized;
    private bool gameInProgress;
    private bool previousMapMaximized;
    private GameObject? hiddenVirtualMfd;
    private bool virtualMfdWasActive;

    private void Awake()
    {
        BuilderUiScale.ApplyResolutionPreset();
        service = new BuilderBuildingPlacementService();
        cameraFollowService = new BuilderCameraFollowService();
        mapService = new BuilderBuildingPlacementMapService(cameraFollowService);
        ui = new BuilderBuildingPlacementUi(service, ExitBuildMode);
        
        gameInProgress = IsGameInProgress();
        if (gameInProgress)
        {
            service.Activate();
        }
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    private void Update()
    {
        BuilderUiScale.RefreshResolutionPreset();
        SyncLauncherVisibility();

        bool nowInProgress = IsGameInProgress();
        if (nowInProgress != gameInProgress)
        {
            gameInProgress = nowInProgress;
            if (gameInProgress)
            {
                service?.Activate();
            }
            else
            {
                ui?.Hide();
                service?.Deactivate();
                service?.ResetSession();
                mapService?.Close();
                mapService?.ResetSession();
            }
        }

        if (!gameInProgress)
        {
            return;
        }

        service?.TickPersistent();
        mapService?.Tick();

        // Auto-close plugin map if default game map is opened
        bool currentMapMaximized = DynamicMap.mapMaximized;
        if (currentMapMaximized && !previousMapMaximized && mapService?.IsActive == true)
        {
            // Default map just opened, close plugin map to restore MFD
            mapService?.Close();
        }
        previousMapMaximized = currentMapMaximized;

        if (service?.AwaitingPlacementSelection == true)
        {
            service.TickActive();
            HandleWorldPlacementClick();
        }
    }

    private void OnGUI()
    {
        if (!gameInProgress)
        {
            return;
        }

        Matrix4x4 previousMatrix = BuilderUiScale.Begin();

        BuilderUiTheme.Ensure();
        if (ShouldShowLauncher())
        {
            EnsureLauncherPosition();

            if (GUI.Button(launcherRect, GetLauncherLabel(), BuilderUiTheme.PrimaryButton))
            {
                if (ui?.Visible == true)
                {
                    if (ui.Minimized)
                    {
                        // Minimized -> Open (restore window)
                        ui.Unminimize();
                    }
                    else
                    {
                        // Visible -> Minimize (keep map open)
                        ui.Minimize();
                    }
                }
                else
                {
                    // Closed -> Open
                    ui?.Toggle();
                    mapService?.Open();
                }
            }

            if (ui?.Visible == true)
            {
                ui.Draw();
            }
        }

        DrawPendingPlacementCountdowns();

        if (service?.AwaitingPlacementSelection == true)
        {
            DrawPlacementCursor();
        }

        // Only render mapService UI when in build mode and game map isn't open
        if (ui?.Visible == true && !DynamicMap.mapMaximized)
        {
            mapService?.DrawControls();
        }

        BuilderUiScale.End(previousMatrix);
    }

    private void OnRenderObject()
    {
        // 3D rendering disabled - using UI arrow indicator instead
    }

    private void OnDestroy()
    {
        RestoreVirtualMfd();
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        service?.Deactivate();
    }

    private void OnActiveSceneChanged(Scene _, Scene __)
    {
        service?.ResetSession();
        ui?.Hide();
        ui?.ResetPosition();
        RestoreVirtualMfd();
    }

    private void SyncLauncherVisibility()
    {
        if (ShouldShowLauncher())
        {
            return;
        }

        if (ui?.Visible == true)
        {
            ui.Hide();
        }
    }

    private void HandleWorldPlacementClick()
    {
        if (ui?.Visible == true && ui.ContainsScreenPoint(Input.mousePosition))
        {
            return;
        }

        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (DynamicMap.mapMaximized && dynamicMap != null && dynamicMap.IsCursorInMapRectangle())
        {
            return;
        }

        if (BuilderShortcutInput.IsDown(BuilderSettings.PrimaryAction))
        {
            service?.TrySetPlacementFromWorld(Input.mousePosition);
        }
    }

    private void DrawPlacementCursor()
    {
        if (service == null)
        {
            return;
        }

        DrawOrientationPreview(
            service,
            out float previewYawDegrees,
            out bool showingOrientationPreview);

        // Position floating panel offset from cursor (right/below)
        Vector2 guiPoint = BuilderUiScale.ScreenToGui(Input.mousePosition);
        
        GUIStyle panelStyle = BuilderUiTheme.Panel;
        GUIStyle labelStyle = BuilderUiTheme.Label;
        GUIStyle headerStyle = BuilderUiTheme.Header;
        GUIStyle mutedStyle = BuilderUiTheme.MutedLabel;

        // Get text content
        string buildingName = service.PreviewLabel;
        string headingText = $"{Mathf.RoundToInt(previewYawDegrees):000}°";
        string hint = showingOrientationPreview
            ? "Mouse wheel: rotate | Enter: confirm"
            : "Click: place | Rotate after placement";

        // Measure content for panel sizing
        float nameWidth = Mathf.Max(140f, headerStyle.CalcSize(new GUIContent(buildingName)).x);
        float headingWidth = Mathf.Max(200f, labelStyle.CalcSize(new GUIContent(headingText + "  ")).x + 100f); // Extra space for larger arrow
        float hintWidth = Mathf.Max(220f, mutedStyle.CalcSize(new GUIContent(hint)).x);

        float panelWidth = Mathf.Max(nameWidth, headingWidth, hintWidth) + 20f;
        float panelHeight = 24f + 72f + 24f + 20f; // Increased heading area for larger arrow

        // Flip to the left/above the cursor instead of overflowing the screen edge
        const float cursorOffset = 20f;
        float panelX = guiPoint.x + panelWidth + cursorOffset > BuilderUiScale.Width
            ? guiPoint.x - cursorOffset - panelWidth
            : guiPoint.x + cursorOffset;
        float panelY = guiPoint.y + panelHeight + cursorOffset > BuilderUiScale.Height
            ? guiPoint.y - cursorOffset - panelHeight
            : guiPoint.y + cursorOffset;

        Rect panelRect = new(panelX, panelY, panelWidth, panelHeight);

        // Draw floating panel with subtle shadow
        DrawFloatingPanel(panelRect);

        // Building name header
        Rect nameRect = new(panelRect.x + 10f, panelRect.y + 8f, panelRect.width - 20f, 20f);
        GUI.Label(nameRect, buildingName, headerStyle);

        // Heading with large arrow on right
        Rect headingAreaRect = new(panelRect.x + 10f, panelRect.y + 32f, panelRect.width - 20f, 72f); // Larger height for arrow
        DrawHeadingWithArrow(headingAreaRect, headingText, previewYawDegrees, showingOrientationPreview);

        // Hint at bottom
        Rect hintRect = new(panelRect.x + 10f, panelRect.yMax - 26f, panelRect.width - 20f, 18f);
        GUI.Label(hintRect, hint, mutedStyle);
    }

    private void DrawFloatingPanel(Rect rect)
    {
        GUIStyle panelStyle = BuilderUiTheme.Panel;
        
        // Semi-transparent background
        GUI.Box(rect, string.Empty, panelStyle);
        
        // Subtle border
        BuilderUiTheme.DrawSubtleBorder(rect, 1f);
        
        // Subtle glow effect
        GUI.color = new Color(0.34f, 0.78f, 0.75f, 0.15f);
        GUI.DrawTexture(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    private void DrawHeadingWithArrow(Rect rect, string headingText, float yawDegrees, bool showingOrientationPreview)
    {
        GUIStyle labelStyle = BuilderUiTheme.Label;
        GUIStyle panelStyle = BuilderUiTheme.Panel;
        
        // Larger arrow box on right side - 64x64 with minimal padding
        float arrowSize = 64f;
        Rect arrowBoxRect = new(rect.xMax - arrowSize - 6f, rect.y, arrowSize, rect.height);
        
        // Draw box around arrow
        GUI.Box(arrowBoxRect, string.Empty, panelStyle);
        BuilderUiTheme.DrawSubtleBorder(arrowBoxRect, 1f);
        
        // Draw arrow inside box - centered with minimal padding to prevent clipping
        Rect arrowRect = new(arrowBoxRect.x + 4f, arrowBoxRect.y + 4f, arrowSize - 8f, arrowSize - 8f);
        DrawCompassArrow(arrowRect, yawDegrees, showingOrientationPreview);
        
        // Heading text on left
        Rect textRect = new(rect.x, rect.y + 20f, rect.width - arrowSize - 12f, 32f);
        GUI.Label(textRect, "HEADING " + headingText, labelStyle);
    }

    private void DrawCompassArrow(Rect rect, float yawDegrees, bool showingOrientationPreview)
    {
        if (!showingOrientationPreview)
        {
            return;
        }
        
        // Get camera yaw and compensate arrow for camera rotation
        // This makes the arrow rotate opposite to camera, keeping world direction constant
        float cameraYaw = Camera.main != null ? Camera.main.transform.eulerAngles.y : 0f;
        float screenAdjustedYaw = yawDegrees - cameraYaw;
        
        // Draw rotating arrow at exact yaw angle
        DrawRotatingArrowIndicator(rect, screenAdjustedYaw);
    }

    private void DrawRotatingArrowIndicator(Rect rect, float yawDegrees)
    {
        // Get compass direction from yaw degrees
        string arrowSymbol = GetCompassArrow(yawDegrees);

        // Cache the style so it isn't reallocated every frame
        arrowStyle ??= new GUIStyle(GUI.skin.label)
        {
            fontSize = 36,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(0.4f, 1.0f, 0.5f, 1f) } // Bright green
        };

        // Draw the arrow character centered in rect
        GUI.Label(rect, arrowSymbol, arrowStyle);
    }

    private string GetCompassArrow(float yawDegrees)
    {
        // Normalize to 0-360
        float normalizedYaw = yawDegrees % 360f;
        if (normalizedYaw < 0f) normalizedYaw += 360f;
        
        // Map to 8 compass directions
        if (normalizedYaw < 22.5f || normalizedYaw >= 337.5f) return "↑";   // N
        if (normalizedYaw < 67.5f) return "↗";   // NE
        if (normalizedYaw < 112.5f) return "→";  // E
        if (normalizedYaw < 157.5f) return "↘";  // SE
        if (normalizedYaw < 202.5f) return "↓";  // S
        if (normalizedYaw < 247.5f) return "↙";  // SW
        if (normalizedYaw < 292.5f) return "←";  // W
        return "↖";                                 // NW
    }

    private void DrawOrientationPreview(
        BuilderBuildingPlacementService placementService,
        out float yawDegrees,
        out bool showingOrientationPreview)
    {
        yawDegrees = 0f;
        showingOrientationPreview = false;
        if (!placementService.TryGetPlacementOrientationPreview(out BuilderBuildingPlacementService.PlacementOrientationPreview preview)
            || !preview.hasDirection)
        {
            return;
        }

        yawDegrees = preview.yawDegrees;
        showingOrientationPreview = true;
    }

    private void DrawPendingPlacementCountdowns()
    {
        if (service == null)
        {
            return;
        }

        BuilderBuildingPlacementService.PendingPlacementStatus[] pending = service.GetPendingPlacementStatuses();
        if (pending.Length == 0)
        {
            return;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null)
        {
            return;
        }

        GUIStyle style = BuilderUiTheme.Panel;
        for (int i = 0; i < pending.Length; i++)
        {
            Vector3 localPosition = pending[i].target.ToLocalPosition();
            if (Vector3.Distance(camera.transform.position, localPosition) > PendingPlacementUiMaxDistance)
            {
                continue;
            }

            Vector3 screenPoint = camera.WorldToScreenPoint(localPosition);
            if (screenPoint.z <= 0f)
            {
                continue;
            }

            Vector2 guiPoint = BuilderUiScale.ScreenToGui(new Vector2(screenPoint.x, screenPoint.y));
            string countdown = FormatCountdown(pending[i].remainingSeconds);
            string depotType = pending[i].isShip ? "Large Factory" : pending[i].isVehicle ? "Vehicle Depot" : "Jackknife";
            string text = pending[i].remainingSeconds < 0f
                ? $"{pending[i].name}\nWAITING\n{depotType} < {pending[i].jackknifeRadius:0}m"
                : $"{pending[i].name}\n{countdown}";
            float width = Mathf.Max(190f, style.CalcSize(new GUIContent(text)).x + 20f);
            float height = pending[i].remainingSeconds < 0f ? 60f : 48f;
            Rect marker = new(guiPoint.x + 12f, guiPoint.y - (pending[i].remainingSeconds < 0f ? 72f : 52f), width, height);
            GUI.Box(marker, text, style);
            BuilderUiTheme.DrawSubtleBorder(marker, 1f);

            // Flip to the marker's left if the button would overflow the screen's right edge
            float cancelButtonX = marker.xMax + 6f;
            if (cancelButtonX + height > BuilderUiScale.Width)
            {
                cancelButtonX = marker.x - 6f - height;
            }
            Rect cancelButton = new(cancelButtonX, marker.y, height, height);
            // Early return required: `pending` is a snapshot, so indices desync from pendingPlacements after a cancel
            if (GUI.Button(cancelButton, "X", BuilderUiTheme.DangerButton))
            {
                service.CancelPendingPlacement(i);
                return;
            }
        }
    }

    private static string FormatCountdown(float remainingSeconds)
    {
        int totalSeconds = Mathf.CeilToInt(Mathf.Max(0f, remainingSeconds));
        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;
        return $"{minutes:00}:{seconds:00}";
    }

    private void EnsureLauncherPosition()
    {
        if (launcherInitialized)
        {
            return;
        }

        float centerY = BuilderUiScale.Height * 0.5f;
        launcherRect = new Rect(10f, Mathf.Max(10f, centerY + 48f), 52f, 84f);
        launcherInitialized = true;
    }

    private string GetLauncherLabel()
    {
        if (ui?.Visible == true)
        {
            return ui.Minimized ? "B\nU\nI\nL\nD\n-" : "B\nU\nI\nL\nD\n<";
        }
        return "B\nU\nI\nL\nD";
    }

    private static bool IsGameInProgress()
    {
        return GameManager.gameState == GameState.SinglePlayer
            || GameManager.gameState == GameState.Multiplayer;
    }

    private bool ShouldShowLauncher()
    {
        // Show button when map is open or when UI is already visible
        return DynamicMap.mapMaximized || ui?.Visible == true;
    }

    private void ExitBuildMode()
    {
        ui?.Hide();
        mapService?.Close();  // This calls RestoreVirtualMfd internally
        service?.CancelPlacementSelection();
    }



    private void HideFullMapPanels()
    {
        HideVirtualMfd();
        MinimizeGameMap();
        // Keep cursor visible even with map closed
        DynamicMap.AllowedToOpen = true;
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
        RestoreGameMap();
    }

    private void MinimizeGameMap()
    {
        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap != null && DynamicMap.mapMaximized)
        {
            dynamicMap.Minimize();
        }
    }

    private void RestoreGameMap()
    {
        // Close the map and reset to default state
        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap != null && DynamicMap.mapMaximized)
        {
            dynamicMap.Minimize();
        }
    }
}