using System.Collections.Generic;
using UnityEngine;
using TMPro;
using Meta.XR;

/// <summary>
/// Renders YOLOv8-seg contours as LineRenderers in VR.
/// Uses ViewportPointToRay for world-space alignment.
/// </summary>
[RequireComponent(typeof(YoloSegDetector))]
public class ContourRenderer : MonoBehaviour
{
    [Header("Rendering")]
    public float displayDistance = 2f;
    public float lineWidth = 0.004f;
    public float labelSize = 0.4f;

    YoloSegDetector detector;
    List<YoloSegDetector.SegDetection> currentDetections = new();

    Transform anchor;
    GameObject statusIndicator;
    Material statusMat;
    List<GameObject> contourPool = new();
    float logTimer;
    int totalFrames;

    static readonly Dictionary<int, Color> ClassColors = new()
    {
        {0, Color.green},
        {1, new Color(0.2f, 0.6f, 1f)}, {2, new Color(0.2f, 0.6f, 1f)},
        {3, new Color(0.2f, 0.6f, 1f)}, {5, new Color(0.2f, 0.6f, 1f)},
        {7, new Color(0.2f, 0.6f, 1f)},
        {14, Color.yellow}, {15, Color.yellow}, {16, Color.yellow}, {17, Color.yellow},
        {56, new Color(0.8f, 0.4f, 1f)}, {57, new Color(0.8f, 0.4f, 1f)},
        {59, new Color(0.8f, 0.4f, 1f)}, {60, new Color(0.8f, 0.4f, 1f)},
        {62, Color.cyan}, {63, Color.cyan}, {66, Color.cyan}, {67, Color.cyan},
    };

    void Awake()
    {
        detector = GetComponent<YoloSegDetector>();
        detector.OnDetections += OnDetections;
    }

    void Start()
    {
        var rig = FindFirstObjectByType<OVRCameraRig>();
        anchor = rig != null ? rig.centerEyeAnchor : Camera.main?.transform;

        statusIndicator = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        statusIndicator.name = "SegStatusIndicator";
        Destroy(statusIndicator.GetComponent<Collider>());
        statusIndicator.transform.localScale = Vector3.one * 0.05f;
        statusMat = CreateUnlitMaterial(Color.red);
        if (statusMat != null)
            statusIndicator.GetComponent<MeshRenderer>().material = statusMat;

        Debug.Log("[ContourRenderer] Started.");
    }

    void OnDestroy()
    {
        if (detector != null)
            detector.OnDetections -= OnDetections;
    }

    void OnDetections(List<YoloSegDetector.SegDetection> detections)
    {
        currentDetections = detections;
        totalFrames++;
    }

    Color GetClassColor(int classId)
    {
        return ClassColors.TryGetValue(classId, out var c) ? c : Color.white;
    }

