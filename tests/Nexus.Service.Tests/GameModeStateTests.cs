using System;
using System.Collections.Generic;
using Nexus.Service.Games;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests;

public class GameModeStateTests
{
    private const int AlivePid = 4242;
    private const int DeadPid = 9999;

    // Liveness is injected so a test can retire a pid without a real process.
    private static (GameModeState State, HashSet<int> Alive) Build(
        string manualState = GameModeState.StateAuto, int exitGraceSeconds = 0,
        TimeSpan? activationDelay = null)
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.GameMode.State = manualState;
            s.GameMode.ExitGraceSeconds = exitGraceSeconds;
        });
        var alive = new HashSet<int> { AlivePid };
        var state = new GameModeState(
            store, activationDelay ?? TimeSpan.Zero, g => alive.Contains(g.Pid));
        return (state, alive);
    }

    /// <summary>A noted game only activates once a sweep promotes it, so every
    /// "game is running" case runs one first.</summary>
    private static void Start(GameModeState state, string key, string name, int pid)
    {
        state.NoteGameStarted(key, name, pid);
        state.SweepForTests();
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
        Start(state, "steam:1", "Test Game", AlivePid);

        Assert.True(state.IsActive);
        Assert.Equal("auto", state.Reason);
        Assert.Equal("Test Game", Assert.Single(state.Games).Name);
    }

    [Fact]
    public void ARunningGameSurvivesTheSweep()
    {
        var (state, _) = Build();
        Start(state, "steam:1", "Test Game", AlivePid);

        state.SweepForTests();

        Assert.True(state.IsActive);
    }

    [Fact]
    public void AnExitedGameDeactivatesOnceTheGraceHasPassed()
    {
        var (state, alive) = Build(exitGraceSeconds: 0);
        Start(state, "steam:1", "Gone", AlivePid);
        Assert.True(state.IsActive);

        alive.Remove(AlivePid);
        state.SweepForTests();

        Assert.False(state.IsActive);
        Assert.Empty(state.Games);
    }

    [Fact]
    public void AnExitedGameStaysActiveInsideTheGrace()
    {
        var (state, alive) = Build(exitGraceSeconds: 600);
        Start(state, "steam:1", "Gone", AlivePid);

        alive.Remove(AlivePid);
        state.SweepForTests();

        Assert.True(state.IsActive);
        Assert.Empty(state.Games);
    }

    [Fact]
    public void TheLastGameToExitEndsIt()
    {
        var (state, _) = Build(exitGraceSeconds: 0);
        Start(state, "steam:1", "Alive", AlivePid);
        state.NoteGameStarted("steam:2", "Gone", DeadPid);
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
        Start(state, "steam:1", "Test Game", AlivePid);

        Assert.False(state.IsActive);
    }

    [Fact]
    public void SwitchingToOffWhileActiveDeactivates()
    {
        var (state, _) = Build();
        Start(state, "steam:1", "Test Game", AlivePid);
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

        Start(state, "steam:1", "Test Game", AlivePid);
        state.NoteGameStarted("steam:1", "Test Game", AlivePid);
        state.SweepForTests();
        Assert.Equal(1, flips);

        state.SetManualState(GameModeState.StateOff);
        Assert.Equal(2, flips);
    }

    [Fact]
    public void RenotingAPidKeepsTheOriginalStart()
    {
        var (state, _) = Build();
        Start(state, "steam:1", "First", AlivePid);
        var first = Assert.Single(state.Games);

        state.NoteGameStarted("steam:1", "Renamed", AlivePid);
        state.SweepForTests();

        var again = Assert.Single(state.Games);
        Assert.Equal(first.StartedUtcMs, again.StartedUtcMs);
        Assert.Equal("First", again.Name);
    }

    [Fact]
    public void AnInvalidPidIsIgnored()
    {
        var (state, _) = Build();
        state.NoteGameStarted("steam:1", "Test Game", 0);
        state.SweepForTests();

        Assert.False(state.IsActive);
        Assert.Empty(state.Games);
    }

    [Fact]
    public void AProcessThatDiesInsideTheActivationDelayNeverActivates()
    {
        // A game's launcher, updater or shutdown handler lives in the same
        // install dir, so GameCatalog resolves it to the same game; one taking
        // focus after a quit must not re-arm Game Mode.
        var (state, _) = Build(activationDelay: TimeSpan.FromMinutes(5));
        state.NoteGameStarted("steam:1", "System Shock", DeadPid);

        state.SweepForTests();

        Assert.False(state.IsActive);
        Assert.Empty(state.Games);
    }

    [Fact]
    public void ALiveProcessStillInsideTheActivationDelayHasNotActivatedYet()
    {
        var (state, _) = Build(activationDelay: TimeSpan.FromMinutes(5));
        state.NoteGameStarted("steam:1", "System Shock", AlivePid);

        state.SweepForTests();

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
