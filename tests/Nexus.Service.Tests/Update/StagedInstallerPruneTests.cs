using System.IO;
using System.Linq;
using Nexus.Service.Update;
using Xunit;

namespace Nexus.Service.Tests.Update;

public sealed class StagedInstallerPruneTests
{
    [Fact]
    public void PruneSupersededVersions_keeps_only_the_target_installer_and_its_launcher()
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
            // The superseded installer's launcher goes with it; the launcher of
            // the version being staged stays.
            var keepLauncher = UpdateInstaller.LauncherName("v3.0.1-beta.4");
            File.WriteAllText(Path.Combine(dir, keepLauncher), "echo");
            File.WriteAllText(Path.Combine(dir, UpdateInstaller.LauncherName("v3.0.1-beta.2")), "echo");
            // Marker and logs must survive.
            File.WriteAllText(Path.Combine(dir, "pending-install.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "ota-install-v3.0.1-beta.2.log"), "log");

            UpdateDownloader.PruneSupersededVersions(dir, keep, keepLauncher);

            var remaining = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
            Assert.Equal(
                new[] { "Nexus-Setup-v3.0.1-beta.4.exe", "ota-install-v3.0.1-beta.2.log", "pending-install.json", keepLauncher },
                remaining);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
