#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Meta.XR;

public static class SetupPassthroughCamera
{
    [MenuItem("MetaVision/Add Passthrough Camera to Scene")]
    public static void Setup()
    {
        // Find or create PassthroughCameraAccess
        var existing = Object.FindFirstObjectByType<PassthroughCameraAccess>();
        if (existing != null)
        {
            Debug.Log("[MetaVision] PassthroughCameraAccess already exists.");
        }
        else
        {
            // Add to OVRCameraRig if present, otherwise create new GO
            var rig = Object.FindFirstObjectByType<OVRCameraRig>();
            GameObject target;
            if (rig != null)
            {
                target = rig.gameObject;
            }
            else
            {
                target = new GameObject("PassthroughCamera");
                Undo.RegisterCreatedObjectUndo(target, "Add PassthroughCamera");
            }

            var pca = target.AddComponent<PassthroughCameraAccess>();
            pca.CameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
            pca.RequestedResolution = new Vector2Int(1280, 960);
            EditorUtility.SetDirty(pca);
            Debug.Log("[MetaVision] PassthroughCameraAccess added to " + target.name);
        }

        // Wire up YoloDetector
        var detector = Object.FindFirstObjectByType<YoloDetector>();
        var cam = Object.FindFirstObjectByType<PassthroughCameraAccess>();
        if (detector != null && cam != null)
        {
            detector.passthroughCamera = cam;
            // Clear test texture so live camera takes priority
            detector.testTexture = null;
            EditorUtility.SetDirty(detector);
            Debug.Log("[MetaVision] YoloDetector linked to PassthroughCameraAccess.");
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        EditorUtility.DisplayDialog("MetaVision",
            "Passthrough Camera setup done!\n\n" +
            "- PassthroughCameraAccess added (Left, 1280x960)\n" +
            "- YoloDetector linked to live camera\n" +
            "- Test texture cleared\n\n" +
            "Build and Run to test on Quest 3.\n" +
            "Save the scene (Ctrl+S).", "OK");
    }
}
#endif
