using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player-side aiming and clicking. Casts one ray a frame from the camera: the result
/// both drives the crosshair's target-acquired state and decides what a click hits.
///
/// The ray comes from the camera, not the player body, because pitch lives on the camera
/// transform. Using the body's forward would give a ray that is always horizontal.
/// </summary>
public class WhiteboardInteractor : MonoBehaviour
{
    [Tooltip("The player camera. Found automatically in children if left empty.")]
    public Transform aimSource;
    [Tooltip("How far the player can reach a whiteboard, in metres.")]
    public float range = 6f;
    public LayerMask hitMask = ~0;

    [Tooltip("Reticle to drive. Found automatically if left empty.")]
    public HudCrosshair crosshair;

    [Header("Debug")]
    [Tooltip("Draws the aim ray in the Scene view.")]
    public bool showAimRay = false;

    DoomPlayerController _player;

    void Awake()
    {
        _player = GetComponent<DoomPlayerController>();
        if (crosshair == null) crosshair = FindAnyObjectByType<HudCrosshair>();

        if (aimSource == null)
        {
            Camera cam = GetComponentInChildren<Camera>();
            if (cam != null) aimSource = cam.transform;
        }
    }

    void Update()
    {
        if (aimSource == null) return;

        // Ignore aiming while a canvas has the mouse, otherwise the click that submits a
        // drawing would immediately re-open the board behind it.
        bool active = (_player == null || _player.controlEnabled)
                      && Cursor.lockState == CursorLockMode.Locked;

        if (!active)
        {
            crosshair?.SetTargetAcquired(false);
            return;
        }

        if (showAimRay)
            Debug.DrawRay(aimSource.position, aimSource.forward * range, Color.cyan);

        Whiteboard board = null;
        Vector3 hitPoint = Vector3.zero;

        if (Physics.Raycast(aimSource.position, aimSource.forward, out RaycastHit hit, range, hitMask))
        {
            Whiteboard candidate = hit.collider.GetComponentInParent<Whiteboard>();
            if (candidate != null && candidate.IsAwaitingInput && candidate.IsOnSpot(hit.point))
            {
                board = candidate;
                hitPoint = hit.point;
            }
        }

        crosshair?.SetTargetAcquired(board != null);

        Mouse mouse = Mouse.current;
        if (board != null && mouse != null && mouse.leftButton.wasPressedThisFrame)
            board.TryActivate(hitPoint);
    }
}
