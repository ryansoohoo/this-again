// Pure combat rules. CanHit is the friend/foe gate ONLY — the underworld-only restriction is the inInstance/region
// check at the call site (OnStrike), and self-hits are skipped there too. Pure + unit-tested so the same rule
// serves server hit detection today and future client-side hit prediction.
public static class CombatRules
{
    public static bool CanHit(Faction attacker, Faction victim)
    {
        if (attacker == Faction.Enemy && victim == Faction.Enemy) return false;   // no AI friendly-fire
        return true;   // Player↔Player (in-instance PvP), Player↔Enemy, Enemy↔Player
    }
}
