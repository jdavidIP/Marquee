namespace Marquee.Api.Dtos;

/// <summary>
/// Shared page/pageSize clamping for the paged endpoints under <c>/api/library</c> and
/// <c>/api/users/{username}/...</c>, so the bound is enforced once rather than copied at each call
/// site — a magic number duplicated at every controller is still a magic number (CLAUDE.md §7).
/// </summary>
public static class Paging
{
    public const int MaxPageSize = 100;

    public static (int Page, int PageSize) Clamp(int page, int pageSize) =>
        (page < 1 ? 1 : page, Math.Clamp(pageSize, 1, MaxPageSize));
}
