using UnityEngine;

// Live-tunable day/night knobs (serialized on Game; persisted via JsonPref "daynight.json" like the other
// settings groups). Look-only: a clock length + a 4-stop colour ramp. No sun/shadow fields — nothing is
// sun-driven (the structure shadow is static; the character shadow is the pack's, untouched).
[System.Serializable]
public class DayNightSettings
{
    [Min(1f)] public float cycleSeconds = 480f;                       // full day length (~8 min)
    public Color nightColor = new Color(0.20f, 0.26f, 0.48f, 1f);     // deep dim blue
    public Color dawnColor  = new Color(1.00f, 0.78f, 0.58f, 1f);     // warm
    public Color noonColor  = Color.white;                            // neutral / bright
    public Color duskColor  = new Color(1.00f, 0.62f, 0.40f, 1f);     // orange
    [Range(0.01f, 0.49f)] public float dawnTime = 0.25f;             // colour-stop time for dawn
    [Range(0.51f, 0.99f)] public float duskTime = 0.75f;             // colour-stop time for dusk
}
