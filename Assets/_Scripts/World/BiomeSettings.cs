using UnityEngine;

// Live-tunable land/water knobs: serialized on the Game object, mutated by TunerPanels, saved/loaded as one
// JSON blob (PlayerPrefs). Climate sub-classification was removed — terrain is a binary land/water field.
[System.Serializable]
public class BiomeSettings
{
    public int seed = 1337;
    public float biomeScale = 0.08f;                        // domain-warp frequency (smaller = larger warp features)
    public float warpStrength = 8f;                         // domain-warp distortion, in cells
    [Range(0f, 1f)] public float seaLevel = 0.40f;          // continentalness below this -> ocean
    public float waterScale = 0.03f;                        // LOW-freq continent field (smaller = bigger continents/oceans)
    public float continentContrast = 2.5f;                 // pushes the continent field to extremes (solid land, wide ocean)
    [Range(1f, 3f)] public float minimapBrightness = 1.6f;  // minimap colors x this (brighter than the in-game map)
}
