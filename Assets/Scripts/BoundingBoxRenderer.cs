using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>
/// Renders YOLO bounding boxes as 3D wireframe cubes in VR.
/// Estimates distance from box size. Shows class name + confidence + distance.
/// </summary>
[RequireComponent(typeof(YoloDetector))]
public class BoundingBoxRenderer : MonoBehaviour
{
    [Header("Rendering")]
    public float displayDistance = 2f;
    public float displayWidth = 1.2f;
    public float lineWidth = 0.005f;
    public float labelSize = 0.4f;
    [Tooltip("Depth of 3D box as fraction of estimated distance")]
    public float boxDepthFactor = 0.15f;
    public bool render3DBoxes = false;

    YoloDetector detector;
    List<YoloDetector.Detection> currentDetections = new();

    Transform anchor;
    GameObject statusIndicator;
    Material statusMat;
    List<GameObject> boxPool = new();
    Dictionary<int, Material> classMaterials = new();

    static readonly Dictionary<int, Color> ClassColors = new()
    {
        {0, Color.green},
        {1, new Color(0.2f, 0.6f, 1f)}, {2, new Color(0.2f, 0.6f, 1f)},
        {3, new Color(0.2f, 0.6f, 1f)}, {4, new Color(0.2f, 0.6f, 1f)},
        {5, new Color(0.2f, 0.6f, 1f)}, {6, new Color(0.2f, 0.6f, 1f)},
        {7, new Color(0.2f, 0.6f, 1f)}, {8, new Color(0.2f, 0.6f, 1f)},
        {14, Color.yellow}, {15, Color.yellow}, {16, Color.yellow}, {17, Color.yellow},
        {18, Color.yellow}, {19, Color.yellow}, {20, Color.yellow}, {21, Color.yellow},
        {22, Color.yellow}, {23, Color.yellow},
        {62, Color.cyan}, {63, Color.cyan}, {64, Color.cyan}, {65, Color.cyan},
        {66, Color.cyan}, {67, Color.cyan},
        {46, new Color(1f, 0.5f, 0f)}, {47, new Color(1f, 0.5f, 0f)},
        {48, new Color(1f, 0.5f, 0f)}, {49, new Color(1f, 0.5f, 0f)},
        {50, new Color(1f, 0.5f, 0f)}, {51, new Color(1f, 0.5f, 0f)},
        {52, new Color(1f, 0.5f, 0f)}, {53, new Color(1f, 0.5f, 0f)},
        {54, new Color(1f, 0.5f, 0f)}, {55, new Color(1f, 0.5f, 0f)},
        {56, new Color(0.8f, 0.4f, 1f)}, {57, new Color(0.8f, 0.4f, 1f)},
        {58, new Color(0.8f, 0.4f, 1f)}, {59, new Color(0.8f, 0.4f, 1f)},
        {60, new Color(0.8f, 0.4f, 1f)}, {61, new Color(0.8f, 0.4f, 1f)},
    };
    float logTimer;
    int totalFrames;

    void Awake()
    {
        detector = GetComponent<YoloDetector>();
        detector.OnDetections += OnDetections;
    }

    void Start()
    {
        var rig = FindFirstObjectByType<OVRCameraRig>();
        anchor = rig != null ? rig.centerEyeAnchor : Camera.main?.transform;

        statusIndicator = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        statusIndicator.name = "StatusIndicator";
        Destroy(statusIndicator.GetComponent<Collider>());
        statusIndicator.transform.localScale = Vector3.one * 0.05f;
        statusMat = CreateUnlitMaterial(Color.red);
        if (statusMat != null)
            statusIndicator.GetComponent<MeshRenderer>().material = statusMat;

        Debug.Log("[BBR] 3D box renderer started.");
    }

    void OnDestroy()
    {
        if (detector != null)
            detector.OnDetections -= OnDetections;
    }

    void OnDetections(List<YoloDetector.Detection> detections)
    {
        currentDetections = detections;
        totalFrames++;
    }

