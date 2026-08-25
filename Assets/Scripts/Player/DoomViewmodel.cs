using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Doom-style first person viewmodel: a flat sprite drawn over the screen that bobs
/// with movement. Doom never rendered the weapon in 3D, it blitted a sprite at a
/// screen offset, which is why it never clips into walls and never needs an extra camera.
///
/// The bob curve is the one from the original source:
///     psp->sx = FRACUNIT + FixedMul(player->bob, finecosine[angle]);
///     angle &= FINEANGLES/2-1;
///     psp->sy = WEAPONTOP + FixedMul(player->bob, finesine[angle]);
/// Horizontal uses cosine over the full circle, vertical uses sine over only half of it,
/// so the sprite always dips *downward* and does it twice per horizontal sweep. That
/// asymmetry is what makes it read as Doom rather than a generic figure-eight bob.
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class DoomViewmodel : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Player controller to read speed and grounded state from. Found automatically in the parents if left empty.")]
    public DoomPlayerController player;

    [Header("Bob")]
    public bool enableBob = true;
    [Tooltip("Bob distance in reference pixels at full run.")]
    public float bobAmount = 26f;
    [Tooltip("Bob cycles per second at full run. Keep this matched to the camera's Bob Frequency.")]
    public float bobFrequency = 1.9f;
    [Tooltip("Extra vertical bob as a fraction of the horizontal. Doom used the same value for both.")]
    [Range(0.5f, 2f)] public float verticalBobRatio = 1f;

    [Header("Idle Sway")]
    [Tooltip("Slow drift while standing still, so the arm never looks frozen.")]
    public bool enableIdleSway = true;
    public float idleSwayAmount = 5f;
    public float idleSwayFrequency = 0.35f;

    [Header("Look Sway")]
    [Tooltip("The arm lags behind fast turns. Not in vanilla Doom, but it sells the weight. Set to 0 to disable.")]
    public float lookSwayAmount = 12f;
    [Tooltip("Largest offset look sway may reach, in reference pixels.")]
    public float lookSwayClamp = 55f;
    [Tooltip("How quickly the arm catches back up after a turn.")]
    public float lookSwayRecovery = 8f;

    [Header("Landing")]
    [Tooltip("How far the arm punches down on landing, in reference pixels.")]
    public float landDip = 40f;
    public float landRecovery = 6f;

    RectTransform _rect;
    Vector2 _restPosition;
    Vector2 _lookSway;
    float _bobTimer;
    float _idleTimer;
    float _landOffset;
    bool _wasGrounded = true;

    void Awake()
    {
        _rect = GetComponent<RectTransform>();
        _restPosition = _rect.anchoredPosition;

        if (player == null) player = GetComponentInParent<DoomPlayerController>();
        if (player == null) player = FindAnyObjectByType<DoomPlayerController>();
    }

    void LateUpdate()
    {
        float dt = Time.deltaTime;

        Vector2 offset = Vector2.zero;
        offset += CalculateBob(dt);
        offset += CalculateIdleSway(dt);
        offset += CalculateLookSway(dt);
        offset.y -= CalculateLandDip(dt);

        _rect.anchoredPosition = _restPosition + offset;
    }

    Vector2 CalculateBob(float dt)
    {
        if (!enableBob || player == null) return Vector2.zero;

        float speedFactor = player.IsGrounded
            ? Mathf.Clamp01(player.CurrentSpeed / Mathf.Max(player.runSpeed, 0.01f))
            : 0f;

        if (speedFactor > 0.01f)
        {
            _bobTimer += dt * bobFrequency * Mathf.PI * 2f * speedFactor;
        }
        else
        {
            // Unwind to a neutral pose rather than freezing mid-swing.
            _bobTimer = Mathf.MoveTowards(_bobTimer % (Mathf.PI * 2f), 0f, dt * 10f);
            speedFactor = Mathf.Max(speedFactor, 0f);
        }

        float scale = bobAmount * speedFactor;

        // Full circle horizontally, half circle vertically: the Doom asymmetry.
        float x = Mathf.Cos(_bobTimer) * scale;
        float y = -Mathf.Abs(Mathf.Sin(_bobTimer)) * scale * verticalBobRatio;

        return new Vector2(x, y);
    }

    Vector2 CalculateIdleSway(float dt)
    {
        if (!enableIdleSway) return Vector2.zero;

        // Only breathe when essentially stationary, so it does not fight the bob.
        float stillness = player != null
            ? 1f - Mathf.Clamp01(player.CurrentSpeed / 1.5f)
            : 1f;

        if (stillness <= 0.01f) return Vector2.zero;

        _idleTimer += dt * idleSwayFrequency * Mathf.PI * 2f;

        float x = Mathf.Sin(_idleTimer) * idleSwayAmount * stillness;
        float y = Mathf.Sin(_idleTimer * 2f) * idleSwayAmount * 0.5f * stillness;

        return new Vector2(x, y);
    }

    Vector2 CalculateLookSway(float dt)
    {
        if (lookSwayAmount <= 0f) return Vector2.zero;

        Vector2 lookDelta = Vector2.zero;

        Mouse mouse = Mouse.current;
        if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
            lookDelta += mouse.delta.ReadValue();

        Gamepad pad = Gamepad.current;
        if (pad != null) lookDelta += pad.rightStick.ReadValue() * (60f * dt);

        // Push the arm opposite to the turn, then ease it back to centre.
        _lookSway -= lookDelta * lookSwayAmount * 0.05f;
        _lookSway = Vector2.ClampMagnitude(_lookSway, lookSwayClamp);
        _lookSway = Vector2.Lerp(_lookSway, Vector2.zero, 1f - Mathf.Exp(-lookSwayRecovery * dt));

        return _lookSway;
    }

    float CalculateLandDip(float dt)
    {
        if (player != null)
        {
            bool grounded = player.IsGrounded;
            if (grounded && !_wasGrounded) _landOffset = landDip;
            _wasGrounded = grounded;
        }

        _landOffset = Mathf.Lerp(_landOffset, 0f, 1f - Mathf.Exp(-landRecovery * dt));
        return _landOffset;
    }
}
