using FbGateway.Configuration;
using FbGateway.Data;

namespace FbGateway.Admin;

/*
 * Administration without a desktop.
 *
 * Windows Server can be installed with no GUI at all, and the control
 * panel is a WPF window, so on Server Core it simply cannot run. Without
 * these commands the service would install and start there and then be
 * impossible to point at a database, which would make the whole
 * no-login story useless in the one place it matters most.
 *
 * Everything here writes the same SQLite file the control panel writes,
 * and the running service picks changes up within a few seconds. There
 * is no need to restart it after any of these.
 */
public static class Cli
{
    public const string Usage = """
        Easy FB Soft gateway service

        Running with no arguments starts the Windows service. The
        commands below configure it from a terminal, for machines with
        no desktop:

          status                 Show the listener and the databases
          on | off               Whether the gateway should listen
          port <number>          Change the listening port
          key show               Print the API key
          key new                Replace the API key

          db list                List the configured databases
          db enable  <name>      Start answering for a database
          db disable <name>      Stop answering for a database
          db remove  <name>      Forget a database
          db add --name <n> --server <host> --path <file>
                 --user <u> --password <p> [--port <3050>]

        Changes apply within a few seconds; the service does not need
        restarting.
        """;

    /*
     * Returns the process exit code, or null when the arguments are not
     * a command at all, which is the signal to run as a service.
     */
    public static int? Run(string[] args, SqliteDatabase? database = null)
    {
        if (args.Length == 0)
        {
            return null;
        }

        if (args[0] is "-h" or "--help" or "/?" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        try
        {
            return Dispatch(args, database ?? new SqliteDatabase());
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("error: " + error.Message);
            return 1;
        }
    }

    private static int Dispatch(string[] args, SqliteDatabase database) =>
        args[0].ToLowerInvariant() switch
        {
            "status" => Status(database),
            "on" => SetRunning(database, true),
            "off" => SetRunning(database, false),
            "port" => SetPort(database, args),
            "key" => Key(database, args),
            "db" => Db(database, args),
            _ => Unknown(args[0])
        };

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"error: unknown command '{verb}'.");
        Console.Error.WriteLine();
        Console.Error.WriteLine(Usage);

        return 1;
    }

    private static int Status(SqliteDatabase database)
    {
        var config = database.GetGatewayConfig();
        var connections = database.GetConnections();

        Console.WriteLine($"listening   {(config.AutoStart ? "yes" : "no")}");
        Console.WriteLine($"address     {config.BaseUrl}");
        Console.WriteLine($"api key     {(string.IsNullOrEmpty(config.ApiKey) ? "not set" : "set (run: key show)")}");
        Console.WriteLine($"row cap     {config.MaxRows}");
        Console.WriteLine($"timeout     {config.CommandTimeoutSeconds}s");
        Console.WriteLine();

        if (connections.Count == 0)
        {
            Console.WriteLine("No databases configured. Add one with: db add --help");
            return 0;
        }

        Console.WriteLine("databases");

        foreach (var item in connections)
        {
            Console.WriteLine(
                $"  {(item.Enabled ? "on " : "off")}  {item.Name}  "
                + $"{item.Server}:{item.Port}  {item.Database}");
        }

        return 0;
    }

    private static int SetRunning(SqliteDatabase database, bool running)
    {
        var config = database.GetGatewayConfig();

        config.AutoStart = running;

        database.SaveGatewayConfig(config);

        Console.WriteLine(
            running
                ? $"The gateway will listen on {config.BaseUrl}."
                : "The gateway will stop listening.");

        return 0;
    }

    private static int SetPort(SqliteDatabase database, string[] args)
    {
        if (args.Length < 2
            || !int.TryParse(args[1], out var port)
            || port is < 1 or > 65535)
        {
            Console.Error.WriteLine("error: port takes a number from 1 to 65535.");
            return 1;
        }

        var config = database.GetGatewayConfig();

        config.Port = port;

        database.SaveGatewayConfig(config);

        Console.WriteLine($"The gateway will listen on {config.BaseUrl}.");

        return 0;
    }

    private static int Key(SqliteDatabase database, string[] args)
    {
        var action = args.Length > 1 ? args[1].ToLowerInvariant() : "show";

        switch (action)
        {
            case "show":
                var key = database.GetGatewayConfig().ApiKey;

                if (string.IsNullOrEmpty(key))
                {
                    Console.Error.WriteLine("error: no API key has been generated yet.");
                    return 1;
                }

                Console.WriteLine(key);
                return 0;

            case "new":
                Console.WriteLine(database.RegenerateApiKey());
                Console.Error.WriteLine(
                    "The old key stops working within a few seconds. "
                    + "Update anything that calls the gateway.");
                return 0;

            default:
                Console.Error.WriteLine("error: key takes 'show' or 'new'.");
                return 1;
        }
    }

    private static int Db(SqliteDatabase database, string[] args)
    {
        var action = args.Length > 1 ? args[1].ToLowerInvariant() : "list";

        if (action == "list")
        {
            return Status(database);
        }

        if (action == "add")
        {
            return AddDb(database, args);
        }

        if (args.Length < 3)
        {
            Console.Error.WriteLine($"error: db {action} needs a database name.");
            return 1;
        }

        var found = database.FindConnection(args[2]);

        if (found == null)
        {
            Console.Error.WriteLine($"error: no database called '{args[2]}'.");
            return 1;
        }

        switch (action)
        {
            case "enable":
                database.SetEnabled(found.Id, true);
                Console.WriteLine($"{found.Name} is now answering requests.");
                return 0;

            case "disable":
                database.SetEnabled(found.Id, false);
                Console.WriteLine($"{found.Name} will not answer requests.");
                return 0;

            case "remove":
                database.DeleteConnection(found.Id);
                Console.WriteLine($"{found.Name} removed.");
                return 0;

            default:
                Console.Error.WriteLine($"error: unknown db command '{action}'.");
                return 1;
        }
    }

    private static int AddDb(SqliteDatabase database, string[] args)
    {
        var options = Options(args, 2);

        string? Required(string name)
        {
            if (options.TryGetValue(name, out var value)
                && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            Console.Error.WriteLine($"error: db add needs --{name}.");
            return null;
        }

        var name = Required("name");
        var server = Required("server");
        var path = Required("path");
        var user = Required("user");
        var password = Required("password");

        if (name == null || server == null || path == null
            || user == null || password == null)
        {
            return 1;
        }

        var port = 3050;

        if (options.TryGetValue("port", out var portText)
            && !int.TryParse(portText, out port))
        {
            Console.Error.WriteLine("error: --port takes a number.");
            return 1;
        }

        database.AddConnection(new DatabaseConfig
        {
            Name = name,
            Server = server,
            Port = port,
            Username = user,
            Password = password,
            Database = path,
            Enabled = true
        });

        Console.WriteLine($"{name} added and answering requests.");

        return 0;
    }

    /*
     * Reads --key value pairs from the tail of the command line. Values
     * are taken verbatim, so a password containing spaces works when
     * the shell quotes it.
     */
    private static Dictionary<string, string> Options(string[] args, int from)
    {
        var options = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        for (var i = from; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = args[i][2..];

            if (i + 1 < args.Length
                && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                options[key] = args[++i];
            }
            else
            {
                options[key] = string.Empty;
            }
        }

        return options;
    }
}
