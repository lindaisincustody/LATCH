using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// A physical sticky note carrying the player's drawing. Either stuck flat to the
/// whiteboard, or dropped to flutter down to the floor.
///
/// The note's visible face points along its own -Z, matching Unity's Quad primitive, so
/// its children need no extra rotation and a board-facing note uses the same
/// LookRotation(-normal) convention as everything else on the whiteboard.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(BoxCollider))]
public class StickyNote : MonoBehaviour
{
    [Header("Paper physics")]
    [Tooltip("Real sticky notes are about a gram, but masses that small make the solver jittery. 20g behaves.")]
    public float mass = 0.02f;
    public float linearDamping = 1.2f;
    public float angularDamping = 2.5f;

    [Tooltip("Extra drag applied along the note's own face normal. This is what makes paper flutter instead of dropping like a card.")]
    public float faceDrag = 9f;
    [Tooltip("Sideways swaying force while airborne, in m/s^2.")]
    public float flutterStrength = 1.1f;
    [Tooltip("Sways per second. A small stiff note wobbles faster and shallower than a full sheet.")]
    public float flutterFrequency = 5f;
    [Tooltip("Tumbling torque paired with the sway.")]
    public float flutterTorque = 0.25f;
    [Tooltip("Centre of mass shifted toward the glued edge, as a fraction of note size. This is what makes it lead with that edge and see-saw on the way down.")]
    [Range(0f, 0.4f)] public float adhesiveEdgeBias = 0.16f;
    [Tooltip("Torque that flattens the note against whatever it lands on.")]
    public float settleTorque = 6f;
    [Tooltip("Below this speed the note is treated as having come to rest.")]
    public float restSpeed = 0.08f;

    Rigidbody _body;
    BoxCollider _collider;
    Material[] _materials;
    Texture2D _ink;
    float _phase;
    bool _falling;
    float _restTimer;
    float _size;

    /// <summary>The direction the drawing faces.</summary>
    public Vector3 FaceNormal => -transform.forward;

    void Awake()
    {
        _body = GetComponent<Rigidbody>();
        _collider = GetComponent<BoxCollider>();
        _phase = Random.Range(0f, Mathf.PI * 2f);
    }

    // --------------------------------------------------------------- creation

    /// <summary>
    /// Builds a note: a coloured paper quad with the drawing laid on top of it.
    /// Ownership of <paramref name="ink"/> passes to the note.
    /// </summary>
    public static StickyNote Create(Texture2D ink, float size, Color paperColor, Color inkColor, float alphaCutoff)
    {
        GameObject root = new GameObject("StickyNote");
        StickyNote note = root.AddComponent<StickyNote>();

        BoxCollider box = root.GetComponent<BoxCollider>();
        box.size = new Vector3(size, size, 0.006f);

        note._ink = ink;
        note._size = size;
        note._materials = new Material[2];

        // Paper: lit, so a note on the board picks up room lighting like everything else.
        GameObject paper = CreateQuad(root.transform, "Paper", size, 0f);
        note._materials[0] = MakeMaterial("Universal Render Pipeline/Lit", null, paperColor, 0f);
        paper.GetComponent<Renderer>().material = note._materials[0];

        // Ink: alpha-clipped so it stays in the opaque queue and cannot be sorted behind
        // the paper it is sitting a fraction of a millimetre in front of.
        GameObject drawing = CreateQuad(root.transform, "Ink", size * 0.8f, -0.0015f);
        note._materials[1] = MakeMaterial("Universal Render Pipeline/Unlit", ink, inkColor, alphaCutoff);
        drawing.GetComponent<Renderer>().material = note._materials[1];

        return note;
    }

    static Mesh _quadMesh;

    /// <summary>
    /// Unity's built-in quad mesh, borrowed once from a throwaway primitive.
    ///
    /// CreatePrimitive attaches a MeshCollider, and Destroy only takes effect at the end
    /// of the frame. Parenting two of those under a Rigidbody - even for a single frame -
    /// gives it non-convex MeshColliders in its compound, which PhysX rejects outright.
    /// Building the quads from a bare mesh means no collider ever exists to clean up.
    /// </summary>
    static Mesh QuadMesh
    {
        get
        {
            if (_quadMesh == null)
            {
                GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _quadMesh = temp.GetComponent<MeshFilter>().sharedMesh;
                Destroy(temp);
            }
            return _quadMesh;
        }
    }

    static GameObject CreateQuad(Transform parent, string name, float size, float zOffset)
    {
        GameObject quad = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
        quad.GetComponent<MeshFilter>().sharedMesh = QuadMesh;

        quad.transform.SetParent(parent, false);
        quad.transform.localPosition = new Vector3(0f, 0f, zOffset);
        quad.transform.localScale = Vector3.one * size;

        return quad;
    }

    static Material MakeMaterial(string shaderName, Texture texture, Color color, float alphaCutoff)
    {
        Shader shader = Shader.Find(shaderName);
        if (shader == null) shader = Shader.Find("Sprites/Default");

        Material material = new Material(shader);
        if (texture != null) material.mainTexture = texture;

        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);

