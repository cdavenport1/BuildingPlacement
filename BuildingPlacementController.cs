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
    private Rect launcherRect;
    private bool launcherInitialized;
    private bool gameInProgress;

    private void Awake()
    {
        CommanderUiScale.ApplyResolutionPreset();
        service = new CommanderBuildingPlacementService();
        ui = new CommanderBuildingPlacementUi(service);
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
            }
        }

        if (!gameInProgress)
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

            if (GUI.Button(launcherRect, GetLauncherLabel(), CommanderUiTheme.PrimaryButton))
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

        if (!IsFinite(localPosition))
        {
            return;
        }

        Bounds bounds = new Bounds(Vector3.zero, new Vector3(40f, 20f, 40f));
        if (definition.unitPrefab != null
            && TryGetPreviewBounds(definition.unitPrefab, out Bounds computedBounds)
            && IsValidBounds(computedBounds))
        {
            bounds = computedBounds;
        }

        DrawStructureOrientationPreview(definition, localPosition, rotation, bounds);
        DrawOrientationArrowPreview(localPosition, rotation, bounds);
    }

    private void OnDestroy()
    {
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
        return true;
    }
}