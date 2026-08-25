using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Turns a cube into a whiteboard challenge. A red spot appears somewhere on the chosen
/// face along with a target symbol; clicking the spot opens the drawing canvas, and the
/// drawing is checked against that target. A correct answer stamps the glyph onto the
/// board at that spot and moves the spot somewhere it has not been before.
///
/// Put this on the cube itself. It needs a Collider so the player can aim at it.
/// </summary>
[RequireComponent(typeof(Collider))]
public class Whiteboard : MonoBehaviour
{
    [Header("References")]
    public SymbolDrawingCanvas drawingCanvas;

    [Header("Challenge")]
    [Tooltip("Labels the board may ask for. Must match entries in SymbolLibrary.Labels. Leave empty to draw from the full set.")]
    public string[] challengeSymbols =
    {
        "_Heart", "_star", "_square", "_bigtriangleup", "_Sigma",
        "_infty", "_alpha", "_theta", "_lambda", "_downarrow"
    };

    [Header("Spot")]
    [Tooltip("Which local face the spot sits on. -Z is the cube's back face, +Z its forward face.")]
    public Vector3 faceNormal = Vector3.back;
    [Tooltip("Spot diameter in world units.")]
    public float spotSize = 0.25f;
    [Tooltip("How close the aim ray must land to the spot's centre to count as a click, in world units.")]
    public float clickRadius = 0.2f;
    [Tooltip("Fraction of the face kept clear at the edges so spots never straddle a corner.")]
    [Range(0f, 0.45f)] public float edgeMargin = 0.15f;
    [Tooltip("Minimum world-space distance between a new spot and any note already on the board. Automatically raised to clear the note's own diagonal, so notes cannot land on top of each other.")]
    public float minSpotSeparation = 0.6f;
    [Tooltip("Allow notes to overlap. Real boards look like this; notes still layer cleanly.")]
    public bool allowOverlap = false;
    [Tooltip("How far the spot and stamped drawings float off the board surface, in WORLD metres. Too small and they z-fight with the board and wink out at certain angles.")]
    public float surfaceOffset = 0.01f;

    [Header("Sticky Notes")]
    [Tooltip("Width and height of a sticky note in world metres.")]
    public float noteSize = 0.35f;
    [Tooltip("Notes pick one of these at random. Classic Post-it shades: yellow, pink, blue, green.")]
    public Color[] paperColors =
    {
        new Color(1.00f, 0.90f, 0.36f),   // yellow
        new Color(1.00f, 0.55f, 0.70f),   // pink
        new Color(0.53f, 0.80f, 0.92f),   // blue
        new Color(0.71f, 0.90f, 0.52f)    // green
    };
    [Tooltip("Ink colour the player's strokes are tinted with.")]
    public Color symbolColor = new Color(0.1f, 0.1f, 0.12f);
    [Tooltip("Alpha below this is discarded from the ink. Lower keeps more of the soft stroke edge.")]
    [Range(0.05f, 0.9f)] public float alphaCutoff = 0.35f;
    [Tooltip("Largest random tilt applied to a note stuck on the board, in degrees. Nobody sticks them on straight.")]
    [Range(0f, 25f)] public float maxNoteTilt = 8f;
    [Tooltip("Length of the pop-in animation when a note lands on the board.")]
    public float stampDuration = 0.35f;
    [Tooltip("Extra gap given to each successive note. Without this, overlapping notes are exactly coplanar and z-fight along the overlap.")]
    public float noteStackSpacing = 0.0015f;

    [Header("Rejected notes")]
    [Tooltip("Seconds a rejected note stays stuck on the board before it peels off, so the player can see what they drew.")]
    public float rejectHoldTime = 0.6f;
    [Tooltip("How hard a rejected note is pushed away from the board as it peels off.")]
    public float rejectPushSpeed = 0.45f;
    [Tooltip("Seconds before a note on the floor is cleaned up. 0 keeps them forever.")]
    public float rejectedNoteLifetime = 30f;

