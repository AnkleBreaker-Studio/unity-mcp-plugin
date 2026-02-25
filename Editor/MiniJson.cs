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
            return Serializer.Serialize(obj);
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
            StringBuilder builder;
            Serializer() { builder = new StringBuilder(); }

            public static string Serialize(object obj)
            {
                var instance = new Serializer();
                instance.SerializeValue(obj);
                return instance.builder.ToString();
            }

            void SerializeValue(object value)
            {
                if (value == null) { builder.Append("null"); return; }

                if (value is string s) { SerializeString(s); return; }
                if (value is bool b) { builder.Append(b ? "true" : "false"); return; }

                if (value is IDictionary dict) { SerializeDictionary(dict); return; }
                if (value is IList list) { SerializeArray(list); return; }

                if (value is char c) { SerializeString(c.ToString()); return; }

                // Numbers
                if (value is int || value is long || value is short || value is byte
                    || value is uint || value is ulong || value is ushort || value is sbyte)
                {
                    builder.Append(value);
                    return;
                }

                if (value is float f)
                {
                    builder.Append(f.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    return;
                }

                if (value is double d)
                {
                    builder.Append(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    return;
                }

                if (value is decimal m)
                {
                    builder.Append(m.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return;
                }

                // Anonymous types and other objects — serialize public properties
                SerializeObject(value);
            }

            void SerializeObject(object obj)
            {
                builder.Append('{');
                bool first = true;
                var type = obj.GetType();
                foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!prop.CanRead) continue;
                    if (!first) builder.Append(',');
                    SerializeString(prop.Name);
                    builder.Append(':');
                    try
                    {
                        SerializeValue(prop.GetValue(obj, null));
                    }
                    catch
                    {
                        builder.Append("null");
                    }
                    first = false;
                }
                builder.Append('}');
            }

            void SerializeDictionary(IDictionary obj)
            {
                builder.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in obj)
                {
                    if (!first) builder.Append(',');
                    SerializeString(entry.Key.ToString());
                    builder.Append(':');
                    SerializeValue(entry.Value);
                    first = false;
                }
                builder.Append('}');
            }

            void SerializeArray(IList array)
            {
                builder.Append('[');
                bool first = true;
                foreach (var item in array)
                {
                    if (!first) builder.Append(',');
                    SerializeValue(item);
                    first = false;
                }
                builder.Append(']');
            }

            void SerializeString(string str)
            {
                builder.Append('\"');
                foreach (var c in str)
                {
                    switch (c)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\b': builder.Append("\\b"); break;
                        case '\f': builder.Append("\\f"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (c < ' ')
                            {
                                builder.Append("\\u");
                                builder.Append(((int)c).ToString("x4"));
                            }
                            else builder.Append(c);
                            break;
                    }
                }
                builder.Append('\"');
            }
        }
    }
}
