using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.InferenceEngine;
using Meta.XR;

/// <summary>
/// YOLOv8n-seg inference: detection + instance segmentation.
/// Outputs contour points per detection instead of just bounding boxes.
/// output0: (1, 116, 2100) = 4 bbox + 80 classes + 32 mask coefficients
/// output1: (1, 32, 80, 80) = 32 prototype masks at 80x80
/// </summary>
public class YoloSegDetector : MonoBehaviour
{
    [Header("Model")]
    public ModelAsset modelAsset;

    [Header("Detection Settings")]
    [Range(0.1f, 1f)] public float confidenceThreshold = 0.5f;
    [Range(0.1f, 1f)] public float nmsThreshold = 0.45f;
    [Range(1, 50)] public int maxDetections = 10;
    [Range(0.3f, 0.7f)] public float maskThreshold = 0.5f;

    [Header("Performance")]
    [Range(1, 10)] public int inferenceInterval = 1;
    [Tooltip("Number of radial samples for contour (more = smoother)")]
    [Range(12, 64)] public int contourPoints = 32;

    [Header("Smoothing")]
    [Range(0f, 0.95f)] public float smoothing = 0.8f;
    [Range(0.01f, 0.5f)] public float trackingIoUThreshold = 0.15f;
    [Range(0, 15)] public int persistenceFrames = 5;

    [Header("Input")]
    public int inputWidth = 320;
    public int inputHeight = 320;

    [Header("Camera")]
    public PassthroughCameraAccess passthroughCamera;

    Worker worker;
    Model model;
    Tensor<float> inputTensor;
    int frameCounter;
    float lastInferenceTime;
    public float InferenceTimeMs => lastInferenceTime;

    List<SegDetection> trackedDetections = new();
    public event Action<List<SegDetection>> OnDetections;

    const int MaskProtoH = 80;
    const int MaskProtoW = 80;
    const int NumProtos = 32;
    const int NumClasses = 80;

    public struct SegDetection
    {
        public Rect box;
        public int classId;
        public float confidence;
        public string className;
        public int framesUnseen;
        public List<Vector2> contour; // normalized 0-1 points
    }

    static readonly string[] CocoClasses =
    {
        "person", "bicycle", "car", "motorcycle", "airplane", "bus", "train", "truck", "boat",
        "traffic light", "fire hydrant", "stop sign", "parking meter", "bench", "bird", "cat",
        "dog", "horse", "sheep", "cow", "elephant", "bear", "zebra", "giraffe", "backpack",
        "umbrella", "handbag", "tie", "suitcase", "frisbee", "skis", "snowboard", "sports ball",
        "kite", "baseball bat", "baseball glove", "skateboard", "surfboard", "tennis racket",
        "bottle", "wine glass", "cup", "fork", "knife", "spoon", "bowl", "banana", "apple",
        "sandwich", "orange", "broccoli", "carrot", "hot dog", "pizza", "donut", "cake", "chair",
        "couch", "potted plant", "bed", "dining table", "toilet", "tv", "laptop", "mouse",
        "remote", "keyboard", "cell phone", "microwave", "oven", "toaster", "sink",
        "refrigerator", "book", "clock", "vase", "scissors", "teddy bear", "hair drier", "toothbrush"
    };

    void Start()
    {
        if (modelAsset == null)
        {
            Debug.LogError("[YoloSeg] ModelAsset not assigned!");
            enabled = false;
            return;
        }

        model = ModelLoader.Load(modelAsset);
        worker = new Worker(model, BackendType.GPUCompute);
        inputTensor = new Tensor<float>(new TensorShape(1, 3, inputHeight, inputWidth));

        if (passthroughCamera == null)
            passthroughCamera = FindFirstObjectByType<PassthroughCameraAccess>();

        Debug.Log($"[YoloSeg] Ready: {inputWidth}x{inputHeight}, seg model loaded.");
    }

    void OnDestroy()
    {
        inputTensor?.Dispose();
        worker?.Dispose();
    }

