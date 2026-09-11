using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using FbGateway.Configuration;

namespace FbGateway.Data;

public class SqliteDatabase
{
    private const string UpsertSetting = """
        INSERT INTO Settings (SettingKey, SettingValue)
        VALUES ($key, $value)
        ON CONFLICT(SettingKey) DO UPDATE
            SET SettingValue = excluded.SettingValue;
        """;

    private readonly string _databasePath;
    private readonly string _connectionString;

    /*
     * The folder holding the settings file and the request log.
     */
    public string DataDirectory { get; }

    /*
     * Where the request log is kept, beside the settings file so both
     * live under one folder an operator can find and back up.
     */
    public string LogDirectory { get; }

    /*
     * Why the folder could not be locked down, when it could not. Null
     * when it was, and whenever there was nothing to lock down. The
     * service and the command line report it; nothing refuses to run
     * over it.
     */
    public Exception? PermissionsError { get; }

    public SqliteDatabase()
        : this(null)
    {
    }

    /*
     * dataRoot replaces the folder the app stores under, which is
     * CommonApplicationData in a real install. Tests pass a temporary
     * directory so they never touch a machine's real settings, and so
     * the legacy path the migration reads from can be set up too.
     */
    public SqliteDatabase(string? dataRoot)
    {
        var commonData =
            dataRoot
            ?? Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);

        var directory =
            Path.Combine(commonData, "EasyFbSoft");

        Directory.CreateDirectory(directory);

        /*
         * Locked down before anything is written into it. The control
         * panel and the command line can each be the first to create
         * this folder, and the first thing either does is mint an API
         * key into it -- the command line may write a Firebird password
         * too -- so protecting it only when the service started left
         * that file readable by every account until then, and for good
         * on a machine where the service never ran.
         *
         * Only for a real install: a test passes its own temporary root,
         * and has no business changing the permissions on it.
         */
        if (dataRoot == null && OperatingSystem.IsWindows())
        {
            PermissionsError = DataFolderSecurity.Ensure(directory);
        }

        DataDirectory = directory;

        _databasePath =
            Path.Combine(directory, "easyfbsoft.db");

        LogDirectory = Path.Combine(directory, "logs");

        MigrateLegacyDatabase(commonData);

        _connectionString =
            $"Data Source={_databasePath}";

