using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace FbGateway.Data;

/*
 * Locks down the folder holding the settings file.
 *
 * That file carries the Firebird passwords in the clear and the gateway
 * API key, and the key is the only thing between the public internet,
 * by way of the tunnel, and those databases. Left to inherit from
 * C:\ProgramData it would be readable by every account on the machine,
 * which on a server is a real number of accounts.
 *
 * SqliteDatabase applies this itself, right after creating the folder,
 * so it covers every process that can create it. It used to live in the
 * service alone, which left the control panel and the command line --
 * either of which can be the first to create the folder and write a key
 * into it -- with nothing protecting that file until the service next
 * started. It is re-applied on every open, so a folder that drifts, from
 * a restore or someone editing permissions, is put back.
 */
[SupportedOSPlatform("windows")]
public static class DataFolderSecurity
{
    /*
     * Returns what went wrong rather than throwing. Worth reporting,
     * never worth refusing to run over: a gateway that will not start
     * protects nothing.
     */
    public static Exception? Ensure(string folder)
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

            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }
}
