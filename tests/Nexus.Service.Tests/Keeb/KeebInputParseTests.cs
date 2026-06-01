using Nexus.Service.Peripherals.Hyte.Keeb;
using static Nexus.Service.Peripherals.Hyte.Keeb.KeebProtocol;

namespace Nexus.Service.Tests.Keeb;

/// <summary>
/// Golden vectors for the interrupt-IN callback parser (hyte-refs
/// firmware-protocol/Keeb/9-callback.md): rotary scroll, key-matrix, software
/// key, profile. 8-byte EP2 reports beginning 0x05.
/// </summary>
public class KeebInputParseTests
{
    [Fact]
    public void Right_encoder_up_and_down()
    {
        Assert.Equal(new KeebInputEvent(KeebInputKind.ScrollUp, KeebEncoder.Right),
            ParseInputEvent(new byte[] { 0x05, 0x09, 0x20, 0x00, 0, 0, 0, 0 }));
        Assert.Equal(new KeebInputEvent(KeebInputKind.ScrollDown, KeebEncoder.Right),
            ParseInputEvent(new byte[] { 0x05, 0x09, 0x40, 0x00, 0, 0, 0, 0 }));
    }

    [Fact]
    public void Left_encoder_up_and_down()
    {
        Assert.Equal(new KeebInputEvent(KeebInputKind.ScrollUp, KeebEncoder.Left),
            ParseInputEvent(new byte[] { 0x05, 0x09, 0x00, 0x20, 0, 0, 0, 0 }));
        Assert.Equal(new KeebInputEvent(KeebInputKind.ScrollDown, KeebEncoder.Left),
            ParseInputEvent(new byte[] { 0x05, 0x09, 0x00, 0x40, 0, 0, 0, 0 }));
    }

    [Fact]
    public void Middle_button_click()
    {
        var ev = ParseInputEvent(new byte[] { 0x05, 0x09, 0x00, 0x10, 0, 0, 0, 0 });
        Assert.Equal(KeebInputKind.ScrollMiddle, ev.Kind);
    }

    [Fact]
    public void Key_matrix_row_col()
    {
        var ev = ParseInputEvent(new byte[] { 0x05, 0xFA, 0x03, 0x05, 0, 0, 0, 0 });
        Assert.Equal(KeebInputKind.KeyMatrix, ev.Kind);
        Assert.Equal(3, ev.Row);
        Assert.Equal(5, ev.Column);
    }

    [Fact]
    public void Software_key_press_and_release()
    {
        var down = ParseInputEvent(new byte[] { 0x05, 0xF1, 0x07, 0x01, 0, 0, 0, 0 });
        Assert.Equal(KeebInputKind.SoftwareKey, down.Kind);
        Assert.Equal(7, down.ApCode);
        Assert.True(down.Pressed);

        var up = ParseInputEvent(new byte[] { 0x05, 0xF1, 0x07, 0x00, 0, 0, 0, 0 });
        Assert.False(up.Pressed);
    }

    [Fact]
    public void Profile_change()
    {
        var ev = ParseInputEvent(new byte[] { 0x05, 0x02, 0x01, 0, 0, 0, 0, 0 });
        Assert.Equal(KeebInputKind.Profile, ev.Kind);
        Assert.Equal(1, ev.Profile);
    }

    [Fact]
    public void Tolerates_leading_report_id_byte()
    {
        var ev = ParseInputEvent(new byte[] { 0x00, 0x05, 0xFA, 0x02, 0x04, 0, 0, 0, 0 });
        Assert.Equal(KeebInputKind.KeyMatrix, ev.Kind);
        Assert.Equal(2, ev.Row);
        Assert.Equal(4, ev.Column);
    }

    [Fact]
    public void Unknown_or_idle_returns_none()
    {
        Assert.Equal(KeebInputKind.None, ParseInputEvent(new byte[] { 0, 0, 0, 0 }).Kind);
        Assert.Equal(KeebInputKind.None, ParseInputEvent(new byte[] { 0x05, 0x99, 0, 0 }).Kind);
    }
}
