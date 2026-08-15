using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace NuclearOptionCommander;

internal sealed class BuildingPlacementController : MonoBehaviour
{
    private Material? structurePreviewMaterial;
    private Material? placementBoxMaterial;
    private Material? orientationArrowMaterial;
    private Mesh? unitCubeMesh;
    private Mesh? orientationArrowMesh;
    private readonly Dictionary<int, Bounds> previewBoundsByPrefab = new();
    private CommanderBuildingPlacementService? service;
    private CommanderBuildingPlacementUi? ui;
    private CommanderCameraFollowService? cameraFollowService;
    private CommanderBuildingPlacementMapService? mapService;
    private Rect launcherRect;
    private bool launcherInitialized;
    private bool gameInProgress;
    private GameObject? hiddenVirtualMfd;
    private bool virtualMfdWasActive;

    private void Awake()
    {
        CommanderUiScale.ApplyResolutionPreset();
        service = new CommanderBuildingPlacementService();
        cameraFollowService = new CommanderCameraFollowService();
        mapService = new CommanderBuildingPlacementMapService(cameraFollowService);
        ui = new CommanderBuildingPlacementUi(service, ExitBuildMode);
        
        gameInProgress = IsGameInProgress();
        if (gameInProgress)
        {
            service.Activate();
        }
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    private void Update()
    {
        CommanderUiScale.RefreshResolutionPreset();
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

        Matrix4x4 previousMatrix = CommanderUiScale.Begin();

        CommanderUiTheme.Ensure();
        if (ShouldShowLauncher())
        {
            EnsureLauncherPosition();

            if (GUI.Button(launcherRect, GetLauncherLabel(), CommanderUiTheme.PrimaryButton))
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

        // Only render mapService UI when in build mode
        if (ui?.Visible == true)
        {
            mapService?.DrawControls();
        }

        CommanderUiScale.End(previousMatrix);
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
        if (structurePreviewMaterial != null)
        {
            Destroy(structurePreviewMaterial);
            structurePreviewMaterial = null;
        }
        if (placementBoxMaterial != null)
        {
            Destroy(placementBoxMaterial);
            placementBoxMaterial = null;
        }
        if (orientationArrowMaterial != null)
        {
            Destroy(orientationArrowMaterial);
            orientationArrowMaterial = null;
        }
        if (unitCubeMesh != null)
        {
            Destroy(unitCubeMesh);
            unitCubeMesh = null;
        }
        if (orientationArrowMesh != null)
        {
            Destroy(orientationArrowMesh);
            orientationArrowMesh = null;
        }
        previewBoundsByPrefab.Clear();
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

        if (CommanderShortcutInput.IsDown(CommanderSettings.PrimaryAction))
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
        Vector2 guiPoint = CommanderUiScale.ScreenToGui(Input.mousePosition);
        guiPoint = new Vector2(guiPoint.x + 20f, guiPoint.y + 20f);
        
        GUIStyle panelStyle = CommanderUiTheme.Panel;
        GUIStyle labelStyle = CommanderUiTheme.Label;
        GUIStyle headerStyle = CommanderUiTheme.Header;
        GUIStyle mutedStyle = CommanderUiTheme.MutedLabel;

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

        Rect panelRect = new(guiPoint.x, guiPoint.y, panelWidth, panelHeight);

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
        GUIStyle panelStyle = CommanderUiTheme.Panel;
        
        // Semi-transparent background
        GUI.Box(rect, string.Empty, panelStyle);
        
        // Subtle border
        CommanderUiTheme.DrawSubtleBorder(rect, 1f);
        
        // Subtle glow effect
        GUI.color = new Color(0.34f, 0.78f, 0.75f, 0.15f);
        GUI.DrawTexture(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    private void DrawHeadingWithArrow(Rect rect, string headingText, float yawDegrees, bool showingOrientationPreview)
    {
        GUIStyle labelStyle = CommanderUiTheme.Label;
        GUIStyle panelStyle = CommanderUiTheme.Panel;
        
        // Larger arrow box on right side - 64x64 with minimal padding
        float arrowSize = 64f;
        Rect arrowBoxRect = new(rect.xMax - arrowSize - 6f, rect.y, arrowSize, rect.height);
        
        // Draw box around arrow
        GUI.Box(arrowBoxRect, string.Empty, panelStyle);
        CommanderUiTheme.DrawSubtleBorder(arrowBoxRect, 1f);
        
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
        
        // Create a large text style for the arrow
        GUIStyle arrowStyle = new GUIStyle(GUI.skin.label)
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

    private static Vector2 RotatePoint(Vector2 point, float angleRad)
    {
        float cos = Mathf.Cos(angleRad);
        float sin = Mathf.Sin(angleRad);
        return new Vector2(
            point.x * cos - point.y * sin,
            point.x * sin + point.y * cos
        );
    }

    private static void DrawUILine(Vector2 from, Vector2 to, Texture2D texture, float width)
    {
        Vector2 direction = (to - from).normalized;
        float distance = Vector2.Distance(from, to);
        float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
        
        GUIUtility.RotateAroundPivot(angle, from);
        GUI.DrawTexture(new Rect(from.x, from.y - width * 0.5f, distance, width), texture);
        GUI.matrix = Matrix4x4.identity;
    }

    private void DrawOrientationPreview(
        CommanderBuildingPlacementService placementService,
        out float yawDegrees,
        out bool showingOrientationPreview)
    {
        yawDegrees = 0f;
        showingOrientationPreview = false;
        if (!placementService.TryGetPlacementOrientationPreview(out CommanderBuildingPlacementService.PlacementOrientationPreview preview)
            || !preview.hasDirection)
        {
            return;
        }

        yawDegrees = preview.yawDegrees;
        showingOrientationPreview = true;
    }

    private void DrawStructureOrientationPreview(BuildingDefinition definition, Vector3 localPosition, Quaternion rotation, Bounds bounds)
    {
        DrawPlacementBoxPreview(localPosition, rotation, bounds);

        EnsureStructurePreviewMaterial();
        if (structurePreviewMaterial == null || definition.unitPrefab == null)
        {
            return;
        }

        Transform root = definition.unitPrefab.transform;
        MeshFilter[] filters = definition.unitPrefab.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter filter = filters[i];
            if (filter.sharedMesh == null)
            {
                continue;
            }

            // Calculate local position relative to root using local transformation hierarchy
            Vector3 meshLocalPosition = root.InverseTransformPoint(filter.transform.position);
            Quaternion meshLocalRotation = Quaternion.Inverse(root.rotation) * filter.transform.rotation;
            
            // Build the world matrix: local mesh transform -> root offset/rotation -> final placement
            Matrix4x4 localMeshMatrix = Matrix4x4.TRS(meshLocalPosition, meshLocalRotation, Vector3.one);
            Matrix4x4 worldRoot = Matrix4x4.TRS(localPosition, rotation, Vector3.one);
            Matrix4x4 worldMatrix = worldRoot * localMeshMatrix;
            
            if (!IsFinite(worldMatrix))
            {
                continue;
            }
            Graphics.DrawMesh(filter.sharedMesh, worldMatrix, structurePreviewMaterial, 0);
        }
    }

    private void DrawPlacementBoxPreview(Vector3 localPosition, Quaternion rotation, Bounds bounds)
    {
        EnsurePlacementBoxRenderResources();
        if (placementBoxMaterial == null || unitCubeMesh == null)
        {
            return;
        }

        if (!IsValidBounds(bounds))
        {
            bounds = new Bounds(Vector3.zero, new Vector3(40f, 20f, 40f));
        }

        Vector3 worldCenter = localPosition + rotation * bounds.center;
        Matrix4x4 boxMatrix = Matrix4x4.TRS(worldCenter, rotation, bounds.size);
        if (!IsFinite(boxMatrix))
        {
            return;
        }
        Graphics.DrawMesh(unitCubeMesh, boxMatrix, placementBoxMaterial, 0);
    }

    private void DrawOrientationArrowPreview(Vector3 localPosition, Quaternion rotation, Bounds bounds)
    {
        EnsureOrientationArrowRenderResources();
        if (orientationArrowMaterial == null || orientationArrowMesh == null)
        {
            return;
        }

        Vector3 extents = bounds.extents;
        if (!IsFinite(extents))
        {
            return;
        }

        float horizontalExtent = Mathf.Max(3f, Mathf.Max(extents.x, extents.z));
        float verticalOffset = Mathf.Clamp(extents.y * 0.35f, 1.5f, 10f);
        float arrowLength = Mathf.Clamp(horizontalExtent * 1.8f, 7f, 32f);
        float arrowWidth = Mathf.Clamp(horizontalExtent * 0.7f, 2.2f, 12f);
        float arrowHeight = Mathf.Clamp(horizontalExtent * 0.45f, 1.6f, 8f);

        Vector3 anchor = localPosition + rotation * (bounds.center + Vector3.up * (extents.y + verticalOffset));
        Vector3 scale = new(arrowWidth, arrowHeight, arrowLength);
        Matrix4x4 arrowMatrix = Matrix4x4.TRS(anchor, rotation, scale);
        if (!IsFinite(arrowMatrix))
        {
            return;
        }

        Graphics.DrawMesh(orientationArrowMesh, arrowMatrix, orientationArrowMaterial, 0);
    }

    private bool TryGetPreviewBounds(GameObject prefab, out Bounds bounds)
    {
        int key = prefab.GetInstanceID();
        if (previewBoundsByPrefab.TryGetValue(key, out bounds))
        {
            return true;
        }

        Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            bounds = default;
            return false;
        }

        Transform root = prefab.transform;
        bool initialized = false;
        Bounds localBounds = default;
        for (int i = 0; i < renderers.Length; i++)
        {
            Bounds rendererBounds = renderers[i].bounds;
            Vector3 extents = rendererBounds.extents;
            Vector3[] corners =
            {
                rendererBounds.center + new Vector3( extents.x,  extents.y,  extents.z),
                rendererBounds.center + new Vector3( extents.x,  extents.y, -extents.z),
                rendererBounds.center + new Vector3( extents.x, -extents.y,  extents.z),
                rendererBounds.center + new Vector3( extents.x, -extents.y, -extents.z),
                rendererBounds.center + new Vector3(-extents.x,  extents.y,  extents.z),
                rendererBounds.center + new Vector3(-extents.x,  extents.y, -extents.z),
                rendererBounds.center + new Vector3(-extents.x, -extents.y,  extents.z),
                rendererBounds.center + new Vector3(-extents.x, -extents.y, -extents.z)
            };

            for (int cornerIndex = 0; cornerIndex < corners.Length; cornerIndex++)
            {
                Vector3 localPoint = root.InverseTransformPoint(corners[cornerIndex]);
                if (!initialized)
                {
                    localBounds = new Bounds(localPoint, Vector3.zero);
                    initialized = true;
                }
                else
                {
                    localBounds.Encapsulate(localPoint);
                }
            }
        }

        localBounds.size = Vector3.Max(localBounds.size, new Vector3(2f, 2f, 2f));
        if (!IsValidBounds(localBounds))
        {
            bounds = default;
            return false;
        }

        previewBoundsByPrefab[key] = localBounds;
        bounds = localBounds;
        return true;
    }

    private void EnsureStructurePreviewMaterial()
    {
        if (structurePreviewMaterial != null)
        {
            return;
        }

        Shader? shader = Shader.Find("Legacy Shaders/Transparent/Diffuse")
            ?? Shader.Find("Unlit/Transparent")
            ?? Shader.Find("Standard");
        if (shader == null)
        {
            return;
        }

        structurePreviewMaterial = new Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave,
            color = new Color(0.32f, 0.82f, 0.78f, 0.35f)
        };
        ConfigureTransparentMaterial(structurePreviewMaterial);
    }

    private void EnsurePlacementBoxRenderResources()
    {
        if (unitCubeMesh == null)
        {
            unitCubeMesh = CreateUnitCubeMesh();
        }

        if (placementBoxMaterial != null)
        {
            return;
        }

        Shader? shader = Shader.Find("Legacy Shaders/Transparent/Diffuse")
            ?? Shader.Find("Unlit/Transparent")
            ?? Shader.Find("Standard");
        if (shader == null)
        {
            return;
        }

        placementBoxMaterial = new Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave,
            color = new Color(0.2f, 0.88f, 0.28f, 0.26f)
        };
        ConfigureTransparentMaterial(placementBoxMaterial);
    }

    private void EnsureOrientationArrowRenderResources()
    {
        if (orientationArrowMesh == null)
        {
            orientationArrowMesh = CreateOrientationArrowMesh();
        }

        if (orientationArrowMaterial != null)
        {
            return;
        }

        Shader? shader = Shader.Find("Legacy Shaders/Transparent/Diffuse")
            ?? Shader.Find("Unlit/Transparent")
            ?? Shader.Find("Standard");
        if (shader == null)
        {
            return;
        }

        orientationArrowMaterial = new Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave,
            color = new Color(0.2f, 0.95f, 0.35f, 0.9f)
        };
        ConfigureTransparentMaterial(orientationArrowMaterial);
    }

