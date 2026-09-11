namespace NuclearOptionBuilder;

internal sealed class BuilderCameraFollowService
{
    private bool followEnabled;
    private bool povMode;

    internal bool Enabled => followEnabled;
    internal bool PovMode => povMode;
    internal bool CanFollow => true;

    internal void Toggle()
    {
        followEnabled = !followEnabled;
        if (followEnabled)
        {
            povMode = false;
        }
    }

    internal void TogglePov()
    {
        povMode = !povMode;
        if (povMode)
        {
            followEnabled = false;
        }
    }

    internal void CenterOnSelection()
    {
        // Navigation feature - centers current camera view
        // In future, this could integrate with game's selection system
    }

    internal void Tick()
    {
        // Reserved for future camera-follow behavior; nothing to update yet
    }
}


