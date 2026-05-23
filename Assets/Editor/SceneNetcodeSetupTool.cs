using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Editor utility for the hand-rolled, interest-managed replication setup. Builds:
//  - a "Netcode" GameObject (NetworkManager + UnityTransport + RelayConnector + RelayTestHUD) with
//    PlayerPrefab = null (NGO auto-spawn is OFF; there is no per-player NetworkObject), and
//  - a "Replication" GameObject (the single NetworkObject) carrying ReplicationHub + GhostManager + LocalPlayer,
//    with GhostManager.ghostPrefab wired to Ghost.prefab.
// Also converts Player.prefab into a visual-only Ghost.prefab (strips NetworkObject/NetworkTransform/
// PlayerMovement) the first time it runs. Idempotent. Run via Tools > Minifantasy > Setup Scene Netcode.
public static class SceneNetcodeSetupTool
{
    const string PlayerPath = "Assets/_Prefabs/Player.prefab";
    const string GhostPath  = "Assets/_Prefabs/Ghost.prefab";

    [MenuItem("Tools/Minifantasy/Setup Scene Netcode")]
    public static void Setup()
    {
        var scene = EditorSceneManager.GetActiveScene();

        // 1) Ghost.prefab = Player.prefab with the network components stripped (visual-only).
        var ghost = AssetDatabase.LoadAssetAtPath<GameObject>(GhostPath);
        if (ghost == null && AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPath) != null)
        {
            AssetDatabase.CopyAsset(PlayerPath, GhostPath);
            AssetDatabase.ImportAsset(GhostPath);
            var root = PrefabUtility.LoadPrefabContents(GhostPath);
            var nt = root.GetComponent<NetworkTransform>(); if (nt != null) Object.DestroyImmediate(nt, true);
            var pm = root.GetComponent("PlayerMovement");   if (pm != null) Object.DestroyImmediate(pm, true);
            var no = root.GetComponent<NetworkObject>();     if (no != null) Object.DestroyImmediate(no, true);
            PrefabUtility.SaveAsPrefabAsset(root, GhostPath);
            PrefabUtility.UnloadPrefabContents(root);
            AssetDatabase.Refresh();
            ghost = AssetDatabase.LoadAssetAtPath<GameObject>(GhostPath);
        }

        // 2) Netcode object: NetworkManager + transport, auto-spawn OFF.
        var nm = Object.FindFirstObjectByType<NetworkManager>();
        GameObject go = nm != null ? nm.gameObject : new GameObject("Netcode");
        if (nm == null) nm = go.AddComponent<NetworkManager>();

        var utp = go.GetComponent<UnityTransport>();
        if (utp == null) utp = go.AddComponent<UnityTransport>();

        if (nm.NetworkConfig == null) nm.NetworkConfig = new NetworkConfig();
        nm.NetworkConfig.NetworkTransport = utp;
        nm.NetworkConfig.PlayerPrefab = null;   // hand-rolled replication: no auto-spawned player object

        if (go.GetComponent<RelayConnector>() == null) go.AddComponent<RelayConnector>();
        if (go.GetComponent<RelayTestHUD>() == null) go.AddComponent<RelayTestHUD>();

        // 3) Replication object: the single NetworkObject + the ghost/local-player components, pre-placed.
        var repl = GameObject.Find("Replication");
        if (repl == null) repl = new GameObject("Replication");
        if (repl.GetComponent<NetworkObject>() == null) repl.AddComponent<NetworkObject>();
        if (repl.GetComponent<ReplicationHub>() == null) repl.AddComponent<ReplicationHub>();
        if (repl.GetComponent<GhostManager>() == null) repl.AddComponent<GhostManager>();
        if (repl.GetComponent<LocalPlayer>() == null) repl.AddComponent<LocalPlayer>();

        var so = new SerializedObject(repl.GetComponent<GhostManager>());
        so.FindProperty("ghostPrefab").objectReferenceValue = ghost;
        so.ApplyModifiedPropertiesWithoutUndo();

        EditorUtility.SetDirty(nm);
        EditorUtility.SetDirty(repl);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[SceneNetcode] 'Netcode' + 'Replication' ready — PlayerPrefab null, ghost set: {ghost != null}, transport set: {utp != null}. Scene saved.");
    }
}
