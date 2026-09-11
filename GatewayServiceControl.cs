using System;
using System.ComponentModel;
using System.Net.Http;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace FbGateway;

public enum ServiceState
{
    NotInstalled,
    Stopped,
    Running,
    Pending
}

/*
 * The control panel's view of the service that now hosts the gateway.
 *
 * Two different questions get asked here, and they are not the same
 * question. Whether the service is running is answered by Windows.
 * Whether the gateway is actually answering requests is answered by
 * asking it, over the same loopback address a tunnel would use.
 *
 * Keeping them apart matters: the service can be running perfectly
 * while the gateway inside it refuses to bind, because the port is
 * taken or HTTP.SYS turned the reservation down. Reporting the service
 * as proof the gateway works would hide exactly the failure that makes
 * a tunnel return 502.
 */
public sealed class GatewayServiceControl : IDisposable
{
    public const string ServiceName = "ByteBridge";

    /*
     * Long enough for a service that has to open a database and bind a
     * socket, short enough that a wedged one does not hang the window.
     */
    private static readonly TimeSpan ControlTimeout =
        TimeSpan.FromSeconds(20);

    /*
     * Proxy detection is switched off deliberately, and it is the
     * difference between a window that appears at once and one that
     * hangs for seconds on a machine with "automatically detect
     * settings" turned on. The first request would otherwise go looking
     * for a WPAD server before it would consent to talk to 127.0.0.1.
     *
     * There is no case where a loopback request should go through a
     * proxy, so nothing is lost. With it off, a port nothing is
     * listening on refuses the connection immediately instead of
     * burning the timeout.
     */
    private readonly HttpClient _client = new(
        new HttpClientHandler { UseProxy = false, Proxy = null })
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    public ServiceState State()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);

            return controller.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                _ => ServiceState.Pending
            };
        }
        catch (InvalidOperationException)
        {
            /*
             * What ServiceController throws when the service is not
             * registered at all, which is the normal case for someone
             * running the app from a build rather than an install.
             */
            return ServiceState.NotInstalled;
        }
    }

    public void Start()
    {
        using var controller = new ServiceController(ServiceName);

        if (controller.Status == ServiceControllerStatus.Running)
        {
            return;
        }

        controller.Start();

        controller.WaitForStatus(
            ServiceControllerStatus.Running,
            ControlTimeout);
    }

    /*
     * Asks the gateway itself, rather than trusting that a running
     * service means a listening socket. /health needs no API key, which
     * is what makes this usable as a liveness check.
     */
    public async Task<bool> IsAnsweringAsync(
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _client.GetAsync(
                baseUrl.TrimEnd('/') + "/health",
                cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    /*
     * True when this process could control the service if it wanted to.
     * The app asks for administrator rights in its manifest, so this is
     * a check that something has not gone wrong rather than a branch
     * the UI is expected to take.
     */
    public bool CanControl()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);

            _ = controller.Status;

            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose() => _client.Dispose();
}
