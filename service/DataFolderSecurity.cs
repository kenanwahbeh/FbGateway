using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace FbGateway.Service;

/*
 * Locks down the folder holding the settings file.
 *
 * That file carries the Firebird passwords in the clear and the gateway
 * API key, and the key is the only thing between the public internet,
 * by way of the tunnel, and those databases. Left to inherit from
 * C:\ProgramData it would be readable by every account on the machine,
 * which on a server is a real number of accounts.
 *
 * Done by the service rather than by the installers so there is one
 * implementation instead of two, so it covers the .msi as well as the
 * .exe, and so a folder that drifts, from a restore or someone editing
 * permissions, is put back on the next start.
 */
[SupportedOSPlatform("windows")]
public static class DataFolderSecurity
{
    public static void Ensure(string folder, ILogger logger)
    {
        try
        {
            var directory = new DirectoryInfo(folder);

            if (!directory.Exists)
            {
                directory.Create();
            }

            /*
             * Well-known SIDs rather than names. "NT AUTHORITY\SYSTEM"
             * and "BUILTIN\Administrators" are translated on localised
             * installations of Windows and would not resolve; the SIDs
             * are identical everywhere.
             */
            var system = new SecurityIdentifier(
                WellKnownSidType.LocalSystemSid, null);

            var administrators = new SecurityIdentifier(
                WellKnownSidType.BuiltinAdministratorsSid, null);

            var security = new DirectorySecurity();

            // Drops everything inherited from ProgramData.
            security.SetAccessRuleProtection(
                isProtected: true,
                preserveInheritance: false);

            security.SetOwner(administrators);

            foreach (var account in new[] { system, administrators })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    account,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit
                    | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            directory.SetAccessControl(security);
        }
        catch (Exception error)
        {
            /*
             * Worth knowing about, never worth refusing to run over: a
             * gateway that will not start protects nothing.
             */
            logger.LogWarning(
                error,
                "Could not restrict permissions on {Folder}. "
                + "It may be readable by other accounts on this machine.",
                folder);
        }
    }
}
