using Nexus.Service.Lifecycle;
using Xunit;

namespace Nexus.Service.Tests.Lifecycle;

public class ServiceDaclSddlTests
{
    // The DACL the SCM reports for a freshly created service. This is what the
    // install path now reads, since it queries DACL_SECURITY_INFORMATION only.
    private const string FreshDacl =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)" +
        "(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    private const string Sacl = "S:(AU;FA;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;WD)";

    [Fact]
    public void AppendsAceWhenNoSaclPresent()
    {
        var result = ServiceDaclSddl.InsertAuthUsersStartAce(FreshDacl);

        Assert.Equal(FreshDacl + ServiceDaclSddl.AuthUsersStartAce, result);
    }

    [Fact]
    public void InsertsAceBeforeSacl()
    {
        var result = ServiceDaclSddl.InsertAuthUsersStartAce(FreshDacl + Sacl);

        Assert.Equal(FreshDacl + ServiceDaclSddl.AuthUsersStartAce + Sacl, result);
    }

    // Pins the output to the descriptor observed on a real install (captured
    // from the sc.exe sdset command line the previous implementation emitted),
    // so the advapi32 path produces a byte-identical DACL.
    [Fact]
    public void ReproducesDescriptorFromObservedInstall()
    {
        const string expected =
            "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)" +
            "(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)(A;;LCRP;;;AU)" +
            "S:(AU;FA;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;WD)";

        Assert.Equal(expected, ServiceDaclSddl.InsertAuthUsersStartAce(FreshDacl + Sacl));
    }

    [Fact]
    public void ReturnsNullWhenAceAlreadyPresent()
    {
        var once = ServiceDaclSddl.InsertAuthUsersStartAce(FreshDacl);
        Assert.NotNull(once);

        Assert.Null(ServiceDaclSddl.InsertAuthUsersStartAce(once!));
    }

    [Fact]
    public void GrantsOnlyQueryStatusAndStart()
    {
        // LC = SERVICE_QUERY_STATUS, RP = SERVICE_START. No WD/WO/DC, so the
        // ACE cannot reconfigure or delete the service.
        Assert.Equal("(A;;LCRP;;;AU)", ServiceDaclSddl.AuthUsersStartAce);
    }
}
