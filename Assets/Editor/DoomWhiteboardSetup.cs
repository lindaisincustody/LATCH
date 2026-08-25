using TMPro;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Builds the drawing UI and wires the recognizer, so none of it has to be assembled by
/// hand. Select the cube you want to use as a whiteboard and run
/// Tools > Doom > Set Up Whiteboard.
/// </summary>
public static class DoomWhiteboardSetup
{
    [MenuItem("Tools/Doom/Set Up Whiteboard", false, 20)]
    static void SetUpWhiteboard()
    {
        GameObject cube = Selection.activeGameObject;
        if (cube == null || cube.GetComponent<Renderer>() == null)
        {
            EditorUtility.DisplayDialog(
                "Select the whiteboard cube",
                "Select the cube you want to draw on in the Hierarchy, then run this again.",
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

        Undo.SetCurrentGroupName("Set Up Whiteboard");
        int group = Undo.GetCurrentGroup();

        SymbolRecognizer recognizer = CreateRecognizer();
        SymbolDrawingCanvas canvas = CreateDrawingUI(player);
        HudCrosshair crosshair = CreateCrosshair(player);
        ConfigureCube(cube, canvas);
        ConfigureInteractor(player, crosshair);

        Undo.CollapseUndoOperations(group);

        if (recognizer.modelAsset == null)
        {
            Debug.LogWarning("Whiteboard ready, but no ONNX model was found. Assign one to the " +
                             "SymbolRecognizer's Model Asset field.", recognizer);
        }
        else
        {
            Debug.Log("Whiteboard ready. Walk up to the cube and click the red spot.", cube);
        }
    }

    static SymbolRecognizer CreateRecognizer()
    {
        SymbolRecognizer recognizer = Object.FindAnyObjectByType<SymbolRecognizer>();
        if (recognizer == null)
        {
            GameObject go = new GameObject("SymbolRecognizer");
            Undo.RegisterCreatedObjectUndo(go, "Create recognizer");
            recognizer = go.AddComponent<SymbolRecognizer>();
        }

        if (recognizer.modelAsset == null)
        {
            string[] guids = AssetDatabase.FindAssets("t:" + nameof(ModelAsset));
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                Undo.RecordObject(recognizer, "Assign model");
                recognizer.modelAsset = AssetDatabase.LoadAssetAtPath<ModelAsset>(path);
            }
        }

        return recognizer;
    }

    static SymbolDrawingCanvas CreateDrawingUI(DoomPlayerController player)
    {
        SymbolDrawingCanvas existing = Object.FindAnyObjectByType<SymbolDrawingCanvas>();
        if (existing != null) return existing;

        GameObject root = new GameObject("DrawingUI");
        Undo.RegisterCreatedObjectUndo(root, "Create drawing UI");

        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Above the viewmodel arm, which sits at 10.
        canvas.sortingOrder = 30;

        CanvasScaler scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        // The component lives on the always-active root, not on the panel it toggles,
        // so its Awake always runs even though the panel starts hidden.
        SymbolDrawingCanvas drawing = root.AddComponent<SymbolDrawingCanvas>();
        drawing.player = player;

        GameObject panel = CreateChild(root.transform, "Panel");
        Image background = panel.AddComponent<Image>();
        background.color = new Color(0f, 0f, 0f, 0.85f);
        Stretch(panel.GetComponent<RectTransform>());

        // The paper is the drawable rect; the ink RawImage stretches over it so both
        // share exactly one coordinate space and strokes land where the cursor is.
        GameObject area = CreateChild(panel.transform, "StickyNote");
        RectTransform areaRect = area.GetComponent<RectTransform>();
        areaRect.anchorMin = areaRect.anchorMax = new Vector2(0.5f, 0.5f);
        areaRect.pivot = new Vector2(0.5f, 0.5f);
        areaRect.sizeDelta = new Vector2(640f, 640f);
        areaRect.anchoredPosition = Vector2.zero;

        Image paper = area.AddComponent<Image>();
        paper.color = new Color(1f, 0.92f, 0.35f);

        GameObject inkObject = CreateChild(area.transform, "Ink");
        Stretch(inkObject.GetComponent<RectTransform>());
        RawImage display = inkObject.AddComponent<RawImage>();
        display.raycastTarget = false;

        TextMeshProUGUI target = CreateText(panel.transform, "TargetText", 140f, 300f, 0.82f);
        TextMeshProUGUI status = CreateText(panel.transform, "StatusText", 28f, 60f, 0.12f);

        drawing.panelRoot = panel;
        drawing.drawArea = areaRect;
        drawing.display = display;
        drawing.paperBackground = paper;
        drawing.targetText = target;
        drawing.statusText = status;

        return drawing;
    }

    static GameObject CreateChild(Transform parent, string name)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    static TextMeshProUGUI CreateText(Transform parent, string name, float fontSize, float height, float anchorY)
    {
        GameObject go = CreateChild(parent, name);

        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, anchorY);
        rect.anchorMax = new Vector2(0.5f, anchorY);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(1400f, height);
        rect.anchoredPosition = Vector2.zero;

        TextMeshProUGUI text = go.AddComponent<TextMeshProUGUI>();
        text.fontSize = fontSize;
        text.alignment = TextAlignmentOptions.Center;
        text.color = Color.white;
        text.raycastTarget = false;
        text.text = string.Empty;

        return text;
    }

    static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    static void ConfigureCube(GameObject cube, SymbolDrawingCanvas canvas)
    {
        if (cube.GetComponent<Collider>() == null)
            Undo.AddComponent<BoxCollider>(cube);

        Whiteboard board = cube.GetComponent<Whiteboard>();
        if (board == null) board = Undo.AddComponent<Whiteboard>(cube);
        else Undo.RecordObject(board, "Configure whiteboard");

        board.drawingCanvas = canvas;
    }

    static HudCrosshair CreateCrosshair(DoomPlayerController player)
    {
        HudCrosshair existing = Object.FindAnyObjectByType<HudCrosshair>();
        if (existing != null) return existing;

        GameObject root = new GameObject("HudCanvas");
        Undo.RegisterCreatedObjectUndo(root, "Create HUD canvas");
        root.transform.SetParent(player.transform, false);

        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Above the viewmodel arm (10), below the drawing panel (30).
        canvas.sortingOrder = 20;

        CanvasScaler scaler = root.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        GameObject reticle = new GameObject("Crosshair", typeof(RectTransform));
        reticle.transform.SetParent(root.transform, false);

        return reticle.AddComponent<HudCrosshair>();
    }

    static void ConfigureInteractor(DoomPlayerController player, HudCrosshair crosshair)
    {
        WhiteboardInteractor interactor = player.GetComponent<WhiteboardInteractor>();
        if (interactor == null) interactor = Undo.AddComponent<WhiteboardInteractor>(player.gameObject);
        else Undo.RecordObject(interactor, "Configure interactor");

        interactor.aimSource = player.cameraPivot;
        interactor.crosshair = crosshair;
    }
}
