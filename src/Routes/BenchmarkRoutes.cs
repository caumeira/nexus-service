using Nexus.Service.Benchmarks;
using Nexus.Service.Models;
using Nexus.Service.Models.Benchmarks;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static class BenchmarkRoutes
{
    public static void MapBenchmarkEndpoints(this WebApplication app)
    {
        app.MapPost("/benchmark/start", (StartBenchmarkBody body, BenchmarkRunner runner) =>
        {
            var runId = runner.Start(body?.IncludeGpu ?? true);
            if (runId is null)
            {
                return Results.Conflict(new StartBenchmarkResponse
                {
                    Started = false,
                    Error = "a benchmark is already running",
                });
            }
            return Results.Ok(new StartBenchmarkResponse
            {
                Started = true,
                RunId = runId,
            });
        });

        app.MapGet("/benchmark/status/{runId}", (string runId, BenchmarkRunner runner) =>
        {
            var frame = runner.GetStatus(runId);
            if (frame is null)
                return Results.NotFound(ApiResponse.Fail("unknown run"));
            return Results.Ok(frame);
        });

        app.MapGet("/benchmark/result/{runId}", (string runId, BenchmarkRunner runner) =>
        {
            var result = runner.GetResult(runId);
            if (result is null)
            {
                return Results.Json(
                    ApiResponse.Fail("result not available"),
                    AppJsonContext.Default.ApiResponse,
                    statusCode: runner.State == BenchmarkState.Running ? 409 : 404);
            }
            return Results.Ok(result);
        });

        app.MapPost("/benchmark/cancel/{runId}", (string runId, BenchmarkRunner runner) =>
        {
            var ok = runner.Cancel(runId);
            return ok ? Results.Ok(ApiResponse.Ok()) : Results.BadRequest(ApiResponse.Fail("not running"));
        });

        app.MapPost("/benchmark/reset", (BenchmarkRunner runner) =>
        {
            runner.Reset();
            return Results.Ok(ApiResponse.Ok());
        });
    }
}