    void Update()
    {
        frameCounter++;
        if (frameCounter % inferenceInterval != 0) return;

        if (passthroughCamera != null && passthroughCamera.IsPlaying)
        {
            var tex = passthroughCamera.GetTexture();
            if (tex != null) { Detect(tex); return; }
        }
    }

    public void Detect(Texture sourceTexture)
    {
        if (worker == null || sourceTexture == null) return;

        float t0 = Time.realtimeSinceStartup;

        TextureConverter.ToTensor(sourceTexture, inputTensor, new TextureTransform());
        worker.Schedule(inputTensor);

        // output0: (1, 116, 2100), output1: (1, 32, 80, 80)
        Tensor<float> gpuOut0, gpuOut1;
        try
        {
            gpuOut0 = worker.PeekOutput(0) as Tensor<float>;
            gpuOut1 = worker.PeekOutput(1) as Tensor<float>;
        }
        catch
        {
            // Fallback: try by name
            gpuOut0 = worker.PeekOutput("output0") as Tensor<float>;
            gpuOut1 = worker.PeekOutput("output1") as Tensor<float>;
        }
        if (gpuOut0 == null || gpuOut1 == null)
        {
            Debug.LogWarning("[YoloSeg] Output tensors null!");
            return;
        }

        using var cpuOut0 = gpuOut0.ReadbackAndClone();
        using var cpuOut1 = gpuOut1.ReadbackAndClone();
        lastInferenceTime = (Time.realtimeSinceStartup - t0) * 1000f;

        var rawDetections = PostProcess(cpuOut0, cpuOut1);
        trackedDetections = SmoothDetections(rawDetections, trackedDetections);
        OnDetections?.Invoke(trackedDetections);
    }

    List<SegDetection> PostProcess(Tensor<float> output0, Tensor<float> masks)
    {
        int numCandidates = output0.shape[2]; // 2100
        var candidates = new List<(SegDetection det, float[] coeffs)>();

        for (int i = 0; i < numCandidates; i++)
        {
            float maxConf = 0f;
            int bestClass = 0;
            for (int c = 0; c < NumClasses; c++)
            {
                float conf = output0[0, c + 4, i];
                if (conf > maxConf) { maxConf = conf; bestClass = c; }
            }
            if (maxConf < confidenceThreshold) continue;

            float cx = output0[0, 0, i] / inputWidth;
            float cy = output0[0, 1, i] / inputHeight;
            float w = output0[0, 2, i] / inputWidth;
            float h = output0[0, 3, i] / inputHeight;

            // Extract 32 mask coefficients
            var coeffs = new float[NumProtos];
            for (int p = 0; p < NumProtos; p++)
                coeffs[p] = output0[0, NumClasses + 4 + p, i];

            candidates.Add((new SegDetection
            {
                box = new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h),
                classId = bestClass,
                confidence = maxConf,
                className = bestClass < CocoClasses.Length ? CocoClasses[bestClass] : $"class_{bestClass}",
                framesUnseen = 0
            }, coeffs));
        }