    [Header("Colours")]
    public Color idleColor = new Color(0.9f, 0.1f, 0.1f);
    public Color correctColor = new Color(0.2f, 0.9f, 0.3f);
    public Color wrongColor = new Color(1f, 0.6f, 0f);
    public float flashDuration = 0.6f;

    Transform _spot;
    Transform _noteAnchor;
    Renderer _spotRenderer;
    MaterialPropertyBlock _block;
    string _expectedLabel;
    float _flashTimer;
    Color _flashColor;
    Color _currentPaperColor = Color.white;
    Vector3 _currentLocalPoint;
    int _stackIndex;

    // Local-space centres of every spot already solved, so the next one lands elsewhere.
    readonly List<Vector3> _usedPositions = new List<Vector3>();
    readonly List<StickyNote> _notes = new List<StickyNote>();

    public string ExpectedLabel => _expectedLabel;
    public Vector3 SpotPosition => _spot != null ? _spot.position : transform.position;

    void Start()
    {
        if (drawingCanvas == null) drawingCanvas = FindAnyObjectByType<SymbolDrawingCanvas>();
        CreateNoteAnchor();
        CreateSpot();
        NextChallenge();
    }

    void Update()
    {
        if (_flashTimer <= 0f) return;

        _flashTimer -= Time.deltaTime;
        float t = Mathf.Clamp01(_flashTimer / flashDuration);
        SetSpotColor(Color.Lerp(idleColor, _flashColor, t));
    }

    // ------------------------------------------------------------------- spot

    /// <summary>
    /// An unscaled child of the board that everything visual hangs from.
    ///
    /// A whiteboard is a cube squashed flat, so its scale is heavily non-uniform. Parent a
    /// *rotated* child to that and the result is a sheared matrix, which Unity cannot
    /// store as position/rotation/scale - so the child renders skewed, and the moment it
    /// is unparented for physics the transform snaps to something else entirely. Giving
    /// the anchor the inverse scale cancels the board's out, leaving a clean unit-scale
    /// frame where notes keep their shape and detach without jumping.
    /// </summary>
    void CreateNoteAnchor()
    {
        GameObject anchor = new GameObject("NoteAnchor");
        _noteAnchor = anchor.transform;
        _noteAnchor.SetParent(transform, false);
        _noteAnchor.localPosition = Vector3.zero;
        _noteAnchor.localRotation = Quaternion.identity;

        Vector3 lossy = transform.lossyScale;
        _noteAnchor.localScale = new Vector3(
            1f / Mathf.Max(Mathf.Abs(lossy.x), 1e-4f),
            1f / Mathf.Max(Mathf.Abs(lossy.y), 1e-4f),
            1f / Mathf.Max(Mathf.Abs(lossy.z), 1e-4f));
    }

    void CreateSpot()
    {
        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "TargetSpot";
        // The quad is decoration; the aim ray is tested against the cube's own collider.
        Destroy(quad.GetComponent<Collider>());

        _spot = quad.transform;
        _spot.SetParent(_noteAnchor, false);

        _spotRenderer = quad.GetComponent<Renderer>();
        _block = new MaterialPropertyBlock();

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader != null) _spotRenderer.material = new Material(shader);

