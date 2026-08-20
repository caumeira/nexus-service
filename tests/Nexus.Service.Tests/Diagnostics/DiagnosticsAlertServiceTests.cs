using System;
using System.Collections.Generic;
using Nexus.Service.Diagnostics;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.Diagnostics;

public class DiagnosticsAlertServiceTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static HealthComponent Component(string id, string kind, string status, string reasonSeverity, string code = "x.reason") =>
        new()
        {
            Id = id,
            Kind = kind,
            Name = id,
            Status = status,
            Reasons = new List<HealthComponentReason> { new(code, reasonSeverity, "summary", "detail") },
        };

    [Fact]
    public void MasterDisabled_NeverNotifies_EvenForActComponents()
    {
        var notifications = new DiagnosticsNotifications { Enabled = false, StorageHealth = true };
        var components = new[] { Component("storage:1", "storage", HealthStatuses.Act, HealthStatuses.Act) };

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            components, notifications, new Dictionary<string, DateTime>(), T0);

        Assert.Empty(notices);
    }

    [Fact]
    public void DefaultSettings_AllTogglesOff_ProducesNoNotifications()
    {
        var notifications = new DiagnosticsNotifications();
        var components = new[]
        {
            Component("storage:1", "storage", HealthStatuses.Act, HealthStatuses.Act),
            Component("gpu:0", "gpu", HealthStatuses.Watch, HealthStatuses.Watch),
            Component("memory", "memory", HealthStatuses.Act, HealthStatuses.Act),
            Component("system", "system", HealthStatuses.Watch, HealthStatuses.Watch),
            Component("cooling:pump1", "cooling", HealthStatuses.Act, HealthStatuses.Act),
            Component("cooling", "cooling", HealthStatuses.Watch, HealthStatuses.Watch),
        };

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            components, notifications, new Dictionary<string, DateTime>(), T0);

        Assert.Empty(notices);
    }

    [Fact]
    public void CategoryEnabled_ActComponent_Notifies()
    {
        var notifications = new DiagnosticsNotifications { Enabled = true, StorageHealth = true };
        var components = new[] { Component("storage:1", "storage", HealthStatuses.Act, HealthStatuses.Act) };

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            components, notifications, new Dictionary<string, DateTime>(), T0);

        var notice = Assert.Single(notices);
        Assert.Equal("storage:1", notice.Title);
        Assert.Equal("summary", notice.Text);
    }

    [Fact]
    public void CategoryDisabled_MasterEnabled_DoesNotNotify()
    {
        var notifications = new DiagnosticsNotifications { Enabled = true, StorageHealth = false };
        var components = new[] { Component("storage:1", "storage", HealthStatuses.Act, HealthStatuses.Act) };

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            components, notifications, new Dictionary<string, DateTime>(), T0);

        Assert.Empty(notices);
    }

    [Fact]
    public void WatchSeverity_NotifiesWhenCategoryApplies()
    {
        var notifications = new DiagnosticsNotifications { Enabled = true, Cooling = true };
        var components = new[] { Component("cooling:fan1", "cooling", HealthStatuses.Watch, HealthStatuses.Watch, "cooling.fanStall") };

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            components, notifications, new Dictionary<string, DateTime>(), T0);

        Assert.Single(notices);
    }

    [Fact]
    public void CoolingAggregateId_MapsToHighTemp_NotCooling()
    {
        var notifications = new DiagnosticsNotifications { Enabled = true, Cooling = true, HighTemp = false };
        var aggregate = Component("cooling", "cooling", HealthStatuses.Watch, HealthStatuses.Watch, "cooling.sustainedHighTemp");

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            new[] { aggregate }, notifications, new Dictionary<string, DateTime>(), T0);

        Assert.Empty(notices);
    }

    [Fact]
    public void PerDeviceStallId_MapsToCooling_NotHighTemp()
    {
        var notifications = new DiagnosticsNotifications { Enabled = true, Cooling = false, HighTemp = true };
        var stall = Component("cooling:pump1", "cooling", HealthStatuses.Act, HealthStatuses.Act, "cooling.pumpStall");

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            new[] { stall }, notifications, new Dictionary<string, DateTime>(), T0);

        Assert.Empty(notices);
    }

    [Fact]
    public void WithinCooldownWindow_SecondCallDoesNotReNotify()
    {
        var notifications = new DiagnosticsNotifications { Enabled = true, StorageHealth = true, CooldownMinutes = 60 };
        var components = new[] { Component("storage:1", "storage", HealthStatuses.Act, HealthStatuses.Act) };
        var lastNotified = new Dictionary<string, DateTime>();

        var first = DiagnosticsAlertService.EvaluateNotifications(components, notifications, lastNotified, T0);
        var second = DiagnosticsAlertService.EvaluateNotifications(components, notifications, lastNotified, T0.AddMinutes(10));

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public void PastCooldownWindow_ReNotifies()
    {
        var notifications = new DiagnosticsNotifications { Enabled = true, StorageHealth = true, CooldownMinutes = 60 };
        var components = new[] { Component("storage:1", "storage", HealthStatuses.Act, HealthStatuses.Act) };
        var lastNotified = new Dictionary<string, DateTime>();

        var first = DiagnosticsAlertService.EvaluateNotifications(components, notifications, lastNotified, T0);
        var second = DiagnosticsAlertService.EvaluateNotifications(components, notifications, lastNotified, T0.AddMinutes(61));

        Assert.Single(first);
        Assert.Single(second);
    }

    [Fact]
    public void OkStatusComponent_NeverNotifies()
    {
        var notifications = new DiagnosticsNotifications { Enabled = true, StorageHealth = true, Cooling = true, HighTemp = true, MemoryTest = true, SystemDevices = true, GpuThrottle = true };
        var components = new[]
        {
            new HealthComponent { Id = "storage:1", Kind = "storage", Name = "Drive", Status = HealthStatuses.Ok },
        };

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            components, notifications, new Dictionary<string, DateTime>(), T0);

        Assert.Empty(notices);
    }

    [Fact]
    public void UnknownStatusComponent_NeverNotifies_EvenWithAnUnknownReason()
    {
        // An unmeasured component carries a reason to be explainable, never to alert.
        var notifications = new DiagnosticsNotifications { Enabled = true, GpuThrottle = true };
        var components = new[]
        {
            Component("gpu:0", "gpu", HealthStatuses.Unknown, HealthStatuses.Unknown, "gpu.noHealthSource"),
        };

        var notices = DiagnosticsAlertService.EvaluateNotifications(
            components, notifications, new Dictionary<string, DateTime>(), T0);

        Assert.Empty(notices);
    }
}
