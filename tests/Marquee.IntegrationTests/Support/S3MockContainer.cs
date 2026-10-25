using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Marquee.IntegrationTests;

/// <summary>
/// The same S3Mock image docker-compose runs, as a throwaway container: the profile-photo bucket
/// (#63) with S3's real API, so the store is tested through the AWS SDK exactly as it runs. Nothing
/// is kept — the store lives in the container's temp directory and goes with it.
/// </summary>
public sealed class S3MockContainer : IAsyncDisposable
{
    public const string Bucket = "marquee-avatars";

    private const string Image = "adobe/s3mock:5.2.3";

    private static readonly HttpClient Http = new();
    private readonly int _port = FreePort();
    private readonly IContainer _container;

    public S3MockContainer()
    {
        _container = new ContainerBuilder(Image)
            .WithPortBinding(_port, 9090)
            .WithEnvironment("COM_ADOBE_TESTING_S3MOCK_STORE_INITIAL_BUCKETS", Bucket)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9090).ForPath($"/{Bucket}")))
            .Build();
    }

    public string Endpoint => $"http://localhost:{_port}";
    public string PublicBaseUrl => $"{Endpoint}/{Bucket}";

    public Task StartAsync() => _container.StartAsync();
    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>The object behind a stored public URL, read anonymously as a browser would.</summary>
    public async Task<HttpResponseMessage> GetAsync(string publicUrl) => await Http.GetAsync(publicUrl);

    public async Task<bool> ExistsAsync(string publicUrl)
    {
        using var response = await GetAsync(publicUrl);
        return response.StatusCode == HttpStatusCode.OK;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
