using UnityEditor;
using UnityEngine;

/// <summary>
/// One-click wiring for the Doom player rig. Select the "Player" object in the Hierarchy
/// and run Tools > Doom > Set Up Player Rig.
///
/// It adds a CharacterController, sizes the child Capsule to match it, strips the
/// Capsule's own collider (it would fight the controller), parents the camera at eye
/// height and hooks everything into DoomPlayerController.
/// </summary>
public static class DoomPlayerSetup
{
    const float PlayerHeight = 1.8f;
    const float PlayerRadius = 0.35f;
    const float EyeHeight = 1.4f;

    [MenuItem("Tools/Doom/Set Up Player Rig", false, 0)]
    static void SetUpPlayerRig()
    {
        GameObject player = Selection.activeGameObject;
        if (player == null)
        {
            EditorUtility.DisplayDialog(
                "No selection",
                "Select the Player GameObject in the Hierarchy first, then run this again.",
                "OK");
            return;
        }

        Undo.SetCurrentGroupName("Set Up Doom Player Rig");
        int undoGroup = Undo.GetCurrentGroup();

        ConfigureCharacterController(player);
        ConfigureBodyMesh(player);
        Transform cam = ConfigureCamera(player);
        ConfigureController(player, cam);

        Undo.CollapseUndoOperations(undoGroup);
        EditorUtility.SetDirty(player);
        Selection.activeGameObject = player;

        Debug.Log("Doom player rig ready on '" + player.name + "'. Press Play: WASD to move, Shift to run, mouse to turn, Esc to free the cursor.", player);
    }

    static void ConfigureCharacterController(GameObject player)
    {
        CharacterController cc = player.GetComponent<CharacterController>();
        if (cc == null) cc = Undo.AddComponent<CharacterController>(player);
        else Undo.RecordObject(cc, "Configure CharacterController");

        cc.height = PlayerHeight;
        cc.radius = PlayerRadius;
        cc.center = new Vector3(0f, PlayerHeight * 0.5f, 0f);
        cc.slopeLimit = 45f;
        cc.stepOffset = 0.4f;
        cc.skinWidth = 0.02f;
        cc.minMoveDistance = 0f;
    }

    /// <summary>
    /// Resizes the visual capsule to the controller's dimensions and removes its collider.
    /// A default Unity capsule is 2 units tall with a 0.5 radius, hence the scale maths.
    ///
    /// The renderer is switched to ShadowsOnly because the camera lives at eye height,
    /// which is *inside* this mesh. Left visible, the near clip plane sweeps through the
    /// capsule wall as the player bobs and turns, and slivers of it flash up on screen.
    /// ShadowsOnly stops it being drawn while still casting a proper shadow into the world.
    /// </summary>
    static void ConfigureBodyMesh(GameObject player)
    {
        foreach (MeshRenderer renderer in player.GetComponentsInChildren<MeshRenderer>(true))
        {
            Transform body = renderer.transform;
            if (body == player.transform) continue;

            Undo.RecordObject(body, "Fit body mesh");
            body.localPosition = new Vector3(0f, PlayerHeight * 0.5f, 0f);
            body.localRotation = Quaternion.identity;
            body.localScale = new Vector3(PlayerRadius * 2f, PlayerHeight * 0.5f, PlayerRadius * 2f);

            Undo.RecordObject(renderer, "Hide body from first person camera");
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;

            Collider stray = body.GetComponent<Collider>();
            if (stray != null) Undo.DestroyObjectImmediate(stray);
        }
    }

    static Transform ConfigureCamera(GameObject player)
    {
        Camera cam = player.GetComponentInChildren<Camera>(true);

        if (cam == null)
        {
            // Reuse the scene's main camera if there is one, otherwise make a fresh one.
            cam = Camera.main;
            if (cam != null)
            {
                Undo.SetTransformParent(cam.transform, player.transform, "Parent camera to player");
            }
            else
            {
                GameObject camObject = new GameObject("PlayerCamera");
                Undo.RegisterCreatedObjectUndo(camObject, "Create player camera");
                camObject.transform.SetParent(player.transform, false);
                cam = camObject.AddComponent<Camera>();
                camObject.AddComponent<AudioListener>();
            }
        }

        Undo.RecordObject(cam.transform, "Place camera at eye height");
        cam.transform.localPosition = new Vector3(0f, EyeHeight, 0f);
        cam.transform.localRotation = Quaternion.identity;
        cam.transform.localScale = Vector3.one;

        Undo.RecordObject(cam, "Configure camera");
        cam.tag = "MainCamera";
        cam.fieldOfView = 74f;   // 90 degrees horizontal at 4:3, same as Doom
        cam.nearClipPlane = 0.05f;

        if (cam.GetComponent<AudioListener>() == null)
            Undo.AddComponent<AudioListener>(cam.gameObject);

        return cam.transform;
    }

    static void ConfigureController(GameObject player, Transform cameraPivot)
    {
        DoomPlayerController controller = player.GetComponent<DoomPlayerController>();
        if (controller == null) controller = Undo.AddComponent<DoomPlayerController>(player);
        else Undo.RecordObject(controller, "Configure DoomPlayerController");

        controller.cameraPivot = cameraPivot;
        controller.eyeHeight = EyeHeight;
    }
}