        if (alphaCutoff > 0f && material.HasProperty("_AlphaClip"))
        {
            material.SetFloat("_AlphaClip", 1f);
            material.SetFloat("_Cutoff", alphaCutoff);
            material.EnableKeyword("_ALPHATEST_ON");
            material.SetOverrideTag("RenderType", "TransparentCutout");
            material.renderQueue = (int)RenderQueue.AlphaTest;
        }

        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.05f);

        return material;
    }

    // ---------------------------------------------------------------- placing

    /// <summary>Sticks the note flat against a surface, frozen and parented to it.</summary>
    public void AttachTo(Transform surface, Vector3 worldPosition, Quaternion worldRotation)
    {
        _falling = false;

        _body.isKinematic = true;
        _body.detectCollisions = false;
        _collider.enabled = false;

        transform.SetPositionAndRotation(worldPosition, worldRotation);
        transform.SetParent(surface, worldPositionStays: true);
    }

    /// <summary>Lets the note fall, fluttering as it goes.</summary>
    public void Drop(Vector3 worldPosition, Quaternion worldRotation, Vector3 initialVelocity)
    {
        if (_body == null)
        {
            Debug.LogError("StickyNote.Drop: no Rigidbody.", this);
            return;
        }

        _falling = true;

        transform.SetParent(null);
        transform.SetPositionAndRotation(worldPosition, worldRotation);

        _body.isKinematic = false;
        _body.useGravity = true;
        // AttachTo switches these off. A note that was stuck to the board first would
        // otherwise fall straight through the floor, ignoring every collider it met.
        _body.detectCollisions = true;
        _body.mass = mass;
        _body.linearDamping = linearDamping;
        _body.angularDamping = angularDamping;
        _body.interpolation = RigidbodyInterpolation.Interpolate;
        // The note is only millimetres thick, so discrete collision lets it tunnel
        // straight through the floor at speed.
        _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        // A sticky note has its adhesive strip along one edge, so it is not balanced.
        // Biasing the centre of mass toward that edge makes it lead with the glued side
        // and see-saw as it comes down, which is the thing that reads as "sticky note"
        // rather than "square of paper".
        _body.centerOfMass = new Vector3(0f, _size * adhesiveEdgeBias, 0f);

        _body.linearVelocity = initialVelocity;
        _body.angularVelocity = Random.insideUnitSphere * 3f;
        _restTimer = 0f;

        PhysicsMaterial paper = new PhysicsMaterial("Paper")
        {
            dynamicFriction = 0.8f,
            staticFriction = 0.9f,
            bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Average,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };
        _collider.material = paper;
        _collider.enabled = true;
    }

    // ---------------------------------------------------------------- physics

    void FixedUpdate()
    {
        if (!_falling || _body.isKinematic) return;

        Vector3 velocity = _body.linearVelocity;
        Vector3 normal = FaceNormal;

        // Anisotropic drag is the whole trick. A sheet of paper barely resists moving
        // edge-on but fights hard when pushed face-on, so whichever way it tips it gets
        // shoved sideways and tips the other way. That feedback loop is the flutter.
        // Uniform drag alone just makes it fall slowly, like a leaf-shaped stone.
        float faceSpeed = Vector3.Dot(velocity, normal);
        _body.AddForce(-normal * (faceSpeed * faceDrag), ForceMode.Acceleration);

        float t = Time.time * flutterFrequency + _phase;
        _body.AddForce(transform.right * (Mathf.Sin(t) * flutterStrength), ForceMode.Acceleration);
        _body.AddTorque(transform.up * (Mathf.Cos(t) * flutterTorque), ForceMode.Acceleration);

        // Stop driving it once it has actually come to rest. Stopping on first contact
        // instead would kill the flutter the moment it clipped the board on its way out,
        // and the rest of the fall would be a dead drop.
        bool slow = _body.linearVelocity.magnitude < restSpeed
                    && _body.angularVelocity.magnitude < restSpeed * 4f;

        _restTimer = slow ? _restTimer + Time.fixedDeltaTime : 0f;
        if (_restTimer > 0.4f) _falling = false;
    }

    void OnCollisionStay(Collision collision)
    {
        if (!_falling || _body.isKinematic || collision.contactCount == 0) return;

        // Paper lands flat. Torque the face toward the surface it is resting against so
        // it does not end up standing on an edge.
        Vector3 surfaceNormal = collision.GetContact(0).normal;
        Vector3 face = FaceNormal;
        Vector3 target = Vector3.Dot(face, surfaceNormal) >= 0f ? surfaceNormal : -surfaceNormal;

        _body.AddTorque(Vector3.Cross(face, target) * settleTorque, ForceMode.Acceleration);
    }

    void OnDestroy()
    {
        if (_ink != null) Destroy(_ink);
        if (_materials == null) return;

        foreach (Material material in _materials)
            if (material != null) Destroy(material);
    }
}
