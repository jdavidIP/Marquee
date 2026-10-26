using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Marquee.IntegrationTests;

/// <summary>
/// Counts the commands EF Core creates for Postgres, so a test can assert that a request did not go
/// to the database at all (#94). An interceptor rather than a log provider because the API replaces
/// the logger factory with Serilog, which ignores providers added from outside. The scheduler is
/// off in this suite, so nothing in the background adds to the count.
/// </summary>
public sealed class DbCommandCounter : DbCommandInterceptor
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Reset() => Interlocked.Exchange(ref _count, 0);

    public override DbCommand CommandCreated(CommandEndEventData eventData, DbCommand result)
    {
        Interlocked.Increment(ref _count);
        return result;
    }
}
