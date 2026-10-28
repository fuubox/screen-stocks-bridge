using System;
using System.Threading.Tasks;

namespace ScreenStocksBridge;

internal interface ITradeRecoveryAttempt
{
    bool IsCurrent { get; }
    void Begin();
    bool Drain();
    Task Reload();
    bool HasFreshState { get; }
    void Reconnect();
    bool IsReady { get; }
    void Cancel();
}

internal enum TradeRecoveryPhase { Idle, Observing, Draining, Reloading, Reconnecting, Failed }

// Driven by Unity Update; every deadline uses one monotonic clock and no detached
// timeout task can subsequently re-open admission after an attempt has failed.
internal sealed class TradeRecoveryController
{
    internal const double AmbiguitySeconds = 30;
    internal const double RecoverySeconds = 15;
    private object? _command;
    private double _observedAt;
    private double _deadline;
    private ITradeRecoveryAttempt? _attempt;
    private Task? _reload;
    internal TradeRecoveryPhase Phase { get; private set; }
    internal bool BlocksCommands => Phase >= TradeRecoveryPhase.Draining;
    internal string Error { get; private set; } = string.Empty;
    internal int CompletedCount { get; private set; }
    internal double SecondsUntilRecovery(double now) => Math.Max(0, AmbiguitySeconds - (now - _observedAt));

    internal void Tick(double now, object? command, Func<ITradeRecoveryAttempt> createAttempt)
    {
        if (Phase == TradeRecoveryPhase.Failed) return;
        try
        {
            if (Phase == TradeRecoveryPhase.Idle || Phase == TradeRecoveryPhase.Observing)
            {
                if (command == null) { Phase = TradeRecoveryPhase.Idle; _command = null; return; }
                if (!ReferenceEquals(command, _command)) { _command = command; _observedAt = now; }
                Phase = TradeRecoveryPhase.Observing;
                if (now - _observedAt < AmbiguitySeconds) return;
                Phase = TradeRecoveryPhase.Draining;
                _deadline = now + RecoverySeconds;
                _attempt = createAttempt();
                _attempt.Begin();
            }

            if (!_attempt!.IsCurrent) { Fail("The game session changed during trade recovery."); return; }
            if (now >= _deadline) { Fail("Trade recovery timed out. Restart the game."); return; }
            if (Phase == TradeRecoveryPhase.Draining && _attempt.Drain())
            {
                _reload = _attempt.Reload();
                Phase = TradeRecoveryPhase.Reloading;
            }
            if (Phase == TradeRecoveryPhase.Reloading && _reload!.IsCompleted)
            {
                _reload.GetAwaiter().GetResult();
                if (!_attempt.HasFreshState) { Fail("Fresh server state could not be confirmed. Restart the game."); return; }
                _attempt.Reconnect();
                Phase = TradeRecoveryPhase.Reconnecting;
            }
            if (Phase == TradeRecoveryPhase.Reconnecting && _attempt.IsReady && _attempt.Drain())
            {
                Phase = TradeRecoveryPhase.Idle;
                _command = null;
                _attempt = null;
                _reload = null;
                CompletedCount++;
            }
        }
        catch (Exception ex) { Fail(ex.GetBaseException().Message); }
    }

    private void Fail(string reason)
    {
        Phase = TradeRecoveryPhase.Failed;
        Error = reason;
        try { _attempt?.Cancel(); }
        catch (Exception ex) { Error += " Cleanup: " + ex.GetBaseException().Message; }
        // Observe a late failure without scheduling a late state transition.
        _reload?.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
