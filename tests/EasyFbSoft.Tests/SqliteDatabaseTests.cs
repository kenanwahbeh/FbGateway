using Xunit;
using FbGateway.Configuration;
using FbGateway.Data;

namespace FbGateway.Tests;

public class SqliteDatabaseTests
{
    private static DatabaseConfig Sample(
        string name = "Sales",
        string server = "127.0.0.1",
        string database = "/data/sales.fdb") =>
        new()
        {
            Name = name,
            Server = server,
            Port = 3050,
            Username = "SYSDBA",
            Password = "secret",
            Database = database,
            Enabled = true
        };

    // ---- Storage -----------------------------------------------------

    [Fact]
    public void A_fresh_install_has_no_connections()
    {
        using var root = new TempDataRoot();

        Assert.Empty(root.OpenDatabase().GetConnections());
    }

    [Fact]
    public void A_connection_round_trips_through_storage()
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        database.AddConnection(Sample());

        var stored = Assert.Single(database.GetConnections());

        Assert.Equal("Sales", stored.Name);
        Assert.Equal("127.0.0.1", stored.Server);
        Assert.Equal(3050, stored.Port);
        Assert.Equal("SYSDBA", stored.Username);
        Assert.Equal("secret", stored.Password);
        Assert.Equal("/data/sales.fdb", stored.Database);
        Assert.True(stored.Enabled);
    }

    [Fact]
    public void Settings_survive_reopening_the_file()
    {
        using var root = new TempDataRoot();

        root.OpenDatabase().AddConnection(Sample());

        Assert.Single(root.OpenDatabase().GetConnections());
    }

    [Fact]
    public void The_same_connection_details_cannot_be_added_twice()
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        database.AddConnection(Sample());

        // Same server, port, user and database under a different name.
        var duplicate = Sample(name: "Sales Copy");

        Assert.Throws<InvalidOperationException>(
            () => database.AddConnection(duplicate));
    }

    [Fact]
    public void Two_different_databases_on_one_server_are_both_allowed()
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        database.AddConnection(Sample(name: "Sales", database: "/data/sales.fdb"));
        database.AddConnection(Sample(name: "Archive", database: "/data/archive.fdb"));

        Assert.Equal(2, database.GetConnections().Count);
    }

    [Fact]
    public void Deleting_a_connection_removes_it()
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        var connection = Sample();
        database.AddConnection(connection);
        database.DeleteConnection(connection.Id);

        Assert.Empty(database.GetConnections());
    }

    [Fact]
    public void Toggling_enabled_is_persisted()
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        var connection = Sample();
        database.AddConnection(connection);

        database.SetEnabled(connection.Id, false);

        Assert.False(database.GetConnections()[0].Enabled);
    }

    [Fact]
    public void A_test_result_records_the_outcome_and_the_time()
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        var connection = Sample();
        database.AddConnection(connection);

        database.SetTestResult(connection.Id, true);

        var stored = database.GetConnections()[0];

        Assert.True(stored.LastTestSuccessful);
        Assert.NotNull(stored.LastTestedAt);
    }

    // ---- Resolving by name or id -------------------------------------

    [Theory]
    [InlineData("Sales")]
    [InlineData("sales")]
    [InlineData("SALES")]
    public void A_connection_is_found_by_name_whatever_the_case(string lookup)
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        database.AddConnection(Sample());

        Assert.NotNull(database.FindConnection(lookup));
    }

    [Fact]
    public void A_connection_is_found_by_id()
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        var connection = Sample();
        database.AddConnection(connection);

        Assert.NotNull(database.FindConnection(connection.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ghost")]
    public void An_unknown_or_blank_lookup_finds_nothing(string lookup)
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        database.AddConnection(Sample());

        Assert.Null(database.FindConnection(lookup));
    }

    // ---- Gateway settings --------------------------------------------

    [Fact]
    public void A_fresh_install_mints_an_api_key()
    {
        using var root = new TempDataRoot();

        var key = root.OpenDatabase().GetGatewayConfig().ApiKey;

        // 32 random bytes, lower-case hex.
        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    [Fact]
    public void The_api_key_is_stable_across_restarts()
    {
        using var root = new TempDataRoot();

        var first = root.OpenDatabase().GetGatewayConfig().ApiKey;
        var second = root.OpenDatabase().GetGatewayConfig().ApiKey;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Rotating_the_api_key_replaces_it()
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        var before = database.GetGatewayConfig().ApiKey;
        var after = database.RegenerateApiKey();

        Assert.NotEqual(before, after);
        Assert.Equal(after, database.GetGatewayConfig().ApiKey);
    }

    [Fact]
    public void Gateway_settings_default_to_loopback_and_8080()
    {
        using var root = new TempDataRoot();

        var config = root.OpenDatabase().GetGatewayConfig();

        Assert.Equal("127.0.0.1", config.Host);
        Assert.Equal(8080, config.Port);
        Assert.True(config.AutoStart);
        Assert.Equal("http://127.0.0.1:8080", config.BaseUrl);
    }

    [Fact]
    public void Saved_gateway_settings_are_read_back()
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        var config = database.GetGatewayConfig();
        config.Port = 9191;
        config.AutoStart = false;
        config.MaxRows = 50;

        database.SaveGatewayConfig(config);

        var reloaded = root.OpenDatabase().GetGatewayConfig();

        Assert.Equal(9191, reloaded.Port);
        Assert.False(reloaded.AutoStart);
        Assert.Equal(50, reloaded.MaxRows);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("not-a-number")]
    public void A_nonsense_stored_port_falls_back_to_the_default(string stored)
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        database.SetSetting("Gateway.Port", stored);

        Assert.Equal(8080, root.OpenDatabase().GetGatewayConfig().Port);
    }

    // ---- Migration from the pre-rename location -----------------------

    private static void SeedLegacyFile(TempDataRoot root, DatabaseConfig connection)
    {
        // Build a real database at the current path, then move it to
        // where a build from before the rename would have left it.
        var seeded = root.OpenDatabase();
        seeded.AddConnection(connection);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        Directory.CreateDirectory(Path.GetDirectoryName(root.LegacyDatabasePath)!);
        File.Move(root.CurrentDatabasePath, root.LegacyDatabasePath);
    }

    [Fact]
    public void A_legacy_settings_file_is_carried_over()
    {
        using var root = new TempDataRoot();

        SeedLegacyFile(root, Sample(name: "Legacy Sales"));

        var migrated = Assert.Single(root.OpenDatabase().GetConnections());

        Assert.Equal("Legacy Sales", migrated.Name);
        Assert.Equal("secret", migrated.Password);
    }

    [Fact]
    public void The_legacy_file_is_left_behind_as_a_backup()
    {
        using var root = new TempDataRoot();

        SeedLegacyFile(root, Sample());

        root.OpenDatabase();

        Assert.True(File.Exists(root.LegacyDatabasePath));
        Assert.True(File.Exists(root.CurrentDatabasePath));
    }

    [Fact]
    public void Migration_does_not_run_again_over_live_data()
    {
        using var root = new TempDataRoot();

        SeedLegacyFile(root, Sample(name: "Legacy Sales"));

        var database = root.OpenDatabase();
        database.AddConnection(Sample(name: "Added Later", database: "/data/later.fdb"));

        // A later start must keep both rows, not restore the legacy file.
        Assert.Equal(2, root.OpenDatabase().GetConnections().Count);
    }

    [Fact]
    public void A_read_only_legacy_file_migrates_to_a_writable_copy()
    {
        using var root = new TempDataRoot();

        SeedLegacyFile(root, Sample(name: "Legacy Sales"));

        new FileInfo(root.LegacyDatabasePath).IsReadOnly = true;

        try
        {
            var database = root.OpenDatabase();

            Assert.False(new FileInfo(root.CurrentDatabasePath).IsReadOnly);

            // The real point: the copy accepts writes.
            database.AddConnection(Sample(name: "Writes Work", database: "/data/w.fdb"));

            Assert.Equal(2, database.GetConnections().Count);
        }
        finally
        {
            new FileInfo(root.LegacyDatabasePath).IsReadOnly = false;
        }
    }

    [Fact]
    public void With_no_legacy_file_a_clean_install_still_works()
    {
        using var root = new TempDataRoot();

        var database = root.OpenDatabase();

        Assert.Empty(database.GetConnections());
        Assert.NotEmpty(database.GetGatewayConfig().ApiKey);
    }
}
