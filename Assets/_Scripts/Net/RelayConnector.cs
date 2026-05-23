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
    public NetState State { get; } = new();   // logic writes this; RelayTestHUD reads it

    void SetStatus(string s) { State.status = s; GameLog.Post(OutputType.System, s); }   // surface progress in chat

    UnityTransport transport;

    // NetworkManager/UnityTransport/RelayConnector/RelayTestHUD now live on a scene "Netcode"
    // GameObject (see SceneNetcodeSetupTool); the runtime AutoBoot was removed when we moved to a
    // scene-placed NetworkManager so the Player prefab can be assigned in the Inspector.
    void Awake() => transport = GetComponent<UnityTransport>();

    // Host: allocate a Relay server, point the transport at it, start as host. Returns/exposes join code.
    public async Task HostAsync(int maxConnections = 3)
    {
        try
        {
            SetStatus("hosting…");
            await InitAndSignIn();
            Allocation alloc = await RelayService.Instance.CreateAllocationAsync(maxConnections);
            State.joinCode = await RelayService.Instance.GetJoinCodeAsync(alloc.AllocationId);
            transport.SetRelayServerData(new RelayServerData(alloc, "dtls"));
            NetworkManager.Singleton.StartHost();
            SetStatus($"host up · code {State.joinCode}");
        }
        catch (System.Exception e) { SetStatus("host failed: " + e.Message); Debug.LogException(e); }
    }

    // Client: resolve the join code to a Relay allocation, point transport at it, start as client.
    public async Task JoinAsync(string joinCode)
    {
        try
        {
            SetStatus("joining…");
            await InitAndSignIn();
            JoinAllocation alloc = await RelayService.Instance.JoinAllocationAsync(joinCode);
            transport.SetRelayServerData(new RelayServerData(alloc, "dtls"));
            NetworkManager.Singleton.StartClient();
            SetStatus($"client → {joinCode}");
        }
        catch (System.Exception e) { SetStatus("join failed: " + e.Message); Debug.LogException(e); }
    }

    static async Task InitAndSignIn()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
            await UnityServices.InitializeAsync();
        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
    }
}
