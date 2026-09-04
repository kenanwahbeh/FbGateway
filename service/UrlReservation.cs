using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FbGateway.Service;

/*
 * HTTP.SYS will not let a process listen on a prefix it has no
 * reservation for, and answers with a bare "Access is denied".
 *
 * The installer cannot simply reserve the port once, because the port
 * is a setting: change it in the control panel and the old reservation
 * covers nothing. So the service reserves whatever prefix it is asked
 * to bind, at the moment it is refused. It runs as Local System, which
 * is allowed to do this; the control panel never has to.
 */
public static class UrlReservation
{
    /*
     * Granted to Local System by SID rather than by name. "NT
     * AUTHORITY\SYSTEM" is translated on localised installations of
     * Windows, and netsh would not find the account under a different
     * name; S-1-5-18 is the same everywhere.
     *
     * GX is the generic-execute right, which is what registering and
     * listening on a prefix requires.
     */
    private const string LocalSystemMayListen = "D:(A;;GX;;;SY)";

    public static bool TryAdd(string prefix, ILogger logger)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments =
                    $"http add urlacl url={prefix} sddl={LocalSystemMayListen}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });

            if (process == null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd()
                + process.StandardError.ReadToEnd();

            /*
             * Bounded because netsh should answer at once; a wedged one
             * must not hold the service's startup open.
             */
            if (!process.WaitForExit(TimeSpan.FromSeconds(15)))
            {
                logger.LogWarning(
                    "netsh did not finish while reserving {Prefix}.",
                    prefix);

                return false;
            }

            if (process.ExitCode == 0)
            {
                logger.LogInformation("Reserved {Prefix}.", prefix);

                return true;
            }

            logger.LogWarning(
                "Could not reserve {Prefix}; netsh exited {Code}: {Output}",
                prefix,
                process.ExitCode,
                output.Trim());

            return false;
        }
        catch (Exception error)
        {
            logger.LogWarning(
                error,
                "Could not run netsh to reserve {Prefix}.",
                prefix);

            return false;
        }
    }
}
