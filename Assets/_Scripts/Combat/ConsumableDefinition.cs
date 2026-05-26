using UnityEngine;

// One consumable item authored as data. Applied to the user on /use via the existing status-effect framework.
// Lives in default Assembly-CSharp because StatusEffectAsset is there (visual data forbids Combat/Core).
[CreateAssetMenu(menuName = "Minifantasy/Consumable Definition", fileName = "Consumable")]
public sealed class ConsumableDefinition : ScriptableObject
{
    public string displayName;
    [TextArea] public string description;
    public StatusEffectAsset onUseEffect;       // applied to user on /use (StatusLogic.Apply)
    [Range(1, 255)] public byte maxStack = 64;
}
