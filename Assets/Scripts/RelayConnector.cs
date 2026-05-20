using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;

// Netcode bootstrap + Relay handshake (LOGIC). Client-hosted: one player hosts via a Relay
// allocation and shares a join code; others join with it. Grid stays un-networked (static geometry).
public class RelayConnector : MonoBehaviour
{
    public string JoinCode { get; private set; }
    public string Status { get; private set; } = "offline";

    UnityTransport transport;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBoot()
    {
        if (NetworkManager.Singleton != null) return;
        var go = new GameObject("Netcode");
        DontDestroyOnLoad(go);
        var nm = go.AddComponent<NetworkManager>();
        var utp = go.AddComponent<UnityTransport>();
        nm.NetworkConfig = new NetworkConfig { NetworkTransport = utp };
        go.AddComponent<RelayConnector>();
        go.AddComponent<RelayTestHUD>();
    }

    void Awake() => transport = NetworkManager.Singleton.GetComponent<UnityTransport>();

    // Host: allocate a Relay server, point the transport at it, start as host. Returns/exposes join code.
    public async Task HostAsync(int maxConnections = 3)
    {
        try
        {
            Status = "hosting…";
            await InitAndSignIn();
            Allocation alloc = await RelayService.Instance.CreateAllocationAsync(maxConnections);
            JoinCode = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);
            transport.SetRelayServerData(new RelayServerData(alloc, "dtls"));
            NetworkManager.Singleton.StartHost();
            Status = $"host up · code {JoinCode}";
        }
        catch (System.Exception e) { Status = "host failed: " + e.Message; Debug.LogException(e); }
    }

    // Client: resolve the join code to a Relay allocation, point transport at it, start as client.
    public async Task JoinAsync(string joinCode)
    {
        try
        {
            Status = "joining…";
            await InitAndSignIn();
            JoinAllocation alloc = await RelayService.Instance.JoinAllocationAsync(joinCode);
            transport.SetRelayServerData(new RelayServerData(alloc, "dtls"));
            NetworkManager.Singleton.StartClient();
            Status = $"client → {joinCode}";
        }
        catch (System.Exception e) { Status = "join failed: " + e.Message; Debug.LogException(e); }
    }

    static async Task InitAndSignIn()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
            await UnityServices.InitializeAsync();
        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }
}
