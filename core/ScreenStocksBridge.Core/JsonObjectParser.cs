namespace ScreenStocksBridge
{
    internal static class JsonObjectParser
    {
        internal static string? ExtractTopLevelString(string json, string propertyName)
        {
            var index = 0;
            SkipWhitespace(json, ref index);
            if (index >= json.Length || json[index++] != '{') return null;
            while (index < json.Length)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length) return null;
                if (json[index] == ',') { index++; continue; }
                if (json[index] != '"') return null;
                var key = ReadString(json, ref index);
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index++] != ':') return null;
                SkipWhitespace(json, ref index);
                if (key == propertyName) return index < json.Length && json[index] == '"' ? ReadString(json, ref index) : null;
                SkipValue(json, ref index);
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] == '}') return null;
            }
            return null;
        }

        private static string ReadString(string json, ref int index)
        {
            var text = new System.Text.StringBuilder();
            index++;
            while (index < json.Length)
            {
                var c = json[index++];
                if (c == '"') return text.ToString();
                if (c != '\\') { text.Append(c); continue; }
                if (index >= json.Length) throw new System.FormatException("Invalid JSON string.");
                c = json[index++];
                switch (c)
                {
                    case '"': case '\\': case '/': text.Append(c); break;
                    case 'b': text.Append('\b'); break;
                    case 'f': text.Append('\f'); break;
                    case 'n': text.Append('\n'); break;
                    case 'r': text.Append('\r'); break;
                    case 't': text.Append('\t'); break;
                    case 'u':
                        if (index + 4 > json.Length) throw new System.FormatException("Invalid unicode escape.");
                        text.Append((char)int.Parse(json.Substring(index, 4), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture)); index += 4; break;
                    default: throw new System.FormatException("Invalid JSON escape.");
                }
            }
            throw new System.FormatException("Unterminated JSON string.");
        }

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
