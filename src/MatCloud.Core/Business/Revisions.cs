using System.Collections.Concurrent;
using System.Data.Common;
using MatCloud.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MatCloud.Core.Business;

/// <summary>
/// The revisions of a stream: the data of one app of one account (the clips of an account, the items of ThixNote of an account). Every change takes
/// the next number of the stream's sequence inside a transaction that holds the lock of the stream (<c>pg_advisory_xact_lock</c>). So the changes of
/// a stream are numbered in the order they are committed: once a device has seen revision N, nothing below N can still appear, and "everything above
/// N" never misses a change. After the commit the waiting long polls of the stream are woken (<see cref="RevisionSignals"/>).
/// The platform (general sync) and the modules (their own sequence) use the same helpers.
/// </summary>
public static class Revisions
{
    /// <summary>The name of a stream: the app and the account ("note/12").</summary>
    public static string Stream(string app, long userId) => app + "/" + userId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Runs <paramref name="change"/> in a transaction that holds the lock of the stream, then wakes the waiters of the stream.</summary>
    public static async Task<T> ChangeAsync<T>(AppDbContext db, string app, long userId, Func<Task<T>> change, CancellationToken ct = default)
    {
        T result;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await ExecuteAsync(db, "SELECT pg_advisory_xact_lock(@p0)", ct, LockKey(app, userId));
            result = await change();
            await transaction.CommitAsync(ct);
        }

        RevisionSignals.Notify(Stream(app, userId));
        return result;
    }

    /// <summary>The next number of a sequence (only call it inside <see cref="ChangeAsync{T}"/>).</summary>
    public static async Task<long> NextAsync(AppDbContext db, string sequence, CancellationToken ct = default)
        => await ScalarAsync(db, $"SELECT nextval('\"{sequence}\"')", ct);

    /// <summary>
    /// Waits for a change of the stream above <paramref name="since"/>: answers at once when <paramref name="latest"/> is above it already, else as
    /// soon as a change is committed, at the latest after <paramref name="timeout"/>. The signal is taken BEFORE the latest revision is read, so no change
    /// between the read and the wait is lost.
    /// </summary>
    public static async Task<long> WaitAsync(string stream, long since, Func<Task<long>> latest, TimeSpan timeout, CancellationToken ct)
    {
        var signal = RevisionSignals.Next(stream);
        var current = await latest();
        if (current > since)
        {
            return current;
        }

        try
        {
            await signal.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            return current;
        }

        return await latest();
    }

    /// <summary>The key of the advisory lock of a stream: the account in the low bits, a hash of the app above, in a range of its own.</summary>
    private static long LockKey(string app, long userId)
    {
        uint hash = 2166136261;
        foreach (var c in app)
        {
            hash = (hash ^ c) * 16777619;
        }

        return 0x4D43_0000_0000_0000L | ((long)(hash & 0xFFFF) << 32) | (userId & 0xFFFF_FFFFL);
    }

    public static async Task<long> ScalarAsync(AppDbContext db, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var command = await CommandAsync(db, sql, parameters, ct);
        var value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<List<long>> ColumnAsync(AppDbContext db, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var command = await CommandAsync(db, sql, parameters, ct);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var values = new List<long>();
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.GetInt64(0));
        }

        return values;
    }

    public static async Task ExecuteAsync(AppDbContext db, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var command = await CommandAsync(db, sql, parameters, ct);
        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>A command on the connection of the context, in its transaction; the parameters are @p0, @p1 ...</summary>
    private static async Task<DbCommand> CommandAsync(AppDbContext db, string sql, object[] parameters, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "p" + i;
            parameter.Value = parameters[i];
            command.Parameters.Add(parameter);
        }

        return command;
    }
}

/// <summary>
/// The long polls of the streams, in memory: a waiter takes the signal of its stream BEFORE it reads the latest revision, and every committed
/// change completes the signal and replaces it, so no change between the read and the wait is lost. One instance of the server.
/// </summary>
public static class RevisionSignals
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> Signals = new();

    /// <summary>The signal the next change of the stream completes.</summary>
    public static Task Next(string stream)
        => Signals.GetOrAdd(stream, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public static void Notify(string stream)
    {
        if (Signals.TryRemove(stream, out var signal))
        {
            signal.TrySetResult();
        }
    }
}
