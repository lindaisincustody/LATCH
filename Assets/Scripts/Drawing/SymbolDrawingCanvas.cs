using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// The pop-up drawing surface. Strokes are rasterised straight into a 96x96 Texture2D
/// on the CPU, which is then handed to SymbolRecognizer.
///
/// The 2D original captured strokes with two extra cameras, a dedicated "Drawing" layer,
/// two LineRenderers and a pair of RenderTextures, then had to subtract that layer from
/// the gameplay camera's culling mask so the strokes did not appear as a blob in world
/// space. None of that is needed here: rasterising directly means the texture the player
/// sees *is* the texture the model receives, so there is nothing to keep in sync and
/// nothing to hide from the main camera.
///
/// The same rasteriser also produces the high-resolution ink stamp that gets left on the
/// whiteboard, so what lands there is the player's own handwriting rather than a glyph.
/// </summary>
public class SymbolDrawingCanvas : MonoBehaviour
{
    const int Res = SymbolRecognizer.Resolution;

    [Header("UI")]
    [Tooltip("Panel toggled on and off. Defaults to this GameObject.")]
    public GameObject panelRoot;
    [Tooltip("The square area the player draws inside.")]
    public RectTransform drawArea;
    [Tooltip("Shows the live ink. Tinted to inkColor, so the texture itself stays white-plus-alpha.")]
    public RawImage display;
    [Tooltip("Paper behind the ink. Coloured to match the sticky note.")]
    public Image paperBackground;
    [Tooltip("Optional. Shows the actual 96x96 black-and-white tensor handed to the model, for debugging.")]
    public RawImage modelDebugDisplay;
    public TextMeshProUGUI targetText;
    public TextMeshProUGUI statusText;

    [Header("Brush")]
    [Tooltip("Stroke width in pixels of the 96x96 image. This is the single biggest accuracy knob.")]
    [Range(1f, 12f)] public float strokeThickness = 3f;
    [Tooltip("Padding around the drawing when it is normalised. 1.2 matches the original's camera margin.")]
    [Range(1f, 2f)] public float normalizeMargin = 1.2f;
    [Tooltip("Flip the image vertically before inference. If predictions are consistently wrong, try toggling this first.")]
    public bool flipVertical = false;

    [Header("Paper")]
    [Tooltip("Resolution of the ink image carried by the sticky note. Higher is crisper but costs memory per note.")]
    public int stampResolution = 384;
    [Tooltip("Resolution of the live preview while drawing.")]
    public int previewResolution = 256;
    [Tooltip("Fallback only. The whiteboard passes the real colour in when it opens the canvas.")]
    public Color paperColor = new Color(1f, 0.9f, 0.36f);
    public Color inkColor = new Color(0.1f, 0.1f, 0.12f);

    [Header("Player")]
    [Tooltip("Frozen while the canvas is open so drawing does not also turn the view.")]
    public DoomPlayerController player;

    Texture2D _modelTexture;      // 96x96 white-on-black, the tensor the model reads
    Color32[] _modelPixels;
    float[] _modelCoverage;

    Texture2D _previewTexture;    // white + alpha, tinted by the RawImage
    Color32[] _previewPixels;
    float[] _previewCoverage;

    readonly List<List<Vector2>> _strokes = new List<List<Vector2>>();
    List<Vector2> _currentStroke;

    string _expectedLabel;
    Action<SymbolRecognizer.Result, bool, Texture2D> _onComplete;
    SymbolRecognizer _recognizer;
    bool _isOpen;
    bool _awaitingRelease;

    public bool IsOpen => _isOpen;

