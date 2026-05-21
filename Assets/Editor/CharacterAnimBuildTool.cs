using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Editor utility: builds the player's DIRECTIONAL locomotion AnimatorController + clips from the
// sliced Minifantasy frames. Each sheet's 4 rows are facings (top -> bottom: SE, SW, NE, NW); each
// clip drives the "Body" and "Shadow" child SpriteRenderers in sync. The controller has Idle/Walk
// 2D blend trees selected by DirX/DirY, with Speed switching idle<->walk. Idle/Walk run at 5 fps
// (200ms/frame per AnimationInfo). Run via Tools > Minifantasy > Build Player Animator.
public static class CharacterAnimBuildTool
{
    const string Base = "Assets/Minifantasy_Dungeon_v2.3_Commercial_Version/Minifantasy_Dungeon_v2.3_Commercial_Version/Minifantasy_Dungeon_Assets/Characters/Human/";
    const string AnimDir = "Assets/Animations";
    const int Rows = 4;                                                   // facings per sheet
    static readonly string[] Dir = { "SE", "SW", "NE", "NW" };           // top -> bottom rows
    static readonly Vector2[] DirPos = { new(1f, -1f), new(-1f, -1f), new(1f, 1f), new(-1f, 1f) };

    [MenuItem("Tools/Minifantasy/Build Player Animator")]
    public static void Build()
    {
        if (!AssetDatabase.IsValidFolder(AnimDir)) AssetDatabase.CreateFolder("Assets", "Animations");

        // Remove the older non-directional clips if they exist.
        AssetDatabase.DeleteAsset(AnimDir + "/Player_Idle.anim");
        AssetDatabase.DeleteAsset(AnimDir + "/Player_Walk.anim");

        var idle = BuildDirClips(Base + "Idle.png", Base + "_Shadows/Idle.png", 5f, "Idle");
        var walk = BuildDirClips(Base + "Walk.png", Base + "_Shadows/Walk.png", 5f, "Walk");
        if (idle == null || walk == null) { Debug.LogError("[AnimBuild] aborted: missing clips."); return; }

        BuildController(AnimDir + "/Player.controller", idle, walk);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[AnimBuild] Directional Player.controller + 4x Idle/Walk clips built.");
    }

    // Splits a sheet's frames into Rows facings and builds one looping clip per facing.
    static AnimationClip[] BuildDirClips(string bodySheet, string shadowSheet, float fps, string prefix)
    {
        var body = LoadFrames(bodySheet);
        var shadow = LoadFrames(shadowSheet);
        if (body.Length == 0 || body.Length % Rows != 0)
        {
            Debug.LogError($"[AnimBuild] {bodySheet}: frame count {body.Length} is not a multiple of {Rows}.");
            return null;
        }
        int cols = body.Length / Rows;
        bool shadowOk = shadow.Length == body.Length;

        var clips = new AnimationClip[Rows];
        for (int d = 0; d < Rows; d++)
        {
            var clip = new AnimationClip { frameRate = fps };
            SetSpriteCurve(clip, "Body", Sub(body, d * cols, cols), fps);
            if (shadowOk) SetSpriteCurve(clip, "Shadow", Sub(shadow, d * cols, cols), fps);

            var s = AnimationUtility.GetAnimationClipSettings(clip);
            s.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, s);

            string path = $"{AnimDir}/Player_{prefix}_{Dir[d]}.anim";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(clip, path);
            clips[d] = clip;
        }
        return clips;
    }

    // Step (PPtr) curve binding child SpriteRenderer.sprite to the frame sequence; a terminator
    // keyframe at frames.Length/fps gives the final frame its full duration before the loop wraps.
    static void SetSpriteCurve(AnimationClip clip, string childPath, Sprite[] frames, float fps)
    {
        var binding = EditorCurveBinding.PPtrCurve(childPath, typeof(SpriteRenderer), "m_Sprite");
        var keys = new ObjectReferenceKeyframe[frames.Length + 1];
        for (int i = 0; i < frames.Length; i++)
            keys[i] = new ObjectReferenceKeyframe { time = i / fps, value = frames[i] };
        keys[frames.Length] = new ObjectReferenceKeyframe { time = frames.Length / fps, value = frames[frames.Length - 1] };
        AnimationUtility.SetObjectReferenceCurve(clip, binding, keys);
    }

    static void BuildController(string controllerPath, AnimationClip[] idle, AnimationClip[] walk)
    {
        AssetDatabase.DeleteAsset(controllerPath);
        var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        controller.AddParameter("DirX", AnimatorControllerParameterType.Float);
        controller.AddParameter("DirY", AnimatorControllerParameterType.Float);

        var sm = controller.layers[0].stateMachine;
        var idleState = sm.AddState("Idle");
        idleState.motion = BuildDirTree(controller, "IdleBlend", idle);
        var walkState = sm.AddState("Walk");
        walkState.motion = BuildDirTree(controller, "WalkBlend", walk);
        sm.defaultState = idleState;

        var toWalk = idleState.AddTransition(walkState);
        toWalk.hasExitTime = false; toWalk.duration = 0f;
        toWalk.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");

        var toIdle = walkState.AddTransition(idleState);
        toIdle.hasExitTime = false; toIdle.duration = 0f;
        toIdle.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");
    }

    static BlendTree BuildDirTree(AnimatorController controller, string name, AnimationClip[] dirClips)
    {
        var tree = new BlendTree
        {
            name = name,
            blendType = BlendTreeType.SimpleDirectional2D,
            blendParameter = "DirX",
            blendParameterY = "DirY",
            hideFlags = HideFlags.HideInHierarchy,
        };
        AssetDatabase.AddObjectToAsset(tree, controller);
        for (int d = 0; d < Rows; d++)
            tree.AddChild(dirClips[d], DirPos[d]);
        return tree;
    }

    static Sprite[] Sub(Sprite[] arr, int start, int len)
    {
        var r = new Sprite[len];
        System.Array.Copy(arr, start, r, 0, len);
        return r;
    }

    static Sprite[] LoadFrames(string sheetPath)
    {
        var all = AssetDatabase.LoadAllAssetsAtPath(sheetPath);
        var sprites = new List<Sprite>();
        foreach (var o in all) if (o is Sprite s) sprites.Add(s);
        sprites.Sort((a, b) => FrameIndex(a.name).CompareTo(FrameIndex(b.name)));
        return sprites.ToArray();
    }

    static int FrameIndex(string spriteName)
    {
        int u = spriteName.LastIndexOf('_');
        if (u >= 0 && int.TryParse(spriteName.Substring(u + 1), out int v)) return v;
        return 0;
    }
}
