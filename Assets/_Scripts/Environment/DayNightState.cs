using UnityEngine;

// Data: the computed look for the current instant. DayNightSystem writes it; views read it.
public sealed class DayNightState
{
    public float timeOfDay;        // 0..1 (0 = midnight, 0.5 = noon)
    public Color tint = Color.white;
}
