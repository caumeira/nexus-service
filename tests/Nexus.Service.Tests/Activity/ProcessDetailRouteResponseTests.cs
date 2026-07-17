using Nexus.Service.Activity;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class ProcessDetailRouteResponseTests
{
    private static readonly ProcessFileDetail EmptyDetail = new(null, null, null, "unknown", null, null, null);

    [Fact]
    public void BuildProcessInfoResponse_IsSupported_AndMapsEveryField()
    {
        var detail = new ProcessFileDetail("App", "1.2.3", "Acme", "signed", "Acme Inc", 1000, 2000);

        var response = ProcessDetailRoutes.BuildProcessInfoResponse(
            "app.exe", "C:\\app.exe", instanceCount: 2, startedAtMs: 3000, detail, sha256: "abc", firstSeenMs: 4000);

        Assert.True(response.Supported);
        Assert.Equal("app.exe", response.Name);
        Assert.Equal("C:\\app.exe", response.Path);
        Assert.Equal(2, response.InstanceCount);
        Assert.Equal(3000, response.StartedAtMs);
        Assert.Equal("App", response.Description);
        Assert.Equal("1.2.3", response.Version);
        Assert.Equal("Acme", response.Company);
        Assert.Equal("Acme Inc", response.Publisher);
        Assert.Equal("signed", response.Signed);
        Assert.Equal("abc", response.Sha256);
        Assert.Equal(1000, response.CreatedAtMs);
        Assert.Equal(2000, response.ModifiedAtMs);
        Assert.Equal(4000, response.FirstSeenMs);
    }

    [Fact]
    public void BuildProcessInfoResponse_LeavesOptionalFieldsNull_WhenDetailCouldNotBeResolved()
    {
        var response = ProcessDetailRoutes.BuildProcessInfoResponse(
            "app.exe", "C:\\app.exe", instanceCount: 0, startedAtMs: null, EmptyDetail, sha256: null, firstSeenMs: null);

        Assert.Equal(0, response.InstanceCount);
        Assert.Null(response.StartedAtMs);
        Assert.Null(response.Description);
        Assert.Null(response.Version);
        Assert.Null(response.Company);
        Assert.Null(response.Publisher);
        Assert.Equal("unknown", response.Signed);
        Assert.Null(response.Sha256);
        Assert.Null(response.CreatedAtMs);
        Assert.Null(response.ModifiedAtMs);
        Assert.Null(response.FirstSeenMs);
    }
}
