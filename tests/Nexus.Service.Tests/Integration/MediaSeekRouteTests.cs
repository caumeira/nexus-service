using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Activity;
using Nexus.Service.Auth;
using Nexus.Service.Models.Activity;
using Xunit;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// POST /api/media/{source}/seek over the real pipeline with the media
/// provider swapped at the DI seam. The route is the only thing that can be
/// exercised off-platform - the SMTC / AppleScript / MPRIS legs need their
/// respective OS.
/// </summary>
[Collection("NexusHost")]
public sealed class MediaSeekRouteTests
{
    private sealed class RecordingMediaProvider : IMediaProvider
    {
        public readonly List<(string Source, long PositionMs)> Seeks = new();
        public IReadOnlyDictionary<string, MediaSession> GetSessions() => new Dictionary<string, MediaSession>();
        public void Control(string source, string action) { }
        public void Seek(string source, long positionMs) => Seeks.Add((source, positionMs));
        public byte[] GetAlbumArt(string source) => System.Array.Empty<byte>();
    }

    private readonly RecordingMediaProvider _provider = new();

    private (WebApplicationFactory<Program> factory, HttpClient client) Boot()
    {
        var factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IMediaProvider>();
                s.AddSingleton<IMediaProvider>(_provider);
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return (factory, client);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Seek_forwards_absolute_position_to_the_provider()
    {
        var (factory, client) = Boot();
        using var _ = factory;

        var res = await client.PostAsync("/api/media/Spotify/seek", Json("{\"positionMs\":42500}"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Single(_provider.Seeks);
        Assert.Equal(("Spotify", 42500L), _provider.Seeks[0]);
    }

    [Fact]
    public async Task Seek_rejects_a_negative_position_without_calling_the_provider()
    {
        var (factory, client) = Boot();
        using var _ = factory;

        var res = await client.PostAsync("/api/media/Spotify/seek", Json("{\"positionMs\":-1}"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(_provider.Seeks);
    }

    [Fact]
    public async Task Seek_accepts_zero_as_a_restart()
    {
        var (factory, client) = Boot();
        using var _ = factory;

        var res = await client.PostAsync("/api/media/Spotify/seek", Json("{\"positionMs\":0}"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(("Spotify", 0L), Assert.Single(_provider.Seeks));
    }
}
