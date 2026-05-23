using UnityEngine;

// Pushes the live WaterSettings into the runtime water material (mesh submesh 1). No mesh rebuild — pure
// shader state. Game calls Apply() at boot and whenever a water slider changes.
public sealed class WaterMaterial
{
    readonly Material mat;
    readonly WaterSettings s;
    readonly float cellWorld;

    public WaterMaterial(Material mat, WaterSettings s, float cellWorld)
    {
        this.mat = mat; this.s = s; this.cellWorld = cellWorld;
        Apply();
    }

    public void Apply()
    {
        if (mat == null) return;
        mat.SetFloat("_AnimSpeed", s.animSpeed);
        mat.SetFloat("_NoiseScale", s.waveFreq);
        mat.SetFloat("_FlowSpeed", s.waveDrift);
        mat.SetFloat("_Calm", s.calm);
        mat.SetVector("_WindDir", new Vector4(s.windX, s.windY, 0f, 0f));
        mat.SetFloat("_StyleRow", s.styleRow);
        mat.SetFloat("_CellSize", cellWorld);
    }
}
