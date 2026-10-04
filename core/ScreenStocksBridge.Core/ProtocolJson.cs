using System;
using System.Globalization;

namespace ScreenStocksBridge
{
    internal static class ProtocolJson
    {
        internal static string Response(string id, bool ok, string rawResult, string rawError)
        {
            return "{\"id\":" + BridgeJson.Quote(id ?? string.Empty) +
                ",\"ok\":" + (ok ? "true" : "false") +
                ",\"result\":" + (string.IsNullOrEmpty(rawResult) ? "null" : rawResult) +
                ",\"error\":" + (string.IsNullOrEmpty(rawError) ? "null" : rawError) + "}";
        }

        internal static string Event(string name, string rawData)
        {
            return "{\"event\":" + BridgeJson.Quote(name ?? string.Empty) +
                ",\"data\":" + (string.IsNullOrEmpty(rawData) ? "null" : rawData) + "}";
        }

        internal static string Error(string id, string code, string message, int retryAfterMs = 0)
        {
            var escaped = "{\"code\":" + BridgeJson.Quote(code ?? string.Empty) +
                ",\"message\":" + BridgeJson.Quote(message ?? string.Empty) +
                ",\"retryAfterMs\":" + Math.Max(0, retryAfterMs).ToString(CultureInfo.InvariantCulture) + "}";
            return Response(id, false, "null", escaped);
        }
    }
}
