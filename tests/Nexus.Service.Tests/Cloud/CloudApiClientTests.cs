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
}
