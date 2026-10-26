namespace Marquee.Infrastructure.Redis;

/// <summary>
/// A read-through cache of JSON values in Redis, for answers that are the same for every visitor
/// (#94). The loader runs at most once per key at a time: when many callers miss together — the
/// burst when a Premiere goes live — one of them reads the database and the rest wait for its
/// answer, instead of all of them hitting Postgres at once.
/// </summary>
public interface IJsonCache
{
    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, or runs <paramref name="load"/> and caches
    /// what it returns. A null result is cached too ("there is no active Premiere" is an answer).
    /// </summary>
    Task<T?> GetOrAddAsync<T>(string key, Func<Task<T?>> load, CancellationToken ct);

    Task RemoveAsync(IReadOnlyCollection<string> keys, CancellationToken ct);
}
