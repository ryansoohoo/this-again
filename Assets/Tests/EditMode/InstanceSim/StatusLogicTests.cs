using NUnit.Framework;

// Pure status core: reduction to GateMod, stacking policies, deterministic ageing + periodic accrual.
public class StatusLogicTests
{
    // Minimal catalog indexed by StatusKind. Durations/periods in ticks.
    static StatusEffectDef[] Defs() => new[]
    {
        new StatusEffectDef { id = 0, durationTicks = 18, blocksMove = true,  blocksAttack = true,  moveScale = 0f, policy = StackPolicy.Refresh, maxStacks = 1, visualId = 1 }, // HitStun
        new StatusEffectDef { id = 1, durationTicks = 0,  blocksMove = false, blocksAttack = true,  moveScale = 1f, policy = StackPolicy.Refresh, maxStacks = 1, visualId = 0 }, // AttackCooldown
        new StatusEffectDef { id = 2, durationTicks = 180,blocksMove = false, blocksAttack = false, moveScale = 1f, periodTicks = 30, amountPerTick = 5, policy = StackPolicy.Stack, maxStacks = 5, visualId = 2 }, // Poison
        new StatusEffectDef { id = 3, durationTicks = 90, blocksMove = true,  blocksAttack = false, moveScale = 0f, policy = StackPolicy.Refresh, maxStacks = 1, visualId = 3 }, // Freeze
        new StatusEffectDef { id = 4, durationTicks = 120,blocksMove = false, blocksAttack = false, moveScale = 0.5f, policy = StackPolicy.Refresh, maxStacks = 1, visualId = 4 }, // Slow
    };

    [Test]
    public void Empty_ReducesToNone()
    {
        var g = StatusLogic.Reduce(new StatusState(), Defs());
        Assert.IsTrue(g.CanMove);
        Assert.IsTrue(g.CanAttack);
        Assert.AreEqual(1f, g.moveScale, 1e-4f);
    }

    [Test]
    public void HitStun_BlocksMoveAndAttack()
    {
        var s = new StatusState();
        StatusLogic.Apply(s, Defs()[(int)StatusKind.HitStun], tick: 1, self: false);
        var g = StatusLogic.Reduce(s, Defs());
        Assert.IsFalse(g.CanMove);
        Assert.IsFalse(g.CanAttack);
    }

    [Test]
    public void OverlappingSlows_MultiplyMoveScale()
    {
        var s = new StatusState();
        StatusLogic.Apply(s, Defs()[(int)StatusKind.Slow], tick: 1, self: false);            // 0.5
        StatusLogic.Apply(s, Defs()[(int)StatusKind.Freeze], tick: 1, self: false);          // 0 (root)
        var g = StatusLogic.Reduce(s, Defs());
        Assert.AreEqual(0f, g.moveScale, 1e-4f);   // 0.5 * 0 = 0
        Assert.IsFalse(g.CanMove);                 // Freeze blocksMove
    }

    [Test]
    public void Refresh_ResetsDuration_NoSecondInstance()
    {
        var s = new StatusState();
        StatusLogic.Apply(s, Defs()[(int)StatusKind.Freeze], tick: 1, self: false);
        s.effects[0].remainingTicks = 5;                       // age it down
        StatusLogic.Apply(s, Defs()[(int)StatusKind.Freeze], tick: 10, self: false);
        Assert.AreEqual(1, s.count);                           // still one instance
        Assert.AreEqual(90, s.effects[0].remainingTicks);      // duration refreshed
    }

    // --- Tick-stacked model (periodTicks > 0): a stack = one pending tick; each period fires FLAT damage then
    //     drops one stack; re-apply adds a stack; gate is live from apply. (Poison: period 30, perTick 5, max 5.) ---

    [Test]
    public void TickStack_Apply_AddsOneStack_NoImmediateDamage()
    {
        var s = new StatusState();
        var defs = Defs();
        StatusLogic.Apply(s, defs[(int)StatusKind.Poison], tick: 1, self: false);
        Assert.AreEqual(1, s.count);
        Assert.AreEqual(1, s.effects[0].stacks);
        int dmg = 0;
        for (int t = 0; t < 29; t++) { StatusLogic.Step(s, defs, out int d); dmg += d; }
        Assert.AreEqual(0, dmg);   // nothing until a full period (30 ticks) elapses
    }

    [Test]
    public void TickStack_FiresFlatDamage_AndDropsOneStackPerPeriod()
    {
        var s = new StatusState();
        var defs = Defs();
        StatusLogic.Apply(s, defs[(int)StatusKind.Poison], tick: 1, self: false);
        StatusLogic.Apply(s, defs[(int)StatusKind.Poison], tick: 1, self: false);   // 2 stacks
        Assert.AreEqual(2, s.effects[0].stacks);

        int dmg = 0;
        for (int t = 0; t < 30; t++) { StatusLogic.Step(s, defs, out int d); dmg += d; }
        Assert.AreEqual(5, dmg);                  // FLAT amountPerTick (not x stacks)
        Assert.AreEqual(1, s.effects[0].stacks);  // one stack consumed

        for (int t = 0; t < 30; t++) { StatusLogic.Step(s, defs, out int d); dmg += d; }
        Assert.AreEqual(10, dmg);                 // second period fires another flat 5
        Assert.AreEqual(0, s.count);              // last stack consumed -> effect removed
    }

    [Test]
    public void TickStack_CapsAtMaxStacks()
    {
        var s = new StatusState();
        var defs = Defs();
        for (int i = 0; i < 7; i++) StatusLogic.Apply(s, defs[(int)StatusKind.Poison], tick: 1, self: false);
        Assert.AreEqual(1, s.count);
        Assert.AreEqual(5, s.effects[0].stacks);   // capped at maxStacks = 5
    }

    [Test]
    public void Step_ExpiresAtZero()
    {
        var s = new StatusState();
        var defs = Defs();
        StatusLogic.Apply(s, defs[(int)StatusKind.HitStun], tick: 1, self: false);   // 18 ticks
        for (int t = 0; t < 18; t++) StatusLogic.Step(s, defs, out _);
        Assert.AreEqual(0, s.count);                 // expired and removed
    }

    [Test]
    public void Step_GateActiveOnFinalTick()
    {
        var s = new StatusState();
        var defs = Defs();
        StatusLogic.Apply(s, defs[(int)StatusKind.HitStun], tick: 1, self: false);
        s.effects[0].remainingTicks = 1;             // one tick left
        var g = StatusLogic.Step(s, defs, out _);    // gate is computed BEFORE decrement
        Assert.IsFalse(g.CanMove);                   // still blocked this tick
        Assert.AreEqual(0, s.count);                 // then expires
    }

    [Test]
    public void Apply_DurationOverride_UsedForAttackCooldown()
    {
        var s = new StatusState();
        StatusLogic.Apply(s, Defs()[(int)StatusKind.AttackCooldown], tick: 1, self: true, durationOverride: 30);
        Assert.AreEqual(30, s.effects[0].remainingTicks);
        Assert.IsTrue(s.effects[0].selfInflicted);
    }

    [Test]
    public void ActiveMask_HasBitPerActiveKind()
    {
        var s = new StatusState();
        var defs = Defs();
        StatusLogic.Apply(s, defs[(int)StatusKind.Poison], tick: 1, self: false);
        StatusLogic.Apply(s, defs[(int)StatusKind.Slow], tick: 1, self: false);
        ushort mask = StatusLogic.ActiveMask(s);
        Assert.AreEqual((1 << (int)StatusKind.Poison) | (1 << (int)StatusKind.Slow), mask);
    }
}
