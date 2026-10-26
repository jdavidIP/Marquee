namespace Marquee.Infrastructure.Avatars;

/// <summary>Where profile photos live (#63): S3 in production, S3Mock locally and in tests.</summary>
public interface IAvatarStore
{
    /// <summary>Stores an already re-encoded photo under a new key and returns its public URL.</summary>
    Task<string> PutAsync(Guid userId, byte[] webp, CancellationToken ct);

    /// <summary>
    /// Deletes the photo a stored URL points at. A URL that is not one of ours is left alone: there is
    /// nothing of ours to delete, and nothing else may be.
    /// </summary>
    Task DeleteAsync(string publicUrl, CancellationToken ct);
}
