#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Meta.XR.EnvironmentDepth;

public static class SetupDepthOcclusion
{
    [MenuItem("MetaVision/Add Depth Occlusion")]
    public static void Setup()
    {
        var existing = Object.FindFirstObjectByType<EnvironmentDepthManager>();
        if (existing != null)
        {
            Debug.Log("[MetaVision] EnvironmentDepthManager already exists.");
            return;
        }

        var rig = Object.FindFirstObjectByType<OVRCameraRig>();
        GameObject target = rig != null ? rig.gameObject : new GameObject("DepthManager");

        var depthMgr = target.AddComponent<EnvironmentDepthManager>();
        depthMgr.RemoveHands = true;
        EditorUtility.SetDirty(depthMgr);
        Undo.RegisterCreatedObjectUndo(target, "Add DepthManager");

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[MetaVision] EnvironmentDepthManager added with hand removal.");

        EditorUtility.DisplayDialog("MetaVision",
            "Depth Occlusion added!\n\n" +
            "- EnvironmentDepthManager on OVRCameraRig\n" +
            "- Hand removal enabled\n\n" +
            "Virtual objects will be occluded by real ones.\n" +
            "Save the scene (Ctrl+S).", "OK");
    }
}
#endif
