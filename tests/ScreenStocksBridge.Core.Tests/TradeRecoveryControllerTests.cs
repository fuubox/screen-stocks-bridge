using ScreenStocksBridge;
using Xunit;

namespace ScreenStocksBridge.Core.Tests;

public sealed class TradeRecoveryControllerTests
{
    private sealed class Attempt : ITradeRecoveryAttempt
    {
        public bool IsCurrent { get; set; } = true;
        public bool Drained;
        public bool HasFreshState { get; set; }
        public bool IsReady { get; set; }
        public int Begins, Reloads, Reconnects, Cancels;
        public readonly TaskCompletionSource<bool> Response = new();
        public void Begin() => Begins++;
        public bool Drain() => Drained;
        public Task Reload() { Reloads++; return Response.Task; }
        public void Reconnect() => Reconnects++;
        public void Cancel() => Cancels++;
    }

    [Fact]
    public void RepeatedAttemptsDoNotExtendTheOriginalThirtySecondWindow()
    {
        var policy = new TradeRecoveryController(); var command = new object(); var attempt = new Attempt();
        policy.Tick(100, command, () => attempt);
        for (var i = 1; i < 30; i++) policy.Tick(100 + i, command, () => attempt);
        Assert.Equal(0, attempt.Begins);
        policy.Tick(130, command, () => attempt);
        Assert.Equal(1, attempt.Begins); Assert.True(policy.BlocksCommands);
    }

    [Fact]
    public void NaturalResolutionBeforeDeadlineDoesNotResetTheSession()
    {
        var policy = new TradeRecoveryController(); var attempt = new Attempt();
        policy.Tick(0, new object(), () => attempt); policy.Tick(29, null, () => attempt);
        Assert.Equal(TradeRecoveryPhase.Idle, policy.Phase); Assert.Equal(0, attempt.Begins);
    }

    [Fact]
    public void ClearedAmbiguityDoesNotReopenAdmissionWhileGatesRemainHeld()
    {
        var policy = new TradeRecoveryController(); var command = new object(); var attempt = new Attempt();
        policy.Tick(0, command, () => attempt); policy.Tick(30, command, () => attempt);
        policy.Tick(44, null, () => attempt);
        Assert.True(policy.BlocksCommands); Assert.Equal(0, attempt.Reloads);
        policy.Tick(45, null, () => attempt); policy.Tick(1000, command, () => attempt);
        Assert.Equal(TradeRecoveryPhase.Failed, policy.Phase);
        Assert.Equal(1, attempt.Begins); Assert.Equal(1, attempt.Cancels);
    }

    [Fact]
    public void RecoveryWaitsForFreshStateAndReconnectBeforeReopening()
    {
        var policy = new TradeRecoveryController(); var command = new object(); var attempt = new Attempt { Drained = true };
        policy.Tick(0, command, () => attempt); policy.Tick(30, command, () => attempt);
        Assert.Equal(TradeRecoveryPhase.Reloading, policy.Phase);
        attempt.HasFreshState = true; attempt.Response.SetResult(false); // An unchanged revision can return false.
        policy.Tick(31, null, () => attempt);
        Assert.Equal(TradeRecoveryPhase.Reconnecting, policy.Phase); Assert.True(policy.BlocksCommands);
        attempt.IsReady = true; policy.Tick(32, null, () => attempt);
        Assert.False(policy.BlocksCommands); Assert.Equal(1, policy.CompletedCount);
    }

    [Fact]
    public void LateReloadCannotUnlockAFailedRecoveryOrRestartItsTimer()
    {
        var policy = new TradeRecoveryController(); var command = new object(); var attempt = new Attempt { Drained = true };
        policy.Tick(0, command, () => attempt); policy.Tick(30, command, () => attempt);
        policy.Tick(45, null, () => attempt);
        attempt.HasFreshState = true; attempt.IsReady = true; attempt.Response.SetResult(true);
        policy.Tick(50, null, () => attempt);
        Assert.Equal(TradeRecoveryPhase.Failed, policy.Phase); Assert.Equal(0, attempt.Reconnects);
    }

    [Fact]
    public void ACompletedFetchWithoutFreshAuthoritativeStateFailsClosed()
    {
        var policy = new TradeRecoveryController(); var command = new object(); var attempt = new Attempt { Drained = true, IsReady = true };
        policy.Tick(0, command, () => attempt); policy.Tick(30, command, () => attempt);
        attempt.Response.SetResult(true); policy.Tick(31, null, () => attempt);
        Assert.Equal(TradeRecoveryPhase.Failed, policy.Phase); Assert.Equal(0, attempt.Reconnects);
    }

    [Fact]
    public void AChangedSessionCannotBeUnlockedByTheOldAttempt()
    {
        var policy = new TradeRecoveryController(); var command = new object(); var attempt = new Attempt();
        policy.Tick(0, command, () => attempt); policy.Tick(30, command, () => attempt);
        attempt.IsCurrent = false; policy.Tick(31, null, () => attempt);
        Assert.Equal(TradeRecoveryPhase.Failed, policy.Phase); Assert.Equal(1, attempt.Cancels);
    }
}
