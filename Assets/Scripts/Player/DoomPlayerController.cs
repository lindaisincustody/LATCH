using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Classic-Doom-style first person controller.
///
/// Feel notes (why the numbers look the way they do):
///  - Doom's player runs at 583 map units/sec. A Doom player is 56 units tall, so scaled
///    to a 1.8m character that is roughly 18.7 m/s. The defaults here are toned down a
///    little to stay playable, but this is meant to feel *fast*.
///  - Movement uses accelerate/friction (like Doom and Quake) rather than lerping to a
///    target velocity. That is what gives the signature snappy-but-slightly-slidey response.
///  - Strafing is full speed, there is no crouch, and jumping is off by default because
///    vanilla Doom had none.
///  - Vertical look is enabled, which vanilla Doom did not have (the view was welded to
///    the horizon and the mouse only turned). It is on because aiming up and down is
///    needed for pointing at things. Untick "Allow Vertical Look" for the classic feel.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class DoomPlayerController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The camera (or an empty parent of it) sitting at eye height. Yaw goes on the body, pitch and bob go here.")]
    public Transform cameraPivot;

    [Header("Speed")]
    [Tooltip("Speed with no run key held, in metres per second.")]
    public float walkSpeed = 8f;
    [Tooltip("Speed while the run key is held. The vanilla Doom equivalent is about 18.")]
    public float runSpeed = 14f;
    [Tooltip("Doom players held the run key permanently. Tick this to skip the key.")]
    public bool alwaysRun = false;
    [Tooltip("Multiplier applied when moving backwards.")]
    [Range(0.3f, 1f)] public float backwardSpeedMultiplier = 0.8f;

    [Header("Acceleration")]
    [Tooltip("How hard the player is pushed toward the wished-for speed while grounded.")]
    public float groundAcceleration = 90f;
    [Tooltip("Deceleration applied while grounded with no input. Lower means more ice.")]
    public float groundFriction = 55f;
    [Tooltip("Control authority while airborne. Doom had almost none.")]
    public float airAcceleration = 12f;

    [Header("Gravity and Jump")]
    public float gravity = 26f;
    [Tooltip("Vanilla Doom has no jump. Boom-era source ports do.")]
    public bool allowJump = false;
    public float jumpHeight = 1.1f;

    [Header("Look")]
    [Tooltip("Degrees of rotation per unit of mouse delta.")]
    public float mouseSensitivity = 0.12f;
    [Tooltip("Degrees per second at full gamepad stick deflection.")]
    public float gamepadLookSpeed = 220f;
    [Tooltip("On is free look, needed for aiming at things. Off is classic Doom, view locked to the horizon.")]
    public bool allowVerticalLook = true;
    [Tooltip("How far up and down you can look. 89 lets you aim at your own feet, useful for drawing on floors.")]
    [Range(30f, 89f)] public float maxPitch = 89f;
    public bool invertY = false;
    [Tooltip("Doom's horizontal FOV was 90 degrees, which is about 74 vertical at 4:3. Set to 0 to leave the camera alone.")]
    public float verticalFieldOfView = 74f;

    [Header("View Bob")]
    public bool enableViewBob = true;
    [Tooltip("Height of the camera above the player's feet, before bob is added.")]
    public float eyeHeight = 1.4f;
    [Tooltip("Vertical bob amplitude in metres at full run.")]
    public float bobAmplitude = 0.07f;
    [Tooltip("Bob cycles per second at full run.")]
    public float bobFrequency = 1.9f;
    [Tooltip("Sideways sway as a fraction of the vertical bob.")]
    [Range(0f, 1f)] public float bobSwayRatio = 0.45f;

    [Header("Cursor")]
    public bool lockCursorOnStart = true;

    [Tooltip("Cleared while a menu or the drawing canvas has the mouse. Gravity keeps running so the player still settles on the ground.")]
    [HideInInspector] public bool controlEnabled = true;

    CharacterController _controller;
    Vector3 _horizontalVelocity;
    float _verticalVelocity;
    float _yaw;
    float _pitch;
    float _bobTimer;

    void Awake()
    {
        _controller = GetComponent<CharacterController>();

        if (cameraPivot == null)
        {
            Camera child = GetComponentInChildren<Camera>();
            if (child != null) cameraPivot = child.transform;
        }

        _yaw = transform.eulerAngles.y;

        if (cameraPivot != null)
        {
            _pitch = 0f;
            cameraPivot.localPosition = new Vector3(0f, eyeHeight, 0f);
            cameraPivot.localRotation = Quaternion.identity;

            if (verticalFieldOfView > 0f)
            {
                Camera cam = cameraPivot.GetComponent<Camera>();
                if (cam != null) cam.fieldOfView = verticalFieldOfView;
            }
        }
    }

    void Start()
    {
        if (lockCursorOnStart) LockCursor(true);
    }

    void Update()
    {
        float dt = Time.deltaTime;

        if (controlEnabled) HandleCursorToggle();

        // The Read* helpers return neutral input while control is off, so look and
        // movement still run: friction brings the player to a stop and gravity keeps
        // them grounded instead of freezing mid-air.
        HandleLook(dt);
        HandleMovement(dt);
        HandleViewBob(dt);
    }

    // ------------------------------------------------------------------ input

    Vector2 ReadMoveInput()
    {
        Vector2 move = Vector2.zero;
        if (!controlEnabled) return move;

        Keyboard kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) move.y += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move.y -= 1f;
            if (kb.dKey.isPressed) move.x += 1f;
            if (kb.aKey.isPressed) move.x -= 1f;
        }

        Gamepad pad = Gamepad.current;
        if (pad != null) move += pad.leftStick.ReadValue();

        return Vector2.ClampMagnitude(move, 1f);
    }

    bool ReadRunInput()
    {
        if (!controlEnabled) return false;
        if (alwaysRun) return true;

        Keyboard kb = Keyboard.current;
        if (kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed)) return true;

        Gamepad pad = Gamepad.current;
        if (pad != null && pad.leftStickButton.isPressed) return true;

        return false;
    }

    bool ReadJumpPressed()
    {
        if (!controlEnabled) return false;

        Keyboard kb = Keyboard.current;
        if (kb != null && kb.spaceKey.wasPressedThisFrame) return true;

        Gamepad pad = Gamepad.current;
        if (pad != null && pad.buttonSouth.wasPressedThisFrame) return true;

        return false;
    }

    Vector2 ReadLookDelta(float dt)
    {
        Vector2 look = Vector2.zero;
        if (!controlEnabled) return look;

        // Mouse delta is already a per-frame value, so it must NOT be scaled by deltaTime.
        Mouse mouse = Mouse.current;
        if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
            look += mouse.delta.ReadValue() * mouseSensitivity;

        // Stick input is a rate, so it does need deltaTime.
        Gamepad pad = Gamepad.current;
        if (pad != null)
            look += pad.rightStick.ReadValue() * (gamepadLookSpeed * dt);

        return look;
    }

    // ------------------------------------------------------------------- look

    void HandleLook(float dt)
    {
        Vector2 look = ReadLookDelta(dt);

        _yaw += look.x;
        transform.rotation = Quaternion.Euler(0f, _yaw, 0f);

        if (cameraPivot == null) return;

        if (allowVerticalLook)
        {
            _pitch += invertY ? look.y : -look.y;
            _pitch = Mathf.Clamp(_pitch, -maxPitch, maxPitch);
        }
        else
        {
            _pitch = 0f;
        }

        cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
    }

    // --------------------------------------------------------------- movement

    void HandleMovement(float dt)
    {
        Vector2 input = ReadMoveInput();
        bool grounded = _controller.isGrounded;

        Vector3 wishDir = transform.right * input.x + transform.forward * input.y;

        float targetSpeed = ReadRunInput() ? runSpeed : walkSpeed;
        if (input.y < 0f) targetSpeed *= backwardSpeedMultiplier;

        float wishSpeed = Mathf.Min(wishDir.magnitude, 1f) * targetSpeed;
        if (wishDir.sqrMagnitude > 0.0001f) wishDir.Normalize();

        if (grounded)
        {
            ApplyFriction(dt);
            Accelerate(wishDir, wishSpeed, groundAcceleration, dt);
        }
        else
        {
            Accelerate(wishDir, wishSpeed, airAcceleration, dt);
        }

        if (grounded)
        {
            // A small downward bias keeps the controller glued to floors, slopes and steps.
            if (_verticalVelocity < 0f) _verticalVelocity = -2f;

            if (allowJump && ReadJumpPressed())
                _verticalVelocity = Mathf.Sqrt(2f * gravity * jumpHeight);
        }
        else
        {
            _verticalVelocity -= gravity * dt;
        }

        Vector3 motion = _horizontalVelocity + Vector3.up * _verticalVelocity;
        _controller.Move(motion * dt);

        // Head hit a ceiling: kill the upward velocity so we do not hang there.
        if ((_controller.collisionFlags & CollisionFlags.Above) != 0 && _verticalVelocity > 0f)
            _verticalVelocity = 0f;
    }

    void ApplyFriction(float dt)
    {
        float speed = _horizontalVelocity.magnitude;
        if (speed < 0.01f)
        {
            _horizontalVelocity = Vector3.zero;
            return;
        }

        float newSpeed = Mathf.Max(speed - groundFriction * dt, 0f);
        _horizontalVelocity *= newSpeed / speed;
    }

    void Accelerate(Vector3 wishDir, float wishSpeed, float acceleration, float dt)
    {
        if (wishSpeed <= 0f) return;

        float speedAlongWish = Vector3.Dot(_horizontalVelocity, wishDir);
        float addSpeed = wishSpeed - speedAlongWish;
        if (addSpeed <= 0f) return;

        float accelSpeed = Mathf.Min(acceleration * wishSpeed * dt, addSpeed);
        _horizontalVelocity += wishDir * accelSpeed;
    }

    // -------------------------------------------------------------------- bob

    void HandleViewBob(float dt)
    {
        if (cameraPivot == null) return;

        Vector3 basePosition = new Vector3(0f, eyeHeight, 0f);

        if (!enableViewBob)
        {
            cameraPivot.localPosition = basePosition;
            return;
        }

        float speedFactor = _controller.isGrounded
            ? Mathf.Clamp01(_horizontalVelocity.magnitude / Mathf.Max(runSpeed, 0.01f))
            : 0f;

        if (speedFactor > 0.01f)
        {
            _bobTimer += dt * bobFrequency * Mathf.PI * 2f * speedFactor;
        }
        else
        {
            // Ease back to neutral instead of snapping when the player stops.
            _bobTimer = Mathf.MoveTowards(_bobTimer % (Mathf.PI * 2f), 0f, dt * 12f);
        }

        float vertical = Mathf.Abs(Mathf.Sin(_bobTimer)) * bobAmplitude * speedFactor;
        float sway = Mathf.Cos(_bobTimer * 0.5f) * bobAmplitude * bobSwayRatio * speedFactor;

        cameraPivot.localPosition = basePosition + new Vector3(sway, vertical, 0f);
    }

    // ----------------------------------------------------------------- cursor

    void HandleCursorToggle()
    {
        Keyboard kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) LockCursor(false);

        Mouse mouse = Mouse.current;
        if (mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
            LockCursor(true);
    }

    static void LockCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    void OnDisable()
    {
        LockCursor(false);
    }

    /// <summary>Current horizontal speed in m/s. Handy for weapon bob or a HUD.</summary>
    public float CurrentSpeed => _horizontalVelocity.magnitude;

    public bool IsGrounded => _controller != null && _controller.isGrounded;
}
