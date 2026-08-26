using Nexus.Service.Conflicts;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// The path-matching half of autostart discovery. Values are real Run entries
/// captured off a lab box: the entry name never equals the process name, and
/// iCUE's is version-stamped, which is why matching is by target executable.
/// </summary>
public class ConflictAutostartLocatorTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Corsair\\Corsair iCUE5 Software\\iCUE Launcher.exe\" --autorun",
                "C:\\Program Files\\Corsair\\Corsair iCUE5 Software\\iCUE Launcher.exe")]
    [InlineData("\"C:\\Program Files\\Razer\\RazerAppEngine\\RazerAppEngine.exe\" --url-params=apps=synapse",
                "C:\\Program Files\\Razer\\RazerAppEngine\\RazerAppEngine.exe")]
    [InlineData("C:\\Program Files\\AppControl\\ui\\AppControl.exe --minimize",
                "C:\\Program Files\\AppControl\\ui\\AppControl.exe")]
    public void ExecutablePath_DropsQuotesAndArguments(string command, string expected)
    {
        Assert.Equal(expected, ConflictAutostartLocator.ExecutablePath(command));
    }

    [Fact]
    public void ExecutablePath_KeepsSpacesInAnUnquotedPath()
    {
        // Cutting at the first space would truncate to "C:\Program".
        Assert.Equal(
            "C:\\Program Files\\Foo\\bar.exe",
            ConflictAutostartLocator.ExecutablePath("C:\\Program Files\\Foo\\bar.exe -x"));
    }

    [Fact]
    public void SamePath_MatchesTheSameExecutable()
    {
        Assert.True(ConflictAutostartLocator.SamePath(
            "C:\\Program Files\\Razer\\RazerAppEngine\\RazerAppEngine.exe",
            "C:\\program files\\razer\\razerappengine\\RazerAppEngine.exe"));
    }

    [Fact]
    public void SamePath_RejectsASiblingInTheSameDirectory()
    {
        // Only SameDirectory may match a sibling; the exact pass must not, or
        // the two-pass precedence collapses.
        Assert.False(ConflictAutostartLocator.SamePath(
            "C:\\Program Files\\Corsair\\Corsair iCUE5 Software\\iCUE Launcher.exe",
            "C:\\Program Files\\Corsair\\Corsair iCUE5 Software\\iCUE.exe"));
    }

    [Fact]
    public void SameDirectory_MatchesALauncherBesideTheRunningExe()
    {
        // iCUE runs as iCUE.exe; its Run value points at a sibling launcher.
        Assert.True(ConflictAutostartLocator.SameDirectory(
            "C:\\Program Files\\Corsair\\Corsair iCUE5 Software\\iCUE Launcher.exe",
            "C:\\Program Files\\Corsair\\Corsair iCUE5 Software\\iCUE.exe"));
    }

    [Fact]
    public void SameDirectory_RejectsAnUnrelatedProgram()
    {
        // The case name matching gets wrong: Windows' own camsvc vs NZXT CAM.
        Assert.False(ConflictAutostartLocator.SameDirectory(
            "C:\\WINDOWS\\system32\\camsvc.dll",
            "C:\\Program Files\\NZXT CAM\\NZXT CAM.exe"));
    }

    [Fact]
    public void SameDirectory_RejectsASharedInstallRoot()
    {
        // Two unrelated vendors both dropping an exe into Common Files would
        // otherwise match each other.
        Assert.False(ConflictAutostartLocator.SameDirectory(
            "C:\\Program Files\\Common Files\\Foo\\updater.exe",
            "C:\\Program Files\\Common Files\\Foo\\other.exe"));
        Assert.False(ConflictAutostartLocator.SameDirectory(
            "C:\\Program Files (x86)\\updater.exe",
            "C:\\Program Files (x86)\\other.exe"));
        Assert.False(ConflictAutostartLocator.SameDirectory(
            "C:\\Users\\Nicola\\AppData\\Roaming\\updater.exe",
            "C:\\Users\\Nicola\\AppData\\Roaming\\other.exe"));
        Assert.False(ConflictAutostartLocator.SameDirectory(
            "C:\\Users\\Nicola\\updater.exe",
            "C:\\Users\\Nicola\\other.exe"));
    }

    [Fact]
    public void SameDirectory_AcceptsAVendorDirectoryUnderASharedRoot()
    {
        Assert.True(ConflictAutostartLocator.SameDirectory(
            "C:\\Users\\Nicola\\AppData\\Local\\NZXT CAM\\launcher.exe",
            "C:\\Users\\Nicola\\AppData\\Local\\NZXT CAM\\NZXT CAM.exe"));
    }

    [Fact]
    public void SamePath_RejectsEmptyInput()
    {
        Assert.False(ConflictAutostartLocator.SamePath("", "C:\\a\\b.exe"));
        Assert.False(ConflictAutostartLocator.SamePath("C:\\a\\b.exe", ""));
        Assert.False(ConflictAutostartLocator.SameDirectory("", "C:\\a\\b.exe"));
        Assert.False(ConflictAutostartLocator.SameDirectory("C:\\a\\b.exe", ""));
    }

    [Theory]
    // The service is LocalSystem, so RegistryKey's own expansion would resolve
    // these under config\systemprofile and no per-user install would match.
    [InlineData("%LOCALAPPDATA%\\NZXT CAM\\NZXT CAM.exe",
                "C:\\Users\\Nicola\\AppData\\Local\\NZXT CAM\\NZXT CAM.exe")]
    [InlineData("%APPDATA%\\Foo\\foo.exe",
                "C:\\Users\\Nicola\\AppData\\Roaming\\Foo\\foo.exe")]
    [InlineData("\"%UserProfile%\\Foo\\foo.exe\" --autorun",
                "\"C:\\Users\\Nicola\\Foo\\foo.exe\" --autorun")]
    public void ExpandForConsoleUser_ResolvesPerUserVariablesAgainstTheConsoleProfile(string value, string expected)
    {
        Assert.Equal(expected, ConflictAutostartLocator.ExpandForConsoleUser(value, "C:\\Users\\Nicola"));
    }

    [Fact]
    public void ExpandForConsoleUser_LeavesAnUnknownVariableAlone()
    {
        // An unresolvable variable stays literal rather than collapsing to a
        // path that would match the wrong directory.
        Assert.Equal(
            "%NEXUS_NO_SUCH_VAR%\\Foo\\foo.exe",
            ConflictAutostartLocator.ExpandForConsoleUser("%NEXUS_NO_SUCH_VAR%\\Foo\\foo.exe", "C:\\Users\\Nicola"));
    }
}
