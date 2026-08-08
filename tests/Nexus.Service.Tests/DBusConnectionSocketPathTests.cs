using Nexus.Service.Platform.Linux.DBus;
using Xunit;

namespace Nexus.Service.Tests;

/// <summary>
/// Pure tests for DBusConnection.ResolveSocketPath, the seam behind the
/// session-vs-system bus socket selection LinuxResumeListener relies on.
/// Runs on any OS (string logic only, no socket I/O).
/// </summary>
public class DBusConnectionSocketPathTests
{
    [Fact]
    public void Session_NoEnvOverride_UsesPerUidRuntimeSocket()
    {
        var path = DBusConnection.ResolveSocketPath(DBusBusKind.Session, sessionAddress: null, systemAddress: null, uid: 1000);

        Assert.Equal("/run/user/1000/bus", path);
    }

    [Fact]
    public void Session_EnvOverride_ExtractsUnixPath()
    {
        var path = DBusConnection.ResolveSocketPath(
            DBusBusKind.Session, sessionAddress: "unix:path=/run/user/1000/bus,guid=abc123", systemAddress: null, uid: 1000);

        Assert.Equal("/run/user/1000/bus", path);
    }

    [Fact]
    public void Session_EnvOverride_WithoutUnixPathPrefix_FallsBackToDefault()
    {
        var path = DBusConnection.ResolveSocketPath(
            DBusBusKind.Session, sessionAddress: "tcp:host=127.0.0.1,port=1234", systemAddress: null, uid: 42);

        Assert.Equal("/run/user/42/bus", path);
    }

    [Fact]
    public void System_NoEnvOverride_UsesWellKnownSocket()
    {
        var path = DBusConnection.ResolveSocketPath(DBusBusKind.System, sessionAddress: null, systemAddress: null, uid: 0);

        Assert.Equal("/run/dbus/system_bus_socket", path);
    }

    [Fact]
    public void System_EnvOverride_ExtractsUnixPath()
    {
        var path = DBusConnection.ResolveSocketPath(
            DBusBusKind.System, sessionAddress: null, systemAddress: "unix:path=/custom/system_bus_socket", uid: 0);

        Assert.Equal("/custom/system_bus_socket", path);
    }

    [Fact]
    public void System_IgnoresSessionAddress()
    {
        // The system-bus lookup must never fall back to the session address -
        // that would silently authenticate a resume watcher on the wrong bus.
        var path = DBusConnection.ResolveSocketPath(
            DBusBusKind.System, sessionAddress: "unix:path=/run/user/1000/bus", systemAddress: null, uid: 1000);

        Assert.Equal("/run/dbus/system_bus_socket", path);
    }
}
