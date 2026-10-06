namespace ScreenStocksBridge
{
    internal sealed class AutoActionToastService
    {
        private BridgeServer? _server;

        internal bool Available { get; private set; }

        internal void SetServer(BridgeServer? server) => _server = server;

        internal void SetAvailable(bool available) => Available = available;

        internal void Capture(AutoAction action, string displayedText)
        {
            var server = _server;
            if (server == null || !server.HasAutoActionToastSubscribers) return;

            server.PublishAutoActionToast(BridgeJson.SerializeAutoActionToast(new AutoActionToastDto
            {
                text = displayedText ?? string.Empty,
                stockId = action.stockId ?? string.Empty,
                actionType = action.actionType.ToString(),
                condition = action.condition.ToString(),
                targetPrice = action.targetPrice
            }));
        }
    }
}
