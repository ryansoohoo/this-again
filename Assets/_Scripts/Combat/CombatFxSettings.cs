// Live-tunable weapon attack FX, shared by EVERY weapon (one rule for all attacks). Saved via JsonPref, edited in
// the TunerPanels "Weapon FX" panel, and read by AttackView each frame. Plain data (default assembly).
[System.Serializable]
public sealed class CombatFxSettings
{
    public float hitGlow = 1.5f;                 // small glow on the Hit frame (AllIn1 _Glow)
    public float followThroughBright = 0.5f;     // HSV brightness during follow-through (<1 = darker)
    public float followThroughSaturation = 0f;   // HSV saturation during follow-through (0 = gray)
}
