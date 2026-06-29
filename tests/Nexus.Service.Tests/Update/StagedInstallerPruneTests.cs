using System.IO;
using System.Linq;
using Nexus.Service.Update;
using Xunit;

namespace Nexus.Service.Tests.Update;

public sealed class StagedInstallerPruneTests
{
    [Fact]
    public void PruneStaleInstallers_keeps_only_the_target_installer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-prune-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var keep = "Nexus-Setup-v3.0.1-beta.4.exe";
            File.WriteAllText(Path.Combine(dir, keep), "new");
            File.WriteAllText(Path.Combine(dir, "Nexus-Setup-v3.0.1-beta.2.exe"), "old");
            File.WriteAllText(Path.Combine(dir, "Nexus-Setup-v3.0.1-beta.3.exe"), "old");
            // A partial download from a superseded attempt.
            File.WriteAllText(Path.Combine(dir, "Nexus-Setup-v3.0.1-beta.3.exe.tmp"), "partial");
            // Non-installer artifacts must survive (marker / launcher / log).
            File.WriteAllText(Path.Combine(dir, "pending-install.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "run-ota-v3.0.1-beta.2.cmd"), "echo");
            File.WriteAllText(Path.Combine(dir, "ota-install-v3.0.1-beta.2.log"), "log");

            UpdateDownloader.PruneStaleInstallers(dir, keep);

            var remaining = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            Assert.Equal(
                new[] { "Nexus-Setup-v3.0.1-beta.4.exe", "ota-install-v3.0.1-beta.2.log", "pending-install.json", "run-ota-v3.0.1-beta.2.cmd" },
                remaining);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
