using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NewsQuack.Infrastructure.Persistence;

/// <summary>
/// The one sanctioned raw-SQL exception in this codebase (T-015 coordinator decision): connection initialization, not data access.
/// Runs on every connection EF opens — journal_mode=WAL persists per database file, while synchronous=NORMAL is a per-connection
/// setting and must therefore be re-applied on every open. All *data* access stays 100% LINQ.
/// </summary>
public sealed class SqliteConnectionInitInterceptor : DbConnectionInterceptor
{
    private const string JournalModePragma = "PRAGMA journal_mode=WAL;";
    private const string SynchronousPragma = "PRAGMA synchronous=NORMAL;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        base.ConnectionOpened(connection, eventData);

        using var command = connection.CreateCommand();

        command.CommandText = JournalModePragma;   // persisted per file — idempotent on every open
        command.ExecuteNonQuery();

        command.CommandText = SynchronousPragma;   // per-connection — must run on every open
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = JournalModePragma;   // persisted per file — idempotent on every open
        await command.ExecuteNonQueryAsync(cancellationToken);

        command.CommandText = SynchronousPragma;   // per-connection — must run on every open
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