    private static void ConfigureTransparentMaterial(Material material)
    {
        if (material.HasProperty("_Mode"))
        {
            material.SetFloat("_Mode", 3f);
        }

        material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = (int)RenderQueue.Transparent;
    }

    private static Mesh CreateOrientationArrowMesh()
    {
        Mesh mesh = new()
        {
            name = "PlacementPreviewOrientationArrow"
        };

        const float shaftHalfWidth = 0.12f;
        const float shaftHalfHeight = 0.12f;
        const float shaftEndZ = 0.62f;
        const float headHalfWidth = 0.28f;
        const float headHalfHeight = 0.28f;

        Vector3[] vertices =
        {
            new(-shaftHalfWidth, -shaftHalfHeight, 0f),
            new( shaftHalfWidth, -shaftHalfHeight, 0f),
            new( shaftHalfWidth,  shaftHalfHeight, 0f),
            new(-shaftHalfWidth,  shaftHalfHeight, 0f),
            new(-shaftHalfWidth, -shaftHalfHeight, shaftEndZ),
            new( shaftHalfWidth, -shaftHalfHeight, shaftEndZ),
            new( shaftHalfWidth,  shaftHalfHeight, shaftEndZ),
            new(-shaftHalfWidth,  shaftHalfHeight, shaftEndZ),
            new(-headHalfWidth, -headHalfHeight, shaftEndZ),
            new( headHalfWidth, -headHalfHeight, shaftEndZ),
            new( headHalfWidth,  headHalfHeight, shaftEndZ),
            new(-headHalfWidth,  headHalfHeight, shaftEndZ),
            new(0f, 0f, 1f)
        };

        int[] triangles =
        {
            0, 2, 1, 0, 3, 2,
            1, 2, 6, 1, 6, 5,
            5, 6, 7, 5, 7, 4,
            4, 7, 3, 4, 3, 0,
            3, 7, 6, 3, 6, 2,
            4, 0, 1, 4, 1, 5,
            8, 9, 12,
            9, 10, 12,
            10, 11, 12,
            11, 8, 12,
            8, 11, 10,
            8, 10, 9
        };

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.hideFlags = HideFlags.HideAndDontSave;
        return mesh;
    }

