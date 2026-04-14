using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.InferenceEngine;
using Meta.XR;

/// <summary>
/// YOLOv8n inference using Unity Inference Engine.
/// Input: (1, 3, 640, 640) BCHW
/// Output: (1, 84, 8400) -> 84 = 4 bbox + 80 classes, 8400 candidates
/// Uses PassthroughCameraAccess on Quest 3, falls back to testTexture in Editor.
/// </summary>
public class YoloDetector : MonoBehaviour
{
    [Header("Model")]
    public ModelAsset modelAsset;

    [Header("Detection Settings")]
    [Range(0.1f, 1f)] public float confidenceThreshold = 0.5f;
    [Range(0.1f, 1f)] public float nmsThreshold = 0.45f;
    [Range(1, 50)] public int maxDetections = 20;

    [Header("Performance")]
    [Tooltip("Run inference every N frames (1=every frame, 3=skip 2)")]
    [Range(1, 10)] public int inferenceInterval = 1;

    [Header("Smoothing")]
    [Tooltip("Blend factor for box position smoothing (0=no smoothing, 0.85=very smooth)")]
    [Range(0f, 0.95f)] public float smoothing = 0.8f;
    [Tooltip("Min IoU to match detections across frames")]
    [Range(0.01f, 0.5f)] public float trackingIoUThreshold = 0.15f;
    [Tooltip("Frames to keep a detection visible after it disappears")]
    [Range(0, 15)] public int persistenceFrames = 5;

    [Header("Input")]
    public Texture2D testTexture; // Fallback for Editor testing
    public int inputWidth = 640;
    public int inputHeight = 640;

    [Header("Camera")]
    public PassthroughCameraAccess passthroughCamera;

    Worker worker;
    Model model;
    Tensor<float> inputTensor;
    int frameCounter;
    float lastInferenceTime;
    public float InferenceTimeMs => lastInferenceTime;

    List<Detection> trackedDetections = new();

    public event Action<List<Detection>> OnDetections;

    public struct Detection
    {
        public Rect box;        // normalized 0-1
        public int classId;
        public float confidence;
        public string className;
        public int framesUnseen; // 0 = seen this frame, >0 = persisting
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
            Debug.LogError("[YoloDetector] ModelAsset not assigned!");
            enabled = false;
            return;
        }

        model = ModelLoader.Load(modelAsset);
        worker = new Worker(model, BackendType.GPUCompute);
        inputTensor = new Tensor<float>(new TensorShape(1, 3, inputHeight, inputWidth));

        // Auto-find PassthroughCameraAccess if not assigned
        if (passthroughCamera == null)
            passthroughCamera = FindFirstObjectByType<PassthroughCameraAccess>();

