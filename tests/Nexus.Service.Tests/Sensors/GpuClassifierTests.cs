using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class GpuClassifierTests
{
    [Theory]
    // The 9800X3D iGPU (found on T1): LHM reports a 512 MB UMA carve-out as its
    // "GPU Memory Total", so a VRAM>0 check alone would mark it discrete - the
    // "…Graphics" name with no RX/Pro model is what correctly flags it integrated.
    [InlineData("AMD Radeon(TM) Graphics", "amd", true)]
    [InlineData("AMD Radeon RX 7900 XTX", "amd", false)]
    [InlineData("AMD Radeon Pro W6800", "amd", false)]
    [InlineData("NVIDIA GeForce RTX 5080", "nvidia", false)]
    [InlineData("Intel(R) UHD Graphics 770", "intel", true)]
    [InlineData("Intel Arc A770", "intel", false)]
    [InlineData("Apple M3 Max", "apple", true)]
    public void FromName_classifies_vendor_and_integrated(string name, string vendor, bool integrated)
    {
        var (v, i) = GpuClassifier.FromName(name);
        Assert.Equal(vendor, v);
        Assert.Equal(integrated, i);
    }
}