        SetSpotColor(idleColor);
    }

    void SetSpotColor(Color color)
    {
        if (_spotRenderer == null) return;

        _spotRenderer.GetPropertyBlock(_block);
        // URP/Unlit reads _BaseColor; _Color is kept for the built-in fallback.
        _block.SetColor("_BaseColor", color);
        _block.SetColor("_Color", color);
        _spotRenderer.SetPropertyBlock(_block);
    }

    /// <summary>Builds the face's local axes: one along the normal, two across it.</summary>
    void GetFaceAxes(out Vector3 normal, out Vector3 right, out Vector3 up)
    {
        normal = faceNormal.normalized;
        up = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.9f ? Vector3.forward : Vector3.up;
        right = Vector3.Cross(up, normal).normalized;
        up = Vector3.Cross(normal, right).normalized;
    }

    void PlaceSpot()
    {
        GetFaceAxes(out Vector3 normal, out Vector3 right, out Vector3 up);

        float spread = 0.5f - edgeMargin;
        Vector3 localPoint = Vector3.zero;

        // Reject candidates that land on top of an already-used spot. After enough tries
        // the board is simply full, so take the last candidate rather than hang.
        const int maxAttempts = 40;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            localPoint =
                normal * 0.5f +
                right * Random.Range(-spread, spread) +
                up * Random.Range(-spread, spread);

            if (IsClearOfUsedPositions(localPoint)) break;
        }

        _currentLocalPoint = localPoint;

        // Positioned in world space against the anchor's unit-scale frame, so the spot
        // stays round and the right size whatever the board is scaled to.
        Vector3 worldNormal = transform.TransformDirection(normal).normalized;
        Vector3 worldUp = transform.TransformDirection(up).normalized;

        _spot.position = transform.TransformPoint(localPoint) + worldNormal * surfaceOffset;
        _spot.rotation = Quaternion.LookRotation(-worldNormal, worldUp);
        _spot.localScale = Vector3.one * spotSize;
    }

    bool IsClearOfUsedPositions(Vector3 localPoint)
    {
        if (allowOverlap) return true;

        // A tilted square reaches its own diagonal, so two notes need at least that much
        // between centres to stay clear. Trusting the raw inspector value alone lets a
        // small separation with a large note size produce guaranteed overlaps.
        float separation = Mathf.Max(minSpotSeparation, noteSize * Mathf.Sqrt(2f) * 1.05f);
        Vector3 world = transform.TransformPoint(localPoint);

        foreach (Vector3 used in _usedPositions)
        {
            if (Vector3.Distance(world, transform.TransformPoint(used)) < separation)
                return false;
        }

        return true;
    }

    // -------------------------------------------------------------- challenge

    public void NextChallenge()
    {
        string[] pool = (challengeSymbols != null && challengeSymbols.Length > 0)
            ? challengeSymbols
            : SymbolLibrary.Labels;

        _expectedLabel = pool[Random.Range(0, pool.Length)];

        if (!SymbolLibrary.IsValidLabel(_expectedLabel))
        {
            Debug.LogWarning($"Whiteboard: '{_expectedLabel}' is not a label the model knows. " +
                             "Check the Challenge Symbols list against SymbolLibrary.Labels.", this);
        }

        PlaceSpot();
        SetSpotColor(idleColor);
    }

    /// <summary>True if a world point is close enough to the spot to count as hitting it.</summary>
    public bool IsOnSpot(Vector3 worldPoint)
    {
        if (_spot == null) return false;
        return Vector3.Distance(worldPoint, _spot.position) <= clickRadius;
    }

    /// <summary>True if this board is currently waiting to be clicked.</summary>
    public bool IsAwaitingInput => drawingCanvas != null && !drawingCanvas.IsOpen;

    /// <summary>
    /// Called by WhiteboardInteractor when the player clicks while aiming at this board.
    /// Returns true if the click landed on the spot and opened the canvas.
    /// </summary>
    public bool TryActivate(Vector3 worldHitPoint)
    {
        if (!IsAwaitingInput || !IsOnSpot(worldHitPoint)) return false;

        // Roll the colour now, before the canvas opens, so the paper the player draws on
        // is the paper that ends up on the board or on the floor.
        _currentPaperColor = PickPaperColor();

        drawingCanvas.Open(_expectedLabel, _currentPaperColor, OnDrawingComplete);
        return true;
    }

    Color PickPaperColor()
    {
        if (paperColors == null || paperColors.Length == 0) return Color.white;
        return paperColors[Random.Range(0, paperColors.Length)];
    }

    void OnDrawingComplete(SymbolRecognizer.Result result, bool correct, Texture2D ink)
    {
        _flashColor = correct ? correctColor : wrongColor;
        _flashTimer = flashDuration;
        SetSpotColor(_flashColor);

        if (ink == null) return;   // cancelled, nothing was drawn

        if (correct) StickNoteToBoard(ink);
        else         DropNote(ink);
    }

    // ----------------------------------------------------------- sticky notes

    /// <summary>
    /// Builds a note oriented flat against the board, sitting exactly where the red spot
    /// is. Both outcomes start from here, so a rejected note appears in the same place a
    /// correct one would.
    /// </summary>
    StickyNote CreateNoteAtSpot(Texture2D ink, out Vector3 position, out Quaternion rotation, out Vector3 worldNormal)
    {
        GetFaceAxes(out Vector3 normal, out _, out Vector3 up);

        worldNormal = transform.TransformDirection(normal).normalized;
        Vector3 worldUp = transform.TransformDirection(up).normalized;

        // The spot's own transform is the source of truth: it is literally the red square
        // the player clicked, offset off the board and all. Each note is then pushed one
        // step further out than the last, so overlapping notes have an unambiguous depth
        // order and read as layered paper instead of z-fighting along the seam.
        position = _spot.position + worldNormal * (_stackIndex * noteStackSpacing);
        rotation = Quaternion.LookRotation(-worldNormal, worldUp)
                   * Quaternion.Euler(0f, 0f, Random.Range(-maxNoteTilt, maxNoteTilt));

        return StickyNote.Create(ink, noteSize, _currentPaperColor, symbolColor, alphaCutoff);
    }

    /// <summary>Correct answer: the note stays up and the spot moves on.</summary>
    void StickNoteToBoard(Texture2D ink)
    {
        Vector3 usedPoint = _currentLocalPoint;

        StickyNote note = CreateNoteAtSpot(ink, out Vector3 position, out Quaternion rotation, out _);
        _notes.Add(note);

        note.AttachTo(_noteAnchor, position, rotation);
        StartCoroutine(StampIn(note.transform, stampDuration));

        _usedPositions.Add(usedPoint);
        _stackIndex++;
        NextChallenge();
    }

    /// <summary>
    /// Wrong or unclear answer: the note goes up on the board exactly like a correct one,
    /// sits there long enough to be read, then peels off and flutters to the floor.
    /// </summary>
    void DropNote(Texture2D ink)
    {
        StickyNote note = CreateNoteAtSpot(ink, out Vector3 position, out Quaternion rotation, out Vector3 worldNormal);
        StartCoroutine(PeelOffAndFall(note, position, rotation, worldNormal));
        // The spot stays put so the player can retry the same symbol.
    }

    IEnumerator PeelOffAndFall(StickyNote note, Vector3 position, Quaternion rotation, Vector3 worldNormal)
    {
        note.AttachTo(_noteAnchor, position, rotation);
        yield return StampIn(note.transform, stampDuration);

        yield return new WaitForSeconds(rejectHoldTime);
        if (note == null) yield break;

        // Push out and slightly down, as though a corner let go. Straight down would
        // scrape the board on the way past and dampen the flutter before it starts.
        Vector3 push = worldNormal * rejectPushSpeed + Vector3.down * 0.15f;
        note.Drop(note.transform.position, note.transform.rotation, push);

        if (rejectedNoteLifetime > 0f) Destroy(note.gameObject, rejectedNoteLifetime);
    }

    /// <summary>Overshoot-and-settle pop, ported from the 2D original.</summary>
    IEnumerator StampIn(Transform target, float duration)
    {
        static float BackOut(float t)
        {
            const float s = 1.70158f;
            t -= 1f;
            return t * t * ((s + 1f) * t + s) + 1f;
        }

        Vector3 finalScale = target.localScale;
        Quaternion baseRotation = target.localRotation;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            if (target == null) yield break;

            float t = elapsed / duration;
            target.localScale = finalScale * BackOut(t);
            target.localRotation = baseRotation * Quaternion.Euler(0f, 0f, Mathf.Sin(t * Mathf.PI) * (1f - t) * 6f);

            elapsed += Time.deltaTime;
            yield return null;
        }

        if (target == null) yield break;
        target.localScale = finalScale;
        target.localRotation = baseRotation;
    }

    void OnDrawGizmosSelected()
    {
        if (_spot == null) return;
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(_spot.position, clickRadius);
    }
}
