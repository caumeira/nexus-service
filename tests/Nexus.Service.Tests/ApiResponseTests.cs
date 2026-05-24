using Nexus.Service.Models;

namespace Nexus.Service.Tests;

public class ApiResponseTests
{
    [Fact]
    public void Ok_ReturnsNoError()
    {
        var response = ApiResponse.Ok();
        Assert.False(response.Error);
        Assert.Equal("Ok", response.Msg);
    }

    [Fact]
    public void Ok_WithMessage()
    {
        var response = ApiResponse.Ok("done");
        Assert.False(response.Error);
        Assert.Equal("done", response.Msg);
    }

    [Fact]
    public void Fail_ReturnsError()
    {
        var response = ApiResponse.Fail("something broke");
        Assert.True(response.Error);
        Assert.Equal("something broke", response.Msg);
    }

    [Fact]
    public void Fail_WithException()
    {
        var ex = new InvalidOperationException("test");
        var response = ApiResponse.Fail("crashed", ex);
        Assert.True(response.Error);
        Assert.Same(ex, response.Exception);
    }

    [Fact]
    public void Default_IsNotError()
    {
        var response = new ApiResponse();
        Assert.False(response.Error);
        Assert.Equal("Ok", response.Msg);
        Assert.Null(response.Exception);
    }
}
