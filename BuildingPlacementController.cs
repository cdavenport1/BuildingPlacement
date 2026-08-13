using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NuclearOptionCommander;

internal sealed class BuildingPlacementController : MonoBehaviour
{
    private readonly CommanderExternalActivationGate externalActivationGate = new();
    private Texture2D? orientationLineTexture;
    private Material? structurePreviewMaterial;
    private Material? placementBoxMaterial;
    private Mesh? unitCubeMesh;
    private readonly Dictionary<int, Bounds> previewBoundsByPrefab = new();
    private CommanderBuildingPlacementService? service;
    private CommanderBuildingPlacementUi? ui;
    private Rect launcherRect;
    private bool launcherInitialized;
    private bool gameInProgress;
    private bool buildButtonActive = true;

    private void Awake()
    {
        CommanderUiScale.ApplyResolutionPreset();
        service = new CommanderBuildingPlacementService();
        ui = new CommanderBuildingPlacementUi(service);
        gameInProgress = IsGameInProgress();
        buildButtonActive = externalActivationGate.IsActive;
        if (gameInProgress && buildButtonActive)
        {
            service.Activate();
        }
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    private void Update()
    {
        CommanderUiScale.RefreshResolutionPreset();
        RefreshBuildButtonActivation();
        SyncLauncherVisibility();

        bool nowInProgress = IsGameInProgress();
        if (nowInProgress != gameInProgress)
        {
            gameInProgress = nowInProgress;
            if (gameInProgress && buildButtonActive)
            {
                service?.Activate();
            }
            else
            {
                ui?.Hide();
                service?.Deactivate();
                service?.ResetSession();
            }
        }

        if (!gameInProgress)
        {
            return;
        }

        if (!buildButtonActive)
        {
            return;
        }

        service?.TickPersistent();

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

            bool previousEnabled = GUI.enabled;
            Color previousColor = GUI.color;
            GUI.enabled = previousEnabled && buildButtonActive;
            if (!buildButtonActive)
            {
                GUI.color = new Color(0.6f, 0.6f, 0.6f, 0.95f);
            }
            GUIStyle launcherStyle = buildButtonActive
                ? CommanderUiTheme.PrimaryButton
                : CommanderUiTheme.Button;
            if (GUI.Button(launcherRect, GetLauncherLabel(), launcherStyle))
            {
                if (ui?.Visible == true)
                {
                    ui.Hide();
                }
                else
                {
                    ui?.Toggle();
                }
            }
            GUI.color = previousColor;
            GUI.enabled = previousEnabled;

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

        CommanderUiScale.End(previousMatrix);
    }

    private void OnRenderObject()
    {
        if (!gameInProgress || service == null)
        {
            return;
        }

        if (!service.TryGetStructureOrientationPreview(
            out BuildingDefinition definition,
            out Vector3 localPosition,
            out Quaternion rotation))
        {
            return;
        }

        DrawStructureOrientationPreview(definition, localPosition, rotation);
    }

    private void OnDestroy()
    {
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        service?.Deactivate();
        if (orientationLineTexture != null)
        {
            Destroy(orientationLineTexture);
            orientationLineTexture = null;
        }
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
        if (unitCubeMesh != null)
        {
            Destroy(unitCubeMesh);
            unitCubeMesh = null;
        }
        previewBoundsByPrefab.Clear();
    }

    private void OnActiveSceneChanged(Scene _, Scene __)
    {
        service?.ResetSession();
        ui?.Hide();
        ui?.ResetPosition();
    }

    private void RefreshBuildButtonActivation()
    {
        bool nowActive = externalActivationGate.IsActive;
        if (nowActive == buildButtonActive)
        {
            return;
        }

        buildButtonActive = nowActive;
        if (buildButtonActive)
        {
            if (gameInProgress)
            {
                service?.Activate();
            }

            return;
        }

        ui?.Hide();
        service?.Deactivate();
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
            out bool showingOrientationPreview,
            out GlobalPosition orientationTarget,
            out bool hasOrientationTarget);

        Vector2 guiPoint = CommanderUiScale.ScreenToGui(Input.mousePosition);
        string label = service.PreviewLabel;
        GUIStyle style = CommanderUiTheme.Panel;
        float width = Mathf.Max(90f, style.CalcSize(new GUIContent(label)).x + 18f);
        Rect marker = new(guiPoint.x + 14f, guiPoint.y + 14f, width, 28f);
        GUI.Box(marker, label, style);
        CommanderUiTheme.DrawFrame(marker, 1f);

        string headingText = $"HEADING {Mathf.RoundToInt(previewYawDegrees):000}";
        float headingWidth = Mathf.Max(140f, style.CalcSize(new GUIContent(headingText)).x + 20f);
        Rect headingRect = new(guiPoint.x + 14f, guiPoint.y + 46f, headingWidth, 28f);
        GUI.Box(headingRect, headingText, style);
        CommanderUiTheme.DrawFrame(headingRect, 1f);

        string hint = showingOrientationPreview
            ? "Mouse wheel rotates heading. Press Enter to confirm"
            : "Click location first, then rotate with mouse wheel";
        float hintWidth = Mathf.Max(360f, style.CalcSize(new GUIContent(hint)).x + 22f);
        Rect hintRect = new(guiPoint.x + 14f, guiPoint.y + 78f, hintWidth, 36f);
        GUI.Box(hintRect, string.Empty, style);
        GUI.Label(new Rect(hintRect.x + 8f, hintRect.y + 7f, hintRect.width - 16f, hintRect.height - 12f), hint, CommanderUiTheme.Label);
        CommanderUiTheme.DrawFrame(hintRect, 1f);

        Vector2 markerAnchor = guiPoint;
        if (hasOrientationTarget && TryGetGuiPointForTarget(orientationTarget, out Vector2 targetGuiPoint))
        {
            markerAnchor = targetGuiPoint;
        }

        DrawCursorPlacementBox(markerAnchor, previewYawDegrees, showingOrientationPreview);
    }

