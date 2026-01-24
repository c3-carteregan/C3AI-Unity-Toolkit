using System.Text;

namespace C3AI.Voice
{
    /// <summary>
    /// Lightweight JSON parsing utilities for streaming/low-allocation scenarios.
    /// For full JSON parsing, consider using JsonUtility, Newtonsoft.Json, or System.Text.Json.
    /// </summary>
    public static class JsonUtilities
    {
        // ==================== READING ====================

        /// <summary>
        /// Reads a JSON string value starting at position i (which should be at the opening quote).
        /// Advances i past the closing quote. Handles escape sequences.
        /// </summary>
        /// <param name="json">The JSON string.</param>
        /// <param name="i">Current position (should be at opening quote). Will be advanced past closing quote.</param>
        /// <returns>The unescaped string value.</returns>
        public static string ReadString(string json, ref int i)
        {
            if (i >= json.Length || json[i] != '"')
                return null;

            int n = json.Length;
            i++; // skip opening quote

            int start = i;
            bool hasEscape = false;

            while (i < n)
            {
                char c = json[i];
                if (c == '\\') { hasEscape = true; i += 2; continue; }
                if (c == '"') break;
                i++;
            }

            int end = i;
            if (i < n && json[i] == '"') i++; // skip closing quote

            if (!hasEscape)
            {
                return json.Substring(start, end - start);
            }

            return Unescape(json, start, end);
        }

        /// <summary>
        /// Reads a JSON boolean value (true or false) at position i.
        /// Advances i past the value if found.
        /// </summary>
        /// <param name="json">The JSON string.</param>
        /// <param name="i">Current position. Will be advanced if bool found.</param>
        /// <returns>True, false, or null if not a boolean.</returns>
        public static bool? ReadBool(string json, ref int i)
        {
            int n = json.Length;
            if (i + 3 < n && json[i] == 't' && json[i + 1] == 'r' && json[i + 2] == 'u' && json[i + 3] == 'e')
            {
                i += 4;
                return true;
            }
            if (i + 4 < n && json[i] == 'f' && json[i + 1] == 'a' && json[i + 2] == 'l' && json[i + 3] == 's' && json[i + 4] == 'e')
            {
                i += 5;
                return false;
            }
            return null;
        }

