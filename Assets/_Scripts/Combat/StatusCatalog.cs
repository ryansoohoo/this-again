using UnityEngine;

// Shared byte-id <-> status-effect map (the wire carries defId == StatusKind). Holds the effect ASSETS in
// StatusKind order; Defs compiles the pure StatusEffectDef[] the deterministic sim consumes; Visual() exposes
// the asset (sprites/tint) to the View layer. Wired on Game; built by Tools > Combat > Build Status Catalog.
[CreateAssetMenu(menuName = "Minifantasy/Status Catalog", fileName = "StatusCatalog")]
public sealed class StatusCatalog : ScriptableObject
{
    public StatusEffectAsset[] effects;   // ordered so effects[i].kind == (StatusKind)i

    StatusEffectDef[] _defs;
    public StatusEffectDef[] Defs => _defs ??= Build();

    StatusEffectDef[] Build()
    {
        int n = effects != null ? effects.Length : 0;
        var defs = new StatusEffectDef[n];
        for (int i = 0; i < n; i++) defs[i] = effects[i] != null ? effects[i].ToDef() : default;
        return defs;
    }

    // The effect asset for a defId/mask bit (View layer only). Null if out of range or unassigned.
    public StatusEffectAsset Visual(int defId) =>
        (effects != null && defId >= 0 && defId < effects.Length) ? effects[defId] : null;

    void OnValidate() => _defs = null;   // rebuild after Inspector edits
}
