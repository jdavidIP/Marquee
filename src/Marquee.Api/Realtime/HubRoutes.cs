namespace Marquee.Api.Realtime;

public static class HubRoutes
{
    /// <summary>
    /// Where <see cref="PremiereHub"/> is mapped. Shared with the JWT bearer configuration, which
    /// only honours a query-string token under this path.
    /// </summary>
    public const string Premieres = "/hubs/premieres";
}
