using BepInEx.Configuration;
using UnityEngine;

namespace NuclearOptionBuilder;

internal static class BuilderShortcutInput
{
    internal static bool IsDown(KeyboardShortcut shortcut)
    {
        return shortcut.MainKey != KeyCode.None
            && Input.GetKeyDown(shortcut.MainKey)
            && AreModifiersPressed(shortcut);
    }

    internal static bool IsHeld(KeyboardShortcut shortcut)
    {
        return shortcut.MainKey != KeyCode.None
            && Input.GetKey(shortcut.MainKey)
            && AreModifiersPressed(shortcut);
    }

    private static bool AreModifiersPressed(KeyboardShortcut shortcut)
    {
        foreach (KeyCode modifier in shortcut.Modifiers)
        {
            if (!Input.GetKey(modifier))
            {
                return false;
            }
        }

        return true;
    }
}