    void Update()
    {
        if (anchor == null) return;

        // Status
        Color statusColor = Color.red;
        string status = "NO INPUT";
        var pcaRef = detector.passthroughCamera;
        if (pcaRef != null && pcaRef.IsPlaying)
        {
            var tex = pcaRef.GetTexture();
            status = tex != null ? $"SEG LIVE {tex.width}x{tex.height}" : "SEG (no tex)";
            statusColor = tex != null ? Color.green : Color.yellow;
        }

        if (statusIndicator != null)
            statusIndicator.transform.position = anchor.position + anchor.forward * displayDistance + anchor.up * 0.4f + anchor.right * 0.5f;
        if (statusMat != null)
            statusMat.color = statusColor;

        logTimer += Time.deltaTime;
        if (logTimer > 2f)
        {
            logTimer = 0;
            Debug.Log($"[SEG] {status} | Det:{currentDetections?.Count ?? 0} | F:{totalFrames} | {detector.InferenceTimeMs:F0}ms");
        }

        // Clear old contours
        foreach (var c in contourPool)
            if (c != null) c.SetActive(false);

        if (currentDetections == null || currentDetections.Count == 0) return;

        bool useRaycast = pcaRef != null && pcaRef.IsPlaying;

        for (int i = 0; i < currentDetections.Count; i++)
        {
            var det = currentDetections[i];
            var obj = GetOrCreateContour(i);
            obj.SetActive(true);

            var color = GetClassColor(det.classId);
            var lr = obj.GetComponent<LineRenderer>();

            if (det.contour != null && det.contour.Count >= 3)
            {
                // Render contour points
                lr.positionCount = det.contour.Count + 1; // +1 to close
                for (int p = 0; p < det.contour.Count; p++)
                {
                    Vector3 worldPos = MapToWorld(det.contour[p], useRaycast, pcaRef);
                    lr.SetPosition(p, worldPos);
                }
                lr.SetPosition(det.contour.Count, MapToWorld(det.contour[0], useRaycast, pcaRef)); // close
            }
            else
            {
                // Fallback: rectangle
                lr.positionCount = 5;
                lr.SetPosition(0, MapToWorld(new Vector2(det.box.xMin, det.box.yMin), useRaycast, pcaRef));
                lr.SetPosition(1, MapToWorld(new Vector2(det.box.xMax, det.box.yMin), useRaycast, pcaRef));
                lr.SetPosition(2, MapToWorld(new Vector2(det.box.xMax, det.box.yMax), useRaycast, pcaRef));
                lr.SetPosition(3, MapToWorld(new Vector2(det.box.xMin, det.box.yMax), useRaycast, pcaRef));
                lr.SetPosition(4, MapToWorld(new Vector2(det.box.xMin, det.box.yMin), useRaycast, pcaRef));
            }

            var mat = CreateUnlitMaterial(color);
            if (mat != null) lr.material = mat;
            lr.startColor = color;
            lr.endColor = color;

            // Label
            var tmp = obj.GetComponentInChildren<TextMeshPro>();
            if (tmp != null)
            {
                tmp.text = $"<b>{det.className}</b> {det.confidence:P0}";
                tmp.color = color;
                Vector3 labelPos = MapToWorld(new Vector2(det.box.xMin, det.box.yMin), useRaycast, pcaRef);
                tmp.transform.position = labelPos;
                tmp.transform.rotation = Quaternion.LookRotation(labelPos - anchor.position, anchor.up);
                float boxH = Mathf.Abs(det.box.height) * displayDistance;
                tmp.fontSize = Mathf.Clamp(boxH * 2f, 0.2f, 0.8f);
            }
        }
    }

    Vector3 MapToWorld(Vector2 normalized, bool useRaycast, PassthroughCameraAccess pca)
    {
        if (useRaycast && pca != null)
        {
            Vector2 vp = new Vector2(normalized.x, 1f - normalized.y);
            return pca.ViewportPointToRay(vp).GetPoint(displayDistance);
        }

        float dw = 1.2f, dh = 0.9f;
        Vector3 origin = anchor.position + anchor.forward * displayDistance;
        float x = (normalized.x - 0.5f) * dw;
        float y = -(normalized.y - 0.5f) * dh;
        return origin + anchor.right * x + anchor.up * y;
    }

    static Material CreateUnlitMaterial(Color color)
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) return null;
        var mat = new Material(shader);
        mat.color = color;
        return mat;
    }

    GameObject GetOrCreateContour(int index)
    {
        if (index < contourPool.Count)
            return contourPool[index];

        var obj = new GameObject($"Contour_{index}");
        var lr = obj.AddComponent<LineRenderer>();
        lr.loop = false;
        lr.useWorldSpace = true;
        lr.startWidth = lineWidth;
        lr.endWidth = lineWidth;

        // Label
        var labelObj = new GameObject("Label");
        labelObj.transform.SetParent(obj.transform, false);
        var tmp = labelObj.AddComponent<TextMeshPro>();
        tmp.fontSize = labelSize;
        tmp.alignment = TextAlignmentOptions.TopLeft;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.sortingOrder = 10;
        var rt = tmp.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(0.5f, 0.1f);
        rt.pivot = new Vector2(0f, 0f);

        contourPool.Add(obj);
        return obj;
    }
}
