using System.Reflection;
using Nexus.Service.Activity; // ProcessFrame, NetworkFrame, ScreenTimeFrame
using Nexus.Service.Models;
using Nexus.Service.Models.Activity;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Models.Devices;
using Nexus.Service.Models.Monitoring;
using Nexus.Service.Models.Peripherals;
using Nexus.Service.Models.Sensors;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// AOT safety net for JSON serialisation. The service runs with
/// <c>PublishAot=true</c> on Windows, which means every type we return from a
/// route or broadcast on a WebSocket MUST be registered in
/// <see cref="AppJsonContext"/>. If it isn't, AOT silently serialises the type
/// as <c>{}</c> -- debug builds won't catch it because JIT falls back to
/// reflection-based serialisation.
///
/// This test enumerates the full set of types flagged with
/// <see cref="JsonSerializableAttribute"/> on <see cref="AppJsonContext"/>
/// via reflection (test builds are JIT so reflection is fine), and asserts
/// that every critical response and broadcast frame type we ship is in that
/// set. Adding a new response type without registering it now fails a test
/// instead of shipping a silent data loss.
///
/// The curated list below is the floor, not the ceiling. If a new response
/// DTO is introduced, add it here -- forgetting to register in
/// AppJsonContext becomes a compile/test failure instead of a production
/// surprise.
/// </summary>
public class AotJsonSafetyTests
{
    private static readonly HashSet<Type> Registered = LoadRegisteredTypes();

    private static HashSet<Type> LoadRegisteredTypes()
    {
        // JsonSerializable attributes are stamped on the AppJsonContext class
        // by the source generator. Their Type argument is the first ctor
        // argument. Using CustomAttributesData keeps us off the .Type property
        // path which isn't universally exposed across SDK versions.
        var set = new HashSet<Type>();
        foreach (var data in typeof(AppJsonContext).GetCustomAttributesData())
        {
            if (data.AttributeType.Name != "JsonSerializableAttribute")
            {
                continue;
            }
            if (data.ConstructorArguments.Count > 0 &&
                data.ConstructorArguments[0].Value is Type t)
            {
                set.Add(t);
            }
        }
        return set;
    }

    public static IEnumerable<object[]> CriticalTypes() => new[]
    {
        // Core
        new object[] { typeof(ApiResponse) },
        new object[] { typeof(PingResponse) },
        new object[] { typeof(PairResponse) },

        // System / sensors
        new object[] { typeof(HardwareSensor) },
        new object[] { typeof(HardwareComponent) },
        new object[] { typeof(StorageComponent) },
        new object[] { typeof(PerformanceSnapshot) },

        // Cooling
        new object[] { typeof(GetAllCoolingResponse) },
        new object[] { typeof(GetFanChannelsResponse) },
        new object[] { typeof(GetTemperatureSourcesResponse) },
        new object[] { typeof(GetCurvesResponse) },

        // Monitoring broadcast frames
        new object[] { typeof(MonitoringFrame) },
        new object[] { typeof(ProcessFrame) },
        new object[] { typeof(NetworkFrame) },
        new object[] { typeof(ScreenTimeFrame) },

        // Activity / screen time
        new object[] { typeof(FocusSession) },
        new object[] { typeof(AppUsage) },

        // Peripherals
        new object[] { typeof(GetPeripheralsResponse) },
        new object[] { typeof(PeripheralDto) },

        // Gallery
        new object[] { typeof(Nexus.Service.Models.Gallery.GallerySourcesResponse) },
        new object[] { typeof(Nexus.Service.Models.Gallery.GallerySourceMutationResponse) },
        new object[] { typeof(Nexus.Service.Models.Gallery.GalleryItemsResponse) },
        new object[] { typeof(Nexus.Service.Models.Gallery.GalleryPickResponse) },
        new object[] { typeof(Nexus.Service.Models.Gallery.GallerySourcesFile) },
        new object[] { typeof(Nexus.Service.Models.Panel.GalleryChangedFrame) },
    };

    [Theory]
    [MemberData(nameof(CriticalTypes))]
    public void CriticalType_IsRegistered_InAppJsonContext(Type type)
    {
        // Regression test: if this fails, a /*Response/Body/Frame type was
        // dropped or renamed without updating AppJsonContext. On AOT/Windows
        // that means the route silently returns {} and the frontend breaks.
        Assert.True(
            Registered.Contains(type),
            $"{type.FullName} is not registered in AppJsonContext. " +
            $"Add [JsonSerializable(typeof({type.Name}))] to Serialization/AppJsonContext.cs.");
    }

    [Fact]
    public void AppJsonContext_HasReasonableCoverage()
    {
        // Sanity check: if the context lost its attributes entirely (e.g. a
        // bad merge nuked the file header), this fires loud.
        Assert.True(Registered.Count > 50,
            $"AppJsonContext registered only {Registered.Count} types -- expected 100+.");
    }

    [Fact]
    public void EveryResponseTypeInModels_IsRegistered()
    {
        // Reflection-driven safety net: find every `public class *Response` or
        // `public class *Frame` under Nexus.Service.Models.* and assert
        // they're registered. Catches new DTOs added without AppJsonContext
        // entry. If a DTO is intentionally NOT broadcast (internal-only), add
        // it to the allow-list below.
        var allowList = new HashSet<string>
        {
            // Add internal-only DTO names here if they intentionally aren't
            // exposed via AppJsonContext. Keep this list minimal.
        };

        var assembly = typeof(AppJsonContext).Assembly;
        var missing = new List<Type>();
        foreach (var t in assembly.GetTypes())
        {
            if (t.Namespace is null || !t.Namespace.StartsWith("Nexus.Service.Models", StringComparison.Ordinal))
                continue;
            if (!t.IsClass || t.IsAbstract || !t.IsPublic)
                continue;
            var name = t.Name;
            if (!(name.EndsWith("Response", StringComparison.Ordinal) ||
                  name.EndsWith("Frame", StringComparison.Ordinal)))
                continue;
            if (allowList.Contains(name))
                continue;
            if (!Registered.Contains(t))
                missing.Add(t);
        }

        Assert.True(missing.Count == 0,
            "The following *Response / *Frame types live under Models.* but are not " +
            "registered in AppJsonContext (AOT will serialise them as {}):\n" +
            string.Join("\n", missing.Select(t => "  - " + t.FullName)));
    }
}
