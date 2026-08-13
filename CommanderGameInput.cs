using Rewired;

namespace NuclearOptionCommander;

internal static class CommanderGameInput
{
    internal static bool CancelDown => GetButtonDown("Cancel");

    private static bool GetButtonDown(string action)
    {
        if (!ReInput.isReady)
        {
            return false;
        }

        Player player = ReInput.players.GetPlayer(0);
        return player != null && player.GetButtonDown(action);
    }
}
