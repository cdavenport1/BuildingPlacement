namespace NuclearOptionCommander;

internal sealed class CommanderPlacementCursorState
{
    private bool active;
    private bool previousMapFlag;
    private bool previousForceHidden;

    internal bool IsActive => active;

    internal void Activate()
    {
        if (active)
        {
            return;
        }

        previousMapFlag = CursorManager.GetFlag(CursorFlags.Map);
        previousForceHidden = CursorManager.GetFlags() != CursorFlags.None && !CursorManager.Visible;
        active = true;
        CursorManager.ForceHidden(false);
        CursorManager.SetFlag(CursorFlags.Map, true);
    }

    internal void Deactivate()
    {
        if (!active)
        {
            return;
        }

        CursorManager.SetFlag(CursorFlags.Map, previousMapFlag);
        CursorManager.ForceHidden(previousForceHidden);
        active = false;
    }

    internal void Tick()
    {
        if (!active)
        {
            return;
        }

        if (!CursorManager.GetFlag(CursorFlags.Map))
        {
            CursorManager.SetFlag(CursorFlags.Map, true);
        }

        if (!CursorManager.Visible)
        {
            CursorManager.ForceHidden(false);
            CursorManager.Refresh();
        }
    }
}