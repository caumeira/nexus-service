namespace Nexus.Service.Peripherals.LianLi;

public sealed class LianLiState
{
    /// <summary>Last decoded RPM per port (0..3). -1 = no data yet.</summary>
    public int[] Rpm { get; } = new int[] { -1, -1, -1, -1 };

    /// <summary>Fan duty percent per port.</summary>
    public int[] Duty { get; } = new int[LianLiProtocol.PortCount];

    public bool IsConnected { get; set; }
}
