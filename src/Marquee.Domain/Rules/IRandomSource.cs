namespace Marquee.Domain.Rules;

/// <summary>
/// Abstraction over randomness so the "draw the rolls" layer of each formula is testable.
/// The pure calculators below take rolls explicitly; this only feeds them.
/// </summary>
public interface IRandomSource
{
    /// <summary>Uniform double in [0, 1).</summary>
    double NextDouble();

    /// <summary>Uniform integer in [minInclusive, maxInclusive].</summary>
    int NextInt(int minInclusive, int maxInclusive);
}

/// <summary>Default System.Random-backed source. Not for use in unit tests.</summary>
public sealed class SystemRandomSource : IRandomSource
{
    private readonly Random _random;

    public SystemRandomSource(Random? random = null) => _random = random ?? Random.Shared;

    public double NextDouble() => _random.NextDouble();

    public int NextInt(int minInclusive, int maxInclusive) => _random.Next(minInclusive, maxInclusive + 1);
}
