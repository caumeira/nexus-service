using System.Collections.Generic;

namespace Qos.Service.Peripherals.Capabilities;

public interface IDpiCapability : IPeripheralCapability
{
    /// <summary>Minimum supported DPI (e.g. 100).</summary>
    int MinDpi { get; }

    /// <summary>Maximum supported DPI (e.g. 20000).</summary>
    int MaxDpi { get; }

    /// <summary>Resolution step (e.g. 50 or 100).</summary>
    int Step { get; }

    /// <summary>Number of on-device DPI stages (0 if the device has a single current DPI).</summary>
    int StageCount { get; }

    /// <summary>Currently-active stage index, or -1 if not stage-based.</summary>
    int ActiveStage { get; }

    /// <summary>Configured DPI per stage. Empty if not stage-based (use <see cref="GetCurrent"/>).</summary>
    IReadOnlyList<int> StageDpi { get; }

    /// <summary>Reads the current DPI (for non-staged devices, or the active stage's DPI).</summary>
    int GetCurrent();

    /// <summary>Sets DPI directly (non-staged) or for the active stage.</summary>
    bool SetDpi(int dpi);

    /// <summary>Switches to a stage index (0-based). Returns false if unsupported.</summary>
    bool SetActiveStage(int index);

    /// <summary>Sets the DPI stored in a specific stage. Returns false if unsupported.</summary>
    bool SetStageDpi(int stageIndex, int dpi);
}
