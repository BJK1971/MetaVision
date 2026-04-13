#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Unity.InferenceEngine;

public static class SetupYoloDetector
{
    [MenuItem("MetaVision/Add YOLO Detector to Scene")]
    public static void Setup()
    {
        // Find or create detector GameObject
        var existing = Object.FindFirstObjectByType<YoloDetector>();
        GameObject detectorObj;

        if (existing != null)
        {
            detectorObj = existing.gameObject;
            Debug.Log("[MetaVision] YoloDetector already exists, updating config.");
        }
        else
        {
            detectorObj = new GameObject("YoloDetector");
            detectorObj.AddComponent<YoloDetector>();
            detectorObj.AddComponent<BoundingBoxRenderer>();
            Undo.RegisterCreatedObjectUndo(detectorObj, "Add YoloDetector");
            Debug.Log("[MetaVision] Created YoloDetector GameObject.");
        }

        // Assign model asset
        var guids = AssetDatabase.FindAssets("yolov8n t:ModelAsset");
        if (guids.Length == 0)
        {
            // Try searching by filename
            guids = AssetDatabase.FindAssets("yolov8n");
        }

        if (guids.Length > 0)
        {
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".onnx")) continue;

                var asset = AssetDatabase.LoadAssetAtPath<ModelAsset>(path);
                if (asset != null)
                {
                    var detector = detectorObj.GetComponent<YoloDetector>();
                    detector.modelAsset = asset;
                    EditorUtility.SetDirty(detector);
                    Debug.Log($"[MetaVision] Assigned model: {path}");
                    break;
                }
            }
        }
        else
        {
            Debug.LogWarning("[MetaVision] yolov8n.onnx not found in Assets. Assign manually in Inspector.");
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        EditorUtility.DisplayDialog("MetaVision",
            "YOLO Detector added!\n\n" +
            "- YoloDetector + BoundingBoxRenderer\n" +
            "- Model: yolov8n.onnx\n\n" +
            "To test: assign a test Texture2D in the Inspector,\n" +
            "then press Play.\n\n" +
            "Save the scene (Ctrl+S).", "OK");
    }
}
#endif
