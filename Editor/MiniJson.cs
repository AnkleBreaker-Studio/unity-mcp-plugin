// MiniJSON - Minimal JSON parser and serializer for Unity
// Based on the public domain MiniJSON by Calvin Rien
// Handles serialization/deserialization without external dependencies

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityMCP.Editor
{
    public static class MiniJson
    {
        public static object Deserialize(string json)
        {
            if (json == null) return null;
            return Parser.Parse(json);
        }

        public static string Serialize(object obj)
        {
            return Serializer.Serialize(obj, int.MaxValue);
        }

        public static string Serialize(object obj, int maxUtf8Bytes) => Serializer.Serialize(obj, maxUtf8Bytes);

        public sealed class SerializationException : InvalidOperationException
        {
            public string Reason { get; }
            public long BytesRequired { get; }
            public int LimitBytes { get; }
            internal SerializationException(string reason, string message, long bytesRequired = 0, int limitBytes = 0, Exception inner = null)
                : base(message, inner) { Reason = reason; BytesRequired = bytesRequired; LimitBytes = limitBytes; }
        }

        sealed class Parser : IDisposable
        {
            const string WORD_BREAK = "{}[],:\"";
            StringReader json;

            Parser(string jsonString) { json = new StringReader(jsonString); }

            public static object Parse(string jsonString)
            {
                using (var instance = new Parser(jsonString))
                    return instance.ParseValue();
            }

            public void Dispose() { json.Dispose(); }

            Dictionary<string, object> ParseObject()
            {
                var table = new Dictionary<string, object>();
                json.Read(); // {
                while (true)
                {
                    switch (NextToken)
                    {
                        case TOKEN.NONE: return null;
                        case TOKEN.CURLY_CLOSE: return table;
                        case TOKEN.COMMA: continue;
                        default:
                            string name = ParseString();
                            if (name == null) return null;
                            if (NextToken != TOKEN.COLON) return null;
                            json.Read(); // :
                            table[name] = ParseValue();
                            break;
                    }
                }
            }

            List<object> ParseArray()
            {
                var array = new List<object>();
                json.Read(); // [
                bool parsing = true;
                while (parsing)
                {
                    TOKEN nextToken = NextToken;
                    switch (nextToken)
                    {
                        case TOKEN.NONE: return null;
                        case TOKEN.SQUARED_CLOSE: parsing = false; break;
                        case TOKEN.COMMA: continue;
                        default:
                            object value = ParseByToken(nextToken);
                            array.Add(value);
                            break;
                    }
                }
                return array;
            }

            object ParseValue()
            {
                TOKEN nextToken = NextToken;
                return ParseByToken(nextToken);
            }

            object ParseByToken(TOKEN token)
            {
                switch (token)
                {
                    case TOKEN.STRING: return ParseString();
                    case TOKEN.NUMBER: return ParseNumber();
                    case TOKEN.CURLY_OPEN: return ParseObject();
                    case TOKEN.SQUARED_OPEN: return ParseArray();
                    case TOKEN.TRUE: return true;
                    case TOKEN.FALSE: return false;
                    case TOKEN.NULL: return null;
                    default: return null;
                }
            }

            string ParseString()
            {
                StringBuilder s = new StringBuilder();
                char c;
                json.Read(); // "
                bool parsing = true;
                while (parsing)
                {
                    if (json.Peek() == -1) { parsing = false; break; }
                    c = NextChar;
                    switch (c)
                    {
                        case '"': parsing = false; break;
                        case '\\':
                            if (json.Peek() == -1) { parsing = false; break; }
                            c = NextChar;
                            switch (c)
                            {
                                case '"': case '\\': case '/': s.Append(c); break;
                                case 'b': s.Append('\b'); break;
                                case 'f': s.Append('\f'); break;
                                case 'n': s.Append('\n'); break;
                                case 'r': s.Append('\r'); break;
                                case 't': s.Append('\t'); break;
                                case 'u':
                                    var hex = new char[4];
                                    for (int i = 0; i < 4; i++) hex[i] = NextChar;
                                    s.Append((char)Convert.ToInt32(new string(hex), 16));
                                    break;
                            }
                            break;
                        default: s.Append(c); break;
                    }
                }
                return s.ToString();
            }

            object ParseNumber()
            {
                string number = NextWord;
                if (number.IndexOf('.') == -1 && number.IndexOf('E') == -1 && number.IndexOf('e') == -1)
                {
                    if (long.TryParse(number, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out long l))
                    {
                        if (l >= int.MinValue && l <= int.MaxValue) return (int)l;
                        return l;
                    }
                }
                if (double.TryParse(number, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double d))
                    return d;
                return 0;
            }

            void EatWhitespace()
            {
                while (Char.IsWhiteSpace(PeekChar)) { json.Read(); if (json.Peek() == -1) break; }
            }

            char PeekChar => Convert.ToChar(json.Peek());
            char NextChar => Convert.ToChar(json.Read());

            string NextWord
            {
                get
                {
                    StringBuilder word = new StringBuilder();
                    while (!IsWordBreak(PeekChar))
                    {
                        word.Append(NextChar);
                        if (json.Peek() == -1) break;
                    }
                    return word.ToString();
                }
            }

            TOKEN NextToken
            {
                get
                {
                    EatWhitespace();
                    if (json.Peek() == -1) return TOKEN.NONE;
                    switch (PeekChar)
                    {
                        case '{': return TOKEN.CURLY_OPEN;
                        case '}': json.Read(); return TOKEN.CURLY_CLOSE;
                        case '[': return TOKEN.SQUARED_OPEN;
                        case ']': json.Read(); return TOKEN.SQUARED_CLOSE;
                        case ',': json.Read(); return TOKEN.COMMA;
                        case '"': return TOKEN.STRING;
                        case ':': return TOKEN.COLON;
                        case '0': case '1': case '2': case '3': case '4':
                        case '5': case '6': case '7': case '8': case '9':
                        case '-': return TOKEN.NUMBER;
                    }
                    string word = NextWord;
                    switch (word)
                    {
                        case "false": return TOKEN.FALSE;
                        case "true": return TOKEN.TRUE;
                        case "null": return TOKEN.NULL;
                    }
                    return TOKEN.NONE;
                }
            }

            static bool IsWordBreak(char c) { return Char.IsWhiteSpace(c) || WORD_BREAK.IndexOf(c) != -1; }

            enum TOKEN { NONE, CURLY_OPEN, CURLY_CLOSE, SQUARED_OPEN, SQUARED_CLOSE, COLON, COMMA, STRING, NUMBER, TRUE, FALSE, NULL }
        }

        sealed class Serializer
        {
            private const int MaxDepth = 64;
            private const int MaxValues = 1000000;
            private readonly StringBuilder builder = new StringBuilder();
            private readonly List<object> active = new List<object>();
            private readonly int maxUtf8Bytes;
            private int valueCount;
            private long bytes;

            private Serializer(int limit)
            {
                if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
                maxUtf8Bytes = limit;
            }

            public static string Serialize(object obj, int limit)
            {
                var instance = new Serializer(limit);
                instance.SerializeValue(obj, 0);
                return instance.builder.ToString();
            }

            private void Reserve(int count)
            {
                long required = bytes + count;
                if (required > maxUtf8Bytes)
                    throw new SerializationException("byte_limit", "Serialized response exceeded its UTF-8 byte limit", required, maxUtf8Bytes);
                bytes = required;
            }

            private void Append(char value, int utf8Bytes = 1) { Reserve(utf8Bytes); builder.Append(value); }
            private void Append(string value) { Reserve(Encoding.UTF8.GetByteCount(value)); builder.Append(value); }

            private void SerializeValue(object value, int depth)
            {
                if (++valueCount > MaxValues)
                    throw new SerializationException("value_limit", "Serialization exceeded " + MaxValues + " values");
                if (value == null) { Append("null"); return; }
                if (value is string text) { SerializeString(text); return; }
                if (value is bool boolean) { Append(boolean ? "true" : "false"); return; }
                if (value is char character) { SerializeString(character.ToString()); return; }
                if (value is int || value is long || value is short || value is byte
                    || value is uint || value is ulong || value is ushort || value is sbyte || value is decimal)
                {
                    Append(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                    return;
                }
                if (value is float single)
                {
                    string number = single.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    if (float.IsNaN(single) || float.IsInfinity(single)) SerializeString(number); else Append(number);
                    return;
                }
                if (value is double real)
                {
                    string number = real.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    if (double.IsNaN(real) || double.IsInfinity(real)) SerializeString(number); else Append(number);
                    return;
                }
                if (depth >= MaxDepth)
                    throw new SerializationException("depth_limit", "Serialization exceeded " + MaxDepth + " container levels");
                foreach (var ancestor in active)
                    if (ReferenceEquals(ancestor, value))
                        throw new SerializationException("reference_cycle", "Serialization encountered a reference cycle");
                active.Add(value);
                try
                {
                    if (value is IDictionary dictionary) SerializeDictionary(dictionary, depth);
                    else if (value is IList list) SerializeArray(list, depth);
                    else SerializeObject(value, depth);
                }
                catch (SerializationException) { throw; }
                catch (System.Threading.ThreadAbortException) { throw; }
                catch (Exception error)
                {
                    throw new SerializationException("object_serialization_failed", error.GetBaseException().Message, inner: error);
                }
                finally { active.RemoveAt(active.Count - 1); }
            }

            private void SerializeObject(object obj, int depth)
            {
                Append('{');
                bool first = true;
                foreach (var prop in obj.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!prop.CanRead) continue;
                    if (!first) Append(',');
                    SerializeString(prop.Name);
                    Append(':');
                    object member;
                    try { member = prop.GetValue(obj, null); }
                    catch (System.Threading.ThreadAbortException) { throw; }
                    catch { member = null; }
                    // Only failed getters become null; nested serialization failures must discard the response.
                    SerializeValue(member, depth + 1);
                    first = false;
                }
                Append('}');
            }

            private void SerializeDictionary(IDictionary obj, int depth)
            {
                Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in obj)
                {
                    if (!first) Append(',');
                    SerializeString(entry.Key.ToString());
                    Append(':');
                    SerializeValue(entry.Value, depth + 1);
                    first = false;
                }
                Append('}');
            }

            private void SerializeArray(IList array, int depth)
            {
                Append('[');
                bool first = true;
                foreach (var item in array)
                {
                    if (!first) Append(',');
                    SerializeValue(item, depth + 1);
                    first = false;
                }
                Append(']');
            }

            private void SerializeString(string str)
            {
                Append('"');
                for (int i = 0; i < str.Length; i++)
                {
                    char c = str[i];
                    switch (c)
                    {
                        case '"': Append("\\\""); break;
                        case '\\': Append("\\\\"); break;
                        case '\b': Append("\\b"); break;
                        case '\f': Append("\\f"); break;
                        case '\n': Append("\\n"); break;
                        case '\r': Append("\\r"); break;
                        case '\t': Append("\\t"); break;
                        default:
                            if (char.IsHighSurrogate(c) && i + 1 < str.Length && char.IsLowSurrogate(str[i + 1]))
                            {
                                Reserve(4);
                                builder.Append(c).Append(str[++i]);
                            }
                            else if (c < ' ' || char.IsSurrogate(c))
                            {
                                Append("\\u");
                                Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                            }
                            else Append(c, c < 0x80 ? 1 : c < 0x800 ? 2 : 3);
                            break;
                    }
                }
                Append('"');
            }
        }
    }
}
