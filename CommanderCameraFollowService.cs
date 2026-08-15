using UnityEngine;

namespace NuclearOptionCommander;

internal sealed class CommanderCameraFollowService
{
    private const float CameraHeight = 20f;
    private const float CameraPovDistance = 15f;
    
    private bool followEnabled;
    private bool povMode;
    private Vector3 lastCameraPos = Vector3.zero;

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
        // Track camera state for UI display purposes
        Camera? camera = Camera.main;
        if (camera != null)
        {
            lastCameraPos = camera.transform.position;
        }
    }
}

