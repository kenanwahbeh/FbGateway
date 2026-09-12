using System;

namespace ByteBridge.Configuration;

public class DatabaseConfig
{
    public string Id { get; set; } =
        Guid.NewGuid().ToString();

    public string Name { get; set; } =
        string.Empty;

    public string Server { get; set; } =
        "localhost";

    public int Port { get; set; } =
        3050;

    public string Username { get; set; } =
        "SYSDBA";

    public string Password { get; set; } =
        string.Empty;

    public string Database { get; set; } =
        string.Empty;

    public bool Enabled { get; set; } =
        true;

    public bool LastTestSuccessful { get; set; }

    public DateTime? LastTestedAt { get; set; }

    /*
     * Identifies the actual database connection.
     *
     * Name is intentionally NOT included.
     * Password is intentionally NOT included.
     */
    public string ConnectionKey =>
        $"{Server.Trim().ToLowerInvariant()}|" +
        $"{Port}|" +
        $"{Username.Trim().ToLowerInvariant()}|" +
        $"{Database.Trim().ToLowerInvariant()}";
}