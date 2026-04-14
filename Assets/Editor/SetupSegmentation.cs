#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Unity.InferenceEngine;

public static class SetupSegmentation
{
    [MenuItem("MetaVision/Switch to Segmentation Mode")]
    public static void Setup()
    {
        // Disable old YoloDetector + BoundingBoxRenderer
        var oldDetector = Object.FindFirstObjectByType<YoloDetector>();
        if (oldDetector != null)
        {
            oldDetector.enabled = false;
            var bbr = oldDetector.GetComponent<BoundingBoxRenderer>();
            if (bbr != null) bbr.enabled = false;
            Debug.Log("[MetaVision] Disabled YoloDetector + BoundingBoxRenderer.");
        }

        // Find or create SegDetector
        var segDetector = Object.FindFirstObjectByType<YoloSegDetector>();
        GameObject segObj;

        if (segDetector != null)
        {
            segObj = segDetector.gameObject;
        }
        else
        {
            segObj = new GameObject("YoloSegDetector");
            segDetector = segObj.AddComponent<YoloSegDetector>();
            segObj.AddComponent<ContourRenderer>();
            Undo.RegisterCreatedObjectUndo(segObj, "Add YoloSegDetector");
        }

        // Assign seg model
        var guids = AssetDatabase.FindAssets("yolov8n_seg_320");
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!path.EndsWith(".onnx")) continue;
            var asset = AssetDatabase.LoadAssetAtPath<ModelAsset>(path);
            if (asset != null)
            {
                segDetector.modelAsset = asset;
                EditorUtility.SetDirty(segDetector);
                Debug.Log($"[MetaVision] Seg model assigned: {path}");
                break;
            }
        }

        // Link PCA
        var pca = Object.FindFirstObjectByType<Meta.XR.PassthroughCameraAccess>();
        if (pca != null)
        {
            segDetector.passthroughCamera = pca;
            EditorUtility.SetDirty(segDetector);
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorUtility.DisplayDialog("MetaVision",
            "Switched to Segmentation Mode!\n\n" +
            "- YoloSegDetector + ContourRenderer active\n" +
            "- Old YoloDetector disabled\n" +
            "- Contours follow object edges\n\n" +
            "Save (Ctrl+S) and Build.", "OK");
    }

    [MenuItem("MetaVision/Switch to Detection Mode")]
    public static void SwitchBack()
    {
        var oldDetector = Object.FindFirstObjectByType<YoloDetector>();
        if (oldDetector != null)
        {
            oldDetector.enabled = true;
            var bbr = oldDetector.GetComponent<BoundingBoxRenderer>();
            if (bbr != null) bbr.enabled = true;
        }

        var segDetector = Object.FindFirstObjectByType<YoloSegDetector>();
        if (segDetector != null)
        {
            segDetector.enabled = false;
            var cr = segDetector.GetComponent<ContourRenderer>();
            if (cr != null) cr.enabled = false;
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[MetaVision] Switched back to Detection Mode.");
    }
}
#endif
