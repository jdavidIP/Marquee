namespace Marquee.Api.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Key { get; set; } = "";
    public string Issuer { get; set; } = "marquee";
    public string Audience { get; set; } = "marquee";
    public int ExpiryHours { get; set; } = 24;
}
