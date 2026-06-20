using System.IO;
using Nexus.Service.Platform;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// ServiceLog tees Console.Out / Console.Error to a rotating nexus-service.log
/// under per-platform LocalAppData. We only smoke-test that it doesn't throw
/// and that subsequent Console.WriteLine reaches the resolved file. Full
/// rotation behaviour is hard to assert deterministically without a 5 MB
/// write so we cover that path indirectly via the helper.
/// </summary>
public class ServiceLogTests
{
    [Fact]
    public void Initialize_does_not_throw_and_resolves_a_path()
    {
        ServiceLog.Initialize();
        // Path resolution may fail on locked-down sandbox CI, but on every
        // dev / production target we expect a usable directory.
        Assert.NotNull(ServiceLog.LogFilePath);
    }

    [NonWindowsFact]
    public void Console_write_after_init_appears_in_log_file()
    {
        ServiceLog.Initialize();
        var marker = "service-log-marker-" + Guid.NewGuid().ToString("N");
        Console.WriteLine(marker);

        var path = ServiceLog.LogFilePath;
        Assert.NotNull(path);
        // FileShare.Read keeps the writer open; reopening for read must
        // succeed concurrently.
        var contents = File.ReadAllText(path!);
        Assert.Contains(marker, contents);
    }
}
