using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace UnityMCP.Editor
{
    internal sealed class RequestInputException : FormatException
    {
        internal readonly int Status;
        internal readonly string Code;
        internal RequestInputException(int status, string code, string message) : base(message)
        { Status = status; Code = code; }
    }

    internal static class MCPRequestInput
    {
        internal const long MaxBodyBytes = 32L * 1024 * 1024;
        internal const int MaxDepth = 64;
        internal const int MaxValues = 1000000;
        internal const int MaxConcurrentBodyReads = 8;
        internal const long MaxReservedBodyBytes = 64L * 1024 * 1024;
        internal const int BodyReadTimeoutMs = 30000;

        internal static string Read(Stream input, Encoding encoding, long limit)
        {
            using (var reader = new StreamReader(new LimitedStream(input, limit), encoding, true, 4096))
                return reader.ReadToEnd();
        }

        internal static string ReadHttpBody(Stream input, Encoding encoding, long expectedBytes)
        {
            int bufferSize = expectedBytes < 0 ? 8192 : expectedBytes > 32768 ? 32768 : 4096;
            using (var framing = ChunkedReadGuard.Observe(input))
            using (var reader = new StreamReader(new LimitedStream(input, MaxBodyBytes, expectedBytes, BodyReadTimeoutMs, framing), encoding, true, bufferSize))
                return reader.ReadToEnd();
        }

        // Mono can return zero decoded bytes for either partial framing or premature socket EOF.
        // Observe raw EOF separately so a split trailer stays valid but a truncated message cannot dispatch.
        private sealed class ChunkedReadGuard : IDisposable
        {
            private readonly Stream input, original;
            private readonly FieldInfo streamField;
            private readonly object decoder;
            private readonly PropertyInfo wantMore;
            private readonly EofStream observed;
            private ChunkedReadGuard(Stream input, FieldInfo field, Stream original, object decoder, PropertyInfo wantMore)
            {
                this.input = input; streamField = field; this.original = original; this.decoder = decoder; this.wantMore = wantMore;
                observed = new EofStream(original); streamField.SetValue(input, observed);
            }
            internal static ChunkedReadGuard Observe(Stream input)
            {
                var type = input.GetType();
                if (type.FullName != "System.Net.ChunkedInputStream") return null;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var field = type.BaseType?.GetField("stream", flags);
                var original = field?.GetValue(input) as Stream;
                var decoder = type.GetProperty("Decoder", flags)?.GetValue(input, null);
                var wantMore = decoder?.GetType().GetProperty("WantMore", flags);
                if (original == null || wantMore?.PropertyType != typeof(bool))
                    throw new RequestInputException(503, "chunk_validation_unavailable", "Cannot validate this runtime's chunk framing. Send a Content-Length body instead.");
                return new ChunkedReadGuard(input, field, original, decoder, wantMore);
            }
            internal bool NeedsMore()
            {
                if (!(bool)wantMore.GetValue(decoder, null)) return false;
                if (observed.Eof)
                    throw new RequestInputException(400, "incomplete_request_body", "Chunked request ended before its final chunk and trailers.");
                return true;
            }
            public void Dispose() { streamField.SetValue(input, original); }

            private sealed class EofStream : Stream
            {
                private readonly Stream source;
                internal bool Eof;
                internal EofStream(Stream source) { this.source = source; }
                private int Observe(int count) { if (count == 0) Eof = true; return count; }
                public override int Read(byte[] buffer, int offset, int count) => Observe(source.Read(buffer, offset, count));
                public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state) => source.BeginRead(buffer, offset, count, callback, state);
                public override int EndRead(IAsyncResult result) => Observe(source.EndRead(result));
                public override bool CanRead => source.CanRead;
                public override bool CanSeek => false;
                public override bool CanWrite => false;
                public override long Length => throw new NotSupportedException();
                public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
                public override void Flush() { }
                public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
                public override void SetLength(long value) => throw new NotSupportedException();
                public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            }
        }

        private sealed class LimitedStream : Stream
        {
            private readonly Stream source;
            private readonly long limit;
            private readonly long expectedBytes, started;
            private readonly int timeoutMs;
            private readonly ChunkedReadGuard framing;
            private long received;
            internal LimitedStream(Stream source, long limit, long expectedBytes = -1, int timeoutMs = 0, ChunkedReadGuard framing = null)
            {
                if (limit < 0 || limit == long.MaxValue) throw new ArgumentOutOfRangeException(nameof(limit));
                this.source = source; this.limit = limit;
                this.expectedBytes = expectedBytes; this.timeoutMs = timeoutMs; started = Stopwatch.GetTimestamp();
                this.framing = framing;
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int boundedCount = (int)Math.Min(count, limit - received + 1);
                int read;
                do { read = timeoutMs == 0 ? source.Read(buffer, offset, boundedCount) : ReadBeforeDeadline(buffer, offset, boundedCount); }
                while (read == 0 && framing != null && framing.NeedsMore());
                received += read;
                MCPHttpDiagnostics.ReadBytes(read);
                if (received > limit)
                    throw new RequestInputException(413, "request_too_large", "Request body exceeded the " + limit + "-byte limit.");
                if (read == 0 && expectedBytes >= 0 && received != expectedBytes)
                    throw new RequestInputException(400, "incomplete_request_body", "Request body ended before its declared Content-Length.");
                return read;
            }

            private int RemainingMs()
            {
                double elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                if (elapsed >= timeoutMs)
                    throw new RequestInputException(408, "request_body_timeout", "Request body was not received within " + timeoutMs + " ms.");
                return Math.Max(1, (int)Math.Ceiling(timeoutMs - elapsed));
            }

            private int ReadBeforeDeadline(byte[] buffer, int offset, int count)
            {
                RemainingMs();
                var pending = new PendingRead(source);
                try
                {
                    var result = source.BeginRead(buffer, offset, count, PendingRead.Complete, pending);
                    if (result.IsCompleted) PendingRead.Complete(result);
                }
                catch (Exception error) when (!(error is ThreadAbortException))
                { throw new RequestInputException(400, "request_body_read_failed", "Could not read the request body."); }
                lock (pending)
                {
                    while (!pending.Done) Monitor.Wait(pending, RemainingMs());
                    RemainingMs();
                    if (pending.Error != null)
                        throw new RequestInputException(400, "request_body_read_failed", "Could not read the complete request body.");
                    return pending.Count;
                }
            }

            // Mono does not implement HttpListener entity timeouts. APM lets the waiting worker enforce one deadline.
            // After timeout, the handler closes the response connection; the callback still consumes EndRead safely.
            private sealed class PendingRead
            {
                private readonly Stream source;
                internal bool Done;
                internal int Count;
                internal Exception Error;
                private int ending;
                internal PendingRead(Stream source) { this.source = source; }
                internal static readonly AsyncCallback Complete = result => {
                    var pending = (PendingRead)result.AsyncState;
                    if (Interlocked.CompareExchange(ref pending.ending, 1, 0) != 0) return;
                    try { pending.Count = pending.source.EndRead(result); }
                    catch (Exception error) { pending.Error = error; }
                    finally { lock (pending) { pending.Done = true; Monitor.PulseAll(pending); } }
                };
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        internal static Dictionary<string, object> ParseObject(string text)
            => ParseObject(text, out _);

        internal static Dictionary<string, object> ParseObject(string text, out long argumentCost)
        {
            argumentCost = 128;
            if (string.IsNullOrEmpty(text)) return new Dictionary<string, object>();
            var parser = new Parser(text);
            object value = parser.Parse();
            // Charge strings and decoded nodes without a second tree walk; this is accounting, not heap measurement.
            argumentCost += 2L * text.Length + 64L * parser.ValueCount;
            if (value is Dictionary<string, object> result) return result;
            throw new RequestInputException(400, "invalid_request", "Request JSON must be an object.");
        }

        // Network input must be complete before dispatch; legacy asset readers retain MiniJson's contract.
        private sealed class Parser
        {
            private readonly string text;
            private int index;
            private int values;
            internal int ValueCount => values;
            internal Parser(string text) { this.text = text; }
            private RequestInputException Invalid(string message) => new RequestInputException(400, "invalid_json", message + " At character " + index + ".");
            private void Space()
            {
                while (index < text.Length && (text[index] == ' ' || text[index] == '\t' || text[index] == '\r' || text[index] == '\n')) index++;
            }
            private bool Take(char character)
            {
                if (index >= text.Length || text[index] != character) return false;
                index++; return true;
            }
            private void Count()
            {
                if (++values > MaxValues) throw new RequestInputException(413, "json_value_limit", "Request JSON exceeded " + MaxValues + " values and property names.");
            }
            internal object Parse()
            {
                Space(); object value = Value(0); Space();
                if (index != text.Length) throw Invalid("Unexpected content after JSON value.");
                return value;
            }
            private object Value(int depth)
            {
                Space(); Count();
                if (index >= text.Length) throw Invalid("Expected a JSON value.");
                char current = text[index];
                if (current == '"') return String();
                if (current == '{' || current == '[')
                {
                    if (depth >= MaxDepth) throw new RequestInputException(413, "json_depth_limit", "Request JSON exceeded " + MaxDepth + " container levels.");
                    index++;
                    return current == '{' ? (object)Object(depth + 1) : Array(depth + 1);
                }
                if (current == 't') { Literal("true"); return true; }
                if (current == 'f') { Literal("false"); return false; }
                if (current == 'n') { Literal("null"); return null; }
                if (current == '-' || (current >= '0' && current <= '9')) return Number();
                throw Invalid("Expected a JSON value.");
            }
            private Dictionary<string, object> Object(int depth)
            {
                var result = new Dictionary<string, object>(); Space();
                if (Take('}')) return result;
                while (true)
                {
                    Space(); Count();
                    if (index >= text.Length || text[index] != '"') throw Invalid("Expected a quoted property name.");
                    string name = String(); Space();
                    if (!Take(':')) throw Invalid("Expected ':' after the property name.");
                    result[name] = Value(depth); Space();
                    if (Take('}')) return result;
                    if (!Take(',')) throw Invalid("Expected ',' or '}' after the property value.");
                }
            }
            private List<object> Array(int depth)
            {
                var result = new List<object>(); Space();
                if (Take(']')) return result;
                while (true)
                {
                    result.Add(Value(depth)); Space();
                    if (Take(']')) return result;
                    if (!Take(',')) throw Invalid("Expected ',' or ']' after the array value.");
                }
            }
            private string String()
            {
                index++; int start = index; StringBuilder result = null;
                while (index < text.Length)
                {
                    char current = text[index++];
                    if (current == '"')
                    {
                        if (result == null) return text.Substring(start, index - start - 1);
                        result.Append(text, start, index - start - 1); return result.ToString();
                    }
                    if (current < 0x20) throw Invalid("Unescaped control character in string.");
                    if (current != '\\') continue;
                    if (result == null) result = new StringBuilder();
                    result.Append(text, start, index - start - 1);
                    if (index >= text.Length) throw Invalid("Incomplete string escape.");
                    switch (text[index++])
                    {
                        case '"': result.Append('"'); break;
                        case '\\': result.Append('\\'); break;
                        case '/': result.Append('/'); break;
                        case 'b': result.Append('\b'); break;
                        case 'f': result.Append('\f'); break;
                        case 'n': result.Append('\n'); break;
                        case 'r': result.Append('\r'); break;
                        case 't': result.Append('\t'); break;
                        case 'u':
                            int code = 0;
                            for (int i = 0; i < 4; i++)
                            {
                                if (index >= text.Length) throw Invalid("Incomplete Unicode escape.");
                                char hex = text[index++];
                                int digit = hex >= '0' && hex <= '9' ? hex - '0' : hex >= 'a' && hex <= 'f' ? hex - 'a' + 10 : hex >= 'A' && hex <= 'F' ? hex - 'A' + 10 : -1;
                                if (digit < 0) throw Invalid("Invalid Unicode escape.");
                                code = code * 16 + digit;
                            }
                            result.Append((char)code); break;
                        default: throw Invalid("Invalid string escape.");
                    }
                    start = index;
                }
                throw Invalid("Unterminated string.");
            }
            private void Literal(string literal)
            {
                if (text.Length - index < literal.Length || string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
                    throw Invalid("Invalid JSON literal.");
                index += literal.Length;
            }
            private bool Digit() => index < text.Length && text[index] >= '0' && text[index] <= '9';
            private object Number()
            {
                int start = index; Take('-');
                if (!Take('0'))
                {
                    if (!Digit()) throw Invalid("Expected a number digit.");
                    while (Digit()) index++;
                }
                bool integral = true;
                if (Take('.'))
                {
                    integral = false;
                    if (!Digit()) throw Invalid("Expected a fractional digit.");
                    while (Digit()) index++;
                }
                if (Take('e') || Take('E'))
                {
                    integral = false; if (!Take('+')) Take('-');
                    if (!Digit()) throw Invalid("Expected an exponent digit.");
                    while (Digit()) index++;
                }
                string number = text.Substring(start, index - start);
                if (integral && long.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
                {
                    if (integer >= int.MinValue && integer <= int.MaxValue) return (int)integer;
                    return integer;
                }
                if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double real) && !double.IsInfinity(real) && !double.IsNaN(real)) return real;
                throw Invalid("Number exceeds the supported finite range.");
            }
        }
    }
}
