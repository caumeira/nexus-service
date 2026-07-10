using Nexus.Service.Peripherals.StreamDeck;

namespace Nexus.Service.Tests.StreamDeck;

public class SimulatedStreamDeckSurfaceContractTests : StreamDeckSurfaceContractTests
{
    protected override IStreamDeckSurface CreateSurface() =>
        new SimulatedStreamDeckSurface(StreamDeckModels.ByProductId(0x0063)!, "sim-test");
}
