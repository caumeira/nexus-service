using Nexus.Service.Mcp.Assistant;
using Xunit;

namespace Nexus.Service.Tests.Mcp.Assistant;

public sealed class AssistantReasoningStripperTests
{
    [Fact]
    public void Strip_removes_a_single_think_block()
    {
        var content = "<think>the user wants the CPU temp</think>Your CPU is at 45C.";
        Assert.Equal("Your CPU is at 45C.", AssistantReasoningStripper.Strip(content));
    }

    [Fact]
    public void Strip_removes_a_multiline_think_block()
    {
        var content = "<think>\nstep one\nstep two\n</think>\nDone.";
        Assert.Equal("Done.", AssistantReasoningStripper.Strip(content));
    }

    [Fact]
    public void Strip_removes_multiple_think_blocks()
    {
        var content = "<think>a</think>Part one. <think>b</think>Part two.";
        Assert.Equal("Part one. Part two.", AssistantReasoningStripper.Strip(content));
    }

    [Fact]
    public void Strip_leaves_content_without_a_think_block_unchanged()
    {
        Assert.Equal("Just an answer.", AssistantReasoningStripper.Strip("Just an answer."));
    }

    [Fact]
    public void Strip_trims_surrounding_whitespace()
    {
        Assert.Equal("Answer.", AssistantReasoningStripper.Strip("  <think>reasoning</think>  Answer.  "));
    }

    [Fact]
    public void Strip_of_empty_content_returns_empty()
    {
        Assert.Equal("", AssistantReasoningStripper.Strip(""));
    }

    [Fact]
    public void Strip_removes_an_unterminated_trailing_think_block()
    {
        Assert.Equal("", AssistantReasoningStripper.Strip("<think>the reasoning was cut off mid"));
    }

    [Fact]
    public void Strip_removes_an_unterminated_trailing_think_block_after_a_completed_one()
    {
        var content = "<think>a</think>Part one. <think>b cut off";
        Assert.Equal("Part one.", AssistantReasoningStripper.Strip(content));
    }
}