        // NMS
        candidates.Sort((a, b) => b.det.confidence.CompareTo(a.det.confidence));
        var results = new List<SegDetection>();

        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].det.confidence <= 0) continue;

            // Generate contour from mask
            var det = candidates[i].det;
            det.contour = ExtractContour(masks, candidates[i].coeffs, det.box);
            results.Add(det);

            if (results.Count >= maxDetections) break;

            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (IoU(candidates[i].det.box, candidates[j].det.box) > nmsThreshold)
                {
                    var s = candidates[j];
                    s.det.confidence = 0;
                    candidates[j] = s;
                }
            }
        }

        return results;
    }

    List<Vector2> ExtractContour(Tensor<float> prototypes, float[] coeffs, Rect box)
    {
        // Compute mask value at a point (with sigmoid)
        float MaskAt(int px, int py)
        {
            if (px < 0 || px >= MaskProtoW || py < 0 || py >= MaskProtoH) return 0;
            float val = 0;
            for (int p = 0; p < NumProtos; p++)
                val += coeffs[p] * prototypes[0, p, py, px];
            return 1f / (1f + Mathf.Exp(-val));
        }

        // Center of box in mask space
        float mcx = (box.x + box.width * 0.5f) * MaskProtoW;
        float mcy = (box.y + box.height * 0.5f) * MaskProtoH;
        float maxR = Mathf.Max(box.width * MaskProtoW, box.height * MaskProtoH) * 0.75f;

        var contour = new List<Vector2>();

        // Sample N angles from center, find edge along each ray
        for (int i = 0; i < contourPoints; i++)
        {
            float angle = i * Mathf.PI * 2f / contourPoints;
            float dx = Mathf.Cos(angle);
            float dy = Mathf.Sin(angle);

            // Walk outward from center until mask drops below threshold
            float edgeR = 0;
            for (float r = 0; r < maxR; r += 0.5f)
            {
                int px = Mathf.RoundToInt(mcx + dx * r);
                int py = Mathf.RoundToInt(mcy + dy * r);
                if (MaskAt(px, py) > maskThreshold)
                    edgeR = r;
                else if (edgeR > 0)
                    break; // found edge
            }

            if (edgeR > 0)
            {
                float nx = (mcx + dx * edgeR) / MaskProtoW;
                float ny = (mcy + dy * edgeR) / MaskProtoH;
                contour.Add(new Vector2(nx, ny));
            }
        }

        return contour.Count >= 3 ? contour : BoxToContour(box);
    }

    static List<Vector2> BoxToContour(Rect box)
    {
        return new List<Vector2>
        {
            new(box.xMin, box.yMin), new(box.xMax, box.yMin),
            new(box.xMax, box.yMax), new(box.xMin, box.yMax)
        };
    }

    List<SegDetection> SmoothDetections(List<SegDetection> current, List<SegDetection> previous)
    {
        var result = new List<SegDetection>();
        var matchedPrev = new bool[previous.Count];

        for (int i = 0; i < current.Count; i++)
        {
            int bestIdx = -1;
            float bestIoU = trackingIoUThreshold;
            for (int j = 0; j < previous.Count; j++)
            {
                if (matchedPrev[j] || previous[j].classId != current[i].classId) continue;
                float iou = IoU(current[i].box, previous[j].box);
                if (iou > bestIoU) { bestIoU = iou; bestIdx = j; }
            }

            if (bestIdx >= 0 && smoothing > 0)
            {
                matchedPrev[bestIdx] = true;
                float s = smoothing;
                var prev = previous[bestIdx];
                var cur = current[i];
                var smoothed = cur;
                smoothed.box = new Rect(
                    Mathf.Lerp(cur.box.x, prev.box.x, s),
                    Mathf.Lerp(cur.box.y, prev.box.y, s),
                    Mathf.Lerp(cur.box.width, prev.box.width, s),
                    Mathf.Lerp(cur.box.height, prev.box.height, s));
                smoothed.framesUnseen = 0;
                result.Add(smoothed);
            }
            else
            {
                result.Add(current[i]);
            }
        }

        // Persist unmatched
        for (int j = 0; j < previous.Count; j++)
        {
            if (matchedPrev[j]) continue;
            var prev = previous[j];
            if (prev.framesUnseen < persistenceFrames)
            {
                prev.framesUnseen++;
                prev.confidence *= 0.9f;
                result.Add(prev);
            }
        }

        return result;
    }

    static float IoU(Rect a, Rect b)
    {
        float x1 = Mathf.Max(a.xMin, b.xMin);
        float y1 = Mathf.Max(a.yMin, b.yMin);
        float x2 = Mathf.Min(a.xMax, b.xMax);
        float y2 = Mathf.Min(a.yMax, b.yMax);
        float inter = Mathf.Max(0, x2 - x1) * Mathf.Max(0, y2 - y1);
        float union = a.width * a.height + b.width * b.height - inter;
        return union > 0 ? inter / union : 0;
    }
}
