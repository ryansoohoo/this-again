using System.Collections.Generic;
using UnityEngine;

// Single consolidated debug overlay (IMGUI) for every live-tuning knob group — biome noise, ground cover, water.
// Replaces the old one-MonoBehaviour-per-panel setup (BiomeTuner/GroundTuner/WaterTuner), each of which had its
// own hand-placed Rect and a copy of the slider helpers. Panels are now data-driven entries in one accordion:
// stacked vertically from the top in a single column, with only ONE panel expanded at a time. Adding a knob
// group is one line in Build() plus a body method. Added to the Game object at boot.
public sealed class TunerPanels : MonoBehaviour
{
    sealed class Panel
    {
        public readonly string title;
        public readonly System.Action body;    // draw this panel's sliders + buttons (uses F/I; sets dirty on change)
        public readonly System.Action apply;    // run once this frame if any of this panel's sliders changed

        public Panel(string title, System.Action body, System.Action apply)
        {
            this.title = title; this.body = body; this.apply = apply;
        }
    }

    const float X = 10f, Top = 150f, Width = 300f, Margin = 10f;

    List<Panel> panels;
    int open;            // index of the one expanded panel (-1 = all collapsed); only one open at a time
    bool dirty;          // a slider in the open panel changed this frame
    Vector2 scroll;
    Game gm;      // refreshed each OnGUI; the body/apply delegates read it

    void Build() => panels = new List<Panel>
    {
        new Panel("Biome noise",   BiomeBody,  () => gm.Regenerate()),
        new Panel("Ground biomes", GroundBody, () => gm.Regenerate()),
        new Panel("Water",         WaterBody,  () => gm.ApplyWaterSettings()),
        new Panel("Structures",    StructureBody, () => gm.Regenerate()),
        new Panel("Day/Night",     DayNightBody,  () => {}),
    };

