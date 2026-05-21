using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

// Editor utility: builds the chat/output popup as a Screen Space-Overlay Canvas with an auto-sizing
// vertical panel (VerticalLayoutGroup + ContentSizeFitter) of text rows above the command input, plus an
// inactive entry template the ChatPopup clones per message. Saves it as a prefab + scene instance.
// Re-running rebuilds it. Run via Tools > Minifantasy > Setup Chat Popup. Uses the built-in LegacyRuntime font.
public static class ChatPopupSetupTool
{
    const string PrefabPath = "Assets/Prefabs/ChatPopup.prefab";

    [MenuItem("Tools/Minifantasy/Setup Chat Popup")]
    public static void Setup()
    {
        var scene = EditorSceneManager.GetActiveScene();

        var existing = Object.FindFirstObjectByType<ChatPopup>();
        if (existing != null) Object.DestroyImmediate(existing.gameObject);   // rebuild from scratch

        var root = new GameObject("ChatPopup", typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup), typeof(ChatPopup));
        var canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 99;   // just under the command input (100)
        var scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        var group = root.GetComponent<CanvasGroup>();
        group.alpha = 0f; group.interactable = false; group.blocksRaycasts = false;

        // Auto-sizing log panel, anchored bottom-center just above the input, growing upward.
        var log = new GameObject("Log", typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        log.transform.SetParent(root.transform, false);
        var logRect = log.GetComponent<RectTransform>();
        logRect.anchorMin = logRect.anchorMax = logRect.pivot = new Vector2(0.5f, 0f);
        logRect.sizeDelta = new Vector2(760f, 0f);            // width fixed; height driven by the fitter
        logRect.anchoredPosition = new Vector2(0f, 104f);     // sits just above the 48-tall input at y=48
        log.GetComponent<Image>().color = new Color(0.04f, 0.05f, 0.07f, 0.72f);
        log.GetComponent<Image>().raycastTarget = false;
        var vlg = log.GetComponent<VerticalLayoutGroup>();
        vlg.childControlWidth = true; vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        vlg.spacing = 4f; vlg.padding = new RectOffset(12, 12, 8, 8);
        vlg.childAlignment = TextAnchor.LowerLeft;
        var fitter = log.GetComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

        // One row template (inactive); ChatPopup clones it per message and recolors by OutputType.
        var entryGO = new GameObject("EntryTemplate", typeof(Text));
        entryGO.transform.SetParent(log.transform, false);
        var entryRect = entryGO.GetComponent<RectTransform>();
        entryRect.anchorMin = new Vector2(0f, 1f); entryRect.anchorMax = new Vector2(0f, 1f); entryRect.pivot = new Vector2(0f, 1f);
        var entry = entryGO.GetComponent<Text>();
        entry.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        entry.fontSize = 18;
        entry.color = Color.white;
        entry.alignment = TextAnchor.UpperLeft;
        entry.horizontalOverflow = HorizontalWrapMode.Wrap;
        entry.verticalOverflow = VerticalWrapMode.Overflow;
        entry.supportRichText = true;
        entry.raycastTarget = false;
        entryGO.SetActive(false);

        var popup = root.GetComponent<ChatPopup>();
        var so = new SerializedObject(popup);
        so.FindProperty("group").objectReferenceValue = group;
        so.FindProperty("content").objectReferenceValue = logRect;
        so.FindProperty("entryTemplate").objectReferenceValue = entry;
        so.ApplyModifiedPropertiesWithoutUndo();

        if (!AssetDatabase.IsValidFolder("Assets/Prefabs")) AssetDatabase.CreateFolder("Assets", "Prefabs");
        var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(root, PrefabPath, InteractionMode.UserAction);

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Selection.activeObject = prefab;
        Debug.Log($"[ChatPopup] (re)built {PrefabPath} and added an instance to the scene. Output (help, etc.) now shows here.");
    }
}