        Debug.Log("[YoloDetector] Model loaded, worker ready (GPUCompute).");
    }

    void OnDestroy()
    {
        inputTensor?.Dispose();
        worker?.Dispose();
    }

    /// <summary>
    /// Run detection on a Texture. Call from Update or on-demand.
    /// </summary>
    public void Detect(Texture sourceTexture)
    {
        if (worker == null || sourceTexture == null) return;

        float t0 = Time.realtimeSinceStartup;

        TextureConverter.ToTensor(sourceTexture, inputTensor, new TextureTransform());
        worker.Schedule(inputTensor);

        var gpuOutput = worker.PeekOutput() as Tensor<float>;
        if (gpuOutput == null) return;

        using var cpuOutput = gpuOutput.ReadbackAndClone();
        lastInferenceTime = (Time.realtimeSinceStartup - t0) * 1000f;

        var rawDetections = PostProcess(cpuOutput);
        trackedDetections = SmoothDetections(rawDetections, trackedDetections);
        OnDetections?.Invoke(trackedDetections);
    }

    void Update()
    {
        // Frame skipping
        frameCounter++;
        if (frameCounter % inferenceInterval != 0) return;

        // Priority: live camera > test texture
        if (passthroughCamera != null && passthroughCamera.IsPlaying)
        {
            var camTexture = passthroughCamera.GetTexture();
            if (camTexture != null)
            {
                Detect(camTexture);
                return;
            }
        }

        // Fallback to test texture (Editor testing)
        if (testTexture != null)
        {
            Detect(testTexture);
        }
    }

    List<Detection> SmoothDetections(List<Detection> current, List<Detection> previous)
    {
        var result = new List<Detection>();
        var matchedPrev = new bool[previous.Count];
        var matchedCurr = new bool[current.Count];

        // Match current detections to previous by IoU (same class)
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
                matchedCurr[i] = true;
                float s = smoothing;
                var prev = previous[bestIdx].box;
                var cur = current[i];
                result.Add(new Detection
                {
                    box = new Rect(
                        Mathf.Lerp(cur.box.x, prev.x, s),
                        Mathf.Lerp(cur.box.y, prev.y, s),
                        Mathf.Lerp(cur.box.width, prev.width, s),
                        Mathf.Lerp(cur.box.height, prev.height, s)),
                    classId = cur.classId,
                    confidence = cur.confidence,
                    className = cur.className,
                    framesUnseen = 0
                });
            }
            else
            {
                matchedCurr[i] = true;
                result.Add(current[i]); // new detection
            }
        }

        // Persist unmatched previous detections for a few frames
        for (int j = 0; j < previous.Count; j++)
        {
            if (matchedPrev[j]) continue;
            var prev = previous[j];
            if (prev.framesUnseen < persistenceFrames)
            {
                prev.framesUnseen++;
                prev.confidence *= 0.9f; // fade confidence
                result.Add(prev);
            }
        }

        return result;
    }

    List<Detection> PostProcess(Tensor<float> output)
    {
        // output shape: (1, 84, 8400)
        // Row 0-3: cx, cy, w, h (in pixels, 640x640 space)
        // Row 4-83: class confidences
        int numCandidates = output.shape[2]; // 8400
        int numClasses = output.shape[1] - 4; // 80

        var candidates = new List<Detection>();

        for (int i = 0; i < numCandidates; i++)
        {
            // Find best class
            float maxConf = 0f;
            int bestClass = 0;
            for (int c = 0; c < numClasses; c++)
            {
                float conf = output[0, c + 4, i];
                if (conf > maxConf)
                {
                    maxConf = conf;
                    bestClass = c;
                }
            }

            if (maxConf < confidenceThreshold) continue;

            float cx = output[0, 0, i] / inputWidth;
            float cy = output[0, 1, i] / inputHeight;
            float w = output[0, 2, i] / inputWidth;
            float h = output[0, 3, i] / inputHeight;

            candidates.Add(new Detection
            {
                box = new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h),
                classId = bestClass,
                confidence = maxConf,
                className = bestClass < CocoClasses.Length ? CocoClasses[bestClass] : $"class_{bestClass}"
            });
        }

        // Non-Maximum Suppression
        candidates.Sort((a, b) => b.confidence.CompareTo(a.confidence));
        var results = new List<Detection>();

        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].confidence <= 0) continue;
            results.Add(candidates[i]);
            if (results.Count >= maxDetections) break;

            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (IoU(candidates[i].box, candidates[j].box) > nmsThreshold)
                {
                    var suppressed = candidates[j];
                    suppressed.confidence = 0;
                    candidates[j] = suppressed;
                }
            }
        }

        return results;
    }

    static float IoU(Rect a, Rect b)
    {
        float x1 = Mathf.Max(a.xMin, b.xMin);
        float y1 = Mathf.Max(a.yMin, b.yMin);
        float x2 = Mathf.Min(a.xMax, b.xMax);
        float y2 = Mathf.Min(a.yMax, b.yMax);

        float intersection = Mathf.Max(0, x2 - x1) * Mathf.Max(0, y2 - y1);
        float union = a.width * a.height + b.width * b.height - intersection;

        return union > 0 ? intersection / union : 0;
    }
}
