using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// Editor utility: builds the custom command-line UI as a self-contained Screen Space-Overlay Canvas
// (Canvas + CommandConsole + a bottom-center Panel holding a selection-highlight box behind a Text
// label), saves it as a prefab, and leaves a connected instance in the active scene. Re-running rebuilds
// it. Run via Tools > Minifantasy > Setup Command Console. Uses the built-in LegacyRuntime font so no
// TMP/font import is required.
public static class CommandConsoleSetupTool
{
    const string PrefabPath = "Assets/Prefabs/CommandConsole.prefab";

    [MenuItem("Tools/Minifantasy/Setup Command Console")]
    public static void Setup()
    {
        var scene = EditorSceneManager.GetActiveScene();

        var existing = Object.FindFirstObjectByType<CommandConsole>();
        if (existing != null) Object.DestroyImmediate(existing.gameObject);   // rebuild from scratch

        var root = new GameObject("CommandConsole", typeof(Canvas), typeof(CanvasScaler), typeof(CommandConsole));
        var canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        var panel = new GameObject("Panel", typeof(Image), typeof(CanvasGroup));
        panel.transform.SetParent(root.transform, false);
        var panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = panelRect.anchorMax = panelRect.pivot = new Vector2(0.5f, 0f);
        panelRect.sizeDelta = new Vector2(760, 48);
        panelRect.anchoredPosition = new Vector2(0f, 48f);   // bottom-center, lifted off the edge
        var panelImg = panel.GetComponent<Image>();
        panelImg.color = new Color(0.04f, 0.05f, 0.07f, 0.82f);
        var panelGroup = panel.GetComponent<CanvasGroup>();
        panelGroup.alpha = 0f; panelGroup.interactable = false; panelGroup.blocksRaycasts = false;

        // Selection highlight — first child so it renders behind the text. Positioned at runtime.
        var hl = new GameObject("Highlight", typeof(Image));
        hl.transform.SetParent(panel.transform, false);
        var hlRect = hl.GetComponent<RectTransform>();
        hlRect.anchorMin = hlRect.anchorMax = new Vector2(0.5f, 0.5f);
        hlRect.pivot = new Vector2(0f, 0.5f);
        hlRect.sizeDelta = new Vector2(0f, 32f);
        var hlImg = hl.GetComponent<Image>();
        hlImg.color = new Color(0.23f, 0.45f, 0.95f, 0.45f);
        hlImg.raycastTarget = false;
        hl.SetActive(false);

        var labelGO = new GameObject("Input", typeof(Text));
        labelGO.transform.SetParent(panel.transform, false);   // last child → on top of the highlight
        var labelRect = labelGO.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero; labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(16f, 6f); labelRect.offsetMax = new Vector2(-16f, -6f);
        var label = labelGO.GetComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = 24;
        label.alignment = TextAnchor.MiddleLeft;
        label.color = Color.white;
        label.supportRichText = true;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        label.raycastTarget = false;

        var console = root.GetComponent<CommandConsole>();
        var so = new SerializedObject(console);
        so.FindProperty("panelGroup").objectReferenceValue = panelGroup;
        so.FindProperty("panelRect").objectReferenceValue = panelRect;
        so.FindProperty("panelImage").objectReferenceValue = panelImg;
        so.FindProperty("label").objectReferenceValue = label;
        so.FindProperty("highlightRect").objectReferenceValue = hlRect;
        so.ApplyModifiedPropertiesWithoutUndo();

        if (!AssetDatabase.IsValidFolder("Assets/Prefabs")) AssetDatabase.CreateFolder("Assets", "Prefabs");
        var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(root, PrefabPath, InteractionMode.UserAction);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Selection.activeObject = prefab;
        Debug.Log($"[CommandConsole] (re)built {PrefabPath} and added an instance to the scene. Enter Play mode and press Enter to open it.");
    }
}
