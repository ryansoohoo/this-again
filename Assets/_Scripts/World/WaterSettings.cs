using UnityEngine;

// All live-tunable open-water animation knobs in one place: serialized on Game, mutated by TunerPanels,
// saved/loaded as a single JSON blob (PlayerPrefs). Pushed into the runtime water material by WaterMaterial.
// Defaults match the WaterWaves.shader property defaults.
[System.Serializable]
public class WaterSettings
{
    public float animSpeed = 1.5f;    // ripple spritesheet frames/sec (independent of wave drift)
    public float waveFreq = 0.35f;    // shader _NoiseScale: higher = smaller, more frequent waves
    public float waveDrift = 0.06f;   // shader _FlowSpeed: how fast wave/calm regions travel
    [Range(0f, 1f)] public float calm = 0.5f;   // fraction of water that stays flat blue (no ripple)
    public float windX = 1f;          // wave drift direction
    public float windY = 0.35f;
    [Range(0, 2)] public int styleRow = 0;      // which of the 3 water styles in the sheet
}
