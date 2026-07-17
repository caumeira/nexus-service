using Nexus.Service.Activity;
using Nexus.Service.Tests;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public class ProcessSignatureCheckerTests
{
    // Authenticode is a Windows PE concept; the real crypt32 extraction path
    // (AuthenticodeSigner) has no portable unit test, same posture as
    // WindowsIconExtractor - this only pins the non-Windows contract.
    [NonWindowsFact]
    public void Check_OnNonWindows_AlwaysReturnsUnknown()
    {
        var (signed, publisher) = ProcessSignatureChecker.Check("/nonexistent/path");

        Assert.Equal("unknown", signed);
        Assert.Null(publisher);
    }
}
