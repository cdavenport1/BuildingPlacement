using UnityEngine;
using UnityEngine.SceneManagement;

namespace NuclearOptionCommander;

internal sealed class BuildingPlacementController : MonoBehaviour
{
    private readonly CommanderExternalActivationGate externalActivationGate = new();
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
        if (!gameInProgress || !buildButtonActive)
        {
            return;
        }

        Matrix4x4 previousMatrix = CommanderUiScale.Begin();

        CommanderUiTheme.Ensure();
        EnsureLauncherPosition();

        if (GUI.Button(launcherRect, ui?.Visible == true ? "BUILD <" : "BUILD", CommanderUiTheme.PrimaryButton))
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

        if (service?.AwaitingPlacementSelection == true)
        {
            DrawPlacementCursor();
        }

        CommanderUiScale.End(previousMatrix);
    }

    private void OnDestroy()
    {
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        service?.Deactivate();
    }

    private void OnActiveSceneChanged(Scene previousScene, Scene newScene)
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

        Vector2 guiPoint = CommanderUiScale.ScreenToGui(Input.mousePosition);
        string label = service.PreviewLabel;
        GUIStyle style = CommanderUiTheme.Panel;
        float width = Mathf.Max(90f, style.CalcSize(new GUIContent(label)).x + 18f);
        Rect marker = new(guiPoint.x + 14f, guiPoint.y + 14f, width, 28f);
        GUI.Box(marker, label, style);
        CommanderUiTheme.DrawFrame(marker, 1f);
    }

    private void EnsureLauncherPosition()
    {
        if (launcherInitialized)
        {
            return;
        }

        float centerY = CommanderUiScale.Height * 0.5f;
        launcherRect = new Rect(10f, Mathf.Max(10f, centerY - 88f), 72f, 36f);
        launcherInitialized = true;
    }

    private static bool IsGameInProgress()
    {
        return GameManager.gameState == GameState.SinglePlayer
            || GameManager.gameState == GameState.Multiplayer;
    }
}