using System;
using Nexus.Service.Activity;
using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Tests.Activity;

// AudioState is a process-global singleton; without a serial collection xUnit
// can race two analyser tests across classes and corrupt the shared snapshot.
[CollectionDefinition("AudioState", DisableParallelization = true)]
public class AudioStateCollection { }

[Collection("AudioState")]
public class AudioAnalyserTests
{
    [Fact]
    public void Analyse_OnSilence_LeavesAudioStateAtZero()
    {
        AudioState.Reset();
        var analyser = new AudioAnalyser();
        var silence = new float[AudioAnalyser.WindowSize];

        for (int i = 0; i < 10; i++)
        {
            analyser.Analyse(silence);
        }

        Assert.Equal(0f, AudioState.Level);
        Assert.Equal(0f, AudioState.Bass);
        Assert.Equal(0f, AudioState.Mid);
        Assert.Equal(0f, AudioState.High);
        Assert.Equal(0f, AudioState.Beat);
        foreach (var b in AudioState.Spectrum)
        {
            Assert.Equal(0f, b);
        }
    }

    [Theory]
    [InlineData(80, "bass")]
    [InlineData(1500, "mid")]
    [InlineData(8000, "high")]
    public void Analyse_OnSineWave_LightsUpExpectedBand(int frequencyHz, string expectedBand)
    {
        AudioState.Reset();
        var analyser = new AudioAnalyser();
        var window = SynthesizeSine(frequencyHz, amplitude: 0.5f);

        // Multiple windows so the smoothed spectrum has time to climb out of
        // its zero-initialised state (smoothing factor in PublishAudioState
        // is intentionally slow on the rising edge).
        for (int i = 0; i < 30; i++)
        {
            analyser.Analyse(window);
        }

        Assert.True(AudioState.Level > 0.05f, $"Level should track RMS; got {AudioState.Level}");

        switch (expectedBand)
        {
            case "bass":
                Assert.True(AudioState.Bass > AudioState.High, $"Bass={AudioState.Bass} should dominate at {frequencyHz} Hz");
                break;
            case "mid":
                Assert.True(AudioState.Mid > AudioState.Bass, $"Mid={AudioState.Mid} should dominate at {frequencyHz} Hz");
                Assert.True(AudioState.Mid > AudioState.High, $"Mid={AudioState.Mid} should dominate at {frequencyHz} Hz");
                break;
            case "high":
                Assert.True(AudioState.High > AudioState.Bass, $"High={AudioState.High} should dominate at {frequencyHz} Hz");
                break;
        }
    }

    [Fact]
    public void Reset_ClearsAccumulatedState()
    {
        AudioState.Reset();
        var analyser = new AudioAnalyser();
        var window = SynthesizeSine(1000, amplitude: 0.6f);

        for (int i = 0; i < 20; i++)
        {
            analyser.Analyse(window);
        }
        Assert.True(AudioState.Level > 0f);

        analyser.Reset();

        Assert.Equal(0f, AudioState.Level);
        Assert.Equal(0f, AudioState.Bass);
        Assert.Equal(0f, AudioState.Mid);
        Assert.Equal(0f, AudioState.High);
        Assert.Equal(0f, AudioState.Beat);
    }

    [Fact]
    public void Analyse_ExternalPeak_DrivesVolumeWhenSamplesAreSilent()
    {
        AudioState.Reset();
        var analyser = new AudioAnalyser();
        var silence = new float[AudioAnalyser.WindowSize];

        // Simulates Windows IAudioMeterInformation reporting a non-zero peak
        // while WASAPI loopback is emitting silent packets - shaders should
        // still see a non-zero level so the volume-mixer reading stays
        // visible during silence.
        for (int i = 0; i < 30; i++)
        {
            analyser.Analyse(silence, externalPeak: 0.5f);
        }

        Assert.True(AudioState.Level > 0.5f, $"Level should reflect external peak via perceptual curve; got {AudioState.Level}");
    }

    [Fact]
    public void Analyse_VolumeCurve_IsPerceptual()
    {
        AudioState.Reset();
        var quietAnalyser = new AudioAnalyser();
        var loudAnalyser = new AudioAnalyser();
        var silence = new float[AudioAnalyser.WindowSize];

        for (int i = 0; i < 30; i++)
        {
            quietAnalyser.Analyse(silence, externalPeak: 0.05f);
        }
        float quietLevel = AudioState.Level;

        AudioState.Reset();
        for (int i = 0; i < 30; i++)
        {
            loudAnalyser.Analyse(silence, externalPeak: 1.0f);
        }
        float loudLevel = AudioState.Level;

        // pow(0.05, 0.15) ~= 0.64, pow(1.0, 0.15) ~= 1.0 - perceptual curve
        // flattens the top so quiet content still produces visible level.
        Assert.True(quietLevel > 0.5f, $"Quiet content should still produce visible level; got {quietLevel}");
        Assert.True(loudLevel > quietLevel, $"Loud content should exceed quiet; loud={loudLevel} quiet={quietLevel}");
        Assert.True(loudLevel <= 1.0f, $"Level must clamp at 1.0; got {loudLevel}");
    }

    private static float[] SynthesizeSine(int frequencyHz, float amplitude)
    {
        var samples = new float[AudioAnalyser.WindowSize];
        double step = 2.0 * Math.PI * frequencyHz / AudioAnalyser.SampleRate;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = amplitude * (float)Math.Sin(step * i);
        }
        return samples;
    }
}
