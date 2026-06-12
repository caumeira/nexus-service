using System;
using System.Threading.Tasks;

namespace Nexus.Service.Tests.SmartLights;

internal static class TestWait
{
    /// <summary>Wait until <paramref name="condition"/> holds (emulator traffic
    /// is async); throws on timeout so failures are loud.</summary>
    public static async Task ForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("condition not met in time");
            await Task.Delay(20);
        }
    }
}
