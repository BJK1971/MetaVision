#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class MetaVisionSetup
{
    [MenuItem("MetaVision/Setup Scene for Quest 3")]
    public static void SetupScene()
    {
        // 1. Remove existing Main Camera
        var cam = Camera.main;
        if (cam != null && cam.GetComponent<OVRCameraRig>() == null)
        {
            Undo.DestroyObjectImmediate(cam.gameObject);
            Debug.Log("[MetaVision] Removed Main Camera.");
        }

        // 2. Instantiate OVRCameraRig prefab
        if (Object.FindFirstObjectByType<OVRCameraRig>() == null)
        {
            var guids = AssetDatabase.FindAssets("OVRCameraRig t:Prefab");
            if (guids.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var rig = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                rig.transform.position = Vector3.zero;
                Undo.RegisterCreatedObjectUndo(rig, "Add OVRCameraRig");
                Debug.Log("[MetaVision] OVRCameraRig instantiated from " + path);
            }
        }

        // 3. Configure OVRManager for passthrough
        var manager = Object.FindFirstObjectByType<OVRManager>();
        if (manager != null)
        {
            manager.isInsightPassthroughEnabled = true;
            EditorUtility.SetDirty(manager);
            Debug.Log("[MetaVision] Passthrough enabled on OVRManager.");
        }

        // 4. Add OVRPassthroughLayer as Underlay
        var rigObj = Object.FindFirstObjectByType<OVRCameraRig>();
        if (rigObj != null)
        {
            var ptLayer = rigObj.GetComponent<OVRPassthroughLayer>();
            if (ptLayer == null)
                ptLayer = rigObj.gameObject.AddComponent<OVRPassthroughLayer>();
            ptLayer.overlayType = OVROverlay.OverlayType.Underlay;
            ptLayer.compositionDepth = 0;
            EditorUtility.SetDirty(ptLayer);
            Debug.Log("[MetaVision] OVRPassthroughLayer added (Underlay).");

            // 5. Set center eye camera to transparent
            var centerCam = rigObj.GetComponentInChildren<Camera>();
            if (centerCam != null)
            {
                centerCam.clearFlags = CameraClearFlags.SolidColor;
                centerCam.backgroundColor = new Color(0, 0, 0, 0);
                EditorUtility.SetDirty(centerCam);
            }
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[MetaVision] Setup complete! Save with Ctrl+S.");

        EditorUtility.DisplayDialog("MetaVision",
            "Quest 3 setup done!\n\n" +
            "- OVRCameraRig added\n" +
            "- Passthrough enabled\n" +
            "- PassthroughLayer (Underlay)\n" +
            "- Camera transparent\n\n" +
            "Save the scene (Ctrl+S).", "OK");
    }
}
#endif
