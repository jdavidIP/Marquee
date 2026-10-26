using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Marquee.IntegrationTests;

/// <summary>
/// Counts the reads EF Core sends to Postgres for Premiere data (the <c>premieres</c>, <c>contributions</c>
/// and <c>movies</c> tables), so a test can assert that a request did not go to the database for it (#94).
///
/// Only those tables, because the process has background readers of its own: the MassTransit outbox
/// polls Postgres through the same DbContext every second, and counting every command made the
/// assertions fail whenever a poll landed inside the request being measured. An interceptor rather
/// than a log provider because the API replaces the logger factory with Serilog, which ignores
/// providers added from outside.
/// </summary>
public sealed partial class DbCommandCounter : DbCommandInterceptor
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Reset() => Interlocked.Exchange(ref _count, 0);

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Observe(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Observe(command);
        return ValueTask.FromResult(result);
    }

    private void Observe(DbCommand command)
    {
        if (PremiereTables().IsMatch(command.CommandText))
            Interlocked.Increment(ref _count);
    }

    [GeneratedRegex("""\b(premieres|contributions|movies)\b""")]
    private static partial Regex PremiereTables();
}
