using System;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Peripherals.LianLiTl;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapLianLiTlEndpoints(WebApplication app)
    {
        // GET /devices/lianli-tl/state - connected flag, per-fan port/fanIndex/rpm/duty.
        app.MapGet("/devices/lianli-tl/state", (TlFanHub hub) =>
        {
            // Read snapshot once; index all arrays from this single reference.
            var snap = hub.Snapshot;
            int count = snap.ChannelCount;
            var fans = new LianLiTlFanDto[count];
            for (int i = 0; i < count; i++)
            {
                fans[i] = new LianLiTlFanDto
                {
                    Port = snap.Port[i],
                    FanIndex = snap.FanIndex[i],
                    Rpm = snap.Rpm[i] >= 0 ? snap.Rpm[i] : 0,
                    Duty = snap.Duty[i],
                };
            }
            return Results.Json(
                new LianLiTlStateResponse { IsConnected = hub.IsConnected, Fans = fans },
                AppJsonContext.Default.LianLiTlStateResponse);
        });
    }
}

public sealed class LianLiTlStateResponse
{
    public bool IsConnected { get; set; }
    public LianLiTlFanDto[] Fans { get; set; } = Array.Empty<LianLiTlFanDto>();
}

public sealed class LianLiTlFanDto
{
    public int Port { get; set; }
    public int FanIndex { get; set; }
    public int Rpm { get; set; }
    public int Duty { get; set; }
}
