using System;
using System.Security.Cryptography;
using System.Text;
using FbGateway.Configuration;

namespace FbGateway.Gateway;

/*
 * Manages OAuth sessions for the gateway.
 *
 * After a user authenticates via Cloudflare Access, a session
 * is created and stored in the SQLite database. The session
 * is identified by a random token sent as a cookie.
 *
 * Sessions expire after the configured timeout and are
 * cleaned up on access.
 */
public sealed class OAuthSessionManager
{
    private const string SessionCookieName = "efs_session";

    private readonly OAuthConfig _config;
    private readonly Data.SqliteDatabase _database;

    public OAuthSessionManager(
        OAuthConfig config,
        Data.SqliteDatabase database)
    {
        _config = config;
        _database = database;
    }

    public bool Enabled => _config.Enabled;

    /*
     * Creates a new session for an authenticated user and
     * returns the session token.
     */
    public string CreateSession(string userEmail)
    {
        var token = GenerateSessionToken();

        var expiresAt = DateTime.UtcNow.AddMinutes(
            _config.SessionTimeoutMinutes);

        _database.CreateSession(token, userEmail, expiresAt);

        return token;
    }

    /*
     * Validates a session token and returns the user email
     * if valid, or null if expired/invalid.
     */
    public string? ValidateSession(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var session = _database.GetSession(token);

        if (session == null)
        {
            return null;
        }

        if (session.ExpiresAt < DateTime.UtcNow)
        {
            _database.DeleteSession(token);
            return null;
        }

        return session.UserEmail;
    }

    /*
     * Revokes a session (logout).
     */
    public void RevokeSession(string token)
    {
        _database.DeleteSession(token);
    }

    /*
     * Generates a cryptographically random session token.
     */
    private static string GenerateSessionToken()
    {
        return Convert
            .ToHexString(RandomNumberGenerator.GetBytes(32))
            .ToLowerInvariant();
    }

    /*
     * Cookie helpers.
     */

    public static string FormatCookie(
        string token,
        int timeoutMinutes)
    {
        var expires = DateTime.UtcNow
            .AddMinutes(timeoutMinutes)
            .ToString("R");

        return $"{SessionCookieName}={token}; " +
            $"Path=/; " +
            $"HttpOnly; " +
            $"SameSite=Lax; " +
            $"Expires={expires}";
    }

    public static string ClearCookie()
    {
        return $"{SessionCookieName}=; " +
            "Path=/; " +
            "HttpOnly; " +
            "SameSite=Lax; " +
            "Expires=Thu, 01 Jan 1970 00:00:00 GMT";
    }

    public static string? ExtractTokenFromCookie(
        string? cookieHeader)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return null;
        }

        var cookies = cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var cookie in cookies)
        {
            var trimmed = cookie.Trim();

            if (trimmed.StartsWith(
                    SessionCookieName + "=",
                    StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(SessionCookieName.Length + 1)..].Trim();
            }
        }

        return null;
    }
}

/*
 * Represents a stored session.
 */
public class Session
{
    public string Token { get; set; } = string.Empty;

    public string UserEmail { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }
}
