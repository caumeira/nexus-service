using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Mcp.Assistant;
using Xunit;

namespace Nexus.Service.Tests.Mcp.Assistant;

public sealed class OllamaClientTests
{
    private const int Port = 11555;

    [Fact]
    public async Task TryGetVersionAsync_returns_the_version_on_success()
    {
        var client = new OllamaClient(new HttpClient(new RouteHandler(req =>
            Json(HttpStatusCode.OK, "{\"version\":\"0.32.1\"}"))), Port);

        var version = await client.TryGetVersionAsync(CancellationToken.None);

        Assert.Equal("0.32.1", version);
    }

    [Fact]
    public async Task TryGetVersionAsync_returns_null_on_non_success_status()
    {
        var client = new OllamaClient(new HttpClient(new RouteHandler(req =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))), Port);

        Assert.Null(await client.TryGetVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TryGetVersionAsync_returns_null_when_the_connection_is_refused()
    {
        var client = new OllamaClient(new HttpClient(new RouteHandler(req => throw new HttpRequestException("refused"))), Port);

        Assert.Null(await client.TryGetVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ListModelsAsync_parses_snake_case_details()
    {
        var body = "{\"models\":[{\"name\":\"qwen3.5:4b\",\"model\":\"qwen3.5:4b\",\"size\":3400000000," +
                    "\"digest\":\"abc123\",\"details\":{\"format\":\"gguf\",\"family\":\"qwen3\"," +
                    "\"parameter_size\":\"4B\",\"quantization_level\":\"Q4_0\"}}]}";
        var client = new OllamaClient(new HttpClient(new RouteHandler(req => Json(HttpStatusCode.OK, body))), Port);

        var models = await client.ListModelsAsync(CancellationToken.None);

        var model = Assert.Single(models);
        Assert.Equal("qwen3.5:4b", model.Name);
        Assert.Equal(3_400_000_000, model.Size);
        Assert.NotNull(model.Details);
        Assert.Equal("4B", model.Details!.ParameterSize);
        Assert.Equal("Q4_0", model.Details.QuantizationLevel);
    }

    [Fact]
    public async Task PullModelAsync_reports_each_ndjson_progress_line()
    {
        var ndjson =
            "{\"status\":\"pulling manifest\"}\n" +
            "{\"status\":\"downloading\",\"digest\":\"sha256:abc\",\"total\":100,\"completed\":50}\n" +
            "{\"status\":\"downloading\",\"digest\":\"sha256:abc\",\"total\":100,\"completed\":100}\n" +
            "{\"status\":\"success\"}\n";
        var client = new OllamaClient(new HttpClient(new RouteHandler(req => Json(HttpStatusCode.OK, ndjson))), Port);

        var statuses = new List<string>();
        await client.PullModelAsync("qwen3.5:0.8b", s => statuses.Add(s.Status), CancellationToken.None);

        Assert.Equal(new[] { "pulling manifest", "downloading", "downloading", "success" }, statuses);
    }

    [Fact]
    public async Task PullModelAsync_throws_on_a_mid_stream_error_line()
    {
        var ndjson = "{\"status\":\"pulling manifest\"}\n{\"error\":\"model not found\"}\n";
        var client = new OllamaClient(new HttpClient(new RouteHandler(req => Json(HttpStatusCode.OK, ndjson))), Port);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PullModelAsync("bogus", _ => { }, CancellationToken.None));
        Assert.Contains("model not found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteModelAsync_returns_true_on_200_and_false_on_404()
    {
        var okClient = new OllamaClient(new HttpClient(new RouteHandler(req => new HttpResponseMessage(HttpStatusCode.OK))), Port);
        var missingClient = new OllamaClient(new HttpClient(new RouteHandler(req => new HttpResponseMessage(HttpStatusCode.NotFound))), Port);

        Assert.True(await okClient.DeleteModelAsync("qwen3.5:4b", CancellationToken.None));
        Assert.False(await missingClient.DeleteModelAsync("qwen3.5:4b", CancellationToken.None));
    }

    [Fact]
    public async Task ChatAsync_parses_a_tool_call_with_object_arguments()
    {
        var body = "{\"model\":\"qwen3.5:4b\",\"message\":{\"role\":\"assistant\",\"content\":\"\"," +
                    "\"tool_calls\":[{\"function\":{\"name\":\"get_system_overview\",\"arguments\":{}}}]},\"done\":true}";
        var client = new OllamaClient(new HttpClient(new RouteHandler(req => Json(HttpStatusCode.OK, body))), Port);

        var response = await client.ChatAsync(
            "qwen3.5:4b",
            new[] { new OllamaChatMessage { Role = "user", Content = "what's my cpu temp" } },
            tools: null,
            CancellationToken.None);

        var call = Assert.Single(response.Message.ToolCalls!);
        Assert.Equal("get_system_overview", call.Function.Name);
        Assert.NotNull(call.Function.Arguments);
    }

    [Fact]
    public async Task ChatAsync_parses_a_plain_final_answer_with_no_tool_calls()
    {
        var body = "{\"model\":\"qwen3.5:4b\",\"message\":{\"role\":\"assistant\",\"content\":\"Your CPU is at 45C.\"},\"done\":true}";
        var client = new OllamaClient(new HttpClient(new RouteHandler(req => Json(HttpStatusCode.OK, body))), Port);

        var response = await client.ChatAsync(
            "qwen3.5:4b",
            new[] { new OllamaChatMessage { Role = "user", Content = "cpu temp?" } },
            tools: null,
            CancellationToken.None);

        Assert.Null(response.Message.ToolCalls);
        Assert.Equal("Your CPU is at 45C.", response.Message.Content);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RouteHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
