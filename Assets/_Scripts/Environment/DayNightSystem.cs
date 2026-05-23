using Unity.Netcode;
using UnityEngine;

// Logic: advances the day/night clock and computes the tint into DayNightState. Time is a pure function of
// network/local seconds, so every client agrees with NO replication. Ticked by Game.Update.
public sealed class DayNightSystem
{
    readonly DayNightSettings s;
    public DayNightState State { get; } = new DayNightState();
    public float? TimeOverride { get; set; }   // debug `time` command; client-local, never replicated

    public DayNightSystem(DayNightSettings settings) { s = settings; Tick(); }

    public void Tick()
    {
        float t = TimeOverride ?? DayNightMath.TimeOfDay(NowSeconds(), s.cycleSeconds);
        State.timeOfDay = t;
        State.tint = DayNightMath.Tint(t, s);
    }

    static double NowSeconds()
    {
        var nm = NetworkManager.Singleton;
        if (nm != null && (nm.IsServer || nm.IsConnectedClient)) return nm.ServerTime.Time;
        return Time.unscaledTimeAsDouble;
    }
}

// Pure clock/colour math (no scene deps) — isolated so it is unit-testable if a test-reachable assembly is
// ever added (today Assembly-CSharp is not reachable from the Commands test asmdef).
public static class DayNightMath
{
    public static float TimeOfDay(double seconds, float cycleSeconds)
    {
        if (cycleSeconds <= 0f) return 0f;
        double frac = (seconds / cycleSeconds) % 1.0;
        if (frac < 0.0) frac += 1.0;
        return (float)frac;
    }

    // Piecewise lerp over stops: (0,night) (dawnTime,dawn) (0.5,noon) (duskTime,dusk) (1,night).
    public static Color Tint(float t, DayNightSettings s)
    {
        float dawn = Mathf.Clamp(s.dawnTime, 0.0001f, 0.4999f);
        float dusk = Mathf.Clamp(s.duskTime, 0.5001f, 0.9999f);
        if (t < dawn)  return Color.Lerp(s.nightColor, s.dawnColor, t / dawn);
        if (t < 0.5f)  return Color.Lerp(s.dawnColor,  s.noonColor, (t - dawn) / (0.5f - dawn));
        if (t < dusk)  return Color.Lerp(s.noonColor,  s.duskColor, (t - 0.5f) / (dusk - 0.5f));
        return Color.Lerp(s.duskColor, s.nightColor, (t - dusk) / (1f - dusk));
    }
}
