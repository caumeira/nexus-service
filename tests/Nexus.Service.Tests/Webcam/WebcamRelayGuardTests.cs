using Microsoft.AspNetCore.Http;
using Nexus.Service.Relay;
using Nexus.Service.Webcam;

namespace Nexus.Service.Tests.Webcam;

public class WebcamRelayGuardTests
{
    [Fact]
    public void DirectRequest_IsNotRefused()
    {
        var ctx = new DefaultHttpContext();

        Assert.False(WebcamRelayGuard.IsRelayDispatch(ctx));
        Assert.Null(WebcamRelayGuard.Refuse(ctx));
    }

    [Fact]
    public void RelayDispatch_IsRefusedWithForbidden()
    {
        // Any value at the trusted-dispatch key marks the relay lane; the guard
        // refuses on presence alone (HttpContext.Items is server-populated only).
        var ctx = new DefaultHttpContext();
        ctx.Items[RelayHttpDispatcher.TrustedRelayDispatchKey] = new object();

        Assert.True(WebcamRelayGuard.IsRelayDispatch(ctx));
        var refused = WebcamRelayGuard.Refuse(ctx);
        Assert.NotNull(refused);
        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(refused);
        Assert.Equal(StatusCodes.Status403Forbidden, statusResult.StatusCode);
    }
}
