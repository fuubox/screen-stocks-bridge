using ScreenStocks.Net;

namespace ScreenStocksBridge
{
    internal sealed class NewsTickerService
    {
        private BridgeServer? _server;

        internal bool Available { get; private set; }

        internal void SetServer(BridgeServer? server) => _server = server;

        internal void SetAvailable(bool available) => Available = available;

        internal void Capture(NewsTickerItemDto item)
        {
            if (_server == null || !_server.HasNewsSubscribers) return;
            _server.PublishNewsTicker(BridgeJson.SerializeNewsTickerItem(item));
        }
    }
}
