using UnityEngine;
using UnityEngine.SceneManagement;

namespace NuclearOptionCommander;

internal sealed class StandaloneBuildingPlacementController : MonoBehaviour
{
    private CommanderBuildingPlacementService? service;
    private CommanderBuildingPlacementUi? ui;
    private Rect launcherRect;
    private bool launcherInitialized;

    private void Awake()
    {
        CommanderUiScale.ApplyResolutionPreset();
        service = new CommanderBuildingPlacementService();
        ui = new CommanderBuildingPlacementUi(service);
        service.Activate();
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }

    private void Update()
    {
        CommanderUiScale.RefreshResolutionPreset();
        service?.TickPersistent();

        if (service?.AwaitingPlacementSelection == true)
        {
            service.TickActive();
            HandleWorldPlacementClick();
        }
    }

    private void OnGUI()
    {
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

    private void HandleWorldPlacementClick()
    {
        if (ui?.Visible == true && ui.ContainsScreenPoint(Input.mousePosition))
        {
            return;
        }

        if (DynamicMap.mapMaximized)
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

        launcherRect = new Rect(10f, 10f, 72f, 36f);
        launcherInitialized = true;
    }
}