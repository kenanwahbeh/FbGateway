using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FbGateway.Configuration;
using FbGateway.Data;
using FbGateway.Gateway;

namespace FbGateway.Service;

/*
 * Hosts the gateway for as long as the service runs, and keeps it in
 * step with the settings file.
 *
 * The control panel does not talk to this process. It writes to the
 * same SQLite file and this loop notices, which means there is no IPC
 * channel to secure, nothing to authenticate between the two, and the
 * control panel can be closed, upgraded or absent without the gateway
 * caring.
 *
 * Polling rather than a file watcher: the check is one small read on a
 * local file every few seconds, and a watcher on SQLite is unreliable
 * anyway, since WAL means a commit need not touch the main file at the
 * moment it lands.
 */
public sealed class GatewayWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromSeconds(5);

    private readonly SqliteDatabase _database;
    private readonly GatewayServer _gateway;
    private readonly ILogger<GatewayWorker> _logger;

    /*
     * What the listener is currently bound with, as opposed to what the
     * settings file now says. Null while it is stopped.
     */
    private GatewayConfig? _running;

    /*
     * A bind that failed is retried on the next poll rather than
     * killing the service, but without this every retry would log the
     * same line every five seconds and bury everything else.
     */
    private string? _lastFailure;

    public GatewayWorker(
        SqliteDatabase database,
        GatewayServer gateway,
        ILogger<GatewayWorker> logger)
    {
        _database = database;
        _gateway = gateway;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Easy FB Soft gateway service starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Reconcile();
            }
            catch (Exception error)
            {
                /*
                 * Never let a bad read of the settings file stop the
                 * service. A gateway that keeps serving on its last
                 * known good configuration beats one that exits.
                 */
                _logger.LogError(
                    error,
                    "Could not apply the current settings; keeping the gateway as it is.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (_gateway.IsRunning)
        {
            _gateway.Stop();
        }

        _logger.LogInformation(
            "Easy FB Soft gateway service stopped.");
    }

    /*
     * Brings the listener into line with the settings file.
     *
     * Connections are read from the database on every request, so
     * adding or disabling one needs nothing here. Only the values the
     * listener captured when it started are handled below.
     */
    private void Reconcile()
    {
        var desired = _database.GetGatewayConfig();

        if (!desired.AutoStart)
        {
            if (_gateway.IsRunning)
            {
                _logger.LogInformation(
                    "Gateway turned off in the settings; stopping the listener.");

                _gateway.Stop();
                _running = null;
            }

            return;
        }

        if (!_gateway.IsRunning)
        {
            StartWith(desired);
            return;
        }

        if (_running == null)
        {
            return;
        }

        /*
         * The key is swapped in place, because it is compared per
         * request and rebinding the socket to change it would drop live
         * requests for no reason.
         */
        if (_running.ApiKey != desired.ApiKey)
        {
            _logger.LogInformation("API key rotated; applying it.");

            _gateway.UpdateApiKey(desired.ApiKey);
            _running.ApiKey = desired.ApiKey;
        }

        if (NeedsRebind(_running, desired))
        {
            _logger.LogInformation(
                "Listener settings changed; rebinding from {Old} to {New}.",
                _running.Prefix,
                desired.Prefix);

            _gateway.Stop();
            _running = null;

            StartWith(desired);
        }
    }

    /*
     * Everything the running listener captured at Start time. The key
     * is not here: it is handled without a rebind.
     */
    private static bool NeedsRebind(
        GatewayConfig running,
        GatewayConfig desired) =>
        running.Host != desired.Host
        || running.Port != desired.Port
        || running.MaxRows != desired.MaxRows
        || running.CommandTimeoutSeconds != desired.CommandTimeoutSeconds;

    /*
     * HttpListener's "access denied", which means HTTP.SYS has no
     * reservation for this prefix rather than anything about the file
     * system. GatewayServer wraps the original, so the code is on the
     * inner exception.
     */
    private static bool NeedsReservation(Exception error) =>
        error.InnerException is HttpListenerException { ErrorCode: 5 };

    private void StartWith(GatewayConfig config, bool reserved = false)
    {
        try
        {
            _gateway.Start(config);

            _running = config;
            _lastFailure = null;

            _logger.LogInformation(
                "Gateway listening on {Prefix}.",
                config.Prefix);
        }
        catch (Exception error)
        {
            _running = null;

            /*
             * Port already taken, or HTTP.SYS refusing the reservation.
             * Both are fixable from outside without touching the
             * service, so it keeps retrying instead of giving up.
             */
            /*
             * Reserve the prefix and try once more, so changing the
             * port in the control panel does not need an administrator
             * at a prompt. Guarded so a reservation that does not fix
             * it cannot loop.
             */
            if (!reserved
                && NeedsReservation(error)
                && UrlReservation.TryAdd(config.Prefix, _logger))
            {
                StartWith(config, reserved: true);
                return;
            }

            if (_lastFailure != error.Message)
            {
                _lastFailure = error.Message;

                _logger.LogError(
                    "Could not start the gateway on {Prefix}: {Message} "
                    + "Retrying every {Seconds} seconds.",
                    config.Prefix,
                    error.Message,
                    (int)PollInterval.TotalSeconds);
            }
        }
    }
}
