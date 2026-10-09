using System;

namespace ScreenStocksBridge
{
    [Serializable]
    internal sealed class BridgeRequest
    {
        public string id = string.Empty;
        public string method = string.Empty;
        public string token = string.Empty;
        public RequestParams? @params = null;
    }

    [Serializable]
    public sealed class RequestParams
    {
        public string action = string.Empty;
        public string stockId = string.Empty;
        public string upgradeId = string.Empty;
        public int quantity = 1;
        public float percent = 0f;
        public int slotIndex = -1;
        public string actionType = string.Empty;
        public string condition = string.Empty;
        public float targetPrice = 0f;
        public int amountPercentage = 0;
        public bool enabled = false;
        public bool active = false;
        public int limit = 0;
        public long beforeTick = 0;
        public string mode = string.Empty;
        public string filter = string.Empty;
        public string range = string.Empty;
        public int radius = 0;
        public int count = 0;
    }

    [Serializable]
    internal sealed class BridgeResponse
    {
        public string id = string.Empty;
        public bool ok;
        public string result = string.Empty;
        public string error = string.Empty;
    }

    [Serializable]
    internal sealed class BridgeEvent
    {
        public string @event = string.Empty;
        public string data = string.Empty;
    }
}
