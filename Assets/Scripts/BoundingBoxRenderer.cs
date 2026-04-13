using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Renders YOLO bounding boxes as 3D cubes in VR.
/// Status indicator: green sphere = LIVE, yellow = WAITING, red = NO INPUT.
/// All text info is logged to logcat (adb logcat -s Unity).
/// </summary>
[RequireComponent(typeof(YoloDetector))]
public class BoundingBoxRenderer : MonoBehaviour
{
    [Header("Rendering")]
    public Color boxColor = Color.green;
    public float displayDistance = 2f;
    public float displayWidth = 1.2f;

    YoloDetector detector;
    List<YoloDetector.Detection> currentDetections = new();

    Transform anchor;
    GameObject statusIndicator;
    Material statusMat;
    List<GameObject> boxPool = new();
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

        // Status indicator sphere (top-right of view)
        statusIndicator = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        statusIndicator.name = "StatusIndicator";
        Destroy(statusIndicator.GetComponent<Collider>());
        statusIndicator.transform.localScale = Vector3.one * 0.05f;
        statusMat = CreateUnlitMaterial(Color.red);
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

        // Determine camera status
        string status;
        Color statusColor;
        bool hasInput = false;

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
                    hasInput = true;
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
            hasInput = true;
        }
        else
        {
            status = "NO INPUT";
            statusColor = Color.red;
        }

        // Update status indicator position and color
        if (statusIndicator != null)
        {
            statusIndicator.transform.position = anchor.position
                + anchor.forward * displayDistance
                + anchor.up * 0.4f
                + anchor.right * 0.5f;
        }
        if (statusMat != null)
            statusMat.color = statusColor;

        // Log status periodically
        logTimer += Time.deltaTime;
        if (logTimer > 2f)
        {
            logTimer = 0;
            int detCount = currentDetections?.Count ?? 0;
            Debug.Log($"[BBR] {status} | Det:{detCount} | F:{totalFrames}");
        }

        // Clear old boxes
        foreach (var box in boxPool)
            if (box != null) box.SetActive(false);

        if (currentDetections == null || currentDetections.Count == 0) return;

        // Use PassthroughCameraAccess to map detection coords to world-space rays
        bool useRaycast = pcaRef != null && pcaRef.IsPlaying;

        for (int i = 0; i < currentDetections.Count; i++)
        {
            var det = currentDetections[i];
            GameObject boxObj = GetOrCreateBox(i);
            boxObj.SetActive(true);

            Vector3 tl, tr, br, bl;

            if (useRaycast)
            {
                // Map YOLO normalized coords to camera viewport (0-1)
                // YOLO Y is top-down, viewport Y is bottom-up
                Vector2 vpTL = new Vector2(det.box.xMin, 1f - det.box.yMin);
                Vector2 vpTR = new Vector2(det.box.xMax, 1f - det.box.yMin);
                Vector2 vpBR = new Vector2(det.box.xMax, 1f - det.box.yMax);
                Vector2 vpBL = new Vector2(det.box.xMin, 1f - det.box.yMax);

                // Project rays from camera through viewport points
                tl = pcaRef.ViewportPointToRay(vpTL).GetPoint(displayDistance);
                tr = pcaRef.ViewportPointToRay(vpTR).GetPoint(displayDistance);
                br = pcaRef.ViewportPointToRay(vpBR).GetPoint(displayDistance);
                bl = pcaRef.ViewportPointToRay(vpBL).GetPoint(displayDistance);
            }
            else
            {
                // Fallback: simple projection
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

            var lr = boxObj.GetComponent<LineRenderer>();
            if (lr != null)
            {
                lr.SetPosition(0, tl);
                lr.SetPosition(1, tr);
                lr.SetPosition(2, br);
                lr.SetPosition(3, bl);
                lr.SetPosition(4, tl);
            }
        }
    }

    static Material CreateUnlitMaterial(Color color)
    {
        // Try URP shader first, fallback to built-in
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("UI/Default");
        if (shader == null)
        {
            Debug.LogError("[BBR] No shader found!");
            return null;
        }
        var mat = new Material(shader);
        mat.color = color;
        return mat;
    }

    GameObject GetOrCreateBox(int index)
    {
        if (index < boxPool.Count)
            return boxPool[index];

        var box = new GameObject($"DetBox_{index}");
        var lr = box.AddComponent<LineRenderer>();
        lr.positionCount = 5; // 4 corners + close
        lr.loop = false;
        lr.useWorldSpace = true;
        lr.startWidth = 0.005f;
        lr.endWidth = 0.005f;
        var mat = CreateUnlitMaterial(boxColor);
        if (mat != null)
            lr.material = mat;
        lr.startColor = boxColor;
        lr.endColor = boxColor;

        boxPool.Add(box);
        return box;
    }
}
