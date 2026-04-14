using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>
/// Renders YOLO bounding boxes with LineRenderer + TMP labels in VR.
/// Status indicator: green sphere = LIVE, yellow = WAITING, red = NO INPUT.
/// </summary>
[RequireComponent(typeof(YoloDetector))]
public class BoundingBoxRenderer : MonoBehaviour
{
    [Header("Rendering")]
    public float displayDistance = 2f;
    public float displayWidth = 1.2f;
    public float lineWidth = 0.005f;
    public float labelSize = 0.4f;

    YoloDetector detector;
    List<YoloDetector.Detection> currentDetections = new();

    Transform anchor;
    GameObject statusIndicator;
    Material statusMat;
    List<GameObject> boxPool = new();
    Dictionary<int, Material> classMaterials = new();

    // Colors by class category
    static readonly Dictionary<int, Color> ClassColors = new()
    {
        {0, Color.green}, // person
        {1, new Color(0.2f, 0.6f, 1f)}, {2, new Color(0.2f, 0.6f, 1f)},  // bicycle, car
        {3, new Color(0.2f, 0.6f, 1f)}, {4, new Color(0.2f, 0.6f, 1f)},  // motorcycle, airplane
        {5, new Color(0.2f, 0.6f, 1f)}, {6, new Color(0.2f, 0.6f, 1f)},  // bus, train
        {7, new Color(0.2f, 0.6f, 1f)}, {8, new Color(0.2f, 0.6f, 1f)},  // truck, boat
        {14, Color.yellow}, {15, Color.yellow}, {16, Color.yellow}, {17, Color.yellow},
        {18, Color.yellow}, {19, Color.yellow}, {20, Color.yellow}, {21, Color.yellow},
        {22, Color.yellow}, {23, Color.yellow}, // animals
        {62, Color.cyan}, {63, Color.cyan}, {64, Color.cyan}, {65, Color.cyan},
        {66, Color.cyan}, {67, Color.cyan}, // electronics
        {46, new Color(1f, 0.5f, 0f)}, {47, new Color(1f, 0.5f, 0f)},
        {48, new Color(1f, 0.5f, 0f)}, {49, new Color(1f, 0.5f, 0f)},
        {50, new Color(1f, 0.5f, 0f)}, {51, new Color(1f, 0.5f, 0f)},
        {52, new Color(1f, 0.5f, 0f)}, {53, new Color(1f, 0.5f, 0f)},
        {54, new Color(1f, 0.5f, 0f)}, {55, new Color(1f, 0.5f, 0f)}, // food
        {56, new Color(0.8f, 0.4f, 1f)}, {57, new Color(0.8f, 0.4f, 1f)},
        {58, new Color(0.8f, 0.4f, 1f)}, {59, new Color(0.8f, 0.4f, 1f)},
        {60, new Color(0.8f, 0.4f, 1f)}, {61, new Color(0.8f, 0.4f, 1f)}, // furniture
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
        statusIndicator.GetComponent<MeshRenderer>().material = statusMat;

        Debug.Log("[BBR] 3D box renderer with TMP labels started.");
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
                if (tex != null)
                {
                    status = $"LIVE {tex.width}x{tex.height}";
                    statusColor = Color.green;
                }
                else
                {
                    status = "LIVE (no tex)";
                    statusColor = new Color(0.5f, 1f, 0);
                }
            }
            else
            {
                status = "WAITING PERMISSION";
                statusColor = Color.yellow;
            }
        }
        else if (detector.testTexture != null)
        {
            status = "TEST IMAGE";
            statusColor = Color.cyan;
        }
        else
        {
            status = "NO INPUT";
            statusColor = Color.red;
        }

        if (statusIndicator != null)
        {
            statusIndicator.transform.position = anchor.position
                + anchor.forward * displayDistance
                + anchor.up * 0.4f
                + anchor.right * 0.5f;
        }
        if (statusMat != null)
            statusMat.color = statusColor;

        logTimer += Time.deltaTime;
        if (logTimer > 2f)
        {
            logTimer = 0;
            int detCount = currentDetections?.Count ?? 0;
            Debug.Log($"[BBR] {status} | Det:{detCount} | F:{totalFrames} | {detector.InferenceTimeMs:F0}ms");
        }

        // Clear old boxes
        foreach (var box in boxPool)
            if (box != null) box.SetActive(false);

        if (currentDetections == null || currentDetections.Count == 0) return;

        bool useRaycast = pcaRef != null && pcaRef.IsPlaying;

        for (int i = 0; i < currentDetections.Count; i++)
        {
            var det = currentDetections[i];
            GameObject boxObj = GetOrCreateBox(i);
            boxObj.SetActive(true);

            Vector3 tl, tr, br, bl;

            if (useRaycast)
            {
                Vector2 vpTL = new Vector2(det.box.xMin, 1f - det.box.yMin);
                Vector2 vpTR = new Vector2(det.box.xMax, 1f - det.box.yMin);
                Vector2 vpBR = new Vector2(det.box.xMax, 1f - det.box.yMax);
                Vector2 vpBL = new Vector2(det.box.xMin, 1f - det.box.yMax);

                tl = pcaRef.ViewportPointToRay(vpTL).GetPoint(displayDistance);
                tr = pcaRef.ViewportPointToRay(vpTR).GetPoint(displayDistance);
                br = pcaRef.ViewportPointToRay(vpBR).GetPoint(displayDistance);
                bl = pcaRef.ViewportPointToRay(vpBL).GetPoint(displayDistance);
            }
            else
            {
                float dw = displayWidth;
                float dh = displayWidth * 0.75f;
                Vector3 origin = anchor.position + anchor.forward * displayDistance;
                float left = (det.box.xMin - 0.5f) * dw;
                float right = (det.box.xMax - 0.5f) * dw;
                float top = -(det.box.yMin - 0.5f) * dh;
                float bottom = -(det.box.yMax - 0.5f) * dh;
                tl = origin + anchor.right * left + anchor.up * top;
                tr = origin + anchor.right * right + anchor.up * top;
                br = origin + anchor.right * right + anchor.up * bottom;
                bl = origin + anchor.right * left + anchor.up * bottom;
            }

            // Update LineRenderer
            var color = GetClassColor(det.classId);
            var lr = boxObj.GetComponent<LineRenderer>();
            if (lr != null)
            {
                var mat = GetClassMaterial(det.classId);
                if (mat != null) lr.material = mat;
                lr.startColor = color;
                lr.endColor = color;
                lr.SetPosition(0, tl);
                lr.SetPosition(1, tr);
                lr.SetPosition(2, br);
                lr.SetPosition(3, bl);
                lr.SetPosition(4, tl);
            }

            // Update TMP label — inside box at top edge
            var tmp = boxObj.GetComponentInChildren<TextMeshPro>();
            if (tmp != null)
            {
                tmp.text = $"<b>{det.className}</b> {det.confidence:P0}";
                tmp.color = color;
                // Position at top-left, slightly inside the box
                Vector3 topEdge = tl + (tr - tl) * 0.05f + (bl - tl) * 0.05f;
                tmp.transform.position = topEdge;
                tmp.transform.rotation = Quaternion.LookRotation(
                    topEdge - anchor.position, anchor.up);
                // Scale label proportional to box height
                float boxHeight = Vector3.Distance(tl, bl);
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

    Color GetClassColor(int classId)
    {
        return ClassColors.TryGetValue(classId, out var c) ? c : Color.white;
    }

    Material GetClassMaterial(int classId)
    {
        if (classMaterials.TryGetValue(classId, out var mat))
            return mat;
        var color = GetClassColor(classId);
        mat = CreateUnlitMaterial(color);
        classMaterials[classId] = mat;
        return mat;
    }

    GameObject GetOrCreateBox(int index)
    {
        if (index < boxPool.Count)
            return boxPool[index];

        var box = new GameObject($"DetBox_{index}");

        // LineRenderer for box outline
        var lr = box.AddComponent<LineRenderer>();
        lr.positionCount = 5;
        lr.loop = false;
        lr.useWorldSpace = true;
        lr.startWidth = lineWidth;
        lr.endWidth = lineWidth;

        // TMP label as child
        var labelObj = new GameObject("Label");
        labelObj.transform.SetParent(box.transform, false);
        var tmp = labelObj.AddComponent<TextMeshPro>();
        tmp.fontSize = labelSize;
        tmp.alignment = TextAlignmentOptions.TopLeft;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.sortingOrder = 10;
        tmp.enableAutoSizing = false;
        var rt = tmp.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0.5f, 0.1f);
        rt.pivot = new Vector2(0f, 0f);

        boxPool.Add(box);
        return box;
    }
}
