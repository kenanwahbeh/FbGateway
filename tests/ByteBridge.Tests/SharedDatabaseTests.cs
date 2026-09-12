using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Xunit;
using ByteBridge.Configuration;
using ByteBridge.Data;

namespace ByteBridge.Tests;

/*
 * The gateway moved into a Windows service, so the settings file is now
 * open in two processes at once: the service reading it, and the
 * control panel writing to it. These cover that.
 *
 * Two SqliteDatabase instances over one directory is the same situation
 * as two processes -- separate connections against one file, with no
 * shared lock manager between them.
 */
public class SharedDatabaseTests
{
    private static DatabaseConfig Sample(string name) =>
        new()
        {
            Name = name,
            Server = "127.0.0.1",
            Port = 3050,
            Username = "SYSDBA",
            Password = "secret",
            Database = "/data/" + name + ".fdb",
            Enabled = true
        };

    [Fact]
    public void The_file_is_left_in_wal_mode()
    {
        using var root = new TempDataRoot();
        var _ = root.OpenDatabase();

        using var connection =
            new SqliteConnection($"Data Source={root.CurrentDatabasePath}");

        connection.Open();

        using var command = connection.CreateCommand();

        command.CommandText = "PRAGMA journal_mode;";

        Assert.Equal(
            "wal",
            Convert.ToString(command.ExecuteScalar())!.ToLowerInvariant());
    }

    [Fact]
    public void One_instance_sees_what_the_other_just_wrote()
    {
        using var root = new TempDataRoot();

        var writer = root.OpenDatabase();
        var reader = root.OpenDatabase();

        writer.AddConnection(Sample("Sales"));

        Assert.Contains(
            reader.GetConnections(),
            c => c.Name == "Sales");
    }

    /*
     * Two instances writing in a loop is not actually a contended test:
     * the writes serialise and nobody ever finds the file locked. To
     * exercise busy_timeout the lock has to be held by someone else
     * while a write is attempted, which is what BEGIN IMMEDIATE does.
     *
     * What this pins down is the behaviour the two processes depend on:
     * a contended write waits its turn instead of throwing. It does not
     * pin down which layer provides that, and deliberately so, because
     * measurement showed it is not the one you would guess -- with
     * busy_timeout at SQLite's default of 0 the write still succeeds,
     * on the strength of Microsoft.Data.Sqlite's 30-second Default
     * Timeout alone.
     *
     * It still catches a real regression: drop Default Timeout to 1 and
     * turn busy_timeout off and this fails with "database is locked".
     */
    [Fact]
    public async Task A_write_waits_for_another_process_to_release_the_lock()
    {
        using var root = new TempDataRoot();

        var database = root.OpenDatabase();

        using var holder =
            new SqliteConnection($"Data Source={root.CurrentDatabasePath}");

        holder.Open();

        using var begin = holder.CreateCommand();

        // Takes the write lock immediately rather than on first write.
        begin.CommandText = "BEGIN IMMEDIATE;";
        begin.ExecuteNonQuery();

        var released = false;

        var release = Task.Run(async () =>
        {
            await Task.Delay(750);

            using var commit = holder.CreateCommand();

            commit.CommandText = "COMMIT;";
            commit.ExecuteNonQuery();

            Volatile.Write(ref released, true);
        });

        // Blocks until the holder commits, instead of throwing.
        database.AddConnection(Sample("Waited"));

        Assert.True(
            Volatile.Read(ref released),
            "the write returned before the lock was released, so it never contended");

        await release;

        Assert.Contains(
            root.OpenDatabase().GetConnections(),
            c => c.Name == "Waited");
    }

    [Fact]
    public async Task Both_instances_can_write_at_once()
    {
        using var root = new TempDataRoot();

        var first = root.OpenDatabase();
        var second = root.OpenDatabase();

        var failures = new ConcurrentBag<Exception>();
        using var ready = new Barrier(2);

        void Hammer(SqliteDatabase database, string prefix)
        {
            ready.SignalAndWait();

            for (var i = 0; i < 25; i++)
            {
                try
                {
                    database.AddConnection(Sample($"{prefix}-{i}"));
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }
        }

        await Task.WhenAll(
            Task.Run(() => Hammer(first, "a")),
            Task.Run(() => Hammer(second, "b")));

        Assert.Empty(failures);

        var names = root.OpenDatabase()
            .GetConnections()
            .Select(c => c.Name)
            .ToHashSet();

        for (var i = 0; i < 25; i++)
        {
            Assert.Contains($"a-{i}", names);
            Assert.Contains($"b-{i}", names);
        }
    }
}
