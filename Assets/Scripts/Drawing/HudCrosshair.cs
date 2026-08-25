using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Centre-screen aiming reticle. Four bars around a gap, built from plain UI Images at
/// runtime so there is no sprite to import and nothing to keep at the right resolution.
///
/// It opens up and changes colour when the player is aiming at something clickable, which
/// is what makes it readable as a target scope rather than just a decoration.
/// </summary>
public class HudCrosshair : MonoBehaviour
{
    [Header("Shape")]
    [Tooltip("Distance from centre to the inner end of each bar, in reference pixels.")]
    public float gap = 7f;
    [Tooltip("Length of each bar.")]
    public float length = 11f;
    [Tooltip("Thickness of each bar.")]
    public float thickness = 3f;
    [Tooltip("Extra gap added when a target is acquired, so the reticle blooms outward.")]
    public float acquiredSpread = 5f;
    [Tooltip("Dot drawn in the very centre. Set to 0 to hide it.")]
    public float centerDotSize = 3f;

    [Header("Colours")]
    public Color idleColor = new Color(1f, 1f, 1f, 0.65f);
    public Color acquiredColor = new Color(0.3f, 1f, 0.4f, 0.95f);
    [Tooltip("How quickly the reticle reacts to acquiring or losing a target.")]
    public float responseSpeed = 14f;

    RectTransform _root;
    readonly RectTransform[] _bars = new RectTransform[4];
    readonly Image[] _barImages = new Image[4];
    Image _dot;

    SymbolDrawingCanvas _drawingCanvas;
    bool _acquired;
    float _blend;

    void Awake()
    {
        _root = GetComponent<RectTransform>();
        _drawingCanvas = FindAnyObjectByType<SymbolDrawingCanvas>();

        _root.anchorMin = _root.anchorMax = new Vector2(0.5f, 0.5f);
        _root.pivot = new Vector2(0.5f, 0.5f);
        _root.anchoredPosition = Vector2.zero;
        _root.sizeDelta = Vector2.zero;

        // Up, down, left, right.
        for (int i = 0; i < 4; i++)
        {
            GameObject bar = new GameObject("Bar" + i, typeof(RectTransform));
            bar.transform.SetParent(_root, false);

            _bars[i] = bar.GetComponent<RectTransform>();
            _bars[i].anchorMin = _bars[i].anchorMax = new Vector2(0.5f, 0.5f);
            _bars[i].pivot = new Vector2(0.5f, 0.5f);

            _barImages[i] = bar.AddComponent<Image>();
            _barImages[i].raycastTarget = false;
        }

        if (centerDotSize > 0f)
        {
            GameObject dot = new GameObject("Dot", typeof(RectTransform));
            dot.transform.SetParent(_root, false);

            RectTransform dotRect = dot.GetComponent<RectTransform>();
            dotRect.anchorMin = dotRect.anchorMax = new Vector2(0.5f, 0.5f);
            dotRect.pivot = new Vector2(0.5f, 0.5f);
            dotRect.anchoredPosition = Vector2.zero;
            dotRect.sizeDelta = Vector2.one * centerDotSize;

            _dot = dot.AddComponent<Image>();
            _dot.raycastTarget = false;
        }

        Apply(0f);
    }

    /// <summary>Called every frame by WhiteboardInteractor.</summary>
    public void SetTargetAcquired(bool acquired)
    {
        _acquired = acquired;
    }

    void LateUpdate()
    {
        // Hide entirely while the drawing canvas is up: there is a real cursor then, and
        // a reticle stuck to the middle of a drawing surface is just confusing.
        bool visible = _drawingCanvas == null || !_drawingCanvas.IsOpen;
        for (int i = 0; i < 4; i++) _barImages[i].enabled = visible;
        if (_dot != null) _dot.enabled = visible;
        if (!visible) return;

        _blend = Mathf.MoveTowards(_blend, _acquired ? 1f : 0f, Time.deltaTime * responseSpeed);
        Apply(_blend);
    }

    void Apply(float blend)
    {
        float spread = gap + acquiredSpread * blend;
        Color color = Color.Lerp(idleColor, acquiredColor, blend);

        Vector2 vertical = new Vector2(thickness, length);
        Vector2 horizontal = new Vector2(length, thickness);
        float offset = spread + length * 0.5f;

        SetBar(0, vertical, new Vector2(0f, offset), color);
        SetBar(1, vertical, new Vector2(0f, -offset), color);
        SetBar(2, horizontal, new Vector2(-offset, 0f), color);
        SetBar(3, horizontal, new Vector2(offset, 0f), color);

        if (_dot != null) _dot.color = color;
    }

    void SetBar(int index, Vector2 size, Vector2 position, Color color)
    {
        if (_bars[index] == null) return;

        _bars[index].sizeDelta = size;
        _bars[index].anchoredPosition = position;
        _barImages[index].color = color;
    }
}
