using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Editor utility: converts the old runtime Netcode bootstrap into a scene-placed NetworkManager.
// Creates (or reuses) a "Netcode" GameObject in the active scene with NetworkManager + UnityTransport
// + RelayConnector + RelayTestHUD, and assigns the authored Player prefab as the NetworkManager's
// PlayerPrefab so the server spawns and replicates it. Run via Tools > Minifantasy > Setup Scene Netcode.
public static class SceneNetcodeSetupTool
{
    [MenuItem("Tools/Minifantasy/Setup Scene Netcode")]
    public static void Setup()
    {
        var scene = EditorSceneManager.GetActiveScene();

        var nm = Object.FindFirstObjectByType<NetworkManager>();
        GameObject go = nm != null ? nm.gameObject : new GameObject("Netcode");
        if (nm == null) nm = go.AddComponent<NetworkManager>();

        var utp = go.GetComponent<UnityTransport>();
        if (utp == null) utp = go.AddComponent<UnityTransport>();

        if (nm.NetworkConfig == null) nm.NetworkConfig = new NetworkConfig();
        nm.NetworkConfig.NetworkTransport = utp;

        var player = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Player.prefab");
        nm.NetworkConfig.PlayerPrefab = player;

        if (go.GetComponent<RelayConnector>() == null) go.AddComponent<RelayConnector>();
        if (go.GetComponent<RelayTestHUD>() == null) go.AddComponent<RelayTestHUD>();

        EditorUtility.SetDirty(nm);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[SceneNetcode] '{go.name}' ready — player set: {player != null}, transport set: {utp != null}. Scene saved.");
    }
}
