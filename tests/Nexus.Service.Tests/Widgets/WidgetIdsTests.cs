using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Widgets;

public class WidgetIdsTests
{
    [Theory]
    [InlineData("com.nexusqos.cpu-temp")]
    [InlineData("com.author.widget-id")]
    [InlineData("a.b")]
    [InlineData("com.nexusqos.fixture-basic")]
    public void Accepts_reverse_dns_lowercase_ids(string id)
    {
        Assert.True(WidgetIds.IsValid(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nodot")]
    [InlineData(".leadingdot")]
    [InlineData("trailingdot.")]
    [InlineData("double..dot")]
    [InlineData("UPPERCASE.id")]
    [InlineData("com nexus.spaces")]
    [InlineData("com/nexus/slash")]
    [InlineData("../escape.attempt")]
    [InlineData("com.nexusqos..traversal")]
    [InlineData("com.nexusqos.trailing-")]
    [InlineData("com.nexusqos.-leadingdashinsegment")]
    public void Rejects_invalid_or_dangerous_ids(string? id)
    {
        Assert.False(WidgetIds.IsValid(id));
    }

    [Fact]
    public void Rejects_overlong_ids()
    {
        var s = new string('a', WidgetIds.MaxLength + 1);
        Assert.False(WidgetIds.IsValid(s + ".x"));
    }
}
