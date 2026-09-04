using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FbGateway.Data;
using FbGateway.Gateway;
using FbGateway.Admin;

namespace FbGateway.Service;

/*
 * One executable, two jobs.
 *
 * With arguments it is an admin tool, for configuring the gateway on a
 * machine with no desktop. With none it is the service itself, which is
 * how the Service Control Manager starts it.
 */
public static class Program
{
    public static int Main(string[] args)
    {
        var handled = Cli.Run(args);

        if (handled != null)
        {
            return handled.Value;
        }

        var builder = Host.CreateApplicationBuilder();

        /*
         * Makes this a real service: the SCM is told when startup has
         * finished, stop requests arrive as cancellation, and log
         * messages go to the Windows event log, which is the only place
         * anyone can read them when nobody is signed in.
         */
        builder.Services.AddWindowsService(options =>
            options.ServiceName = "EasyFbSoft");

        builder.Logging.AddEventLog(settings =>
            settings.SourceName = "Easy FB Soft");

        /*
         * One SqliteDatabase and one GatewayServer for the lifetime of
         * the process. SqliteDatabase opens a connection per call, so
         * sharing it costs nothing and keeps the migration and schema
         * setup to a single run at startup.
         */
        builder.Services.AddSingleton<SqliteDatabase>();

        builder.Services.AddSingleton(provider =>
        {
            var database = provider.GetRequiredService<SqliteDatabase>();

            return new GatewayServer(
                database,
                new RequestLog(database.LogDirectory));
        });

        builder.Services.AddHostedService<GatewayWorker>();

        builder.Build().Run();

        return 0;
    }
}
