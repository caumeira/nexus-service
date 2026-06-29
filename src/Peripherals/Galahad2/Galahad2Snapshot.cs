namespace Nexus.Service.Peripherals.Galahad2;

// Immutable; swapped atomically under the hub lock. Readers take the reference once.
public sealed class Galahad2Snapshot
{
    public static readonly Galahad2Snapshot Empty = new(0, 0, 0, 0);

    public Galahad2Snapshot(int fanRpm, int pumpRpm, int fanDuty, int pumpDuty)
    {
        FanRpm = fanRpm;
        PumpRpm = pumpRpm;
        FanDuty = fanDuty;
        PumpDuty = pumpDuty;
    }

    public int FanRpm { get; }
    public int PumpRpm { get; }
    public int FanDuty { get; }
    public int PumpDuty { get; }
}
