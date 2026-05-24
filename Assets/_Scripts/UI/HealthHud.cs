using UnityEngine;

// Visual: minimal on-screen HP readout for the local player (server-authoritative HP from the snapshot via
// LocalPlayer.SelfHp). IMGUI like RelayTestHUD, so it needs no Canvas/Text/font wiring. Underworld only.
public sealed class HealthHud : MonoBehaviour
{
    GUIStyle style;

    void OnGUI()
    {
        var lp = LocalPlayer.Instance;
        if (lp == null || !lp.InInstance) return;
        if (style == null) style = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
        style.normal.textColor = Color.white;
        GUI.Label(new Rect(Screen.width - 140, 16, 130, 32), $"HP {lp.SelfHp}", style);
    }
}