    void Update()
    {
        if (anchor == null) return;

        string status;
        Color statusColor;

        var pcaRef = detector.passthroughCamera;
        if (pcaRef != null)
        {
            if (pcaRef.IsPlaying)
            {
                var tex = pcaRef.GetTexture();
                status = tex != null ? $"LIVE {tex.width}x{tex.height}" : "LIVE (no tex)";
                statusColor = tex != null ? Color.green : new Color(0.5f, 1f, 0);
            }
            else { status = "WAITING"; statusColor = Color.yellow; }
        }
        else if (detector.testTexture != null)
        { status = "TEST"; statusColor = Color.cyan; }
        else { status = "NO INPUT"; statusColor = Color.red; }

        if (statusIndicator != null)
            statusIndicator.transform.position = anchor.position + anchor.forward * 2f + anchor.up * 0.4f + anchor.right * 0.5f;
        if (statusMat != null) statusMat.color = statusColor;

        logTimer += Time.deltaTime;
        if (logTimer > 2f)
        {
            logTimer = 0;
            int detCount = currentDetections?.Count ?? 0;
            Debug.Log($"[BBR] {status} | Det:{detCount} | F:{totalFrames} | {detector.InferenceTimeMs:F0}ms");
        }

        foreach (var box in boxPool)
            if (box != null) box.SetActive(false);

        if (currentDetections == null || currentDetections.Count == 0) return;

        bool useRaycast = pcaRef != null && pcaRef.IsPlaying;

        for (int i = 0; i < currentDetections.Count; i++)
        {
            var det = currentDetections[i];
            GameObject boxObj = GetOrCreateBox(i);
            boxObj.SetActive(true);

            // Fixed display distance (stereo-safe) + estimated distance for label only
            float dist = displayDistance;
            float boxArea = det.box.width * det.box.height;
            float estDist = Mathf.Lerp(5f, 0.5f, Mathf.Sqrt(boxArea));

            // Front face corners
            Vector3 ftl, ftr, fbr, fbl;

            if (useRaycast)
            {
                ftl = pcaRef.ViewportPointToRay(new Vector2(det.box.xMin, 1f - det.box.yMin)).GetPoint(dist);
                ftr = pcaRef.ViewportPointToRay(new Vector2(det.box.xMax, 1f - det.box.yMin)).GetPoint(dist);
                fbr = pcaRef.ViewportPointToRay(new Vector2(det.box.xMax, 1f - det.box.yMax)).GetPoint(dist);
                fbl = pcaRef.ViewportPointToRay(new Vector2(det.box.xMin, 1f - det.box.yMax)).GetPoint(dist);
            }
            else
            {
                float dw = displayWidth, dh = displayWidth * 0.75f;
                Vector3 origin = anchor.position + anchor.forward * dist;
                ftl = origin + anchor.right * ((det.box.xMin - 0.5f) * dw) + anchor.up * (-(det.box.yMin - 0.5f) * dh);
                ftr = origin + anchor.right * ((det.box.xMax - 0.5f) * dw) + anchor.up * (-(det.box.yMin - 0.5f) * dh);
                fbr = origin + anchor.right * ((det.box.xMax - 0.5f) * dw) + anchor.up * (-(det.box.yMax - 0.5f) * dh);
                fbl = origin + anchor.right * ((det.box.xMin - 0.5f) * dw) + anchor.up * (-(det.box.yMax - 0.5f) * dh);
            }

            var color = GetClassColor(det.classId);
            var lr = boxObj.GetComponent<LineRenderer>();

            if (render3DBoxes && lr != null)
            {
                float depth = dist * boxDepthFactor;
                Vector3 push = anchor.forward * depth;
                Vector3 btl = ftl + push, btr = ftr + push, bbr = fbr + push, bbl = fbl + push;

                // 3D wireframe cube as single continuous line
                lr.positionCount = 16;
                lr.SetPosition(0, ftl); lr.SetPosition(1, ftr);
                lr.SetPosition(2, fbr); lr.SetPosition(3, fbl);
                lr.SetPosition(4, ftl); lr.SetPosition(5, btl);
                lr.SetPosition(6, btr); lr.SetPosition(7, bbr);
                lr.SetPosition(8, bbl); lr.SetPosition(9, btl);
                lr.SetPosition(10, btr); lr.SetPosition(11, ftr);
                lr.SetPosition(12, fbr); lr.SetPosition(13, bbr);
                lr.SetPosition(14, bbl); lr.SetPosition(15, fbl);
            }
            else if (lr != null)
            {
                lr.positionCount = 5;
                lr.SetPosition(0, ftl); lr.SetPosition(1, ftr);
                lr.SetPosition(2, fbr); lr.SetPosition(3, fbl);
                lr.SetPosition(4, ftl);
            }

            if (lr != null)
            {
                var mat = GetClassMaterial(det.classId);
                if (mat != null) lr.material = mat;
                lr.startColor = color;
                lr.endColor = color;
            }

            // Label with distance
            var tmp = boxObj.GetComponentInChildren<TextMeshPro>();
            if (tmp != null)
            {
                tmp.text = $"<b>{det.className}</b> {det.confidence:P0} ~{estDist:F1}m";
                tmp.color = color;
                Vector3 topEdge = ftl + (ftr - ftl) * 0.05f + (fbl - ftl) * 0.05f;
                tmp.transform.position = topEdge;
                tmp.transform.rotation = Quaternion.LookRotation(topEdge - anchor.position, anchor.up);
                float boxHeight = Vector3.Distance(ftl, fbl);
                tmp.fontSize = Mathf.Clamp(boxHeight * 2f, 0.2f, 0.8f);
            }
        }
    }

    static Material CreateUnlitMaterial(Color color)
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("UI/Default");
        if (shader == null) return null;
        var mat = new Material(shader);
        mat.color = color;
        return mat;
    }

    Color GetClassColor(int classId) =>
        ClassColors.TryGetValue(classId, out var c) ? c : Color.white;

    Material GetClassMaterial(int classId)
    {
        if (classMaterials.TryGetValue(classId, out var mat)) return mat;
        mat = CreateUnlitMaterial(GetClassColor(classId));
        classMaterials[classId] = mat;
        return mat;
    }

    GameObject GetOrCreateBox(int index)
    {
        if (index < boxPool.Count) return boxPool[index];

        var box = new GameObject($"DetBox_{index}");
        var lr = box.AddComponent<LineRenderer>();
        lr.positionCount = 16;
        lr.loop = false;
        lr.useWorldSpace = true;
        lr.startWidth = lineWidth;
        lr.endWidth = lineWidth;

        var labelObj = new GameObject("Label");
        labelObj.transform.SetParent(box.transform, false);
        var tmp = labelObj.AddComponent<TextMeshPro>();
        tmp.fontSize = labelSize;
        tmp.alignment = TextAlignmentOptions.TopLeft;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.sortingOrder = 10;
        var rt = tmp.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0.5f, 0.1f);
        rt.pivot = new Vector2(0f, 0f);

        boxPool.Add(box);
        return box;
    }
}
