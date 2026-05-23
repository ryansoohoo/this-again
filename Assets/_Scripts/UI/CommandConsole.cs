using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Custom command line (UI) — a hand-rolled text field (not Unity's InputField), with a full editing
// layer: caret + range selection, clipboard (Ctrl+A/C/X/V), arrow/Home/End navigation, click-to-place,
// drag-select, and double-click word select (double-click an already-selected word selects all). Hidden
// until Enter opens it; type a command, Enter submits. Valid commands run and close it; invalid ones
// flash red, shake, clear, and stay open. Commands are data-driven so a future MUD autocomplete can
// enumerate/prefix-match them (see Suggest). While open, InputState.Typing suppresses player/camera input.
public sealed class CommandConsole : MonoBehaviour
{
    [SerializeField] CanvasGroup panelGroup;      // the box; faded in/out with the console (matches ChatPopup)
    [SerializeField] RectTransform panelRect;     // shaken on an invalid command
    [SerializeField] Image panelImage;            // flashed red on an invalid command
    [SerializeField] Text label;                  // prompt + typed text + caret
    [SerializeField] RectTransform highlightRect; // selection box, drawn behind the text
    [SerializeField] float highlightHeight = 32f;
    [SerializeField] float fadeSeconds = 0.4f;    // same rate as ChatPopup so they fade together

    [SerializeField] string prompt = "> ";
    [SerializeField] int maxLength = 80;

    static readonly Color InvalidColor = new(0.62f, 0.12f, 0.12f, 0.92f);
    const float ShakeTime = 0.35f, FlashTime = 0.45f;
    const float DoubleClickTime = 0.3f, DoubleClickPixels = 8f;

    // Edit state: caret in [0, text.Length]; selection is the range [min(caret,anchor), max(...)).
    string text = "";
    int caret, anchor;

    bool open;
    public bool LockOpen { get; set; }   // while true: Esc / click-out / empty-Enter won't close (only a command can)
    Vector2 panelBasePos;
    Color panelBaseColor;
    float shakeUntil, flashUntil;
    float nextRepeatBackspace, nextRepeatDelete;
    float lastClickTime = -1f;
    Vector2 lastClickPos;
    bool dragging;
    Keyboard subscribedKb;

    // Cached glyph-boundary x-positions (label-local units) for the current text, used for both
    // click hit-testing and sizing the selection box. Rebuilt only when the text or width changes.
    readonly TextGenerator hitGen = new();
    float[] bounds = new float[1];
    int boundsCount;
    string boundsText;
    float boundsWidth;

    int SelMin => Mathf.Min(caret, anchor);
    int SelMax => Mathf.Max(caret, anchor);
    bool HasSel => caret != anchor;

    void Awake()
    {
        if (panelRect != null) panelBasePos = panelRect.anchoredPosition;
        if (panelImage != null) panelBaseColor = panelImage.color;
        if (panelGroup != null) panelGroup.alpha = 0f;
        InputState.Typing = false;
    }

    void OnDisable()
    {
        if (subscribedKb != null) { subscribedKb.onTextInput -= OnTextInput; subscribedKb = null; }
        InputState.Typing = false;
    }

    void Update()
    {
        EnsureSubscribed();
        UpdateFade();
        var kb = Keyboard.current;
        if (kb == null) return;

        if (!open)
        {
            if (EnterPressed(kb)) Open();
            return;
        }

        if (kb.escapeKey.wasPressedThisFrame) { if (!LockOpen) Close(); return; }
        if (EnterPressed(kb)) { Confirm(); return; }
        if (kb.tabKey.wasPressedThisFrame) { AcceptGhost(); return; }
        if (HandleMouse()) return;       // a click outside closes and returns true
        HandleShortcuts(kb);
        HandleNavigation(kb);
        HandleDeletes(kb);
        UpdateFeedback();
        Render();
    }

    // ---- Text input (printable chars; respects layout/shift). Control combos arrive as <' ' and are ignored here. ----
    void OnTextInput(char c)
    {
        if (!open || c < ' ' || c == 127) return;
        if (!HasSel && text.Length >= maxLength) return;
        Insert(c.ToString());
        Render();
    }

    void EnsureSubscribed()
    {
        var kb = Keyboard.current;
        if (kb == subscribedKb) return;
        if (subscribedKb != null) subscribedKb.onTextInput -= OnTextInput;
        subscribedKb = kb;
        if (subscribedKb != null) subscribedKb.onTextInput += OnTextInput;
    }

    // ---- Mutations (all go through these so selection is always handled consistently) ----
    void Insert(string s)
    {
        if (HasSel) DeleteSelection();
        if (s.Length > maxLength - text.Length) s = s.Substring(0, Mathf.Max(0, maxLength - text.Length));
        text = text.Insert(caret, s);
        caret += s.Length;
        anchor = caret;
    }

    void DeleteSelection()
    {
        int a = SelMin, b = SelMax;
        text = text.Remove(a, b - a);
        caret = anchor = a;
    }

    // ---- Shortcuts: Ctrl/Cmd + A / C / X / V ----
    void HandleShortcuts(Keyboard kb)
    {
        bool ctrl = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed || kb.leftCommandKey.isPressed || kb.rightCommandKey.isPressed;
        if (!ctrl) return;

        if (kb.aKey.wasPressedThisFrame) { anchor = 0; caret = text.Length; Render(); }
        else if (kb.cKey.wasPressedThisFrame) GUIUtility.systemCopyBuffer = HasSel ? text.Substring(SelMin, SelMax - SelMin) : text;
        else if (kb.xKey.wasPressedThisFrame)
        {
            GUIUtility.systemCopyBuffer = HasSel ? text.Substring(SelMin, SelMax - SelMin) : text;
            if (HasSel) DeleteSelection(); else { text = ""; caret = anchor = 0; }
            Render();
        }
        else if (kb.vKey.wasPressedThisFrame)
        {
            string clip = GUIUtility.systemCopyBuffer;
            if (!string.IsNullOrEmpty(clip)) { Insert(Sanitize(clip)); Render(); }
        }
    }

    // ---- Caret navigation: arrows (Ctrl = by word, Shift = extend selection), Home/End ----
    void HandleNavigation(Keyboard kb)
    {
        bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
        bool ctrl = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;

        if (kb.leftArrowKey.wasPressedThisFrame)
        {
            if (!shift && HasSel) MoveTo(SelMin, false);
            else MoveTo(ctrl ? WordBoundaryLeft(caret) : caret - 1, shift);
        }
        else if (kb.rightArrowKey.wasPressedThisFrame)
        {
            if (!shift && HasSel) MoveTo(SelMax, false);
            else MoveTo(ctrl ? WordBoundaryRight(caret) : caret + 1, shift);
        }
        else if (kb.homeKey.wasPressedThisFrame) MoveTo(0, shift);
        else if (kb.endKey.wasPressedThisFrame) MoveTo(text.Length, shift);
    }

    void MoveTo(int pos, bool extend)
    {
        caret = Mathf.Clamp(pos, 0, text.Length);
        if (!extend) anchor = caret;
        Render();
    }

    // ---- Backspace / Delete (with key-repeat while held) ----
    void HandleDeletes(Keyboard kb)
    {
        float now = Time.unscaledTime;
        var bk = kb.backspaceKey;
        if (bk.wasPressedThisFrame) { Backspace(); nextRepeatBackspace = now + 0.4f; }
        else if (bk.isPressed && now >= nextRepeatBackspace) { Backspace(); nextRepeatBackspace = now + 0.04f; }

        var dk = kb.deleteKey;
        if (dk.wasPressedThisFrame) { ForwardDelete(); nextRepeatDelete = now + 0.4f; }
        else if (dk.isPressed && now >= nextRepeatDelete) { ForwardDelete(); nextRepeatDelete = now + 0.04f; }
    }

    void Backspace()
    {
        if (HasSel) DeleteSelection();
        else if (caret > 0) { text = text.Remove(caret - 1, 1); caret = anchor = caret - 1; }
        Render();
    }

    void ForwardDelete()
    {
        if (HasSel) DeleteSelection();
        else if (caret < text.Length) text = text.Remove(caret, 1);
        Render();
    }

    // ---- Mouse: click places caret, drag extends, double-click selects a word (again → all) ----
    bool HandleMouse()
    {
        var m = Mouse.current;
        if (m == null) return false;

        if (m.leftButton.wasPressedThisFrame || m.rightButton.wasPressedThisFrame || m.middleButton.wasPressedThisFrame)
        {
            Vector2 mp = m.position.ReadValue();
            if (panelRect == null || !RectTransformUtility.RectangleContainsScreenPoint(panelRect, mp, null))
            {
                if (!LockOpen) { Close(); return true; }
                return false;   // locked: ignore clicks outside the panel
            }
            if (m.leftButton.wasPressedThisFrame) OnLeftDown(mp);
        }

        if (dragging)
        {
            if (m.leftButton.isPressed) { MoveTo(IndexFromMouse(m.position.ReadValue()), true); }
            else dragging = false;
        }
        return false;
    }

    void OnLeftDown(Vector2 mp)
    {
        float now = Time.unscaledTime;
        bool isDouble = now - lastClickTime <= DoubleClickTime && Vector2.Distance(mp, lastClickPos) <= DoubleClickPixels;
        lastClickTime = isDouble ? -1f : now;     // reset after a double so a 3rd click starts fresh
        lastClickPos = mp;

        int idx = IndexFromMouse(mp);
        if (isDouble)
        {
            (int wa, int wb) = WordAt(idx);
            if (HasSel && SelMin == wa && SelMax == wb) { anchor = 0; caret = text.Length; }  // already that word → select all
            else { anchor = wa; caret = wb; }
            dragging = false;
        }
        else { caret = anchor = idx; dragging = true; }
        Render();
    }

    void Open()
    {
        open = true; InputState.Typing = true;
        text = ""; caret = anchor = 0;
        dragging = false; lastClickTime = -1f;
        ResetVisuals();
        Render();
    }

    // Open the console and trap it open (used by encounters). Only a command that clears LockOpen can close it.
    // Snap the panel to full alpha so an encounter appears instantly (no fade-in).
    public void OpenLocked()
    {
        if (!open) Open();
        LockOpen = true;
        if (panelGroup != null) panelGroup.alpha = 1f;
    }
    public void Unlock() => LockOpen = false;

    void Close()
    {
        LockOpen = false;
        open = false; InputState.Typing = false;
        text = ""; caret = anchor = 0;
        dragging = false;
        ResetVisuals();   // hide any selection box; the panel then fades out via UpdateFade
    }

    // Fade the whole box in/out with the console's open state — same rate as the chat popup.
    void UpdateFade()
    {
        if (panelGroup == null) return;
        float target = open ? 1f : 0f;
        panelGroup.alpha = Mathf.MoveTowards(panelGroup.alpha, target, Time.unscaledDeltaTime / Mathf.Max(0.01f, fadeSeconds));
    }

    void Confirm()
    {
        string submitted = Collapse(text);
        if (submitted.Length == 0) { if (!LockOpen) Close(); return; }   // empty Enter cancels (unless locked)

        GameLog.Post(OutputType.Command, "> " + submitted);   // echo the typed line into the chat
        var result = CommandRouter.Instance.Execute(text);
        if (result.Status == CommandStatus.Ok)
        {
            if (!string.IsNullOrEmpty(result.Message)) GameLog.Post(result.Output, result.Message);
            if (result.KeepOpen) { text = ""; caret = anchor = 0; Render(); }   // ready for the next command
            else Close();
        }
        else
        {
            string msg = !string.IsNullOrEmpty(result.Message) ? result.Message
                       : result.Status == CommandStatus.Unknown ? "Unknown command." : null;
            if (msg != null) GameLog.Post(OutputType.System, msg);
            Invalid();
        }
    }

    void Invalid()
    {
        text = ""; caret = anchor = 0;
        float now = Time.unscaledTime;
        shakeUntil = now + ShakeTime;
        flashUntil = now + FlashTime;
        Render();
    }

    void UpdateFeedback()
    {
        float now = Time.unscaledTime;
        if (panelRect != null)
        {
            if (now < shakeUntil)
            {
                float amp = Mathf.Min(1f, (shakeUntil - now) / ShakeTime) * 14f;
                panelRect.anchoredPosition = panelBasePos + new Vector2(Mathf.Sin(now * 80f) * amp, 0f);
            }
            else panelRect.anchoredPosition = panelBasePos;
        }
        if (panelImage != null)
            panelImage.color = now < flashUntil
                ? Color.Lerp(panelBaseColor, InvalidColor, (flashUntil - now) / FlashTime)
                : panelBaseColor;
    }

    void ResetVisuals()
    {
        shakeUntil = flashUntil = 0f;
        if (panelRect != null) panelRect.anchoredPosition = panelBasePos;
        if (panelImage != null) panelImage.color = panelBaseColor;
        if (highlightRect != null) highlightRect.gameObject.SetActive(false);
    }

    // ---- Rendering: caret is an alpha-toggled '|' inside the text (stable width, exact position);
    // the selection is a box positioned behind the text from the cached glyph boundaries. ----
    void Render()
    {
        if (label == null) return;
        if (HasSel)
        {
            label.text = prompt + text;
            UpdateHighlight();
        }
        else
        {
            bool caretOn = ((int)(Time.unscaledTime / 0.5f) & 1) == 0;
            string caretGlyph = caretOn ? "<color=#FFFFFFFF>|</color>" : "<color=#FFFFFF00>|</color>";
            string ghost = caret == text.Length ? CommandRouter.Instance.Suggest(text) : "";   // only complete at line end
            if (!string.IsNullOrEmpty(ghost))
                label.text = prompt + text + "<color=#FFFFFF55>" + ghost + "</color>" + caretGlyph;   // ghost flows on as if typed; caret after it
            else
                label.text = prompt + text.Substring(0, caret) + caretGlyph + text.Substring(caret);
            if (highlightRect != null) highlightRect.gameObject.SetActive(false);
        }
    }

    void UpdateHighlight()
    {
        if (highlightRect == null) return;
        EnsureBounds();
        int promptLen = prompt.Length;
        float x0 = BoundX(promptLen + SelMin), x1 = BoundX(promptLen + SelMax);
        highlightRect.gameObject.SetActive(true);
        highlightRect.anchoredPosition = new Vector2(x0, 0f);          // pivot is left-center
        highlightRect.sizeDelta = new Vector2(Mathf.Max(2f, x1 - x0), highlightHeight);
    }

    // ---- Glyph boundaries (label-local x of each character edge), via a non-rich-text layout pass ----
    void EnsureBounds()
    {
        if (label == null) return;
        float w = label.rectTransform.rect.width;
        string plain = prompt + text;
        if (boundsText == plain && Mathf.Approximately(boundsWidth, w) && boundsCount == plain.Length + 1) return;

        var settings = label.GetGenerationSettings(label.rectTransform.rect.size);
        settings.richText = false;
        hitGen.Populate(plain, settings);
        var chars = hitGen.characters;
        float ppu = label.pixelsPerUnit <= 0f ? 1f : label.pixelsPerUnit;

        int n = plain.Length;
        if (bounds.Length < n + 1) bounds = new float[n + 1];
        for (int i = 0; i < n; i++)
            bounds[i] = (i < chars.Count ? chars[i].cursorPos.x : LastEdge(chars)) / ppu;
        bounds[n] = (n > 0 ? LastEdge(chars) : (chars.Count > 0 ? chars[0].cursorPos.x : 0f)) / ppu;
        boundsCount = n + 1;
        boundsText = plain;
        boundsWidth = w;
    }

    static float LastEdge(IList<UICharInfo> chars)
    {
        if (chars.Count == 0) return 0f;
        var last = chars[chars.Count - 1];
        return last.cursorPos.x + last.charWidth;
    }

    float BoundX(int i)
    {
        if (boundsCount == 0) return 0f;
        return bounds[Mathf.Clamp(i, 0, boundsCount - 1)];
    }

    // Map a screen point to the nearest text index (0..text.Length).
    int IndexFromMouse(Vector2 screenPos)
    {
        if (label == null) return 0;
        EnsureBounds();
        RectTransformUtility.ScreenPointToLocalPointInRectangle(label.rectTransform, screenPos, null, out Vector2 local);
        int promptLen = prompt.Length;
        int best = 0;
        float bestD = float.MaxValue;
        for (int i = 0; i <= text.Length; i++)
        {
            float d = Mathf.Abs(local.x - BoundX(promptLen + i));
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // ---- Word boundaries (whitespace-delimited) ----
    (int, int) WordAt(int index)
    {
        if (text.Length == 0) return (0, 0);
        int i = Mathf.Clamp(index, 0, text.Length - 1);
        bool ws = char.IsWhiteSpace(text[i]);
        int a = i, b = i + 1;
        while (a > 0 && char.IsWhiteSpace(text[a - 1]) == ws) a--;
        while (b < text.Length && char.IsWhiteSpace(text[b]) == ws) b++;
        return (a, b);
    }

    int WordBoundaryLeft(int from)
    {
        int i = from;
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
        return i;
    }

    int WordBoundaryRight(int from)
    {
        int i = from;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    // Autocomplete: the completion suffix for the current input among the commands active in the current
    // scope, else "" (empty when the prefix is ambiguous). Rendered inline as a gray ghost; accepted on Tab.
    public string Suggest(string current) => CommandRouter.Instance.Suggest(current);

    void AcceptGhost()
    {
        string ghost = CommandRouter.Instance.Suggest(text);
        if (!string.IsNullOrEmpty(ghost)) { Insert(ghost); Render(); }
    }

    static bool EnterPressed(Keyboard kb) => kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;

    static string Sanitize(string s) => Regex.Replace(s, @"[\x00-\x1F\x7F]", "");   // strip newlines/tabs/control chars (single-line)

    static string Collapse(string s) => Regex.Replace(s.Trim(), @"\s+", " ");
}
