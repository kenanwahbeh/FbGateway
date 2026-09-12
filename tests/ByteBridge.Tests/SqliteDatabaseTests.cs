using Xunit;
using ByteBridge.Configuration;
using ByteBridge.Data;

namespace ByteBridge.Tests;

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

    [Fact]
    public void A_clean_install_creates_a_working_database()
    {
        using var root = new TempDataRoot();

        var database = root.OpenDatabase();

        Assert.Empty(database.GetConnections());
        Assert.NotEmpty(database.GetGatewayConfig().ApiKey);
    }

    // ---- The listener stays on loopback ---------------------------------

    [Theory]
    [InlineData("+")]
    [InlineData("*")]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.10")]
    [InlineData("127.1")]
    [InlineData("127.0.0.1/ sddl=D:(A;;GX;;;WD) url=http://+:8080/")]
    public void A_stored_host_that_is_not_loopback_falls_back_to_loopback(string stored)
    {
        using var root = new TempDataRoot();

        root.OpenDatabase().SetSetting("Gateway.Host", stored);

        Assert.Equal("127.0.0.1", root.OpenDatabase().GetGatewayConfig().Host);
    }

    [Theory]
    [InlineData("127.0.0.2")]
    [InlineData("localhost")]
    public void A_stored_loopback_host_is_kept(string stored)
    {
        using var root = new TempDataRoot();

        root.OpenDatabase().SetSetting("Gateway.Host", stored);

        Assert.Equal(stored, root.OpenDatabase().GetGatewayConfig().Host);
    }

    // ---- Two writers, one file -------------------------------------------

    [Fact]
    public void Saving_settings_does_not_undo_a_key_rotation_made_meanwhile()
    {
        using var root = new TempDataRoot();

        var panel = root.OpenDatabase();
        var commandLine = root.OpenDatabase();

        // The panel reads, the command line rotates, the panel saves.
        var config = panel.GetGatewayConfig();

        var rotated = commandLine.RegenerateApiKey();

        config.Port = 9292;
        panel.SaveGatewayConfig(config);

        var reloaded = root.OpenDatabase().GetGatewayConfig();

        Assert.Equal(rotated, reloaded.ApiKey);
        Assert.Equal(9292, reloaded.Port);
    }

    [Fact]
    public void Saving_an_edit_to_a_connection_that_was_removed_fails()
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        database.AddConnection(Sample());

        var stale = database.GetConnections()[0];

        database.DeleteConnection(stale.Id);

        stale.Server = "10.0.0.5";

        Assert.Throws<InvalidOperationException>(
            () => database.UpdateConnection(stale));

        Assert.Empty(database.GetConnections());
    }

    [Fact]
    public void The_last_test_time_survives_a_culture_with_another_calendar()
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        var connection = Sample();
        database.AddConnection(connection);
        database.SetTestResult(connection.Id, true);

        var original = System.Globalization.CultureInfo.CurrentCulture;

        var thai = (System.Globalization.CultureInfo)
            System.Globalization.CultureInfo.GetCultureInfo("th-TH").Clone();

        thai.DateTimeFormat.Calendar =
            new System.Globalization.ThaiBuddhistCalendar();

        try
        {
            System.Globalization.CultureInfo.CurrentCulture = thai;

            var stored = database.GetConnections()[0];

            Assert.NotNull(stored.LastTestedAt);
            Assert.Equal(DateTime.Now.Year, stored.LastTestedAt!.Value.Year);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    // ---- Names address connections ---------------------------------------

    [Fact]
    public void A_name_already_in_use_cannot_be_added_again()
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        database.AddConnection(Sample(name: "Sales", database: "/data/sales.fdb"));

        Assert.Throws<InvalidOperationException>(
            () => database.AddConnection(
                Sample(name: "SALES", database: "/data/other.fdb")));

        Assert.Single(database.GetConnections());
    }

    [Fact]
    public void An_edit_cannot_take_a_name_another_connection_has()
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        database.AddConnection(Sample(name: "Sales", database: "/data/sales.fdb"));
        database.AddConnection(Sample(name: "Archive", database: "/data/archive.fdb"));

        var archive = database.GetConnections().Single(c => c.Name == "Archive");

        archive.Name = "sales";

        Assert.Throws<InvalidOperationException>(
            () => database.UpdateConnection(archive));
    }

    [Fact]
    public void An_edit_can_keep_its_own_name()
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        database.AddConnection(Sample());

        var stored = database.GetConnections()[0];

        stored.Server = "10.0.0.5";

        database.UpdateConnection(stored);

        Assert.Equal("10.0.0.5", database.GetConnections()[0].Server);
    }

    [Fact]
    public void A_name_shared_by_two_connections_resolves_to_neither()
    {
        using var root = new TempDataRoot();
        var database = root.OpenDatabase();

        database.AddConnection(Sample(name: "Sales", database: "/data/sales.fdb"));

        // As a settings file from before names had to be unique could hold.
        var second = Sample(name: "Sales", database: "/data/sales-2023.fdb");

        InsertBypassingChecks(root, second);

        Assert.Null(database.FindConnection("Sales"));

        database.FindConnection("sales", out var sharedName);

        Assert.Equal(2, sharedName.Count);

        // The id still reaches the one that was meant.
        Assert.NotNull(database.FindConnection(second.Id));
    }

    private static void InsertBypassingChecks(TempDataRoot root, DatabaseConfig connection)
    {
        using var sqlite = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={root.CurrentDatabasePath}");

        sqlite.Open();

        using var command = sqlite.CreateCommand();

        command.CommandText = """
            INSERT INTO Databases
                (Id, Name, Server, Port, Username, Password, DatabaseValue,
                 Enabled, LastTestSuccessful, LastTestedAt, ConnectionKey)
            VALUES
                ($id, $name, $server, $port, $user, $password, $database,
                 1, 0, NULL, $key);
            """;

        command.Parameters.AddWithValue("$id", connection.Id);
        command.Parameters.AddWithValue("$name", connection.Name);
        command.Parameters.AddWithValue("$server", connection.Server);
        command.Parameters.AddWithValue("$port", connection.Port);
        command.Parameters.AddWithValue("$user", connection.Username);
        command.Parameters.AddWithValue("$password", connection.Password);
        command.Parameters.AddWithValue("$database", connection.Database);
        command.Parameters.AddWithValue("$key", connection.ConnectionKey);

        command.ExecuteNonQuery();
    }
}