    private void DrawOrientationPreview(
        CommanderBuildingPlacementService placementService,
        out float yawDegrees,
        out bool showingOrientationPreview,
        out GlobalPosition target,
        out bool hasTarget)
    {
        yawDegrees = 0f;
        showingOrientationPreview = false;
        target = default;
        hasTarget = false;
        if (!placementService.TryGetPlacementOrientationPreview(out CommanderBuildingPlacementService.PlacementOrientationPreview preview)
            || !preview.hasDirection)
        {
            return;
        }

        target = preview.target;
        hasTarget = true;
        yawDegrees = preview.yawDegrees;
        showingOrientationPreview = true;
    }

    private bool TryGetGuiPointForTarget(GlobalPosition target, out Vector2 guiPoint)
    {
        guiPoint = default;

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        if (camera == null)
        {
            return false;
        }

        Vector3 screenPoint = camera.WorldToScreenPoint(target.ToLocalPosition());
        if (screenPoint.z <= 0f)
        {
            return false;
        }

        guiPoint = CommanderUiScale.ScreenToGui(new Vector2(screenPoint.x, screenPoint.y));
        return true;
    }

    private void DrawCursorPlacementBox(Vector2 guiPoint, float yawDegrees, bool hasOrientation)
    {
        float heading = hasOrientation ? yawDegrees : 0f;
        Rect markerBox = new(guiPoint.x - 15f, guiPoint.y - 15f, 30f, 30f);
        GUI.Box(markerBox, string.Empty, CommanderUiTheme.Panel);
        CommanderUiTheme.DrawFrame(markerBox, 2f);

        EnsureOrientationLineTexture();
        if (orientationLineTexture == null)
        {
            return;
        }

        Vector2 center = new(markerBox.x + markerBox.width * 0.5f, markerBox.y + markerBox.height * 0.5f);
        float radians = heading * Mathf.Deg2Rad;
        Vector2 forward = new(Mathf.Sin(radians), -Mathf.Cos(radians));
        float pointerLength = 10f;
        DrawGuiLine(center, center + forward * pointerLength, 2f);
    }

