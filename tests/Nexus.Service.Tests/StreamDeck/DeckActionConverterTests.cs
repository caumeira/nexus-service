using System.Text.Json;
using Nexus.Service.Deck;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.StreamDeck;

/// <summary>
/// Round-trip fixtures for every DeckAction wire shape against the literal
/// JSON nexus-web's deck/types.ts produces (api/streamdeck.ts round-trips
/// its native DeckConfig with no adapter, so this is the actual wire
/// contract, not an approximation). Each case deserializes, asserts fields,
/// then re-serializes and re-deserializes to confirm the converter is
/// stable under a second pass.
/// </summary>
public class DeckActionConverterTests
{
    private static DeckAction Deserialize(string json) =>
        JsonSerializer.Deserialize(json, AppJsonContext.Default.DeckAction)!;

    private static DeckAction RoundTrip(DeckAction action) =>
        JsonSerializer.Deserialize(
            JsonSerializer.Serialize(action, AppJsonContext.Default.DeckAction),
            AppJsonContext.Default.DeckAction)!;

    [Fact]
    public void LaunchApp_RoundTrips()
    {
        var a = Deserialize("{\"type\":\"launchApp\",\"appId\":\"steam\"}");
        Assert.Equal("launchApp", a.Type);
        Assert.Equal("steam", a.AppId);

        var b = RoundTrip(a);
        Assert.Equal(a.Type, b.Type);
        Assert.Equal(a.AppId, b.AppId);
    }

    [Fact]
    public void OpenFile_RoundTrips()
    {
        var a = Deserialize("{\"type\":\"openFile\",\"path\":\"C:\\\\foo.txt\"}");
        Assert.Equal("openFile", a.Type);
        Assert.Equal("C:\\foo.txt", a.Path);

        var b = RoundTrip(a);
        Assert.Equal(a.Path, b.Path);
    }

    [Fact]
    public void OpenFolder_RoundTrips()
    {
        var a = Deserialize("{\"type\":\"openFolder\",\"path\":\"/home/user\"}");
        Assert.Equal("openFolder", a.Type);
        Assert.Equal("/home/user", a.Path);

        var b = RoundTrip(a);
        Assert.Equal(a.Path, b.Path);
    }

    [Fact]
    public void OpenUrl_RoundTrips()
    {
        var a = Deserialize("{\"type\":\"openUrl\",\"url\":\"https://example.com\"}");
        Assert.Equal("openUrl", a.Type);
        Assert.Equal("https://example.com", a.Url);

        var b = RoundTrip(a);
        Assert.Equal(a.Url, b.Url);
    }

    [Fact]
    public void System_ActionObject_SurvivesRoundTrip()
    {
        var json = "{\"type\":\"system\",\"action\":{\"op\":\"volumeSet\",\"value\":0.5,\"step\":0.1,\"displayId\":\"disp-1\",\"source\":\"src-1\"}}";
        var a = Deserialize(json);
        Assert.Equal("system", a.Type);
        Assert.NotNull(a.SystemAction);
        Assert.Equal("volumeSet", a.SystemAction!.Op);
        Assert.Equal(0.5, a.SystemAction.Value);
        Assert.Equal(0.1, a.SystemAction.Step);
        Assert.Equal("disp-1", a.SystemAction.DisplayId);
        Assert.Equal("src-1", a.SystemAction.Source);
        Assert.Null(a.NexusAction);
        Assert.Null(a.PowerAction);

        var b = RoundTrip(a);
        Assert.NotNull(b.SystemAction);
        Assert.Equal(a.SystemAction.Op, b.SystemAction!.Op);
        Assert.Equal(a.SystemAction.Value, b.SystemAction.Value);
        Assert.Equal(a.SystemAction.Step, b.SystemAction.Step);
        Assert.Equal(a.SystemAction.DisplayId, b.SystemAction.DisplayId);
        Assert.Equal(a.SystemAction.Source, b.SystemAction.Source);
    }

    [Fact]
    public void Hotkey_RoundTrips()
    {
        var a = Deserialize("{\"type\":\"hotkey\",\"keys\":\"ctrl+shift+m\"}");
        Assert.Equal("hotkey", a.Type);
        Assert.Equal("ctrl+shift+m", a.Keys);

        var b = RoundTrip(a);
        Assert.Equal(a.Keys, b.Keys);
    }

    [Fact]
    public void Text_RoundTrips()
    {
        var a = Deserialize("{\"type\":\"text\",\"text\":\"hello\",\"paste\":true}");
        Assert.Equal("text", a.Type);
        Assert.Equal("hello", a.Text);
        Assert.True(a.Paste);

        var b = RoundTrip(a);
        Assert.Equal(a.Text, b.Text);
        Assert.Equal(a.Paste, b.Paste);
    }

    [Fact]
    public void Power_ActionIsAPlainString_SurvivesRoundTrip()
    {
        var a = Deserialize("{\"type\":\"power\",\"action\":\"lock\"}");
        Assert.Equal("power", a.Type);
        Assert.Equal("lock", a.PowerAction);
        Assert.Null(a.SystemAction);
        Assert.Null(a.NexusAction);

        var raw = JsonSerializer.Serialize(a, AppJsonContext.Default.DeckAction);
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("action").ValueKind);
        Assert.Equal("lock", doc.RootElement.GetProperty("action").GetString());

