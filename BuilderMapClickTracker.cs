using UnityEngine;

namespace NuclearOptionBuilder;

internal sealed class BuilderMapClickTracker
{
    private bool pressed;

    internal bool Tick(DynamicMap dynamicMap, out GlobalPosition position)
    {
        position = default;

        if (!pressed && BuilderShortcutInput.IsHeld(BuilderSettings.PrimaryAction) && dynamicMap.IsCursorInMapRectangle())
        {
            pressed = true;
        }

        if (!pressed || !Input.GetKeyUp(BuilderSettings.PrimaryAction.MainKey))
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

