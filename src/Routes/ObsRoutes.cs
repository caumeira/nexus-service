using Qos.Service.Auth;
using Qos.Service.Models;
using Qos.Service.Models.Obs;
using Qos.Service.Obs;

namespace Qos.Service.Routes;

public static class ObsRoutes
{
    public static void MapObsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/obs/config", (IObsProvider obs) => obs.GetConfig()).AllowPanel();

        app.MapPost("/api/obs/config", (ObsConfigBody body, IObsProvider obs) =>
        {
            obs.SetConfig(body);
            return ApiResponse.Ok();
        }).AllowPanel();

        app.MapGet("/api/obs/status", async (IObsProvider obs, CancellationToken ct) =>
            await obs.GetStatusAsync(ct)).AllowPanel();

        app.MapPost("/api/obs/connect", async (IObsProvider obs, CancellationToken ct) =>
            await obs.GetStatusAsync(ct)).AllowPanel();

        app.MapPost("/api/obs/recording/toggle", async (IObsProvider obs, CancellationToken ct) =>
            await obs.ToggleRecordingAsync(ct)).AllowPanel();

        app.MapPost("/api/obs/streaming/toggle", async (IObsProvider obs, CancellationToken ct) =>
            await obs.ToggleStreamingAsync(ct)).AllowPanel();

        app.MapPost("/api/obs/scene", async (ObsSetSceneBody body, IObsProvider obs, CancellationToken ct) =>
            await obs.SetSceneAsync(body.SceneName, ct)).AllowPanel();

        app.MapPost("/api/obs/launch", (IObsProvider obs) => obs.Launch()).AllowPanel();
    }
}
