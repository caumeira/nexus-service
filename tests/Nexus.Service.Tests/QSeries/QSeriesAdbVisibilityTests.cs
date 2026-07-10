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
