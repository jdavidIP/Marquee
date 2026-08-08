namespace Marquee.Domain.Entities;

/// <summary>
/// A country a film originates from, mirrored from TMDB.
///
/// Modelled the same way as <see cref="Genre"/> — a table rather than a bare ISO code on the movie —
/// so a "films from South Korea" filter is the same shape of query as "films that are drama", and so
/// the human-readable name lives in data rather than in a hardcoded map.
/// </summary>
public class Country : AuditableEntity
{
    /// <summary>ISO 3166-1 alpha-2, e.g. "US", "KR". The natural key.</summary>
    public string Iso3166Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public ICollection<MovieCountry> MovieCountries { get; set; } = [];
}
