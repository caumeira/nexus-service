using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Nexus.Service.Cloud;
using Xunit;

namespace Nexus.Service.Tests.Cloud;

/// <summary>
/// Exercises CloudApiClient's real HTTP path (not the fake) against a local
/// TCP listener that accepts the connection but never responds, so
/// HttpClient.Timeout fires. That produces a TaskCanceledException whose
/// CancellationToken is HttpClient's own internal linked token, distinct from
/// the caller's ct - the exact case the catch filters across the Cloud
/// subsystem must classify as Offline rather than rethrow (a rethrow escapes
/// CloudProfileSyncService's RunGuardedAsync/CloudDeviceReporter's guard and
/// faults the BackgroundService, which the default
/// BackgroundServiceExceptionBehavior=StopHost turns into the whole service
/// exiting on a single slow api.hellonexus.com call).
///
/// Uses the internal (baseUrl, requestTimeout) constructor rather than the
/// NEXUS_API_BASE environment variable - that variable is process-wide state
/// and mutating it would race any other test that constructs a real
/// CloudApiClient (NexusAppFactory's DI container does, for one) under
/// parallel test execution.
/// </summary>
public sealed class CloudApiClientTests
{
    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [Fact]
    public async Task Timeout_shaped_cancellation_is_classified_offline_not_rethrown()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // Accept and hold the connection open; never write a response, so the
        // client's own short Timeout below is what ends the request.
        var acceptTask = listener.AcceptTcpClientAsync();

        try
        {
            var client = new CloudApiClient(new SingleClientFactory(), $"http://127.0.0.1:{port}", TimeSpan.FromMilliseconds(200));

            var result = await client.LogoutAsync("some-refresh-token", CancellationToken.None);

            Assert.False(result.Success);
            Assert.True(result.Offline);
        }
        finally
        {
            listener.Stop();
            try
            {
                using var accepted = await acceptTask;
            }
            catch { }
        }
    }

    [Fact]
    public async Task Avatar_upload_posts_multipart_field_named_file()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var requestTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync();
            using var stream = accepted.GetStream();
            var buffer = new byte[16384];
            var request = new System.Text.StringBuilder();
            // Read until the terminal multipart boundary; a single read is not
            // guaranteed to capture headers and body in one segment.
            while (!request.ToString().Contains("--\r\n"))
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0)
                {
                    break;
                }
                request.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
            }
            var response = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}";
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(response));
            return request.ToString();
        });

        try
        {
            var client = new CloudApiClient(new SingleClientFactory(), $"http://127.0.0.1:{port}", TimeSpan.FromSeconds(5));

            await client.UploadAvatarAsync("token", new byte[] { 1, 2, 3 }, "image/png", CancellationToken.None);

            var request = await requestTask;
            // nexus-api's FileInterceptor('file') 400s any other field name.
            // .NET quotes the disposition name only when it is not a simple
            // token, so accept both forms; the trailing delimiter keeps a
            // filename=... parameter from ever matching.
            Assert.Matches("name=\"?file\"?;", request);
        }
        finally
        {
            listener.Stop();
        }
    }
}
