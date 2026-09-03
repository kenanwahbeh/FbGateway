using System.Net;
using Xunit;

namespace FbGateway.Tests;

/*
 * Cases that change the gateway's own state, so each gets a fresh
 * listener rather than sharing the class fixture.
 */
public class GatewayLifecycleTests
{
    [Fact]
    public async Task Stopping_releases_the_port()
    {
        using var gateway = new GatewayHarness();

        Assert.True(gateway.Server.IsRunning);

        gateway.Server.Stop();

        Assert.False(gateway.Server.IsRunning);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => gateway.Client.GetAsync("/health"));
    }

    [Fact]
    public async Task The_gateway_restarts_on_the_same_port()
    {
        using var gateway = new GatewayHarness();

        var config = gateway.Database.GetGatewayConfig();
        config.Port = new Uri(gateway.Client.BaseAddress!.ToString()).Port;

        gateway.Server.Stop();
        gateway.Server.Start(config);

        using var response = await gateway.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Starting_an_already_running_gateway_is_a_no_op()
    {
        using var gateway = new GatewayHarness();

        var config = gateway.Database.GetGatewayConfig();

        // Must not throw, and must not bind a second time.
        gateway.Server.Start(config);

        Assert.True(gateway.Server.IsRunning);
    }

    [Fact]
    public void Stopping_twice_is_harmless()
    {
        using var gateway = new GatewayHarness();

        gateway.Server.Stop();
        gateway.Server.Stop();

        Assert.False(gateway.Server.IsRunning);
    }

    [Fact]
    public async Task Rotating_the_key_rejects_the_old_one_immediately()
    {
        using var gateway = new GatewayHarness();

        var old = gateway.ApiKey;

        gateway.RotateApiKey();

        var (rejected, _) = await GatewayHarness.Read(
            gateway.Get("/databases", key: old));

        var (accepted, _) = await GatewayHarness.Read(gateway.Get("/databases"));

        Assert.Equal(HttpStatusCode.Unauthorized, rejected);
        Assert.Equal(HttpStatusCode.OK, accepted);
    }

    [Fact]
    public async Task Turning_a_connection_online_takes_effect_without_a_restart()
    {
        using var gateway = new GatewayHarness();

        // Offline to begin with: a query is a conflict.
        var (before, _) = await GatewayHarness.Read(
            gateway.Post("/query", new { database = "Archive", sql = "SELECT 1 FROM RDB$DATABASE" }));

        Assert.Equal(HttpStatusCode.Conflict, before);

        gateway.Database.SetEnabled(gateway.Offline.Id, true);

        /*
         * Now past the enabled check and into connecting, which fails
         * because there is no Firebird behind it. Anything other than
         * 409 proves the listener re-read the connection list.
         */
        var (after, _) = await GatewayHarness.Read(
            gateway.Post("/query", new { database = "Archive", sql = "SELECT 1 FROM RDB$DATABASE" }));

        Assert.NotEqual(HttpStatusCode.Conflict, after);
    }

    [Fact]
    public async Task A_new_connection_is_visible_without_a_restart()
    {
        using var gateway = new GatewayHarness();

        gateway.Database.AddConnection(new Configuration.DatabaseConfig
        {
            Name = "Added Live",
            Server = "127.0.0.1",
            Port = 3050,
            Username = "SYSDBA",
            Password = "x",
            Database = "/data/live.fdb",
            Enabled = true
        });

        var (status, body) = await GatewayHarness.Read(gateway.Get("/databases"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Added Live", body);
    }

    [Fact]
    public void Two_gateways_cannot_share_a_port()
    {
        using var first = new GatewayHarness();

        var taken = first.Database.GetGatewayConfig();
        taken.Port = new Uri(first.Client.BaseAddress!.ToString()).Port;

        using var second = new GatewayHarness();
        second.Server.Stop();

        var failure = Assert.Throws<InvalidOperationException>(
            () => second.Server.Start(taken));

        /*
         * The advice is built from HttpListener's Windows error codes,
         * which is where the app runs; elsewhere the underlying message
         * comes through instead, so only the contract is asserted.
         */
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("in use", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(taken.Port.ToString(), failure.Message);
        }
    }
}
