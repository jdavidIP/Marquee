namespace Marquee.Api.Dtos;

public sealed record LibraryEntryDto(
    Guid MovieId,
    MovieDto Movie,
    Guid PremiereId,
    DateTime AcquiredAt,
    int? EmblemTier);