    private void DrawGuiLine(Vector2 a, Vector2 b, float thickness)
    {
        Vector2 delta = b - a;
        float length = delta.magnitude;
        if (length <= Mathf.Epsilon)
        {
            return;
        }

        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        Matrix4x4 previous = GUI.matrix;
        GUIUtility.RotateAroundPivot(angle, a);
        GUI.DrawTexture(new Rect(a.x, a.y - thickness * 0.5f, length, thickness), orientationLineTexture!);
        GUI.matrix = previous;
    }

    private void EnsureOrientationLineTexture()
    {
        if (orientationLineTexture != null)
        {
            return;
        }

        orientationLineTexture = new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Point
        };
        orientationLineTexture.SetPixel(0, 0, new Color(0.34f, 0.78f, 0.75f, 0.95f));
        orientationLineTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
    }

    private void DrawStructureOrientationPreview(BuildingDefinition definition, Vector3 localPosition, Quaternion rotation)
    {
        DrawPlacementBoxPreview(definition, localPosition, rotation);

        EnsureStructurePreviewMaterial();
        if (structurePreviewMaterial == null || definition.unitPrefab == null)
        {
            return;
        }

        Transform root = definition.unitPrefab.transform;
        Matrix4x4 rootInverse = root.worldToLocalMatrix;
        Matrix4x4 worldRoot = Matrix4x4.TRS(localPosition, rotation, Vector3.one);
        MeshFilter[] filters = definition.unitPrefab.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            MeshFilter filter = filters[i];
            if (filter.sharedMesh == null)
            {
                continue;
            }

            Matrix4x4 localMatrix = rootInverse * filter.transform.localToWorldMatrix;
            Matrix4x4 worldMatrix = worldRoot * localMatrix;
            Graphics.DrawMesh(filter.sharedMesh, worldMatrix, structurePreviewMaterial, 0);
        }
    }

    private void DrawPlacementBoxPreview(BuildingDefinition definition, Vector3 localPosition, Quaternion rotation)
    {
        EnsurePlacementBoxRenderResources();
        if (placementBoxMaterial == null || unitCubeMesh == null || definition.unitPrefab == null)
        {
            return;
        }

        if (!TryGetPreviewBounds(definition.unitPrefab, out Bounds bounds))
        {
            bounds = new Bounds(Vector3.zero, new Vector3(40f, 20f, 40f));
        }

        Vector3 worldCenter = localPosition + rotation * bounds.center;
        Matrix4x4 boxMatrix = Matrix4x4.TRS(worldCenter, rotation, bounds.size);
        Graphics.DrawMesh(unitCubeMesh, boxMatrix, placementBoxMaterial, 0);
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

        Shader? shader = Shader.Find("Legacy Shaders/Transparent/Diffuse") ?? Shader.Find("Standard");
        if (shader == null)
        {
            return;
        }

        structurePreviewMaterial = new Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave,
            color = new Color(0.32f, 0.82f, 0.78f, 0.35f)
        };
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

        Shader? shader = Shader.Find("Legacy Shaders/Transparent/Diffuse") ?? Shader.Find("Standard");
        if (shader == null)
        {
            return;
        }

        placementBoxMaterial = new Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave,
            color = new Color(0.65f, 0.65f, 0.65f, 0.28f)
        };
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
            string text = $"{pending[i].name}\n{countdown}";
            float width = Mathf.Max(140f, style.CalcSize(new GUIContent(text)).x + 18f);
            Rect marker = new(guiPoint.x + 12f, guiPoint.y - 52f, width, 48f);
            GUI.Box(marker, text, style);
            CommanderUiTheme.DrawFrame(marker, 1f);
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
        return ui?.Visible == true
            ? "B\nU\nI\nL\nD\n<"
            : "B\nU\nI\nL\nD";
    }

    private static bool IsGameInProgress()
    {
        return GameManager.gameState == GameState.SinglePlayer
            || GameManager.gameState == GameState.Multiplayer;
    }

    private bool ShouldShowLauncher()
    {
        return buildButtonActive && DynamicMap.mapMaximized;
    }
}