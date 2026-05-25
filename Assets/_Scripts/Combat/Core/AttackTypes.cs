using System;
using UnityEngine;

// One timed sprite frame: which column of the sheet, and how long to show it (seconds, base — scaled at runtime).
[Serializable]
public struct TimedFrame
{
    public int column;
    public float duration;
}

// One authored direction: the canonical aim vector and which sheet row holds its frames.
[Serializable]
public struct DirectionEntry
{
    public Vector2 canonicalDir;
    public int row;
}

// Per-phase runtime time multipliers (the future stat hook: slows raise, buffs lower). 1.0 = base speed.
[Serializable]
public struct PhaseScales
{
    public float anticipation;
    public float hit;
    public float followThrough;
    public static PhaseScales One => new PhaseScales { anticipation = 1f, hit = 1f, followThrough = 1f };
}

public enum AttackPhase { Idle, Anticipation, TapWindup, Hit, FollowThrough }

// Pure attack state. Written by AttackLogic.Step; read by AttackView. No scene/SO refs.
[Serializable]
public struct AttackState
{
    public AttackPhase phase;
    public int dirIndex;        // index into AttackTimeline.directions
    public float residualDeg;   // tilt to aim exactly at the cursor
    public int frameIndex;      // index into the current phase's TimedFrame[]
    public float phaseElapsed;  // time accumulated on the current frame
    public bool windupComplete; // anticipation fully played (now holding) -> release = full strike
    public Vector2 lockedAim;   // aim direction frozen at commit (dir + residual); drives the lunge, can't be steered
}

// One frame of player input, already reduced to edges + aim. The future server-replication seam.
public struct AttackIntent
{
    public bool pressed;   // LMB down edge
    public bool held;      // LMB level
    public bool released;  // LMB up edge
    public bool feint;     // RMB down edge
    public Vector2 aimDir; // cursorWorld - selfWorldPos (un-normalized ok)
}
