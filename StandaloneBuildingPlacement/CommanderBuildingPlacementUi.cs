using UnityEngine;

namespace NuclearOptionCommander;

internal sealed class CommanderBuildingPlacementUi
{
    private const int WindowId = 0x434F4D42;

    private readonly CommanderBuildingPlacementService service;
    private bool visible;
    private bool helpVisible;
    private bool positionInitialized;
    private Rect windowRect;
    private Vector2 scroll;

    internal CommanderBuildingPlacementUi(CommanderBuildingPlacementService service)
    {
        this.service = service;
    }

    internal bool Visible => visible;

    internal void Toggle()
    {
        visible = !visible;
        helpVisible = false;
        EnsurePosition();
    }

    internal void Hide()
    {
        visible = false;
        helpVisible = false;
        if (service.AwaitingPlacementSelection)
        {
            service.CancelPlacementSelection();
        }
    }

    internal void Draw()
    {
        if (!visible)
        {
            return;
        }

        EnsurePosition();
        windowRect = CommanderUiTheme.ClampWindow(windowRect);
        windowRect = GUI.Window(
            WindowId,
            windowRect,
            DrawWindow,
            "BUILDING PLACEMENT",
            CommanderUiTheme.Window);
    }

    internal bool ContainsScreenPoint(Vector2 screenPosition)
    {
        return visible && windowRect.Contains(CommanderUiScale.ScreenToGui(screenPosition));
    }

    internal void ResetPosition()
    {
        positionInitialized = false;
        EnsurePosition();
    }

    private void DrawWindow(int windowId)
    {
        if (CommanderUiTheme.DrawHelpButton(windowRect.width, ref helpVisible))
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, 34f, windowRect.width - 24f, 92f),
                "Choose a building blueprint, then place it by clicking a point on the tactical map or in the 3D world. This folder is intentionally standalone and does not wire into the main commander UI.");
        }

        if (GUI.Button(
            new Rect(windowRect.width - 34f, 3f, 26f, 22f),
            "X",
            CommanderUiTheme.DangerButton))
        {
            Hide();
            return;
        }

        float y = helpVisible ? 136f : 38f;
        BuildingDefinition? selected = service.SelectedDefinition;
        GUI.Label(
            new Rect(12f, y, windowRect.width - 24f, 24f),
            selected != null ? $"SELECTED  {selected.unitName}" : "NO BUILDING BLUEPRINTS AVAILABLE",
            CommanderUiTheme.Header);
        y += 30f;

        Rect listRect = new(12f, y, windowRect.width - 24f, windowRect.height - y - 122f);
        GUI.Box(listRect, string.Empty, CommanderUiTheme.Panel);
        float contentHeight = Mathf.Max(listRect.height - 8f, service.BuildingDefinitions.Count * 58f + 8f);
        Rect viewRect = new(0f, 0f, listRect.width - 22f, contentHeight);
        scroll = GUI.BeginScrollView(listRect, scroll, viewRect);
        for (int i = 0; i < service.BuildingDefinitions.Count; i++)
        {
            BuildingDefinition definition = service.BuildingDefinitions[i];
            GUIStyle style = ReferenceEquals(definition, selected)
                ? CommanderUiTheme.SelectedButton
                : CommanderUiTheme.Button;
            if (GUI.Button(
                new Rect(4f, 4f + i * 58f, viewRect.width - 8f, 50f),
                service.GetDefinitionLabel(definition),
                style))
            {
                service.SelectDefinition(i);
            }
        }
        GUI.EndScrollView();

        float buttonY = windowRect.height - 104f;
        bool oldEnabled = GUI.enabled;
        GUI.enabled = oldEnabled && selected != null;
        if (GUI.Button(
            new Rect(12f, buttonY, windowRect.width - 24f, 38f),
            service.AwaitingPlacementSelection ? "CANCEL LOCATION SELECTION" : "SELECT LOCATION",
            service.AwaitingPlacementSelection ? CommanderUiTheme.DangerButton : CommanderUiTheme.PrimaryButton))
        {
            if (service.AwaitingPlacementSelection)
            {
                service.CancelPlacementSelection();
            }
            else
            {
                service.BeginPlacementSelection();
            }
        }
        GUI.enabled = oldEnabled;

        string status = service.StatusText;
        if (!string.IsNullOrWhiteSpace(status))
        {
            GUI.Label(
                new Rect(14f, windowRect.height - 60f, windowRect.width - 28f, 42f),
                status,
                CommanderUiTheme.MutedLabel);
        }

        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 72f, 28f));
    }

    private void EnsurePosition()
    {
        if (positionInitialized)
        {
            return;
        }

        float width = Mathf.Min(470f, CommanderUiScale.Width - 24f);
        float height = Mathf.Min(620f, CommanderUiScale.Height - 24f);
        windowRect = new Rect(
            74f,
            Mathf.Max(12f, (CommanderUiScale.Height - height) * 0.5f),
            width,
            height);
        positionInitialized = true;
    }
}