        /// <summary>
        /// Reads a JSON number as a double at position i.
        /// Advances i past the number.
        /// </summary>
        /// <param name="json">The JSON string.</param>
        /// <param name="i">Current position. Will be advanced past the number.</param>
        /// <param name="value">The parsed number value.</param>
        /// <returns>True if a number was read, false otherwise.</returns>
        public static bool TryReadNumber(string json, ref int i, out double value)
        {
            value = 0;
            int n = json.Length;
            int start = i;

            // Read until we hit a non-number character
            while (i < n)
            {
                char c = json[i];
                if (c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E' || (c >= '0' && c <= '9'))
                    i++;
                else
                    break;
            }

            if (i == start)
                return false;

            string numStr = json.Substring(start, i - start);
            return double.TryParse(numStr, System.Globalization.NumberStyles.Float, 
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Checks if position i is at a JSON null value and advances past it.
        /// </summary>
        /// <param name="json">The JSON string.</param>
        /// <param name="i">Current position. Will be advanced if null found.</param>
        /// <returns>True if null was found.</returns>
        public static bool ReadNull(string json, ref int i)
        {
            int n = json.Length;
            if (i + 3 < n && json[i] == 'n' && json[i + 1] == 'u' && json[i + 2] == 'l' && json[i + 3] == 'l')
            {
                i += 4;
                return true;
            }
            return false;
        }

        // ==================== SKIPPING ====================

        /// <summary>
        /// Skips whitespace characters at position i.
        /// </summary>
        /// <param name="json">The JSON string.</param>
        /// <param name="i">Current position. Will be advanced past whitespace.</param>
        public static void SkipWhitespace(string json, ref int i)
        {
            int n = json.Length;
            while (i < n)
            {
                char c = json[i];
                if (c == ' ' || c == '\n' || c == '\r' || c == '\t') 
                    i++;
                else 
                    break;
            }
        }

        /// <summary>
        /// Skips a complete JSON value (string, number, bool, null, object, or array) at position i.
        /// </summary>
        /// <param name="json">The JSON string.</param>
        /// <param name="i">Current position. Will be advanced past the value.</param>
        public static void SkipValue(string json, ref int i)
        {
            int n = json.Length;
            SkipWhitespace(json, ref i);
            if (i >= n) return;

            char c = json[i];

            // String
            if (c == '"')
            {
                ReadString(json, ref i);
                return;
            }

            // Object
            if (c == '{')
            {
                int depth = 0;
                while (i < n)
                {
                    char ch = json[i++];
                    if (ch == '"') { ReadString(json, ref i); continue; }
                    if (ch == '{') depth++;
                    else if (ch == '}')
                    {
                        depth--;
                        if (depth <= 0) break;
                    }
                }
                return;
            }

            // Array
            if (c == '[')
            {
                int depth = 0;
                while (i < n)
                {
                    char ch = json[i++];
                    if (ch == '"') { ReadString(json, ref i); continue; }
                    if (ch == '[') depth++;
                    else if (ch == ']')
                    {
                        depth--;
                        if (depth <= 0) break;
                    }
                    else if (ch == '{')
                    {
                        i--;
                        SkipValue(json, ref i);
                    }
                }
                return;
            }

            // Number, bool, null, or unknown: read until delimiter
            while (i < n)
            {
                char ch = json[i];
                if (ch == ',' || ch == '}' || ch == ']' || ch == '\n' || ch == '\r' || ch == '\t' || ch == ' ')
                    break;
                i++;
            }
        }

        // ==================== STRING UTILITIES ====================

        /// <summary>
        /// Unescapes a JSON string (handles \n, \t, \", \\, \uXXXX, etc).
        /// </summary>
        /// <param name="json">The JSON string containing the escaped content.</param>
        /// <param name="start">Start index of the content (after opening quote).</param>
        /// <param name="end">End index of the content (before closing quote).</param>
        /// <returns>The unescaped string.</returns>
        public static string Unescape(string json, int start, int end)
        {
            StringBuilder sb = new StringBuilder(end - start);

            int i = start;
            while (i < end)
            {
                char c = json[i++];
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i >= end) break;
                char e = json[i++];

                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        // Unicode escape \uXXXX
                        if (i + 3 < end)
                        {
                            int code = 0;
                            for (int k = 0; k < 4; k++)
                            {
                                char h = json[i + k];
                                int v = (h >= '0' && h <= '9') ? (h - '0') :
                                        (h >= 'a' && h <= 'f') ? (10 + (h - 'a')) :
                                        (h >= 'A' && h <= 'F') ? (10 + (h - 'A')) : 0;
                                code = (code << 4) | v;
                            }
                            sb.Append((char)code);
                            i += 4;
                        }
                        break;
                    default:
                        // Unknown escape, keep the character
                        sb.Append(e);
                        break;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Escapes a string for use in JSON.
        /// </summary>
        /// <param name="value">The string to escape.</param>
        /// <returns>The escaped string (without surrounding quotes).</returns>
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            StringBuilder sb = null;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                string escape = null;

                switch (c)
                {
                    case '"': escape = "\\\""; break;
                    case '\\': escape = "\\\\"; break;
                    case '\b': escape = "\\b"; break;
                    case '\f': escape = "\\f"; break;
                    case '\n': escape = "\\n"; break;
                    case '\r': escape = "\\r"; break;
                    case '\t': escape = "\\t"; break;
                    default:
                        if (c < 32)
                            escape = $"\\u{(int)c:X4}";
                        break;
                }

                if (escape != null)
                {
                    if (sb == null)
                    {
                        sb = new StringBuilder(value.Length + 10);
                        sb.Append(value, 0, i);
                    }
                    sb.Append(escape);
                }
                else if (sb != null)
                {
                    sb.Append(c);
                }
            }

            return sb?.ToString() ?? value;
        }

        // ==================== KEY-VALUE EXTRACTION ====================

        /// <summary>
        /// Extracts a string value for a given key from a JSON object.
        /// Simple single-level extraction, does not recurse into nested objects.
        /// </summary>
        /// <param name="json">The JSON string.</param>
        /// <param name="key">The key to find.</param>
        /// <returns>The string value, or null if not found.</returns>
        public static string ExtractString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                return null;

            int i = 0;
            int n = json.Length;

            while (i < n)
            {
                // Find next quote (potential key start)
                while (i < n && json[i] != '"') i++;
                if (i >= n) break;

                string foundKey = ReadString(json, ref i);
                SkipWhitespace(json, ref i);

                if (i < n && json[i] == ':')
                {
                    i++;
                    SkipWhitespace(json, ref i);

                    if (foundKey == key)
                    {
                        if (i < n && json[i] == '"')
                            return ReadString(json, ref i);
                        else
                            return null; // Value is not a string
                    }
                    else
                    {
                        SkipValue(json, ref i);
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Extracts a boolean value for a given key from a JSON object.
        /// </summary>
        /// <param name="json">The JSON string.</param>
        /// <param name="key">The key to find.</param>
        /// <returns>The boolean value, or null if not found or not a boolean.</returns>
        public static bool? ExtractBool(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                return null;

            int i = 0;
            int n = json.Length;

            while (i < n)
            {
                while (i < n && json[i] != '"') i++;
                if (i >= n) break;

                string foundKey = ReadString(json, ref i);
                SkipWhitespace(json, ref i);

                if (i < n && json[i] == ':')
                {
                    i++;
                    SkipWhitespace(json, ref i);

                    if (foundKey == key)
                    {
                        return ReadBool(json, ref i);
                    }
                    else
                    {
                        SkipValue(json, ref i);
                    }
                }
            }

            return null;
        }
    }
}