    void Awake()
    {
        if (panelRoot == null) panelRoot = gameObject;
        if (player == null) player = FindAnyObjectByType<DoomPlayerController>();
        _recognizer = FindAnyObjectByType<SymbolRecognizer>();

        _modelTexture = new Texture2D(Res, Res, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        _modelPixels = new Color32[Res * Res];
        _modelCoverage = new float[Res * Res];

        int preview = Mathf.Clamp(previewResolution, 64, 1024);
        _previewTexture = new Texture2D(preview, preview, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        _previewPixels = new Color32[preview * preview];
        _previewCoverage = new float[preview * preview];

        if (display != null)
        {
            display.texture = _previewTexture;
            display.color = inkColor;
        }
        ResolvePaperBackground();
        if (paperBackground != null) paperBackground.color = paperColor;
        if (modelDebugDisplay != null) modelDebugDisplay.texture = _modelTexture;

        ClearStrokes();
        panelRoot.SetActive(false);
    }

    /// <summary>
    /// Finds the Image that acts as the paper if it was not assigned. A DrawingUI built
    /// before the sticky-note change has no such reference, and without it the draw area
    /// would keep whatever colour it was created with regardless of the note.
    /// </summary>
    void ResolvePaperBackground()
    {
        if (paperBackground != null || drawArea == null) return;

        paperBackground = drawArea.GetComponent<Image>();
        if (paperBackground == null) paperBackground = drawArea.GetComponentInChildren<Image>();

        if (paperBackground == null)
        {
            Debug.LogWarning("SymbolDrawingCanvas: no paper Image found under the draw area, so it " +
                             "cannot match the sticky note colour. Delete the DrawingUI object and " +
                             "re-run Tools > Doom > Set Up Whiteboard to rebuild it.", this);
        }
    }

    // ------------------------------------------------------------------ open

    /// <summary>
    /// Opens the canvas. The callback receives the prediction, whether it matched, and
    /// an ink texture of what was drawn. The texture is only supplied on a match, and
    /// ownership passes to the caller, which must Destroy it when done.
    /// </summary>
    public void Open(string expectedLabel, Color notePaperColor, Action<SymbolRecognizer.Result, bool, Texture2D> onComplete)
    {
        paperColor = notePaperColor;

        // Re-resolved on every open so a reference wired up late still takes effect.
        ResolvePaperBackground();
        if (paperBackground != null) paperBackground.color = paperColor;

        _expectedLabel = expectedLabel;
        _onComplete = onComplete;
        _isOpen = true;

        // The click that opened this canvas is still down, and Update order between this
        // and WhiteboardInteractor is undefined, so the same press can arrive here in the
        // same frame and land a dot. Swallow input until the button comes back up.
        _awaitingRelease = true;

        ClearStrokes();
        panelRoot.SetActive(true);

        if (targetText != null)
            targetText.text = SymbolLibrary.ToGlyph(expectedLabel);
        if (statusText != null)
            statusText.text = "Draw the symbol.   Enter = submit   Backspace = clear   Esc = bin the note";

        // Hand the mouse back to the player and stop the controller eating it for look.
        if (player != null) player.controlEnabled = false;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public void Close()
    {
        _isOpen = false;
        panelRoot.SetActive(false);

        if (player != null) player.controlEnabled = true;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    // ----------------------------------------------------------------- input

    void Update()
    {
        if (!_isOpen) return;

        HandleDrawInput();
        HandleKeys();
    }

    void HandleDrawInput()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null || drawArea == null) return;

        if (_awaitingRelease)
        {
            if (mouse.leftButton.isPressed) return;
            _awaitingRelease = false;
        }

        Vector2 screenPoint = mouse.position.ReadValue();

        // Overlay canvas, so the camera argument is null.
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                drawArea, screenPoint, null, out Vector2 local))
            return;

        Rect r = drawArea.rect;
        Vector2 uv = new Vector2(
            Mathf.InverseLerp(r.xMin, r.xMax, local.x),
            Mathf.InverseLerp(r.yMin, r.yMax, local.y));

        bool inside = uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;

        if (mouse.leftButton.wasPressedThisFrame && inside)
        {
            _currentStroke = new List<Vector2>();
            _strokes.Add(_currentStroke);
        }

        if (mouse.leftButton.isPressed && _currentStroke != null && inside)
        {
            // Skip points that land on the same texel, they add nothing but cost.
            if (_currentStroke.Count == 0 ||
                Vector2.Distance(_currentStroke[_currentStroke.Count - 1], uv) > 1f / Res)
            {
                _currentStroke.Add(uv);
                UpdatePreview();
            }
        }

        if (mouse.leftButton.wasReleasedThisFrame)
            _currentStroke = null;
    }