        Initialize();
    }

    /*
     * Carries over the settings file from the name the app shipped
     * under before it became Easy FB Soft.
     *
     * The old file is copied rather than moved, so it stays behind as
     * a backup and a failed copy cannot lose the only copy of
     * someone's connections. Runs only when there is no current file,
     * which makes it a no-op on every later start.
     */
    private void MigrateLegacyDatabase(string commonData)
    {
        if (File.Exists(_databasePath))
        {
            return;
        }

        var legacy =
            Path.Combine(commonData, "FbGateway", "fbgateway.db");

        if (!File.Exists(legacy))
        {
            return;
        }

        /*
         * Copied under a temporary name and moved into place only once
         * the copy has finished. File.Copy is not atomic, so a copy that
         * failed part-way straight onto the real name left a truncated
         * file there: opening it failed or came back missing rows, and
         * the File.Exists check above took it for a finished migration
         * on every later start.
         */
        var partial = _databasePath + ".migrating";

        try
        {
            File.Delete(partial);

            File.Copy(legacy, partial);

            /*
             * File.Copy carries the source's attributes across, so a
             * legacy file that had been marked read-only -- restored
             * from a backup, or copied off a share -- would arrive
             * read-only and make every later write fail with
             * "attempt to write a readonly database".
             */
            var copied = new FileInfo(partial);

            if (copied.IsReadOnly)
            {
                copied.IsReadOnly = false;
            }

            File.Move(partial, _databasePath);
        }
        catch (IOException)
        {
            // Start with an empty database rather than failing to open.
            TryDelete(partial);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(partial);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /*
     * Two processes share this file now: the service that hosts the
     * gateway, and the control panel that configures it. That is what
     * the pragmas below are for.
     *
     * WAL lets the service keep reading while the control panel writes,
     * instead of the two locking each other out. It is stored in the
     * database header, so setting it here is a one-off that later opens
     * simply confirm.
     *
     * busy_timeout makes SQLite itself wait for a held write lock rather
     * than leaving Microsoft.Data.Sqlite to poll for it. Measured, not
     * assumed: with the pragma left at SQLite's default of 0 a contended
     * write still succeeds, because the provider's own Default Timeout
     * of 30 seconds is what supplies the waiting. So this is set to
     * match that 30 seconds and not below it -- an earlier attempt used
     * 5 seconds, which would have been a quieter cap than the provider
     * already gives, not the improvement it looked like.
     *
     * synchronous=NORMAL is the documented companion to WAL: still
     * crash-safe, without an fsync on every commit.
     */
    private SqliteConnection OpenConnection()
    {
        var connection =
            new SqliteConnection(_connectionString);

        connection.Open();

        using var pragma = connection.CreateCommand();

        pragma.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=30000;
            PRAGMA synchronous=NORMAL;
            """;

        pragma.ExecuteNonQuery();

        return connection;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Databases
            (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                Server TEXT NOT NULL,
                Port INTEGER NOT NULL,
                Username TEXT NOT NULL,
                Password TEXT NOT NULL,
                DatabaseValue TEXT NOT NULL,
                Enabled INTEGER NOT NULL DEFAULT 1,
                LastTestSuccessful INTEGER NOT NULL DEFAULT 0,
                LastTestedAt TEXT NULL,
                ConnectionKey TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS Settings
            (
                SettingKey TEXT NOT NULL PRIMARY KEY,
                SettingValue TEXT NOT NULL
            );
            """;

        command.ExecuteNonQuery();
    }

    public List<DatabaseConfig> GetConnections()
    {
        var result = new List<DatabaseConfig>();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT
                Id,
                Name,
                Server,
                Port,
                Username,
                Password,
                DatabaseValue,
                Enabled,
                LastTestSuccessful,
                LastTestedAt
            FROM Databases
            ORDER BY Name COLLATE NOCASE;
            """;

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            DateTime? testedAt = null;

            /*
             * Written with the round-trip "O" format, which is
             * culture-invariant and always Gregorian, so it is read back
             * the same way. Read with the machine's culture instead, the
             * year was taken in that culture's calendar, which shifts it
             * or rejects it outright, and a rejected time was then lost
             * for good on the next save.
             */
            if (!reader.IsDBNull(9) &&
                DateTime.TryParse(
                    reader.GetString(9),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedDate))
            {
                testedAt = parsedDate;
            }

            result.Add(
                new DatabaseConfig
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    Server = reader.GetString(2),
                    Port = reader.GetInt32(3),
                    Username = reader.GetString(4),
                    Password = reader.GetString(5),
                    Database = reader.GetString(6),
                    Enabled = reader.GetInt32(7) == 1,
                    LastTestSuccessful = reader.GetInt32(8) == 1,
                    LastTestedAt = testedAt
                });
        }

        return result;
    }

    public bool ConnectionExists(
        DatabaseConfig database,
        string? excludeId = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT COUNT(*)
            FROM Databases
            WHERE ConnectionKey = $key
              AND ($excludeId IS NULL OR Id <> $excludeId);
            """;

        command.Parameters.AddWithValue(
            "$key",
            database.ConnectionKey);

        command.Parameters.AddWithValue(
            "$excludeId",
            (object?)excludeId ?? DBNull.Value);

        return Convert.ToInt32(
            command.ExecuteScalar()) > 0;
    }

    /*
     * Requests address a connection by its name, so two connections
     * sharing one name left "database": "Sales" meaning whichever of
     * them happened to sort first. Compared the way FindConnection
     * compares, so any two names this lets through are two names a
     * request can tell apart.
     */
    private bool NameInUse(string name, string? excludeId)
    {
        var wanted = name.Trim();

        foreach (var existing in GetConnections())
        {
            if (excludeId != null &&
                string.Equals(
                    existing.Id,
                    excludeId,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(
                    existing.Name.Trim(),
                    wanted,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NameInUseMessage(string name) =>
        $"Another database connection is already named \"{name.Trim()}\". " +
        "Requests pick a connection by its name, so each one needs its own.";

    public void AddConnection(
        DatabaseConfig database)
    {
        if (ConnectionExists(database))
        {
            throw new InvalidOperationException(
                "This database connection already exists.");
        }

        if (NameInUse(database.Name, excludeId: null))
        {
            throw new InvalidOperationException(
                NameInUseMessage(database.Name));
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO Databases
            (
                Id,
                Name,
                Server,
                Port,
                Username,
                Password,
                DatabaseValue,
                Enabled,
                LastTestSuccessful,
                LastTestedAt,
                ConnectionKey
            )
            VALUES
            (
                $id,
                $name,
                $server,
                $port,
                $username,
                $password,
                $database,
                $enabled,
                $tested,
                $testedAt,
                $key
            );
            """;

        AddParameters(command, database);

        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
            when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                "This database connection already exists.",
                ex);
        }
    }

    public void UpdateConnection(
        DatabaseConfig database)
    {
        if (ConnectionExists(
                database,
                database.Id))
        {
            throw new InvalidOperationException(
                "Another database connection with the same connection details already exists.");
        }

        if (NameInUse(database.Name, database.Id))
        {
            throw new InvalidOperationException(
                NameInUseMessage(database.Name));
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE Databases
            SET
                Name = $name,
                Server = $server,
                Port = $port,
                Username = $username,
                Password = $password,
                DatabaseValue = $database,
                Enabled = $enabled,
                LastTestSuccessful = $tested,
                LastTestedAt = $testedAt,
                ConnectionKey = $key
            WHERE Id = $id;
            """;

        AddParameters(command, database);

        int affected;

        try
        {
            affected = command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
            when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                "Another database connection with the same connection details already exists.",
                ex);
        }

        /*
         * Nothing matched: the connection was removed, from the command
         * line or another window, while this edit was open. Reported
         * rather than swallowed, or the edit would vanish while looking
         * saved.
         */
        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"\"{database.Name}\" no longer exists. It may have been " +
                "removed from another window or from the command line.");
        }
    }

    public void DeleteConnection(string id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            DELETE FROM Databases
            WHERE Id = $id;
            """;

        command.Parameters.AddWithValue("$id", id);

        command.ExecuteNonQuery();
    }

    public void SetEnabled(
        string id,
        bool enabled)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE Databases
            SET Enabled = $enabled
            WHERE Id = $id;
            """;

        command.Parameters.AddWithValue(
            "$enabled",
            enabled ? 1 : 0);

        command.Parameters.AddWithValue(
            "$id",
            id);

        command.ExecuteNonQuery();
    }

    public void SetTestResult(
        string id,
        bool successful)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            UPDATE Databases
            SET
                LastTestSuccessful = $successful,
                LastTestedAt = $testedAt
            WHERE Id = $id;
            """;

        command.Parameters.AddWithValue(
            "$successful",
            successful ? 1 : 0);

        command.Parameters.AddWithValue(
            "$testedAt",
            DateTime.Now.ToString("O"));

        command.Parameters.AddWithValue(
            "$id",
            id);

        command.ExecuteNonQuery();
    }

    /*
     * Resolves the "database" field of an API request.
     *
     * Callers may address a connection either by its Id or by
     * its Name, because the Id is a GUID nobody wants to type
     * into a request body by hand.
     */
    public DatabaseConfig? FindConnection(string identifier) =>
        FindConnection(identifier, out _);

    /*
     * As above, and reports a name that belongs to more than one
     * connection instead of picking one of them.
     *
     * Names are unique now, but a settings file from before that was
     * enforced can still hold two connections under one name. Returning
     * whichever sorted first would have sent a request -- a write, even
     * -- to a database the caller never meant. So nothing is returned,
     * and sharedName holds the candidates, which lets a caller say why
     * and point at their ids instead.
     */
    public DatabaseConfig? FindConnection(
        string identifier,
        out IReadOnlyList<DatabaseConfig> sharedName)
    {
        sharedName = Array.Empty<DatabaseConfig>();

        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var trimmed = identifier.Trim();

        var connections = GetConnections();

        foreach (var connection in connections)
        {
            if (string.Equals(
                    connection.Id,
                    trimmed,
                    StringComparison.OrdinalIgnoreCase))
            {
                return connection;
            }
        }

        var matches = new List<DatabaseConfig>();

        foreach (var connection in connections)
        {
            if (string.Equals(
                    connection.Name.Trim(),
                    trimmed,
                    StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(connection);
            }
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            sharedName = matches;
        }

        return null;
    }

    public string? GetSetting(string key)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT SettingValue
            FROM Settings
            WHERE SettingKey = $key;
            """;

        command.Parameters.AddWithValue("$key", key);

        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = UpsertSetting;

        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);

        command.ExecuteNonQuery();
    }

    /*
     * Loads the gateway settings, filling in defaults for
     * anything that has never been saved.
     *
     * An API key is minted on first call so the gateway is
     * never reachable without one.
     */
    public GatewayConfig GetGatewayConfig()
    {
        var config = new GatewayConfig();

        var host = GetSetting("Gateway.Host");

        /*
         * Only a loopback address is taken from the file. Nothing in the
         * app writes anything else, but a value that got in regardless --
         * "+", "0.0.0.0", a LAN address -- used to be bound as it was,
         * and the service reserves whatever prefix it is refused, so the
         * gateway would have been served on every interface. The same
         * value also ends up on netsh's command line.
         */
        if (IsLoopbackHost(host))
        {
            config.Host = host.Trim();
        }

        if (int.TryParse(
                GetSetting("Gateway.Port"),
                out var port) &&
            port >= 1 &&
            port <= 65535)
        {
            config.Port = port;
        }

        if (int.TryParse(
                GetSetting("Gateway.MaxRows"),
                out var maxRows) &&
            maxRows > 0)
        {
            config.MaxRows = maxRows;
        }

        if (int.TryParse(
                GetSetting("Gateway.CommandTimeoutSeconds"),
                out var timeout) &&
            timeout > 0)
        {
            config.CommandTimeoutSeconds = timeout;
        }

        config.AutoStart =
            GetSetting("Gateway.AutoStart") != "0";

        var apiKey = GetSetting("Gateway.ApiKey");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = GenerateApiKey();

            SetSetting("Gateway.ApiKey", apiKey);
        }

        config.ApiKey = apiKey;

        return config;
    }

    private static bool IsLoopbackHost(
        [NotNullWhen(true)] string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var trimmed = host.Trim();

        if (string.Equals(
                trimmed,
                "localhost",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        /*
         * Canonical dotted IPv4 only. IPAddress.TryParse also accepts
         * "127.1" and a bare integer, which name a loopback address but
         * make a strange prefix, and an IPv6 address would need brackets
         * the prefix does not add.
         */
        return IPAddress.TryParse(trimmed, out var address)
            && address.AddressFamily == AddressFamily.InterNetwork
            && IPAddress.IsLoopback(address)
            && address.ToString() == trimmed;
    }

    /*
     * Writes the listener settings in one transaction, so a failure
     * part-way cannot leave a new port beside an old on/off switch for
     * the service to pick up.
     *
     * The API key is deliberately not among them. Every caller reads the
     * whole config, changes one field and saves it back, and the control
     * panel, the command line and the service all share this file, so a
     * key rotated between someone's read and their save used to be
     * written straight back over, undoing the rotation with nothing
     * reported. The key has its own writer, RegenerateApiKey.
     */
    public void SaveGatewayConfig(GatewayConfig config)
    {
        var settings = new[]
        {
            ("Gateway.Host", config.Host),

            ("Gateway.Port",
                config.Port.ToString(CultureInfo.InvariantCulture)),

            ("Gateway.MaxRows",
                config.MaxRows.ToString(CultureInfo.InvariantCulture)),

            ("Gateway.CommandTimeoutSeconds",
                config.CommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture)),

            ("Gateway.AutoStart", config.AutoStart ? "1" : "0")
        };

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        foreach (var (key, value) in settings)
        {
            using var command = connection.CreateCommand();

            command.Transaction = transaction;
            command.CommandText = UpsertSetting;

            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);

            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public string RegenerateApiKey()
    {
        var apiKey = GenerateApiKey();

        SetSetting("Gateway.ApiKey", apiKey);

        return apiKey;
    }

    private static string GenerateApiKey()
    {
        return Convert
            .ToHexString(
                RandomNumberGenerator.GetBytes(32))
            .ToLowerInvariant();
    }

    private static void AddParameters(
        SqliteCommand command,
        DatabaseConfig database)
    {
        command.Parameters.AddWithValue(
            "$id",
            database.Id);

        command.Parameters.AddWithValue(
            "$name",
            database.Name);

        command.Parameters.AddWithValue(
            "$server",
            database.Server);

        command.Parameters.AddWithValue(
            "$port",
            database.Port);

        command.Parameters.AddWithValue(
            "$username",
            database.Username);

        command.Parameters.AddWithValue(
            "$password",
            database.Password);

        command.Parameters.AddWithValue(
            "$database",
            database.Database);

        command.Parameters.AddWithValue(
            "$enabled",
            database.Enabled ? 1 : 0);

        command.Parameters.AddWithValue(
            "$tested",
            database.LastTestSuccessful ? 1 : 0);

        command.Parameters.AddWithValue(
            "$testedAt",
            database.LastTestedAt?.ToString("O")
                ?? (object)DBNull.Value);

        command.Parameters.AddWithValue(
            "$key",
            database.ConnectionKey);
    }
}
