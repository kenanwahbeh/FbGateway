namespace FbGateway.Configuration;

/*
 * Configuration for Cloudflare Access OAuth integration.
 *
 * The gateway validates JWT tokens issued by Cloudflare Access
 * when users authenticate through the configured identity
 * providers (GitHub, Google, One-time PIN, etc.).
 *
 * Cloudflare handles the identity provider logic; the gateway
 * only validates the JWT and extracts user information.
 */
public class OAuthConfig
{
    /*
     * When enabled, the gateway requires a valid Cloudflare
     * Access session for all endpoints except /health and
     * /auth/*.
     */
    public bool Enabled { get; set; }

    /*
     * The Cloudflare Access team domain, e.g.,
     * "my-team.cloudflareaccess.com".
     *
     * Used to construct the issuer claim for JWT validation.
     */
    public string TeamDomain { get; set; } = string.Empty;

    /*
     * The audience tag from the Access application.
     *
     * Found in Zero Trust → Access → Applications →
     * Settings → Application Audience (AUD) tag.
     */
    public string Audience { get; set; } = string.Empty;

    /*
     * Cloudflare's JWKS endpoint for signature verification.
     *
     * Typically:
     * "https://<team-domain>/cdn-cgi/access/certs"
     */
    public string JwksUri { get; set; } = string.Empty;

    /*
     * How long a session remains valid after login.
     */
    public int SessionTimeoutMinutes { get; set; } = 60;

    /*
     * The OAuth redirect URI for the login callback.
     *
     * This must be registered in the Access application's
     * allowed redirect URIs.
     */
    public string RedirectUri { get; set; } = string.Empty;

    public string Issuer =>
        $"https://{TeamDomain}";

    public string JwksUrl =>
        JwksUri
        ?? $"https://{TeamDomain}/cdn-cgi/access/certs";
}
