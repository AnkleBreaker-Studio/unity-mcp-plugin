using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Typed-first argument readers for command handlers.
    ///
    /// MiniJson delivers JSON numbers as boxed <c>double</c> / <c>long</c>. The old
    /// pattern <c>value.ToString()</c> + invariant TryParse silently DROPPED every
    /// non-integer number on machines whose current culture writes a decimal comma
    /// (18.8 → "18,8" → parse fail → default) — the exact "floats silently ignored"
    /// bug from the battle-test report. Readers here unbox the typed value directly
    /// and only fall back to invariant string parsing for string inputs.
    ///
    /// Design rule (also from the report): a parameter that is PRESENT but not
    /// interpretable throws instead of silently falling back to the default —
    /// a loud failure costs seconds, a silent wrong default costs a debugging session.
    /// </summary>
    internal static class MCPArgs
    {
        public static float GetFloat(Dictionary<string, object> args, string key, float defaultValue)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return defaultValue;

            switch (value)
            {
                case double d: return (float)d;
                case float f: return f;
                case long l: return l;
                case int i: return i;
                case string s:
                    if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                        return parsed;
                    throw new ArgumentException($"Parameter '{key}' is not a valid number: '{s}'.");
                default:
                    throw new ArgumentException($"Parameter '{key}' must be a number (got {value.GetType().Name}).");
            }
        }

        public static int GetInt(Dictionary<string, object> args, string key, int defaultValue)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return defaultValue;

            switch (value)
            {
                case long l: return checked((int)l);
                case int i: return i;
                case double d:
                    // Accept integral doubles (8.0) — reject genuinely fractional values loudly.
                    double rounded = Math.Round(d);
                    if (Math.Abs(d - rounded) < 1e-6) return checked((int)rounded);
                    throw new ArgumentException($"Parameter '{key}' must be an integer (got {d.ToString(CultureInfo.InvariantCulture)}).");
                case string s:
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                        return parsed;
                    throw new ArgumentException($"Parameter '{key}' is not a valid integer: '{s}'.");
                default:
                    throw new ArgumentException($"Parameter '{key}' must be an integer (got {value.GetType().Name}).");
            }
        }

        public static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (args == null || !args.TryGetValue(key, out object value) || value == null)
                return defaultValue;

            switch (value)
            {
                case bool b: return b;
                case string s:
                    string lower = s.ToLowerInvariant();
                    if (lower == "true" || lower == "1") return true;
                    if (lower == "false" || lower == "0") return false;
                    throw new ArgumentException($"Parameter '{key}' is not a valid boolean: '{s}'.");
                case long l: return l != 0;
                default:
                    throw new ArgumentException($"Parameter '{key}' must be a boolean (got {value.GetType().Name}).");
            }
        }
    }
}
