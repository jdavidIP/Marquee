namespace Marquee.Domain;

/// <summary>
/// Converts a local calendar date into the UTC instant range it spans, for querying UTC-stored
/// timestamps against "today" (CLAUDE.md §4.4 speaks in local time; storage is UTC). "Local" is the
/// server's local time zone — v1 has one scope and one clock, not a per-user or per-scope one.
/// </summary>
public static class LocalDay
{
    public static (DateTime StartUtc, DateTime EndUtc) BoundsUtc(DateOnly localDate) =>
        (DateTime.SpecifyKind(localDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime(),
         DateTime.SpecifyKind(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime());
}
