using Nexus.Service.Widgets;

namespace Nexus.Service.Tests.Widgets;

public class AppIdsTests
{
    [Theory]
    [InlineData("com.hellonexus.cpu-temp")]
    [InlineData("com.author.widget-id")]
    [InlineData("a.b")]
    [InlineData("com.hellonexus.fixture-basic")]
    public void Accepts_reverse_dns_lowercase_ids(string id)
    {
        Assert.True(AppIds.IsValid(id));
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
    [InlineData("com.hellonexus..traversal")]
    [InlineData("com.hellonexus.trailing-")]
    [InlineData("com.hellonexus.-leadingdashinsegment")]
    public void Rejects_invalid_or_dangerous_ids(string? id)
    {
        Assert.False(AppIds.IsValid(id));
    }

    [Fact]
    public void Rejects_overlong_ids()
    {
        var s = new string('a', AppIds.MaxLength + 1);
        Assert.False(AppIds.IsValid(s + ".x"));
    }
}
