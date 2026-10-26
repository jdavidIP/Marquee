using Marquee.Api.Dtos;
using Marquee.Infrastructure.Avatars;
using Marquee.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Marquee.Api.Services;

public interface IAvatarService
{
    /// <summary>
    /// Re-encodes the upload, stores it and points the user at it. Throws
    /// <see cref="AvatarImageException"/> for a file that is refused. Null when the user does not exist.
    /// </summary>
    Task<UserDto?> ReplaceAsync(Guid userId, Stream file, CancellationToken ct);

    /// <summary>Back to the monogram. Succeeds when there is no photo to remove.</summary>
    Task<UserDto?> RemoveAsync(Guid userId, CancellationToken ct);
}

/// <summary>
/// Your own profile photo (#63). The order of a replace is deliberate: the new photo is stored, then
/// the row points at it, and only then is the old one deleted — so a failure at any step leaves a
/// face that loads, never a URL to nothing. The cost is that a failed delete leaves an orphaned
/// object in the bucket, which is logged and otherwise harmless.
/// </summary>
public sealed class AvatarService(
    MarqueeDbContext db,
    IAvatarStore store,
    IOptions<AvatarOptions> options,
    ILogger<AvatarService> logger) : IAvatarService
{
    public async Task<UserDto?> ReplaceAsync(Guid userId, Stream file, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return null;

        var webp = await AvatarImage.ToWebpAsync(file, options.Value, ct);
        var previous = user.AvatarUrl;

        user.AvatarUrl = await store.PutAsync(userId, webp, ct);
        await db.SaveChangesAsync(ct);

        if (previous is not null)
            await DeleteQuietlyAsync(previous);
        return UserDto.From(user);
    }

    public async Task<UserDto?> RemoveAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return null;

        var previous = user.AvatarUrl;
        if (previous is null)
            return UserDto.From(user);

        user.AvatarUrl = null;
        await db.SaveChangesAsync(ct);

        await DeleteQuietlyAsync(previous);
        return UserDto.From(user);
    }

    // Not cancellable: the row already points elsewhere, so abandoning this half-way only makes the
    // orphan more likely.
    private async Task DeleteQuietlyAsync(string url)
    {
        try
        {
            await store.DeleteAsync(url, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not delete the previous avatar {AvatarUrl}; it is left in the bucket", url);
        }
    }
}
