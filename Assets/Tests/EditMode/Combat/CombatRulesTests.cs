using NUnit.Framework;

public class CombatRulesTests
{
    [Test] public void PlayerCanHitPlayer()    => Assert.IsTrue(CombatRules.CanHit(Faction.Player, Faction.Player));
    [Test] public void PlayerCanHitEnemy()     => Assert.IsTrue(CombatRules.CanHit(Faction.Player, Faction.Enemy));
    [Test] public void EnemyCanHitPlayer()     => Assert.IsTrue(CombatRules.CanHit(Faction.Enemy, Faction.Player));
    [Test] public void EnemyCannotHitEnemy()   => Assert.IsFalse(CombatRules.CanHit(Faction.Enemy, Faction.Enemy));
}