    void HandleKeys()
    {
        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
            Submit();
        else if (kb.backspaceKey.wasPressedThisFrame)
            ClearStrokes();
        else if (kb.escapeKey.wasPressedThisFrame)
            Cancel();
    }

    void Cancel()
    {
        Action<SymbolRecognizer.Result, bool, Texture2D> callback = _onComplete;
        _onComplete = null;
        Close();
        callback?.Invoke(default, false, null);
    }

    // ------------------------------------------------------------- inference

    void Submit()
    {
        if (_strokes.Count == 0)
        {
            if (statusText != null) statusText.text = "Nothing drawn yet.";
            return;
        }

        if (_recognizer == null)
        {
            Debug.LogError("SymbolDrawingCanvas: no SymbolRecognizer in the scene.", this);
            return;
        }

        // Render with normalisation so the drawing fills the frame the way the model was
        // trained to expect, then classify that.
        BuildModelTexture();

        SymbolRecognizer.Result result = _recognizer.Recognize(_modelTexture);
        bool correct = result.confident && result.label == _expectedLabel;

        Debug.Log($"Drew '{result.label}' ({result.confidence:F3}), expected '{_expectedLabel}' -> " +
                  (correct ? "correct" : result.confident ? "wrong symbol" : "not confident enough"));

        // Unclear and plain wrong are both failures now, and both produce a note that
        // gets dropped. Only the ink matters here; the caller reads result.confident if
        // it wants to tell the two apart.
        Texture2D stamp = CreateStampTexture();

        Action<SymbolRecognizer.Result, bool, Texture2D> callback = _onComplete;
        _onComplete = null;
        Close();
        callback?.Invoke(result, correct, stamp);
    }

    // ----------------------------------------------------------- rasterising

    void ClearStrokes()
    {
        _strokes.Clear();
        _currentStroke = null;
        UpdatePreview();
    }

    /// <summary>
    /// Refreshes the on-screen ink. RGB stays white and the coverage goes into alpha, so
    /// the RawImage's tint decides the ink colour and the paper shows through underneath.
    /// Never normalised: the player should see their strokes where they actually put them.
    /// </summary>
    void UpdatePreview()
    {
        int res = _previewTexture.width;

        Array.Clear(_previewCoverage, 0, _previewCoverage.Length);
        Rasterize(_previewCoverage, res, normalize: false, flip: false);

        for (int i = 0; i < _previewPixels.Length; i++)
        {
            byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(_previewCoverage[i]) * 255f);
            _previewPixels[i] = new Color32(255, 255, 255, a);
        }

