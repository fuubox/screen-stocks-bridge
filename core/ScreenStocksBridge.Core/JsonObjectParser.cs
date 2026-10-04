namespace ScreenStocksBridge
{
    internal static class JsonObjectParser
    {
        internal static string? ExtractTopLevelObject(string json, string propertyName)
        {
            var index = 0;
            SkipWhitespace(json, ref index);
            if (index >= json.Length || json[index++] != '{') return null;

            while (index < json.Length)
            {
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',') { index++; continue; }
                if (index >= json.Length || json[index] == '}') return null;
                if (json[index] != '"') return null;

                var keyStart = ++index;
                var escaped = false;
                while (index < json.Length)
                {
                    var character = json[index++];
                    if (escaped) { escaped = false; continue; }
                    if (character == '\\') { escaped = true; continue; }
                    if (character == '"') break;
                }
                var keyEnd = index - 1;
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index++] != ':') return null;
                SkipWhitespace(json, ref index);
                var valueStart = index;
                var valueEnd = SkipValue(json, ref index);
                if (string.Equals(json.Substring(keyStart, keyEnd - keyStart), propertyName, System.StringComparison.Ordinal) &&
                    valueStart < json.Length && json[valueStart] == '{')
                    return json.Substring(valueStart, valueEnd - valueStart);

                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ',') { index++; continue; }
                return null;
            }
            return null;
        }

        private static int SkipValue(string json, ref int index)
        {
            var start = index;
            if (index >= json.Length) return index;
            var first = json[index];
            if (first == '"')
            {
                index++;
                var escaped = false;
                while (index < json.Length)
                {
                    var character = json[index++];
                    if (escaped) { escaped = false; continue; }
                    if (character == '\\') { escaped = true; continue; }
                    if (character == '"') break;
                }
                return index;
            }
            if (first == '{' || first == '[')
            {
                var depth = 0;
                var inString = false;
                var escaped = false;
                while (index < json.Length)
                {
                    var character = json[index++];
                    if (inString)
                    {
                        if (escaped) escaped = false;
                        else if (character == '\\') escaped = true;
                        else if (character == '"') inString = false;
                        continue;
                    }
                    if (character == '"') { inString = true; continue; }
                    if (character == '{' || character == '[') depth++;
                    else if (character == '}' || character == ']')
                    {
                        depth--;
                        if (depth == 0) return index;
                    }
                }
                return index;
            }
            while (index < json.Length && json[index] != ',' && json[index] != '}') index++;
            while (index > start && char.IsWhiteSpace(json[index - 1])) index--;
            return index;
        }

        private static void SkipWhitespace(string value, ref int index)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
        }
    }
}
