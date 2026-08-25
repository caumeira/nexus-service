// A settings section is only as real as its round trip: the store serializes
// through PersistenceJsonContext, not AppJsonContext, and a source-gen gap
// writes the section back empty without erroring.
using System.Text.Json;
using Nexus.Service.Models.Activity;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;
using Xunit;

namespace Nexus.Service.Tests.Audio;

public sealed class AudioMixerPersistenceTests
{
    [Fact]
    public void MixerLevelsAndPresetsSurviveTheSettingsRoundTrip()
    {
        var settings = new NexusSettings();
        settings.AudioMixer.StickyLevels = false;
        settings.AudioMixer.Levels["spotify"] = new AudioMixerLevelDto { Name = "Spotify", Volume = 0.25, Muted = true };
        settings.AudioMixer.Presets.Add(new AudioMixerPresetDto
        {
            Id = "p1",
            Name = "Just Chatting",
            MasterVolume = 0.6,
            Apps = { new AudioMixerPresetEntryDto { Id = "game", Name = "Game", Volume = 0.4 } },
        });

        var json = JsonSerializer.Serialize(settings, PersistenceJsonContext.Default.NexusSettings);
        var back = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.NexusSettings)!;

        Assert.False(back.AudioMixer.StickyLevels);
        var level = Assert.Contains("spotify", back.AudioMixer.Levels);
        Assert.Equal(0.25, level.Volume);
        Assert.True(level.Muted);
        Assert.Equal("Spotify", level.Name);

        var preset = Assert.Single(back.AudioMixer.Presets);
        Assert.Equal("Just Chatting", preset.Name);
        Assert.Equal(0.6, preset.MasterVolume);
        Assert.Equal("game", Assert.Single(preset.Apps).Id);
    }
}
