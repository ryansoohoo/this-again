// Data: networking status surfaced to the HUD. Logic (RelayConnector) writes it; visual (RelayTestHUD) reads
// it. Plain data — no behavior.
public sealed class NetState
{
    public string status = "offline";
    public string joinCode = "";
}
