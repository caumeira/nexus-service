namespace Nexus.Service.Lifecycle;

public interface IStartupProvider
{
    bool IsEnabled();
    bool SetEnabled(bool enabled, string path, string arguments);
}

public interface IShutdownProvider
{
    void Shutdown();
}

public interface IPawnIoProvider
{
    /// <summary>True if the PawnIO software package is installed (registry check).</summary>
    bool IsInstalled { get; }

    /// <summary>True if the PawnIO kernel driver device is currently open and running.</summary>
    bool IsOpen { get; }
}

public sealed class StubStartupProvider : IStartupProvider
{
    public bool IsEnabled() => false;
    public bool SetEnabled(bool enabled, string path, string arguments) => true;
}

public sealed class StubShutdownProvider : IShutdownProvider
{
    public void Shutdown() { /* no providers to clean up yet */ }
}

public sealed class StubPawnIoProvider : IPawnIoProvider
{
    public bool IsInstalled => false;
    public bool IsOpen => false;
}
