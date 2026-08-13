using UnityEngine;

namespace NuclearOptionCommander;

internal sealed class CommanderMapClickTracker
{
    private bool pressed;

    internal bool Tick(DynamicMap dynamicMap, out GlobalPosition position)
    {
        position = default;

        if (!pressed && CommanderShortcutInput.IsHeld(CommanderSettings.PrimaryAction) && dynamicMap.IsCursorInMapRectangle())
        {
            pressed = true;
        }

        if (!pressed || !Input.GetKeyUp(CommanderSettings.PrimaryAction.MainKey))
        {
            return false;
        }

        pressed = false;

        return dynamicMap.TryGetCursorCoordinates(out position);
    }

    internal void Reset()
    {
        pressed = false;
    }
}
