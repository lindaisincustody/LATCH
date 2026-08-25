using Unity.InferenceEngine;
using UnityEngine;

/// <summary>
/// Wraps the ONNX symbol classifier.
///
/// Ported from a Barracuda implementation. Barracuda does not exist in Unity 6, so this
/// uses the Inference Engine package (com.unity.ai.inference), which is what Sentis was
/// renamed to. The mapping from the old API is:
///     ModelLoader.Load(asset).CreateWorker(device)  ->  new Worker(model, backendType)
///     worker.Execute(tensor)                        ->  worker.Schedule(tensor)
///     worker.PeekOutput()                           ->  worker.PeekOutput() as Tensor&lt;float&gt;
///     tensor.AsFloats()                             ->  tensor.DownloadToArray()
///     new Tensor(renderTexture, 1)                  ->  TextureConverter.ToTensor(tex, tensor)
/// </summary>
public class SymbolRecognizer : MonoBehaviour
{
    public const int Resolution = 96;

    [Header("Model")]
    [Tooltip("Assets/MLmodels/model_barracuda.onnx. The name is historical, the file is plain ONNX and imports fine.")]
    public ModelAsset modelAsset;
    [Tooltip("CPU is the right pick here: the model is tiny and the result is needed on the CPU immediately.")]
    public BackendType backend = BackendType.CPU;

    [Header("Acceptance")]
    [Tooltip("Below this confidence the drawing is rejected as unreadable rather than counted as a wrong answer.")]
    [Range(0f, 1f)] public float confidenceThreshold = 0.91f;
    [Tooltip("Tick if the model outputs raw logits rather than probabilities. Check the Inspector readout: if confidences sit outside 0..1, turn this on.")]
    public bool applySoftmax = false;

    Worker _worker;
    Tensor<float> _input;
    bool _ready;

    public struct Result
    {
        public string label;
        public float confidence;
        public bool confident;
    }

    void Awake()
    {
        if (modelAsset == null)
        {
            Debug.LogError("SymbolRecognizer: no ModelAsset assigned. Drag in Assets/MLmodels/model_barracuda.onnx.", this);
            return;
        }

        Model model = ModelLoader.Load(modelAsset);
        _worker = new Worker(model, backend);

        // NCHW: one image, one channel, 96x96. Single channel because the model was
        // trained on the black-and-white capture, matching the old `new Tensor(rt, 1)`.
        _input = new Tensor<float>(new TensorShape(1, 1, Resolution, Resolution));
        _ready = true;
    }

    /// <summary>
    /// Classifies a 96x96 black-background / white-stroke image.
    /// </summary>
    public Result Recognize(Texture image)
    {
        Result result = new Result { label = "Unknown", confidence = 0f, confident = false };
        if (!_ready || image == null) return result;

        TextureConverter.ToTensor(image, _input);
        _worker.Schedule(_input);

        Tensor<float> output = _worker.PeekOutput() as Tensor<float>;
        if (output == null)
        {
            Debug.LogError("SymbolRecognizer: model output was not a float tensor.", this);
            return result;
        }

        // PeekOutput hands back a reference the worker still owns, so this must not be disposed.
        float[] scores = output.DownloadToArray();
        if (applySoftmax) Softmax(scores);

        int best = 0;
        for (int i = 1; i < scores.Length; i++)
            if (scores[i] > scores[best]) best = i;

        result.confidence = scores[best];
        result.label = best < SymbolLibrary.Labels.Length ? SymbolLibrary.Labels[best] : "Unknown";
        result.confident = result.confidence >= confidenceThreshold;

        if (scores.Length != SymbolLibrary.Labels.Length)
        {
            Debug.LogWarning($"SymbolRecognizer: model returned {scores.Length} classes but " +
                             $"SymbolLibrary lists {SymbolLibrary.Labels.Length}. Labels are probably misaligned.", this);
        }

        return result;
    }

    static void Softmax(float[] values)
    {
        float max = float.NegativeInfinity;
        for (int i = 0; i < values.Length; i++)
            if (values[i] > max) max = values[i];

        float sum = 0f;
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = Mathf.Exp(values[i] - max);
            sum += values[i];
        }

        if (sum <= 0f) return;
        for (int i = 0; i < values.Length; i++) values[i] /= sum;
    }

    void OnDestroy()
    {
        _worker?.Dispose();
        _input?.Dispose();
        _worker = null;
        _input = null;
        _ready = false;
    }
}
