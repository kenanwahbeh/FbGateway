namespace FbGateway.Configuration;

public class GatewayConfig
{
    /*
     * The gateway deliberately binds to loopback only.
     *
     * cloudflared runs on this same machine and reaches the
     * gateway over 127.0.0.1, so there is no reason to expose
     * the listener on the LAN. Binding to a non-loopback
     * address on Windows would also require an HTTP.SYS URL
     * reservation (netsh http add urlacl) or elevation.
     */
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 8080;

    /*
     * Required on every endpoint except /health.
     *
     * Generated on first run and stored in the local SQLite
     * file. The tunnel makes this listener reachable from the
     * public internet, so the key is the only thing standing
     * between the internet and the Firebird databases.
     */
    public string ApiKey { get; set; } = string.Empty;

    public bool AutoStart { get; set; } = true;

    /*
     * Caps the number of rows a single /query may return so a
     * careless SELECT cannot pull an entire table through the
     * tunnel.
     */
    public int MaxRows { get; set; } = 1000;

    public int CommandTimeoutSeconds { get; set; } = 30;

    public string BaseUrl =>
        $"http://{Host}:{Port}";

    public string Prefix =>
        $"http://{Host}:{Port}/";
}
