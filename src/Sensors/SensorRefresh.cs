namespace Nexus.Service.Sensors;

/// <summary>How fresh a caller needs <see cref="LhmComputer.Update"/> to be.
/// Callers state their need; the cadence policy lives in LhmComputer.</summary>
public enum SensorRefresh
{
    /// <summary>Everything that polls on a ~1s cadence. At most one hardware
    /// walk per <c>NormalFloorMs</c>; callers inside that window read the values
    /// the previous walk cached.</summary>
    Normal,

    /// <summary>Fan calibration's RPM settle detection, which samples every
    /// 250ms. Nothing else should use this.</summary>
    Fast,

    /// <summary>Bypass the floor and refresh every group, including ones on a
    /// slow per-group cadence. For LHM warmup and the SMART snapshot, both of
    /// which need current data and run rarely.</summary>
    Force,
}
