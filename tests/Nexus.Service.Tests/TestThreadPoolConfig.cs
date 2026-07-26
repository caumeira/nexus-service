using System.Runtime.CompilerServices;
using System.Threading;

namespace Nexus.Service.Tests;

internal static class TestThreadPoolConfig
{
    /// <summary>
    /// Raises the worker floor before any test runs. The suite has ~26 blocking
    /// waits on async work (.Result / .Wait / GetAwaiter().GetResult()), and
    /// xUnit runs collections in parallel, so pool threads block on work that
    /// itself needs a pool thread. Past the floor the runtime injects only about
    /// one thread per second, which turns that into a multi-minute stall the
    /// hang timeout kills. A floor above the parallel width lets the injection
    /// happen at once instead.
    /// </summary>
    [ModuleInitializer]
    internal static void Init()
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        var floor = Math.Max(Environment.ProcessorCount * 8, 64);
        ThreadPool.SetMinThreads(Math.Max(worker, floor), Math.Max(io, floor));
    }
}