    void OnGUI()
    {
        gm = Game.Instance;
        if (gm == null) return;
        if (panels == null) Build();

        // Transparent full-height area + a content-sized box, so the panel background hugs the open panel
        // instead of painting an empty box down the whole screen; the scroll view guards against overflow.
        GUILayout.BeginArea(new Rect(X, Top, Width, Screen.height - Top - Margin));
        scroll = GUILayout.BeginScrollView(scroll);
        GUILayout.BeginVertical(GUI.skin.box);

        for (int i = 0; i < panels.Count; i++)
        {
            bool isOpen = open == i;
            if (GUILayout.Button((isOpen ? "[-]  " : "[+]  ") + panels[i].title))
                open = isOpen ? -1 : i;          // accordion: clicking the open one collapses; else swap to it

            if (!isOpen) continue;
            dirty = false;
            panels[i].body();
            if (dirty) panels[i].apply();
            GUILayout.Space(6f);
        }

        GUILayout.EndVertical();
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    // ---- panel bodies (each draws its own sliders + Save/Reset row) ----

    void BiomeBody()
    {
        var b = gm.Biome;
        F("Scale", ref b.biomeScale, 0.01f, 0.40f, "0.000");
        F("Warp", ref b.warpStrength, 0f, 20f, "0.0");
        F("Sea level", ref b.seaLevel, 0f, 1f, "0.00");
        F("Continent scale", ref b.waterScale, 0.005f, 0.10f, "0.000");
        F("Continent contrast", ref b.continentContrast, 1f, 6f, "0.0");
        F("Minimap bright", ref b.minimapBrightness, 1f, 3f, "0.0");
        I("Seed", ref b.seed, 0, 9999);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Randomize")) { b.seed = Random.Range(0, 100000); dirty = true; }
        if (GUILayout.Button("Save")) gm.SaveSettings();
        if (GUILayout.Button("Reset")) gm.ResetSavedSettings();
        GUILayout.EndHorizontal();
    }

    void GroundBody()
    {
        var g = gm.Ground;
        F("Region scale", ref g.scale, 0.005f, 0.20f, "0.000");
        I("Octaves", ref g.octaves, 1, 6);
        F("Mountain level", ref g.mountainLevel, 0f, 1f, "0.00");
        F("Rocky level", ref g.rockyLevel, 0f, 1f, "0.00");
        F("Forest level", ref g.forestLevel, 0f, 1f, "0.00");

        GUILayout.Label("Coverage (lower = more blank)");
        F("Grass cover", ref g.grassCoverage, 0f, 1f, "0.00");
        F("Forest cover", ref g.forestCoverage, 0f, 1f, "0.00");
        F("Rocky cover", ref g.rockyCoverage, 0f, 1f, "0.00");
        F("Mountain cover", ref g.mountainCoverage, 0f, 1f, "0.00");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save")) gm.SaveGroundSettings();
        if (GUILayout.Button("Reset")) gm.ResetGroundSettings();
        GUILayout.EndHorizontal();
    }

    void WaterBody()
    {
        var w = gm.Water;
        F("Ripple fps", ref w.animSpeed, 0.1f, 8f, "0.0");
        F("Wave freq (smaller=bigger)", ref w.waveFreq, 0.05f, 1f, "0.00");
        F("Wave drift", ref w.waveDrift, 0f, 0.5f, "0.000");
        F("Calm amount", ref w.calm, 0f, 1f, "0.00");
        F("Wind X", ref w.windX, -1f, 1f, "0.00");
        F("Wind Y", ref w.windY, -1f, 1f, "0.00");
        I("Style", ref w.styleRow, 0, 2);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save")) gm.SaveWaterSettings();
        if (GUILayout.Button("Reset")) gm.ResetWaterSettings();
        GUILayout.EndHorizontal();
    }

    void StructureBody()
    {
        var s = gm.Structure;
        I("Block size (smaller=denser)", ref s.blockSize, 2, 40);
        F("Site chance", ref s.siteChance, 0f, 1f, "0.00");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save")) gm.SaveStructureSettings();
        if (GUILayout.Button("Reset")) gm.ResetStructureSettings();
        GUILayout.EndHorizontal();
    }

    void DayNightBody()
    {
        var d = gm.DayNightCfg;
        F("Cycle seconds", ref d.cycleSeconds, 5f, 1200f, "0");
        F("Dawn time", ref d.dawnTime, 0.01f, 0.49f, "0.00");
        F("Dusk time", ref d.duskTime, 0.51f, 0.99f, "0.00");
        d.nightColor = ColRGB("Night", d.nightColor);
        d.dawnColor  = ColRGB("Dawn",  d.dawnColor);
        d.noonColor  = ColRGB("Noon",  d.noonColor);
        d.duskColor  = ColRGB("Dusk",  d.duskColor);

        float cur = gm.TimeOverride ?? (gm.DayNight != null ? gm.DayNight.timeOfDay : 0f);
        GUILayout.Label($"Time of day  {cur:0.00}" + (gm.TimeOverride.HasValue ? "  (frozen)" : ""));
        float nt = GUILayout.HorizontalSlider(cur, 0f, 1f);
        if (!Mathf.Approximately(nt, cur)) gm.SetTimeOverride(nt);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Live")) gm.SetTimeOverride(null);
        if (GUILayout.Button("Save")) gm.SaveDayNightSettings();
        if (GUILayout.Button("Reset")) gm.ResetDayNightSettings();
        GUILayout.EndHorizontal();
    }

    Color ColRGB(string name, Color c)
    {
        GUILayout.Label(name);
        float r = c.r, g = c.g, b = c.b;
        F("  R", ref r, 0f, 1f, "0.00");
        F("  G", ref g, 0f, 1f, "0.00");
        F("  B", ref b, 0f, 1f, "0.00");
        return new Color(r, g, b, 1f);
    }

    // ---- shared slider helpers (set 'dirty' when the value moves) ----

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
