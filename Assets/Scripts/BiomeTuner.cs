using UnityEngine;

// Debug panel (IMGUI): sliders to tune the biome noise + water colors and rebuild the map live in play.
// Added to the GridManager object at boot. Collapsible; scrolls so it never overflows the screen.
// Each slider edits the shared BiomeSettings in place and flags 'dirty' so we Regenerate once per frame.
public class BiomeTuner : MonoBehaviour
{
    bool show = true;
    bool dirty;
    Vector2 scroll;

    void OnGUI()
    {
        var gm = GridManager.Instance;
        if (gm == null) return;
        var b = gm.Biome;

        GUILayout.BeginArea(new Rect(10, 150, 292, show ? 360 : 30), GUI.skin.box);

        GUILayout.BeginHorizontal();
        GUILayout.Label("Biome noise", GUILayout.ExpandWidth(true));
        if (GUILayout.Button(show ? "hide" : "show", GUILayout.Width(48))) show = !show;
        GUILayout.EndHorizontal();

        if (show)
        {
            scroll = GUILayout.BeginScrollView(scroll);
            dirty = false;

            F("Scale", ref b.biomeScale, 0.01f, 0.40f, "0.000");
            I("Octaves", ref b.octaves, 1, 8);
            F("Warp", ref b.warpStrength, 0f, 20f, "0.0");
            F("Sea level", ref b.seaLevel, 0f, 1f, "0.00");
            F("Continent scale", ref b.waterScale, 0.005f, 0.10f, "0.000");
            F("Continent contrast", ref b.continentContrast, 1f, 6f, "0.0");
            F("Water shore", ref b.waterShoreLevel, 0f, 1f, "0.00");
            F("Water deep", ref b.waterDeepLevel, 0f, 1f, "0.00");
            I("Ocean depth", ref b.waterDepthCells, 1, 40);
            F("Ground bright", ref b.landBackgroundLevel, 0f, 1f, "0.00");
            F("Cold <", ref b.coldThreshold, 0f, 1f, "0.00");
            F("Hot >", ref b.hotThreshold, 0f, 1f, "0.00");
            F("Wet ≥", ref b.wetThreshold, 0f, 1f, "0.00");
            I("Seed", ref b.seed, 0, 9999);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Randomize")) { b.seed = Random.Range(0, 100000); dirty = true; }
            if (GUILayout.Button("Save")) gm.SaveSettings();
            if (GUILayout.Button("Reset")) gm.ResetSavedSettings();
            GUILayout.EndHorizontal();

            GUILayout.EndScrollView();

            if (dirty) gm.Regenerate();
        }

        GUILayout.EndArea();
    }

    void F(string name, ref float field, float min, float max, string fmt)
    {
        GUILayout.Label($"{name}  {field.ToString(fmt)}");
        float v = GUILayout.HorizontalSlider(field, min, max);
        if (!Mathf.Approximately(v, field)) { field = v; dirty = true; }
    }

    void I(string name, ref int field, int min, int max)
    {
        GUILayout.Label($"{name}  {field}");
        int v = Mathf.RoundToInt(GUILayout.HorizontalSlider(field, min, max));
        if (v != field) { field = v; dirty = true; }
    }
}