        var b = RoundTrip(a);
        Assert.Equal(a.PowerAction, b.PowerAction);
    }

    [Fact]
    public void AudioOutput_RoundTrips()
    {
        var a = Deserialize("{\"type\":\"audioOutput\",\"deviceId\":\"spk-1\"}");
        Assert.Equal("audioOutput", a.Type);
        Assert.Equal("spk-1", a.DeviceId);

        var b = RoundTrip(a);
        Assert.Equal(a.DeviceId, b.DeviceId);
    }

    [Fact]
    public void AudioInput_RoundTrips()
    {
        var a = Deserialize("{\"type\":\"audioInput\",\"deviceId\":\"mic-1\"}");
        Assert.Equal("audioInput", a.Type);
        Assert.Equal("mic-1", a.DeviceId);

        var b = RoundTrip(a);
        Assert.Equal(a.DeviceId, b.DeviceId);
    }

    [Fact]
    public void Nexus_ActionObject_SurvivesRoundTrip()
    {
        var json = "{\"type\":\"nexus\",\"action\":{\"op\":\"fanSpeed\",\"effect\":\"rainbow\",\"profileId\":\"p1\",\"profile\":\"silent\",\"deviceId\":\"dev-1\",\"fanId\":\"fan-1\",\"value\":42,\"on\":true,\"orientation\":\"portrait\"}}";
        var a = Deserialize(json);
        Assert.Equal("nexus", a.Type);
        Assert.NotNull(a.NexusAction);
        Assert.Equal("fanSpeed", a.NexusAction!.Op);
        Assert.Equal("rainbow", a.NexusAction.Effect);
        Assert.Equal("p1", a.NexusAction.ProfileId);
        Assert.Equal("silent", a.NexusAction.Profile);
        Assert.Equal("dev-1", a.NexusAction.DeviceId);
        Assert.Equal("fan-1", a.NexusAction.FanId);
        Assert.Equal(42, a.NexusAction.Value);
        Assert.True(a.NexusAction.On);
        Assert.Equal("portrait", a.NexusAction.Orientation);
        Assert.Null(a.SystemAction);
        Assert.Null(a.PowerAction);

        var b = RoundTrip(a);
        Assert.NotNull(b.NexusAction);
        Assert.Equal(a.NexusAction.Op, b.NexusAction!.Op);
        Assert.Equal(a.NexusAction.Effect, b.NexusAction.Effect);
        Assert.Equal(a.NexusAction.Value, b.NexusAction.Value);
        Assert.Equal(a.NexusAction.On, b.NexusAction.On);
    }

    [Fact]
    public void Sequence_StepsRecurseThroughDifferentActionTypes()
    {
        var json = "{\"type\":\"sequence\",\"steps\":["
            + "{\"action\":{\"type\":\"hotkey\",\"keys\":\"ctrl+c\"},\"pressMs\":10,\"gapAfterMs\":20},"
            + "{\"action\":{\"type\":\"text\",\"text\":\"pasted\",\"paste\":true}}"
            + "]}";
        var a = Deserialize(json);
        Assert.Equal("sequence", a.Type);
        Assert.NotNull(a.Steps);
        Assert.Equal(2, a.Steps!.Count);
        Assert.Equal("hotkey", a.Steps[0].Action.Type);
        Assert.Equal("ctrl+c", a.Steps[0].Action.Keys);
        Assert.Equal(10, a.Steps[0].PressMs);
        Assert.Equal(20, a.Steps[0].GapAfterMs);
        Assert.Equal("text", a.Steps[1].Action.Type);
        Assert.Equal("pasted", a.Steps[1].Action.Text);

        var b = RoundTrip(a);
        Assert.Equal(2, b.Steps!.Count);
        Assert.Equal("hotkey", b.Steps[0].Action.Type);
        Assert.Equal("ctrl+c", b.Steps[0].Action.Keys);
        Assert.Equal("text", b.Steps[1].Action.Type);
        Assert.Equal("pasted", b.Steps[1].Action.Text);
    }

    [Fact]
    public void Toggle_BranchesRecurseThroughNexusActions()
    {
        var json = "{\"type\":\"toggle\","
            + "\"on\":{\"type\":\"nexus\",\"action\":{\"op\":\"lightingPower\",\"deviceId\":\"dev-1\",\"on\":true}},"
            + "\"off\":{\"type\":\"nexus\",\"action\":{\"op\":\"lightingPower\",\"deviceId\":\"dev-1\",\"on\":false}},"
            + "\"state\":{\"kind\":\"lightingPower\",\"deviceId\":\"dev-1\"}}";
        var a = Deserialize(json);
        Assert.Equal("toggle", a.Type);
        Assert.NotNull(a.On);
        Assert.NotNull(a.Off);
        Assert.Equal("nexus", a.On!.Type);
        Assert.Equal("lightingPower", a.On.NexusAction!.Op);
        Assert.True(a.On.NexusAction.On);
        Assert.Equal("nexus", a.Off!.Type);
        Assert.False(a.Off.NexusAction!.On);
        Assert.NotNull(a.State);
        Assert.Equal("lightingPower", a.State!.Kind);
        Assert.Equal("dev-1", a.State.DeviceId);

        var b = RoundTrip(a);
        Assert.Equal("nexus", b.On!.Type);
        Assert.True(b.On.NexusAction!.On);
        Assert.False(b.Off!.NexusAction!.On);
        Assert.Equal("lightingPower", b.State!.Kind);
    }

    [Theory]
    [InlineData("\"just a string\"")]
    [InlineData("[1,2,3]")]
    [InlineData("42")]
    [InlineData("true")]
    public void Deserialize_NonObjectNode_ReturnsAnEmptyActionInsteadOfThrowing(string json)
    {
        var a = Deserialize(json);
        Assert.Equal("", a.Type);
        Assert.Null(a.SystemAction);
        Assert.Null(a.NexusAction);
        Assert.Null(a.PowerAction);
    }

    [Fact]
    public void Deserialize_SequenceStepWithNonObjectAction_ReturnsAnEmptyActionForThatStep()
    {
        var json = "{\"type\":\"sequence\",\"steps\":[{\"action\":\"not an object\",\"pressMs\":5}]}";

        var a = Deserialize(json);

        Assert.Equal("sequence", a.Type);
        Assert.Single(a.Steps!);
        Assert.Equal("", a.Steps![0].Action.Type);
        Assert.Equal(5, a.Steps[0].PressMs);
    }

    [Fact]
    public void Page_Next_RoundTrips()
    {
        var a = Deserialize("{\"type\":\"page\",\"op\":\"next\"}");
        Assert.Equal("page", a.Type);
        Assert.Equal("next", a.Op);
        Assert.Null(a.Target);

        var b = RoundTrip(a);
        Assert.Equal(a.Op, b.Op);
    }

    [Fact]
    public void Page_Goto_CarriesTheTargetIndex()
    {
        var a = Deserialize("{\"type\":\"page\",\"op\":\"goto\",\"target\":2}");
        Assert.Equal("goto", a.Op);
        Assert.Equal(2, a.Target);

        var b = RoundTrip(a);
        Assert.Equal(2, b.Target);
    }

    [Fact]
    public void PageIndicator_RoundTrips()
    {
        var a = Deserialize("{\"type\":\"pageIndicator\"}");
        Assert.Equal("pageIndicator", a.Type);

        var raw = JsonSerializer.Serialize(a, AppJsonContext.Default.DeckAction);
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("pageIndicator", doc.RootElement.GetProperty("type").GetString());
        Assert.False(doc.RootElement.TryGetProperty("op", out _));
    }

    [Fact]
    public void DeckBrightness_Set_RoundTrips()
    {
        var a = Deserialize("{\"type\":\"deckBrightness\",\"op\":\"set\",\"value\":75}");
        Assert.Equal("deckBrightness", a.Type);
        Assert.Equal("set", a.Op);
        Assert.Equal(75, a.Value);
        Assert.Null(a.Step);

        var b = RoundTrip(a);
        Assert.Equal(75, b.Value);
    }

    [Fact]
    public void DeckBrightness_UpWithStep_RoundTrips()
    {
        var a = Deserialize("{\"type\":\"deckBrightness\",\"op\":\"up\",\"step\":20}");
        Assert.Equal("up", a.Op);
        Assert.Equal(20, a.Step);
        Assert.Null(a.Value);

        var b = RoundTrip(a);
        Assert.Equal(20, b.Step);
    }

    [Fact]
    public void DeckSleep_RoundTrips()
    {
        var a = Deserialize("{\"type\":\"deckSleep\"}");
        Assert.Equal("deckSleep", a.Type);

        var b = RoundTrip(a);
        Assert.Equal("deckSleep", b.Type);
    }

    [Fact]
    public void HotkeySwitch_RoundTrips()
    {
        var a = Deserialize("{\"type\":\"hotkeySwitch\",\"keysA\":\"ctrl+shift+m\",\"keysB\":\"ctrl+shift+n\"}");
        Assert.Equal("hotkeySwitch", a.Type);
        Assert.Equal("ctrl+shift+m", a.KeysA);
        Assert.Equal("ctrl+shift+n", a.KeysB);

        var b = RoundTrip(a);
        Assert.Equal(a.KeysA, b.KeysA);
        Assert.Equal(a.KeysB, b.KeysB);
    }

    [Fact]
    public void PersistenceJsonContext_UsesTheSameConverter()
    {
        var json = "{\"type\":\"power\",\"action\":\"sleep\"}";
        var a = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.DeckAction)!;
        Assert.Equal("sleep", a.PowerAction);

        var raw = JsonSerializer.Serialize(a, PersistenceJsonContext.Default.DeckAction);
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("sleep", doc.RootElement.GetProperty("action").GetString());
    }
}
