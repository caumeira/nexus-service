using System;
using AdvancedSharpAdbClient.Models;
using Nexus.Service.Devices;
using Nexus.Service.QSeries;
using Xunit;

namespace Nexus.Service.Tests.QSeries;

public class SetHomeActivityTookTests
{
    [Fact]
    public void Success_output_took()
    {
        Assert.True(QSeriesPortWatcher.SetHomeActivityTook("Success"));
        Assert.True(QSeriesPortWatcher.SetHomeActivityTook("success"));
    }

    [Fact]
    public void Early_boot_service_missing_did_not_take()
    {
        Assert.False(QSeriesPortWatcher.SetHomeActivityTook("cmd: Can't find service: package"));
    }

    [Fact]
    public void Empty_output_did_not_take()
    {
        Assert.False(QSeriesPortWatcher.SetHomeActivityTook(""));
    }
}

public class IsMediaTekInstanceIdTests
{
    [Fact]
    public void Qseries_panel_instance_matches()
    {
        Assert.True(QSeriesPortWatcher.IsMediaTekInstanceId(@"USB\VID_0E8D&PID_201C\0123456789ABCDEF"));
        Assert.True(QSeriesPortWatcher.IsMediaTekInstanceId(@"usb\vid_0e8d&pid_2048\0123456789ABCDEF"));
    }

    [Fact]
    public void Non_mediatek_instance_does_not_match()
    {
        Assert.False(QSeriesPortWatcher.IsMediaTekInstanceId(@"USB\VID_18D1&PID_4EE7\SOMEPHONE01"));
        Assert.False(QSeriesPortWatcher.IsMediaTekInstanceId(""));
    }
}

public class BuildAdbVisibilitySignatureTests
{
    [Fact]
    public void Empty_adb_list_with_vendor_bound_devnode()
    {
        // The field case: adb sees nothing while PnP holds a monolithic
        // MediaTek bind - the one-line signature names the misbinding and
        // the INF to remove.
        var signature = QSeriesPortWatcher.BuildAdbVisibilitySignature(
            new DeviceData[0],
            new[]
            {
                new UsbDeviceEntry
                {
                    VendorId = 0x0E8D, ProductId = 0x201C, Name = "HYTE Q60 Display",
                    Manufacturer = "MediaTek", Class = "AndroidUsbDeviceClass", Driver = "oem42.inf",
                },
            });

        Assert.Equal(
            "adb=[] mediatek-pnp=[HYTE Q60 Display pid=201C class=AndroidUsbDeviceClass mfr=MediaTek inf=oem42.inf]",
            signature);
    }

    [Fact]
    public void Offline_device_includes_state_and_omits_empty_model()
    {
        var signature = QSeriesPortWatcher.BuildAdbVisibilitySignature(
            new[] { new DeviceData { Serial = "0123456789ABCDEF", State = DeviceState.Offline } },
            new UsbDeviceEntry[0]);

        Assert.Equal("adb=[0123456789ABCDEF(Offline)] mediatek-pnp=[]", signature);
    }

    [Fact]
    public void Online_device_with_model_and_empty_serial_filtered()
    {
        var signature = QSeriesPortWatcher.BuildAdbVisibilitySignature(
            new[]
            {
                new DeviceData { Serial = "", State = DeviceState.Offline },
                new DeviceData { Serial = "ABC", State = DeviceState.Unauthorized, Model = "HYTE_Q60_Display" },
            },
            new UsbDeviceEntry[0]);

        Assert.Equal("adb=[ABC(Unauthorized,HYTE_Q60_Display)] mediatek-pnp=[]", signature);
    }
}

public class WindowsTimeZoneMapTests
{
    // The service publishes with InvariantGlobalization, where
    // TimeZoneInfo.TryConvertWindowsIdToIanaId returns false for every Windows id
    // - which silently skipped `cmd alarm set-timezone` on every Windows host and
    // left field panels on their factory zone.
    [Theory]
    [InlineData("India Standard Time", "Asia/Calcutta")]
    [InlineData("Pacific Standard Time", "America/Los_Angeles")]
    [InlineData("GMT Standard Time", "Europe/London")]
    [InlineData("W. Europe Standard Time", "Europe/Berlin")]
    [InlineData("UTC", "Etc/UTC")]
    public void Maps_windows_ids_to_iana(string windowsId, string expected)
    {
        Assert.Equal(expected, WindowsTimeZoneMap.ToIana(windowsId));
    }

    [Fact]
    public void Unknown_id_is_null_so_the_panel_zone_is_left_alone()
    {
        Assert.Null(WindowsTimeZoneMap.ToIana("Not A Real Standard Time"));
    }

    [Fact]
    public void No_value_is_a_windows_registry_id()
    {
        // `cmd alarm set-timezone` only takes IANA; a Windows id reaching the
        // panel is the bug this map exists to close.
        foreach (var windowsId in new[] { "India Standard Time", "UTC", "Tokyo Standard Time" })
        {
            Assert.DoesNotContain("Standard Time", WindowsTimeZoneMap.ToIana(windowsId)!, StringComparison.Ordinal);
        }
    }
}

public class HomeRoleAndChooserTests
{
    [Fact]
    public void Chooser_focus_detected()
    {
        Assert.True(QSeriesPortWatcher.IsChooserFocus(
            "  mCurrentFocus=Window{1a2b3c u0 android/com.android.internal.app.ResolverActivity}"));
    }

    [Fact]
    public void Qshell_focus_is_not_a_chooser()
    {
        Assert.False(QSeriesPortWatcher.IsChooserFocus(
            "  mCurrentFocus=Window{1a2b3c u0 com.hellonexus.qshell/com.hellonexus.qshell.MainActivity}"));
        Assert.False(QSeriesPortWatcher.IsChooserFocus(""));
    }

    [Fact]
    public void Role_ok_only_when_the_read_back_names_qshell()
    {
        Assert.Equal("ok", QSeriesPortWatcher.ClassifyHomeRole("", "[com.hellonexus.qshell]\n"));
    }

    [Fact]
    public void Silent_add_with_no_holder_is_not_ok()
    {
        // A shell that never ran returns the same empty string as one that worked.
        Assert.Equal("no holder", QSeriesPortWatcher.ClassifyHomeRole("", ""));
    }

    [Fact]
    public void Refusal_is_reported_over_an_empty_read_back()
    {
        Assert.Equal(
            "Error: java.lang.SecurityException: MANAGE_ROLE_HOLDERS",
            QSeriesPortWatcher.ClassifyHomeRole(
                "Error: java.lang.SecurityException: MANAGE_ROLE_HOLDERS\n", ""));
    }

    [Fact]
    public void Wrong_holder_is_named()
    {
        Assert.Equal(
            "holder=[com.companyname.thiccapp]",
            QSeriesPortWatcher.ClassifyHomeRole("", "[com.companyname.thiccapp]"));
    }
}
