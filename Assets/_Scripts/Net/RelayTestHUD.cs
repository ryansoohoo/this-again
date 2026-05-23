using UnityEngine;

// TEMPORARY debug HUD to test Relay host/join. Manual IMGUI (no GUILayout) + cached labels so it
// allocates almost nothing per frame.
public class RelayTestHUD : MonoBehaviour
{
    RelayConnector net;
    string codeInput = "";
    readonly GUIContent host = new GUIContent("Host");
    readonly GUIContent join = new GUIContent("Join");
    readonly GUIContent status = new GUIContent("");
    readonly GUIContent share = new GUIContent("");
    string lastStatus, lastCode;

    void Awake() => net = GetComponent<RelayConnector>();

    void OnGUI()
    {
        if (net.State.status != lastStatus) { lastStatus = net.State.status; status.text = "Net: " + lastStatus; }
        if (net.State.joinCode != lastCode) { lastCode = net.State.joinCode; share.text = "Share code: " + lastCode; }
        bool hasCode = !string.IsNullOrEmpty(net.State.joinCode);

        GUI.Box(new Rect(10, 10, 280, hasCode ? 134 : 108), GUIContent.none);
        GUI.Label(new Rect(20, 16, 260, 20), status);
        if (GUI.Button(new Rect(20, 42, 120, 26), host)) _ = net.HostAsync();
        codeInput = GUI.TextField(new Rect(20, 74, 140, 26), codeInput);
        if (GUI.Button(new Rect(168, 74, 100, 26), join)) _ = net.JoinAsync(codeInput.Trim().ToUpper());
        if (hasCode) GUI.Label(new Rect(20, 106, 260, 20), share);
    }
}
