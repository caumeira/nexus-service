using System;
using Nexus.Service.Games;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

public class GameModeStateTests
{
    // A pid that cannot resolve, so the liveness sweep sees the game as exited.
    private const int DeadPid = 0x3FFFFFF;

    private static (GameModeState State, InMemoryConfigStore Store) Build(
        string manualState = GameModeState.StateAuto, int exitGraceSeconds = 0)
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.GameMode.State = manualState;
            s.GameMode.ExitGraceSeconds = exitGraceSeconds;
        });
        return (new GameModeState(store), store);
    }

    [Fact]
    public void AutoWithNoGameIsInactive()
    {
        var (state, _) = Build();
        Assert.False(state.IsActive);
        Assert.Equal("", state.Reason);
    }

    [Fact]
    public void ALiveGameActivatesAuto()
    {
        var (state, _) = Build();
        state.NoteGameStarted("steam:1", "Test Game", Environment.ProcessId);

        Assert.True(state.IsActive);
        Assert.Equal("auto", state.Reason);
        Assert.Equal("Test Game", Assert.Single(state.Games).Name);
    }

    [Fact]
    public void ARunningGameSurvivesTheSweep()
    {
        var (state, _) = Build();
        state.NoteGameStarted("steam:1", "Test Game", Environment.ProcessId);

        state.SweepForTests();

        Assert.True(state.IsActive);
    }

    [Fact]
    public void AnExitedGameDeactivatesOnceTheGraceHasPassed()
    {
        var (state, _) = Build(exitGraceSeconds: 0);
        state.NoteGameStarted("steam:1", "Gone", DeadPid);
        Assert.True(state.IsActive);

        state.SweepForTests();

        Assert.False(state.IsActive);
        Assert.Empty(state.Games);
    }

    [Fact]
    public void AnExitedGameStaysActiveInsideTheGrace()
    {
        var (state, _) = Build(exitGraceSeconds: 600);
        state.NoteGameStarted("steam:1", "Gone", DeadPid);

        state.SweepForTests();

        Assert.True(state.IsActive);
        Assert.Empty(state.Games);
    }

    [Fact]
    public void TheLastGameToExitEndsIt()
    {
        var (state, _) = Build(exitGraceSeconds: 0);
        state.NoteGameStarted("steam:1", "Alive", Environment.ProcessId);
        state.NoteGameStarted("steam:2", "Gone", DeadPid);
        Assert.Equal(2, state.Games.Count);

        state.SweepForTests();

        Assert.True(state.IsActive);
        Assert.Equal("Alive", Assert.Single(state.Games).Name);
    }

    [Fact]
    public void ManualOnActivatesWithNoGame()
    {
        var (state, _) = Build(manualState: GameModeState.StateOn);
        state.ReapplyEffects();

        Assert.True(state.IsActive);
        Assert.Equal("manual", state.Reason);
        Assert.Empty(state.Games);
    }

    [Fact]
    public void ManualOffWinsOverARunningGame()
    {
        var (state, _) = Build(manualState: GameModeState.StateOff);
        state.NoteGameStarted("steam:1", "Test Game", Environment.ProcessId);

        Assert.False(state.IsActive);
    }

    [Fact]
    public void SwitchingToOffWhileActiveDeactivates()
    {
        var (state, _) = Build();
        state.NoteGameStarted("steam:1", "Test Game", Environment.ProcessId);
        Assert.True(state.IsActive);

        state.SetManualState(GameModeState.StateOff);

        Assert.False(state.IsActive);
    }

    [Fact]
    public void ActiveChangedFiresOnlyOnTransitions()
    {
        var (state, _) = Build(exitGraceSeconds: 0);
        var flips = 0;
        state.ActiveChanged += _ => flips++;

        state.NoteGameStarted("steam:1", "Test Game", Environment.ProcessId);
        state.NoteGameStarted("steam:1", "Test Game", Environment.ProcessId);
        state.SweepForTests();
        Assert.Equal(1, flips);

        state.SetManualState(GameModeState.StateOff);
        Assert.Equal(2, flips);
    }

    [Fact]
    public void RenotingAPidKeepsTheOriginalStart()
    {
        var (state, _) = Build();
        state.NoteGameStarted("steam:1", "First", Environment.ProcessId);
        var first = Assert.Single(state.Games);

        state.NoteGameStarted("steam:1", "Renamed", Environment.ProcessId);

        var again = Assert.Single(state.Games);
        Assert.Equal(first.StartedUtcMs, again.StartedUtcMs);
        Assert.Equal("First", again.Name);
    }

    [Fact]
    public void AnInvalidPidIsIgnored()
    {
        var (state, _) = Build();
        state.NoteGameStarted("steam:1", "Test Game", 0);

        Assert.False(state.IsActive);
        Assert.Empty(state.Games);
    }

    [Theory]
    [InlineData("on", "on")]
    [InlineData("off", "off")]
    [InlineData("auto", "auto")]
    [InlineData("", "auto")]
    [InlineData("nonsense", "auto")]
    [InlineData(null, "auto")]
    public void UnknownStatesFallBackToAuto(string? input, string expected)
    {
        Assert.Equal(expected, GameModeState.Normalize(input));
    }
}
