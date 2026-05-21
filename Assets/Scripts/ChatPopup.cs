using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Chat/output popup above the command input. Listens to GameLog and appends one widget per entry into a
// vertical, auto-sizing panel (the box grows/shrinks with its content), colored by OutputType. Shows while
// the console is open or briefly after new output, then fades. Entry creation is the seam for richer
// content later (sprites, item rows, encounter cards) — switch on entry.Type in BuildEntry.
public sealed class ChatPopup : MonoBehaviour
{
    [SerializeField] CanvasGroup group;       // whole-popup fade
    [SerializeField] RectTransform content;    // panel with VerticalLayoutGroup + ContentSizeFitter
    [SerializeField] Text entryTemplate;       // inactive Text cloned per entry
    [SerializeField] int maxEntries = 14;      // caps the box height (older rows drop off the top)
    [SerializeField] float fadeSeconds = 0.4f;

    readonly List<GameObject> rows = new();

    void Awake()
    {
        if (entryTemplate != null) entryTemplate.gameObject.SetActive(false);
        if (group != null) group.alpha = 0f;
    }

    void OnEnable()
    {
        GameLog.Posted += OnPosted;
        Rebuild();
    }

    void OnDisable() => GameLog.Posted -= OnPosted;

    void OnPosted(OutputEntry e) => BuildEntry(e);   // visibility is tied to the console (see Update)

    void BuildEntry(OutputEntry e)
    {
        if (entryTemplate == null || content == null) return;
        var go = Instantiate(entryTemplate.gameObject, content);
        go.SetActive(true);
        go.transform.SetAsLastSibling();
        var label = go.GetComponent<Text>();
        label.text = e.Text;
        label.color = ColorFor(e.Type);
        rows.Add(go);
        while (rows.Count > maxEntries) { Destroy(rows[0]); rows.RemoveAt(0); }
        LayoutRebuilder.ForceRebuildLayoutImmediate(content);
    }

    void Rebuild()
    {
        foreach (var r in rows) Destroy(r);
        rows.Clear();
        var hist = GameLog.History;
        for (int i = Mathf.Max(0, hist.Count - maxEntries); i < hist.Count; i++) BuildEntry(hist[i]);
    }

    // The popup lives and dies with the command input: visible only while the console is open.
    void Update()
    {
        if (group == null) return;
        float target = CommandConsole.IsTyping ? 1f : 0f;
        group.alpha = Mathf.MoveTowards(group.alpha, target, Time.unscaledDeltaTime / Mathf.Max(0.01f, fadeSeconds));
    }

    static Color ColorFor(OutputType t) => t switch
    {
        OutputType.System    => new Color(1f, 0.78f, 0.27f),    // amber
        OutputType.Command   => new Color(0.55f, 0.85f, 1f),    // cyan (player echo)
        OutputType.Encounter => new Color(1f, 0.52f, 0.42f),    // red-orange
        OutputType.Inventory => new Color(0.72f, 0.9f, 0.66f),  // green
        _                    => new Color(0.92f, 0.92f, 0.92f), // Text: near-white
    };
}