    private static bool IsValidBounds(Bounds bounds)
    {
        return IsFinite(bounds.center)
            && IsFinite(bounds.extents)
            && bounds.extents.sqrMagnitude > Mathf.Epsilon;
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(Matrix4x4 matrix)
    {
        for (int i = 0; i < 16; i++)
        {
            if (!IsFinite(matrix[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static Mesh CreateUnitCubeMesh()
    {
        Mesh mesh = new()
        {
            name = "PlacementPreviewUnitCube"
        };

        Vector3[] vertices =
        {
            new(-0.5f, -0.5f,  0.5f),
            new( 0.5f, -0.5f,  0.5f),
            new( 0.5f,  0.5f,  0.5f),
            new(-0.5f,  0.5f,  0.5f),
            new(-0.5f, -0.5f, -0.5f),
            new( 0.5f, -0.5f, -0.5f),
            new( 0.5f,  0.5f, -0.5f),
            new(-0.5f,  0.5f, -0.5f)
        };

        int[] triangles =
        {
            0, 2, 1, 0, 3, 2,
            1, 2, 6, 1, 6, 5,
            5, 6, 7, 5, 7, 4,
            4, 7, 3, 4, 3, 0,
            3, 7, 6, 3, 6, 2,
            4, 0, 1, 4, 1, 5
        };

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mesh.hideFlags = HideFlags.HideAndDontSave;
        return mesh;
    }

    private void DrawPendingPlacementCountdowns()
    {
        if (service == null)
        {
            return;
        }

        CommanderBuildingPlacementService.PendingPlacementStatus[] pending = service.GetPendingPlacementStatuses();
        if (pending.Length == 0)
        {
            return;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null)
        {
            return;
        }

        GUIStyle style = CommanderUiTheme.Panel;
        for (int i = 0; i < pending.Length; i++)
        {
            Vector3 screenPoint = camera.WorldToScreenPoint(pending[i].target.ToLocalPosition());
            if (screenPoint.z <= 0f)
            {
                continue;
            }

            Vector2 guiPoint = CommanderUiScale.ScreenToGui(new Vector2(screenPoint.x, screenPoint.y));
            string countdown = FormatCountdown(pending[i].remainingSeconds);
            string text = pending[i].remainingSeconds < 0f
                ? $"{pending[i].name}\nWAITING\nJackknife < {pending[i].jackknifeRadius:0}m"
                : $"{pending[i].name}\n{countdown}";
            float width = Mathf.Max(170f, style.CalcSize(new GUIContent(text)).x + 18f);
            float height = pending[i].remainingSeconds < 0f ? 60f : 48f;
            Rect marker = new(guiPoint.x + 12f, guiPoint.y - (pending[i].remainingSeconds < 0f ? 72f : 52f), width, height);
            GUI.Box(marker, text, style);
            CommanderUiTheme.DrawSubtleBorder(marker, 1f);
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

        float centerY = CommanderUiScale.Height * 0.5f;
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