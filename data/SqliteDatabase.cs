using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using FbGateway.Configuration;

namespace FbGateway.Data;

public class SqliteDatabase
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SqliteDatabase()
    {
        var directory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.CommonApplicationData),
                "FbGateway");

        Directory.CreateDirectory(directory);

        _databasePath =
            Path.Combine(directory, "fbgateway.db");

        _connectionString =
            $"Data Source={_databasePath}";

        Initialize();
    }

    private SqliteConnection OpenConnection()
    {
        var connection =
            new SqliteConnection(_connectionString);

        connection.Open();

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

            if (!reader.IsDBNull(9) &&
                DateTime.TryParse(
                    reader.GetString(9),
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

    public void AddConnection(
        DatabaseConfig database)
    {
        if (ConnectionExists(database))
        {
            throw new InvalidOperationException(
                "This database connection already exists.");
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

        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex)
            when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                "Another database connection with the same connection details already exists.",
                ex);
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
    public DatabaseConfig? FindConnection(string identifier)
    {
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

        foreach (var connection in connections)
        {
            if (string.Equals(
                    connection.Name,
                    trimmed,
                    StringComparison.OrdinalIgnoreCase))
            {
                return connection;
            }
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

        command.CommandText = """
            INSERT INTO Settings (SettingKey, SettingValue)
            VALUES ($key, $value)
            ON CONFLICT(SettingKey) DO UPDATE
                SET SettingValue = excluded.SettingValue;
            """;

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

        if (!string.IsNullOrWhiteSpace(host))
        {
            config.Host = host;
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

    public void SaveGatewayConfig(GatewayConfig config)
    {
        SetSetting("Gateway.Host", config.Host);

        SetSetting(
            "Gateway.Port",
            config.Port.ToString());

        SetSetting(
            "Gateway.MaxRows",
            config.MaxRows.ToString());

        SetSetting(
            "Gateway.CommandTimeoutSeconds",
            config.CommandTimeoutSeconds.ToString());

        SetSetting(
            "Gateway.AutoStart",
            config.AutoStart ? "1" : "0");

        SetSetting("Gateway.ApiKey", config.ApiKey);
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
