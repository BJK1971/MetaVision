using UnityEngine;

/// <summary>
/// Ensures all cameras render transparent background so passthrough underlay is visible.
/// Attach to OVRCameraRig or any persistent GameObject.
/// </summary>
public class PassthroughFix : MonoBehaviour
{
    void Awake()
    {
        // Remove skybox so it doesn't render over passthrough
        RenderSettings.skybox = null;

        // Set all cameras to solid color with transparent black
        foreach (var cam in FindObjectsByType<Camera>(FindObjectsSortMode.None))
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0, 0, 0, 0);
        }

        Debug.Log("[MetaVision] Passthrough fix applied: skybox removed, cameras transparent.");
    }
}
