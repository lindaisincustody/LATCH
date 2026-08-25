using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Builds the first person viewmodel: an overlay Canvas parented to the player with the
/// arm sprite anchored in the bottom right corner, plus the DoomViewmodel bob component.
///
/// It also repairs the texture import settings. A 3D URP project imports PNGs as plain
/// textures, and a UI Image needs a Sprite, so this flips the importer over and turns on
/// alphaIsTransparency (without it the transparent edges of the arm fringe to black).
/// </summary>
public static class DoomViewmodelSetup
{
    const string ViewmodelFolder = "Assets/Art/Viewmodel";
    const float ReferenceHeight = 1080f;

    [MenuItem("Tools/Doom/Set Up Viewmodel Arm", false, 1)]
    static void SetUpViewmodel()
    {
        Texture2D texture = FindArmTexture();
        if (texture == null)
        {
            EditorUtility.DisplayDialog(
                "No arm image found",
                "Put the arm PNG in " + ViewmodelFolder + " (or select it in the Project window), then run this again.",
                "OK");
            return;
        }

        DoomPlayerController player = Object.FindAnyObjectByType<DoomPlayerController>();
        if (player == null)
        {
            EditorUtility.DisplayDialog(
                "No player in the scene",
                "Run Tools > Doom > Set Up Player Rig first.",
                "OK");
            return;
        }

        Sprite sprite = ImportAsSprite(texture);
        if (sprite == null)
        {
            EditorUtility.DisplayDialog(
                "Import failed",
                "Could not build a Sprite from " + texture.name + ". Set its Texture Type to 'Sprite (2D and UI)' manually.",
                "OK");
            return;
        }

        Undo.SetCurrentGroupName("Set Up Doom Viewmodel");
        int undoGroup = Undo.GetCurrentGroup();

        Canvas canvas = CreateCanvas(player.transform);
        Image arm = CreateArmImage(canvas.transform, sprite, player);

        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = arm.gameObject;

        Debug.Log("Viewmodel ready. Adjust its position with the RectTransform, then tune bob on the DoomViewmodel component.", arm);
    }

    static Texture2D FindArmTexture()
    {
        // A texture picked in the Project window always wins.
        if (Selection.activeObject is Texture2D selected) return selected;

        if (!AssetDatabase.IsValidFolder(ViewmodelFolder)) return null;

        string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { ViewmodelFolder });
        if (guids.Length == 0) return null;

        string path = AssetDatabase.GUIDToAssetPath(guids[0]);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static Sprite ImportAsSprite(Texture2D texture)
    {
        string path = AssetDatabase.GetAssetPath(texture);
        TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;

        if (importer != null)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 2048;
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Sprite>(path);
    }

    static Canvas CreateCanvas(Transform player)
    {
        // Reuse an existing viewmodel canvas so running this twice does not stack copies.
        foreach (Canvas existing in player.GetComponentsInChildren<Canvas>(true))
        {
            if (existing.name == "ViewmodelCanvas")
            {
                foreach (Transform child in existing.transform)
                    Undo.DestroyObjectImmediate(child.gameObject);
                return existing;
            }
        }

        GameObject canvasObject = new GameObject("ViewmodelCanvas");
        Undo.RegisterCreatedObjectUndo(canvasObject, "Create viewmodel canvas");
        canvasObject.transform.SetParent(player, false);

        Canvas canvas = canvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;

        CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, ReferenceHeight);
        // Match height so the arm keeps a constant size relative to screen height,
        // instead of shrinking on wide monitors.
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 1f;

        return canvas;
    }

    static Image CreateArmImage(Transform canvas, Sprite sprite, DoomPlayerController player)
    {
        GameObject armObject = new GameObject("ViewmodelArm");
        Undo.RegisterCreatedObjectUndo(armObject, "Create viewmodel arm");
        armObject.transform.SetParent(canvas, false);

        Image image = armObject.AddComponent<Image>();
        image.sprite = sprite;
        image.preserveAspect = true;
        // Without this the arm would swallow clicks meant for the rest of the UI.
        image.raycastTarget = false;

        // Anchor and pivot in the bottom right: the forearm runs off that corner, so the
        // sprite hangs from it and the wrist stays put no matter the aspect ratio.
        RectTransform rect = armObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(1f, 0f);

        float height = ReferenceHeight * 0.9f;
        float aspect = sprite.rect.width / sprite.rect.height;
        rect.sizeDelta = new Vector2(height * aspect, height);

        // Nudge it past the corner so the cut end of the forearm sits off screen.
        rect.anchoredPosition = new Vector2(60f, -80f);

        DoomViewmodel viewmodel = armObject.AddComponent<DoomViewmodel>();
        viewmodel.player = player;
        viewmodel.bobFrequency = player.bobFrequency;

        return image;
    }
}
