using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using SkiaSharp;

namespace Marquee.IntegrationTests;

/// <summary>
/// Adding, changing and removing your own profile photo (#63), end to end: HTTP in, a re-encoded
/// WebP in the bucket, the URL on the user row, and the previous photo gone from the bucket.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AvatarEndpointTests(MarqueeAppFactory factory)
{
    private const string Route = "/api/users/me/avatar";

    private sealed record UserBody(string? AvatarUrl);
    private sealed record ErrorBody(string Error);

    [Fact]
    public async Task Upload_sets_avatar_url_and_stores_object()
    {
        var (client, _, userId) = await TestAuth.NewUserAsync(factory, "av_up");

        var response = await client.PutAsync(Route, Photo(Jpeg(900, 600)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var url = (await response.Content.ReadFromJsonAsync<UserBody>())!.AvatarUrl;
        url.Should().StartWith($"{factory.S3.PublicBaseUrl}/avatars/{userId}/");
        (await client.GetFromJsonAsync<UserBody>("/api/auth/me"))!.AvatarUrl.Should().Be(url);

        // What landed is the API's own image, not the upload: a 640×640 WebP.
        using var stored = await factory.S3.GetAsync(url!);
        using var codec = SKCodec.Create(new MemoryStream(await stored.Content.ReadAsByteArrayAsync()));
        codec.EncodedFormat.Should().Be(SKEncodedImageFormat.Webp);
        (codec.Info.Width, codec.Info.Height).Should().Be((640, 640));
    }

    [Fact]
    public async Task Replace_deletes_previous_object()
    {
        var (client, _, _) = await TestAuth.NewUserAsync(factory, "av_re");
        var first = await UploadAsync(client);

        var second = await UploadAsync(client);

        second.Should().NotBe(first);
        (await factory.S3.ExistsAsync(first)).Should().BeFalse();
        (await factory.S3.ExistsAsync(second)).Should().BeTrue();
    }

    [Fact]
    public async Task Remove_clears_url_and_deletes_object()
    {
        var (client, _, _) = await TestAuth.NewUserAsync(factory, "av_rm");
        var url = await UploadAsync(client);

        var response = await client.DeleteAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<UserBody>())!.AvatarUrl.Should().BeNull();
        (await factory.S3.ExistsAsync(url)).Should().BeFalse();
    }

    [Fact]
    public async Task Remove_without_photo_is_ok()
    {
        var (client, _, _) = await TestAuth.NewUserAsync(factory, "av_none");

        var response = await client.DeleteAsync(Route);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<UserBody>())!.AvatarUrl.Should().BeNull();
    }

    [Fact]
    public async Task Anonymous_upload_is_401()
    {
        var stranger = factory.CreateClient();
        (await stranger.PutAsync(Route, Photo(Jpeg(100, 100)))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var session = await (await stranger.PostAsJsonAsync("/api/sessions/anonymous", new { }))
            .Content.ReadFromJsonAsync<Dictionary<string, string>>();
        stranger.DefaultRequestHeaders.Add("X-Anon-Session", session!["token"]);
        (await stranger.PutAsync(Route, Photo(Jpeg(100, 100)))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await stranger.DeleteAsync(Route)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Non_image_is_400_with_message()
    {
        var (client, _, _) = await TestAuth.NewUserAsync(factory, "av_bad");

        var response = await client.PutAsync(Route, Photo("not a picture"u8.ToArray()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ErrorBody>())!.Error
            .Should().Be("That file type isn't supported. Use a JPEG, PNG or WebP.");
        (await client.GetFromJsonAsync<UserBody>("/api/auth/me"))!.AvatarUrl.Should().BeNull();
    }

    [Fact]
    public async Task Oversize_is_413()
    {
        var (client, _, _) = await TestAuth.NewUserAsync(factory, "av_big");

        var response = await client.PutAsync(Route, Photo(new byte[5_000_001]));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Photo_shows_on_private_profile_for_strangers()
    {
        var (owner, username, _) = await TestAuth.NewUserAsync(factory, "av_priv");
        var url = await UploadAsync(owner);
        await owner.PatchAsJsonAsync("/api/users/me", new { isPrivate = true });
        var (stranger, _, _) = await TestAuth.NewUserAsync(factory, "av_str");

        var profile = await stranger.GetFromJsonAsync<UserBody>($"/api/users/{username}");

        profile!.AvatarUrl.Should().Be(url, "a photo is public, like a username (#63)");
    }

    private sealed record PersonBody(string Username, string? AvatarUrl);

    [Fact]
    public async Task Photo_reaches_search_requests_and_friend_lists_and_null_stays_null()
    {
        var (viewer, viewerName, _) = await TestAuth.NewUserAsync(factory, "av_fv");
        var (withPhoto, photoName, _) = await TestAuth.NewUserAsync(factory, "av_fp");
        var (noPhoto, noPhotoName, _) = await TestAuth.NewUserAsync(factory, "av_fn");
        var url = await UploadAsync(withPhoto);

        // Search: the same payload for a stranger, photo or not.
        var hits = await viewer.GetFromJsonAsync<List<PersonBody>>($"/api/users?query=av_f");
        hits!.Single(h => h.Username == photoName).AvatarUrl.Should().Be(url);
        hits!.Single(h => h.Username == noPhotoName).AvatarUrl.Should().BeNull();

        // Requests: the other party's photo, on the send response and on the pending list.
        foreach (var (client, name) in new[] { (withPhoto, photoName), (noPhoto, noPhotoName) })
            (await client.PostAsJsonAsync("/api/friends/requests", new { username = viewerName }))
                .EnsureSuccessStatusCode();
        var pending = await viewer.GetFromJsonAsync<List<PersonBody>>("/api/friends/requests");
        pending!.Single(r => r.Username == photoName).AvatarUrl.Should().Be(url);
        pending!.Single(r => r.Username == noPhotoName).AvatarUrl.Should().BeNull();

        // Accepting answers with the requester's photo too.
        var requests = await viewer.GetFromJsonAsync<List<System.Text.Json.JsonElement>>("/api/friends/requests");
        foreach (var r in requests!)
        {
            var accepted = await viewer.PostAsJsonAsync($"/api/friends/requests/{r.GetProperty("id").GetGuid()}/accept", new { });
            accepted.EnsureSuccessStatusCode();
            var body = await accepted.Content.ReadFromJsonAsync<PersonBody>();
            body!.AvatarUrl.Should().Be(body.Username == photoName ? url : null);
        }

        // Friend lists: yours, and a stranger's view of yours.
        var own = await viewer.GetFromJsonAsync<List<PersonBody>>("/api/friends");
        own!.Single(f => f.Username == photoName).AvatarUrl.Should().Be(url);
        own!.Single(f => f.Username == noPhotoName).AvatarUrl.Should().BeNull();

        var (stranger, _, _) = await TestAuth.NewUserAsync(factory, "av_fs");
        var theirs = await stranger.GetFromJsonAsync<List<PersonBody>>($"/api/users/{viewerName}/friends");
        theirs!.Single(f => f.Username == photoName).AvatarUrl.Should().Be(url);
    }

    private static async Task<string> UploadAsync(HttpClient client)
    {
        var response = await client.PutAsync(Route, Photo(Jpeg(300, 300)));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<UserBody>())!.AvatarUrl!;
    }

    private static MultipartFormDataContent Photo(byte[] bytes)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return new MultipartFormDataContent { { file, "file", "photo.jpg" } };
    }

    private static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }
}
