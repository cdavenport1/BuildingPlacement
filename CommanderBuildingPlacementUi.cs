using System;
using UnityEngine;

namespace NuclearOptionCommander;

internal sealed class CommanderBuildingPlacementUi
{
    private const int WindowId = 0x434F4D42;
    private const int BuildTimeWindowId = 0x434F4D43;

    private readonly CommanderBuildingPlacementService service;
    private Action? onExit;
    private bool visible;
    private bool minimized;
    private bool helpVisible;
    private bool buildTimeControlsVisible;
    private bool buildTimeWindowPositionInitialized;
    private bool positionInitialized;
    private Rect windowRect;
    private Rect buildTimeWindowRect;
    private Vector2 scroll;

internal CommanderBuildingPlacementUi(CommanderBuildingPlacementService service, Action? onExit = null)
    {
        this.service = service;
        this.onExit = onExit;
    }

    internal bool Visible => visible;
    internal bool Minimized => minimized;

    internal void Minimize()
    {
        minimized = true;
    }

    internal void Unminimize()
    {
        minimized = false;
    }

    internal void Toggle()
    {
        visible = !visible;
        helpVisible = false;
        buildTimeControlsVisible = false;
        EnsurePosition();
    }

    internal void Hide()
    {
        visible = false;
        minimized = false;
        helpVisible = false;
        buildTimeControlsVisible = false;
        if (service.AwaitingPlacementSelection)
        {
            service.CancelPlacementSelection();
        }
    }

