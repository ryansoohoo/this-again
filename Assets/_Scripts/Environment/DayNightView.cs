using UnityEngine;

// Visual: pushes the day/night tint into the world materials. Terrain uses Sprites/Default's _Color (nothing
// else writes it); water uses a dedicated _DayTint uniform so we don't fight WaterMaterial's wave params.
public sealed class DayNightView
{
    readonly Material terrain, water;
    static readonly int DayTintId = Shader.PropertyToID("_DayTint");

    public DayNightView(Material terrain, Material water) { this.terrain = terrain; this.water = water; }

    public void Apply(DayNightState st)
    {
        if (terrain != null) terrain.color = st.tint;
        if (water != null) water.SetColor(DayTintId, st.tint);
    }
}