        _previewTexture.SetPixels32(_previewPixels);
        _previewTexture.Apply(false);
    }

    /// <summary>
    /// Fills the 96x96 buffer the model reads: white strokes on black, matching how the
    /// original captured strokes with a white material against a black camera clear.
    /// </summary>
    void BuildModelTexture()
    {
        Array.Clear(_modelCoverage, 0, _modelCoverage.Length);
        Rasterize(_modelCoverage, Res, normalize: true, flip: flipVertical);

        for (int i = 0; i < _modelPixels.Length; i++)
        {
            byte v = (byte)Mathf.RoundToInt(Mathf.Clamp01(_modelCoverage[i]) * 255f);
            _modelPixels[i] = new Color32(v, v, v, 255);
        }

        _modelTexture.SetPixels32(_modelPixels);
        _modelTexture.Apply(false);
    }

    /// <summary>
    /// Builds a transparent, high-resolution image of the strokes for stamping onto the
    /// whiteboard. RGB is left white so the receiving material can tint it; alpha carries
    /// the stroke coverage. Ownership passes to the caller.
    /// </summary>
    public Texture2D CreateStampTexture()
    {
        int res = Mathf.Clamp(stampResolution, 64, 2048);

        float[] coverage = new float[res * res];
        Rasterize(coverage, res, normalize: true, flip: false);

        Color32[] pixels = new Color32[res * res];
        for (int i = 0; i < pixels.Length; i++)
        {
            byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(coverage[i]) * 255f);
            pixels[i] = new Color32(255, 255, 255, a);
        }

        Texture2D stamp = new Texture2D(res, res, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        stamp.SetPixels32(pixels);
        stamp.Apply(false);

        return stamp;
    }

    /// <summary>
    /// Draws every stroke into a coverage buffer at an arbitrary resolution. When
    /// <paramref name="normalize"/> is set the drawing is scaled and centred to fill the
    /// image, mirroring what the original did by moving an orthographic camera to frame
    /// the stroke bounds. Coverage is fractional at stroke edges, which anti-aliases the
    /// result instead of leaving hard stair-stepped pixels.
    /// </summary>
    void Rasterize(float[] coverage, int res, bool normalize, bool flip)
    {
        if (_strokes.Count == 0) return;

        Vector2 center = new Vector2(0.5f, 0.5f);
        float scale = res;

        if (normalize)
        {
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);

            foreach (List<Vector2> stroke in _strokes)
                foreach (Vector2 p in stroke)
                {
                    min = Vector2.Min(min, p);
                    max = Vector2.Max(max, p);
                }

            center = (min + max) * 0.5f;
            float extent = Mathf.Max(max.x - min.x, max.y - min.y) * normalizeMargin;
            // A dot or a perfectly straight line has zero extent on one axis; clamp so
            // it does not blow up to infinite magnification.
            scale = res / Mathf.Max(extent, 0.05f);
        }

        // Keep the stroke the same visual weight regardless of buffer resolution.
        float radius = strokeThickness * 0.5f * (res / (float)Res);

        foreach (List<Vector2> stroke in _strokes)
        {
            if (stroke.Count == 1)
            {
                DrawDot(coverage, res, ToPixel(stroke[0], center, scale, normalize, res, flip), radius);
                continue;
            }

            for (int i = 1; i < stroke.Count; i++)
            {
                DrawSegment(coverage, res,
                    ToPixel(stroke[i - 1], center, scale, normalize, res, flip),
                    ToPixel(stroke[i], center, scale, normalize, res, flip),
                    radius);
            }
        }
    }

    static Vector2 ToPixel(Vector2 uv, Vector2 center, float scale, bool normalize, int res, bool flip)
    {
        Vector2 p = normalize
            ? (uv - center) * scale + new Vector2(res * 0.5f, res * 0.5f)
            : uv * res;

        if (flip) p.y = res - p.y;
        return p;
    }

    static void DrawSegment(float[] coverage, int res, Vector2 a, Vector2 b, float radius)
    {
        float distance = Vector2.Distance(a, b);
        int steps = Mathf.Max(1, Mathf.CeilToInt(distance));

        for (int i = 0; i <= steps; i++)
            DrawDot(coverage, res, Vector2.Lerp(a, b, i / (float)steps), radius);
    }

    static void DrawDot(float[] coverage, int res, Vector2 p, float radius)
    {
        int minX = Mathf.Max(0, Mathf.FloorToInt(p.x - radius - 1f));
        int maxX = Mathf.Min(res - 1, Mathf.CeilToInt(p.x + radius + 1f));
        int minY = Mathf.Max(0, Mathf.FloorToInt(p.y - radius - 1f));
        int maxY = Mathf.Min(res - 1, Mathf.CeilToInt(p.y + radius + 1f));

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                float dx = x + 0.5f - p.x;
                float dy = y + 0.5f - p.y;
                float d = Mathf.Sqrt(dx * dx + dy * dy);

                // Fade over the outermost pixel for a soft edge.
                float value = Mathf.Clamp01(radius - d + 0.5f);
                if (value <= 0f) continue;

                int index = y * res + x;
                if (value > coverage[index]) coverage[index] = value;
            }
        }
    }

    void OnDestroy()
    {
        if (_modelTexture != null) Destroy(_modelTexture);
        if (_previewTexture != null) Destroy(_previewTexture);
    }
}
