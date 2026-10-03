using UnityEngine;

namespace ScreenStocksBridge
{
    internal static class ProtocolJson
    {
        internal static string Response(string id, bool ok, string rawResult, string rawError)
        {
            var prefix = JsonUtility.ToJson(new BridgeResponse { id = id ?? string.Empty, ok = ok, result = string.Empty, error = string.Empty });
            prefix = prefix.Replace("\"result\":\"\"", "\"result\":" + (string.IsNullOrEmpty(rawResult) ? "null" : rawResult));
            return prefix.Replace("\"error\":\"\"", "\"error\":" + (string.IsNullOrEmpty(rawError) ? "null" : rawError));
        }

        internal static string Event(string name, string rawData)
        {
            var prefix = JsonUtility.ToJson(new BridgeEvent { @event = name, data = string.Empty });
            return prefix.Replace("\"data\":\"\"", "\"data\":" + rawData);
        }

        internal static string Error(string id, string code, string message)
        {
            var escaped = JsonUtility.ToJson(new ErrorDto { code = code, message = message });
            return Response(id, false, "null", escaped);
        }

        [System.Serializable]
        private sealed class ErrorDto
        {
            public string code = string.Empty;
            public string message = string.Empty;
        }
    }
}
