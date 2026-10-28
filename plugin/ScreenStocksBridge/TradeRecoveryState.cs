using System.Reflection;

namespace ScreenStocksBridge
{
    internal static class TradeRecoveryState
    {
        private static readonly FieldInfo? Broker = typeof(StockManager).GetField(
            "_remoteTradeBroker", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static bool IsPending(StockManager? manager) => manager is not null &&
            Broker?.GetValue(manager) is RemoteTradeBroker broker && broker.HasAmbiguousTrade;

        internal static RemoteTradeBroker? BrokerFor(StockManager? manager) => manager is null ? null : Broker?.GetValue(manager) as RemoteTradeBroker;

        internal const string Message = "A previous trade outcome is unknown and the game is reconciling it. " +
            "No new command was submitted; wait for recovery before trying again.";
    }
}
