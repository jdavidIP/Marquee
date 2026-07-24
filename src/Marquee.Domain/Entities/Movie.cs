namespace Marquee.Domain.Entities;

public class Movie : AuditableEntity
{
    public int TmdbId { get; set; }
    public string Title { get; set; } = null!;
    public string? PosterPath { get; set; }
    public int? ReleaseYear { get; set; }
    public string? Overview { get; set; }
    public double VoteAverage { get; set; }
    public int VoteCount { get; set; }
    public DateTime CachedAt { get; set; }
}