    internal void Draw()
    {
        if (!visible || minimized)
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

        if (buildTimeControlsVisible)
        {
            EnsureBuildTimeWindowPosition();
            buildTimeWindowRect = CommanderUiTheme.ClampWindow(buildTimeWindowRect);
            buildTimeWindowRect = GUI.Window(
                BuildTimeWindowId,
                buildTimeWindowRect,
                DrawBuildTimeWindow,
                "BUILD TIME SETTINGS",
                CommanderUiTheme.Window);
        }
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

    private void DrawWindow(int _)
    {
        if (CommanderUiTheme.DrawHelpButton(windowRect.width, ref helpVisible))
        {
            CommanderUiTheme.DrawHelpOverlay(
                new Rect(12f, 34f, windowRect.width - 24f, 92f),
                "Choose a building blueprint, then place it by clicking a point on the tactical map or in the 3D world. This mod does not wire into the main commander UI.");
        }

        if (GUI.Button(
            new Rect(windowRect.width - 34f, 3f, 26f, 22f),
            "X",
            CommanderUiTheme.DangerButton))
        {
            Hide();
            return;
        }

        if (GUI.Button(new Rect(windowRect.width - 94f, 3f, 26f, 22f), "⚙", CommanderUiTheme.Button))
        {
            buildTimeControlsVisible = !buildTimeControlsVisible;
            if (buildTimeControlsVisible)
            {
                EnsureBuildTimeWindowPosition();
            }
        }

        float y = helpVisible ? 136f : 38f;
        PlaceableDefinition? selected = service.SelectedDefinition;
        GUI.Label(
            new Rect(12f, y, windowRect.width - 24f, 24f),
            selected != null ? $"SELECTED  {selected.UnitName}" : "NO BUILDING BLUEPRINTS AVAILABLE",
            CommanderUiTheme.Header);
        y += 30f;

        Rect listRect = new(12f, y, windowRect.width - 24f, windowRect.height - y - 140f);
        GUI.Box(listRect, string.Empty, CommanderUiTheme.Panel);
        CommanderUiTheme.DrawSubtleBorder(listRect, 1f);
        float contentHeight = Mathf.Max(listRect.height - 8f, service.VisibleDefinitions.Count * 58f + 8f);
        Rect viewRect = new(0f, 0f, listRect.width - 22f, contentHeight);
        scroll = GUI.BeginScrollView(listRect, scroll, viewRect);
        for (int i = 0; i < service.VisibleDefinitions.Count; i++)
        {
            PlaceableDefinition definition = service.VisibleDefinitions[i];
            bool canAfford = service.CanAffordDefinition(definition);
            GUIStyle style = ReferenceEquals(definition, selected)
                ? CommanderUiTheme.SelectedButton
                : CommanderUiTheme.Button;
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && canAfford;
            if (GUI.Button(
                new Rect(4f, 4f + i * 58f, viewRect.width - 8f, 50f),
                service.GetDefinitionLabel(definition),
                style))
            {
                service.SelectDefinition(i);
            }
            GUI.enabled = previousEnabled;
        }
        GUI.EndScrollView();

        float buttonY = windowRect.height - 104f;
        
        // Display status text above the buttons
        string status = service.StatusText;
        if (!string.IsNullOrWhiteSpace(status))
        {
            GUI.Label(
                new Rect(14f, buttonY - 50f, windowRect.width - 28f, 42f),
                status,
                CommanderUiTheme.MutedLabel);
        }

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

        // EXIT button below SELECT LOCATION
        if (GUI.Button(
            new Rect(12f, buttonY + 42f, windowRect.width - 24f, 38f),
            "EXIT",
            CommanderUiTheme.DangerButton))
        {
            onExit?.Invoke();
        }

        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 72f, 28f));
    }

    private void DrawBuildTimeWindow(int _)
    {
        if (GUI.Button(new Rect(buildTimeWindowRect.width - 34f, 3f, 26f, 22f), "X", CommanderUiTheme.DangerButton))
        {
            buildTimeControlsVisible = false;
            return;
        }

        GUI.Label(
            new Rect(12f, 34f, buildTimeWindowRect.width - 24f, 22f),
            service.BuildTimeSettingLabel,
            CommanderUiTheme.Header);

        Rect previewRect = new(12f, 62f, buildTimeWindowRect.width - 24f, 42f);
        GUI.Box(previewRect, string.Empty, CommanderUiTheme.Panel);
        GUI.Label(
            new Rect(previewRect.x + 10f, previewRect.y + 8f, previewRect.width - 20f, 24f),
            service.BuildTimePreviewLabel,
            CommanderUiTheme.MutedLabel);
        CommanderUiTheme.DrawSubtleBorder(previewRect, 1f);

        if (GUI.Button(new Rect(12f, 112f, 46f, 30f), "-", CommanderUiTheme.Button))
        {
            service.AdjustBuildTimeMultiplier(increase: false);
        }

        if (GUI.Button(new Rect(64f, 112f, 46f, 30f), "+", CommanderUiTheme.Button))
        {
            service.AdjustBuildTimeMultiplier(increase: true);
        }

        GUI.Label(
            new Rect(120f, 112f, buildTimeWindowRect.width - 132f, 30f),
            "Applies to new queued builds",
            CommanderUiTheme.MutedLabel);

        // Ship visibility toggle
        bool showShips = CommanderSettings.PlacementShowShips;
        if (GUI.Button(
            new Rect(12f, 150f, buildTimeWindowRect.width - 24f, 30f),
            showShips ? "✓ SHOW SHIPS IN LIST" : "✗ HIDE SHIPS IN LIST",
            showShips ? CommanderUiTheme.Button : CommanderUiTheme.DangerButton))
        {
            CommanderSettings.PlacementShowShips = !CommanderSettings.PlacementShowShips;
        }

        GUI.DragWindow(new Rect(0f, 0f, buildTimeWindowRect.width - 38f, 28f));
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

    private void EnsureBuildTimeWindowPosition()
    {
        if (buildTimeWindowPositionInitialized)
        {
            return;
        }

        float width = Mathf.Min(320f, CommanderUiScale.Width - 24f);
        float height = 320f;
        buildTimeWindowRect = new Rect(
            Mathf.Clamp(windowRect.x + windowRect.width + 10f, 12f, Mathf.Max(12f, CommanderUiScale.Width - width - 12f)),
            Mathf.Clamp(windowRect.y + 24f, 12f, Mathf.Max(12f, CommanderUiScale.Height - height - 12f)),
            width,
            height);
        buildTimeWindowPositionInitialized = true;
    }
}