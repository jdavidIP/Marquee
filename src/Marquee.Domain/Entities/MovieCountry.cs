namespace Marquee.Domain.Entities;

/// <summary>
/// Join row between a <see cref="Movie"/> and a <see cref="Country"/> it originates from. A film can
/// have several — an international co-production lists each one.
/// </summary>
public class MovieCountry : AuditableEntity
{
    public Guid MovieId { get; set; }
    public Movie Movie { get; set; } = null!;

    public Guid CountryId { get; set; }
    public Country Country { get; set; } = null!;
}
