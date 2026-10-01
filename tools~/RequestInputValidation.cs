using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;

public static class UnityMcpRequestInputValidation
{
    private static readonly List<object> Checks = new List<object>();
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
    private const string ObjectName = "__McpRequestInputValidation";
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Check(string name, Func<object> action)
    {
        try { Checks.Add(new { name, passed = true, evidence = action() }); }
        catch (Exception error) { Checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
        finally { var go = GameObject.Find(ObjectName); if (go != null) UnityEngine.Object.DestroyImmediate(go); }
    }
    private static object Parse(string input) => typeof(MCPBridgeServer).GetMethod("ParseJson", Flags).Invoke(null, new object[] { input });
    private static bool Refuses(string input)
    {
        try { Parse(input); return false; } catch (TargetInvocationException) { return true; }
    }
    private sealed class ObservedStream : MemoryStream
    {
        internal int BytesRead;
        internal int Chunk = int.MaxValue;
        internal ObservedStream(byte[] bytes) : base(bytes) { }
        public override int Read(byte[] buffer, int offset, int count)
        { int read = base.Read(buffer, offset, Math.Min(count, Chunk)); BytesRead += read; return read; }
    }
    private static string Read(ObservedStream input, Encoding encoding, long limit)
    {
        var method = typeof(MCPBridgeServer).GetMethod("ReadRequestBody", Flags);
        try
        {
            if (method != null) return (string)method.Invoke(null, new object[] { input, encoding, limit });
            using (var reader = new StreamReader(input, encoding, true, 1024, true))
                return (string)typeof(MCPBridgeServer).GetMethod("ReadBounded", Flags).Invoke(null, new object[] { reader, limit });
        }
        catch (TargetInvocationException error)
        {
            if (error.InnerException.GetType().Name == "RequestInputException") return null;
            throw;
        }
    }
    private static object Bytes(string value, Encoding encoding, bool bom, int delta, int chunk = int.MaxValue)
    {
        byte[] bytes = (bom ? encoding.GetPreamble() : new byte[0]).Concat(encoding.GetBytes(value)).ToArray();
        using (var input = new ObservedStream(bytes) { Chunk = chunk })
        {
            string actual = Read(input, encoding, bytes.Length + delta);
            Require(delta >= 0 ? actual == value : actual == null, "Byte boundary was not enforced (" + bytes.Length + " bytes, limit " + (bytes.Length + delta) + ")");
            return new { bytes = bytes.Length, limit = bytes.Length + delta, read = input.BytesRead };
        }
    }
    private static Dictionary<string, object> Http(string route, string body, out int status, bool chunked = false, string origin = null)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var listener = new HttpListener(); string url = "http://127.0.0.1:" + port + "/";
        listener.Prefixes.Add(url); listener.Start();
        string responseBody = null, failure = null, clientStage = "start"; int code = 0, sent = 0, serverStatus = 0;
        var client = new Thread(() => {
            try {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                if (chunked && bytes.Length > 32 * 1024 * 1024)
                {
                    using (var socket = new TcpClient())
                    {
                        socket.Connect(IPAddress.Loopback, port); socket.ReceiveTimeout = 15000; socket.SendTimeout = 15000;
                        using (var stream = socket.GetStream())
                        {
                            clientStage = "headers";
                            byte[] header = Encoding.ASCII.GetBytes("POST /api/" + route + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nContent-Type: application/json; charset=utf-8\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n");
                            stream.Write(header, 0, header.Length);
                            string uploadFailure = null;
                            var upload = new Thread(() => {
                                try {
                                    for (int offset = 0; offset < 32 * 1024 * 1024 + 1;)
                                    {
                                        int count = Math.Min(65536, 32 * 1024 * 1024 + 1 - offset);
                                        byte[] chunkHeader = Encoding.ASCII.GetBytes(count.ToString("x") + "\r\n"); stream.Write(chunkHeader, 0, chunkHeader.Length);
                                        stream.Write(bytes, offset, count); sent += count; stream.Write(new byte[] { 13, 10 }, 0, 2); offset += count;
                                    }
                                    byte[] end = Encoding.ASCII.GetBytes("0\r\n\r\n"); stream.Write(end, 0, end.Length);
                                } catch (Exception error) { uploadFailure = error.Message; }
                            }) { IsBackground = true };
                            upload.Start();
                            clientStage = "response";
                            try { using (var reader = new StreamReader(stream))
                            {
                                string statusLine = reader.ReadLine(); code = int.Parse(statusLine.Split(' ')[1]);
                                int length = -1; string line; bool chunkedResponse = false;
                                while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                                {
                                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
                                    if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)) chunkedResponse = line.ToLowerInvariant().Contains("chunked");
                                }
                                var received = new StringBuilder();
                                do {
                                    if (chunkedResponse) length = Convert.ToInt32(reader.ReadLine(), 16);
                                    Require(length >= 0 && received.Length + length < 10000, "Missing bounded error response");
                                    if (length == 0) break;
                                    var responseChars = new char[length]; Require(reader.ReadBlock(responseChars, 0, length) == length, "Incomplete rejection response");
                                    received.Append(responseChars); if (chunkedResponse) reader.ReadLine();
                                } while (chunkedResponse);
                                responseBody = received.ToString();
                            } } finally { socket.Close(); Require(upload.Join(1000), "Upload worker did not stop"); }
                            Require(code == 413 || uploadFailure == null, "Upload failed without a rejection: " + uploadFailure);
                        }
                    }
                    return;
                }
                var request = WebRequest.CreateHttp(url + "api/" + route);
                request.Method = "POST"; request.Proxy = null; request.Timeout = 15000;
                request.ContentType = "application/json; charset=utf-8";
                if (origin != null) request.Headers["Origin"] = origin;
                if (chunked) request.SendChunked = true; else request.ContentLength = bytes.Length;
                using (var output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
                HttpWebResponse response;
                try { response = (HttpWebResponse)request.GetResponse(); }
                catch (WebException error) { response = (HttpWebResponse)error.Response; if (response == null) throw; }
                using (response) using (var reader = new StreamReader(response.GetResponseStream()))
                { code = (int)response.StatusCode; responseBody = reader.ReadToEnd(); }
            } catch (Exception error) { failure = "Client (" + clientStage + ", bytes sent " + sent + "): " + error.Message; }
        }) { IsBackground = true };
        Thread worker = null;
        try {
            var pending = listener.BeginGetContext(null, null); client.Start();
            Require(pending.AsyncWaitHandle.WaitOne(5000), "Owned request did not connect");
            var context = listener.EndGetContext(pending);
            worker = new Thread(() => { try { typeof(MCPBridgeServer).GetMethod("HandleRequest", Flags).Invoke(null, new object[] { context }); serverStatus = context.Response.StatusCode; } catch (Exception error) { failure = "Worker: " + error.GetBaseException().Message; } }) { IsBackground = true };
            worker.Start();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (client.IsAlive && DateTime.UtcNow < deadline) { MCPRequestQueue.ProcessNextRequests(); Thread.Sleep(1); }
            Require(client.Join(1000) && worker.Join(1000) && failure == null, "Owned request failed (server " + serverStatus + "): " + failure);
            MCPRequestQueue.ProcessNextRequests();
            status = code;
            return (Dictionary<string, object>)MiniJson.Deserialize(responseBody);
        } finally { listener.Close(); if (worker != null && worker.IsAlive) { worker.Abort(); worker.Join(1000); } client.Join(1000); }
    }
    public static void Run()
    {
        Require(File.Exists(".unity-mcp-validation"), "Use a marked disposable project");
        Check("Existing empty and valid object payloads retain their shapes", () => {
            Require(((Dictionary<string, object>)Parse("")).Count == 0, "Empty body changed");
            var d = (Dictionary<string, object>)Parse("{\"text\":\"\u4e2d\ud83d\ude00\",\"int\":4,\"long\":2147483648,\"real\":1.25,\"list\":[true,false,null]}");
            Require(d["int"] is int && d["long"] is long && d["real"] is double && ((List<object>)d["list"]).Count == 3, "Value types changed"); return d;
        });
        Check("Duplicate names preserve the existing last-value behavior", () => { Require((int)((Dictionary<string, object>)Parse("{\"v\":1,\"v\":2}"))["v"] == 2, "Duplicate semantics changed"); return true; });
        foreach (string input in new[] { "{} trailing", "{\"v\":1,}", "{\"v\":1 \"b\":2}", "{\"v\":01}", "{\"v\":1e}", "{\"v\":\"bad\\q\"}", "{\"v\":\"raw\nline\"}", "[]", "null", "{\"v\":[1,2,]}" })
        {
            string sample = input;
            Check("Reject invalid request " + sample.Replace("\n", "\\n"), () => { Require(Refuses(sample), "Invalid request was accepted"); return true; });
        }
        Check("Deep input stops before exhausting the native thread stack", () => { string input = "{\"v\":" + new string('[', 100) + "0" + new string(']', 100) + "}"; Require(Refuses(input), "100-level input was accepted"); return true; });
        Check("Exact depth boundary accepts 64 containers and rejects 65", () => {
            string allowed = "{\"v\":" + new string('[', 63) + "0" + new string(']', 63) + "}";
            Require(Parse(allowed) != null && Refuses("{\"extra\":" + allowed + "}"), "Depth boundary changed"); return true;
        });
        Check("Wide arrays stop at the shared value budget", () => {
            string prefix = "{\"v\":[" + string.Join(",", Enumerable.Repeat("0", 999997));
            Require(((List<object>)((Dictionary<string, object>)Parse(prefix + "]}"))["v"]).Count == 999997, "Exact value budget rejected");
            Require(Refuses(prefix + ",0]}"), "Over-budget array accepted"); return new { maximumValuesAndKeys = 1000000 };
        });
        Check("Malformed delimiters, EOF and escapes terminate with input errors", () => {
            string[] samples = { "{", "{\"a\":", "{\"a\":[:::]}", "{\"a\":[1 2]}", "{\"a\":\"unfinished", "{\"a\":\"\\u12\"}", "{\"a\":\"\\uXXXX\"}", "{\"a\":truefalse}", "{\"a\":.1}", "{\"a\":1.}", "{\"a\":-}", "{\"a\":1e309}", "{\"a\":+1}", "{\"a\":--1}", "{\"a\":false,}" };
            foreach (var sample in samples) Require(Refuses(sample), "Accepted malformed delimiter/escape/number"); return new { count = samples.Length };
        });
        Check("Escaped Unicode, control characters and finite numbers retain values", () => {
            var value = (Dictionary<string, object>)Parse("{\"s\":\"a\\n\\t\\r\\b\\f\\/\\\\\\\"\\u4e2d\\ud83d\\ude00\\ud800\",\"n\":-1.25e+2,\"large\":9223372036854775808}");
            Require((string)value["s"] == "a\n\t\r\b\f/\\\"\u4e2d\ud83d\ude00\ud800" && (double)value["n"] == -125 && value["large"] is double, "Decoded values changed"); return true;
        });
        Check("ASCII exact byte limit", () => Bytes("hello", Encoding.UTF8, false, 0));
        Check("ASCII over byte limit", () => Bytes("hello", Encoding.UTF8, false, -1));
        Check("UTF-8 exact byte limit across split sequences", () => Bytes("\u4e2d\ud83d\ude00", Encoding.UTF8, false, 0, 1));
        Check("UTF-8 over byte limit", () => Bytes("\u4e2d\ud83d\ude00", Encoding.UTF8, false, -1));
        Check("UTF-16 BOM counts toward the byte limit", () => Bytes("hello", Encoding.Unicode, true, -1));
        Check("UTF-16 BOM exact limit stays compatible", () => Bytes("\u4e2d\ud83d\ude00", Encoding.Unicode, true, 0, 1));
        Check("UTF-8 BOM exact limit stays compatible", () => Bytes("hello", new UTF8Encoding(true), true, 0, 1));
        Check("Byte reader stops at limit plus one without draining the body", () => {
            using (var input = new ObservedStream(new byte[100000])) {
                Require(Read(input, Encoding.UTF8, 10) == null && input.BytesRead <= 11, "Over-limit reader consumed " + input.BytesRead + " bytes"); return new { read = input.BytesRead };
            }
        });
        Check("Malformed legacy write is rejected before creating an object", () => {
            var result = Http("gameobject/create", "{\"name\":\"" + ObjectName + "\"} trailing", out int status);
            Require(status == 400 && GameObject.Find(ObjectName) == null, "Malformed write returned " + status + " or created an object"); return result;
        });
        Check("Malformed queue envelope is rejected before issuing a ticket", () => {
            var result = Http("queue/submit", "{\"apiPath\":\"editor/state\",\"body\":\"{}\"} trailing", out int status);
            Require(status == 400 && !result.ContainsKey("ticketId"), "Malformed envelope returned " + status); return result;
        });
        Check("Malformed inner write is rejected before issuing a ticket", () => {
            string body = MiniJson.Serialize(new { apiPath = "gameobject/create", body = "{\"name\":\"" + ObjectName + "\"} trailing" });
            var result = Http("queue/submit", body, out int status, true);
            Require(status == 400 && !result.ContainsKey("ticketId") && GameObject.Find(ObjectName) == null, "Malformed queued write returned " + status); return result;
        });
        Check("Valid chunked queue request is still accepted", () => {
            var result = Http("queue/submit", "{\"apiPath\":\"editor/state\",\"body\":\"{}\"}", out int status, true);
            Require(status == 202 && result.ContainsKey("ticketId"), "Valid request returned " + status); return new { status };
        });
        Check("Valid legacy request is still accepted", () => {
            var result = Http("gameobject/create", "{\"name\":\"" + ObjectName + "\"}", out int status);
            Require(status == 200 && GameObject.Find(ObjectName) != null, "Valid legacy write returned " + status); return new { status };
        });
        Check("Invalid deferred input is refused before a native operation starts", () => {
            var result = Http("packages/add", "{\"name\":\"example.invalid\"} trailing", out int status);
            Require(status == 400 && (bool)result["requestAccepted"] == false, "Invalid deferred input returned " + status); return result;
        });
        Check("Queue envelope field types are checked before dispatch", () => {
            foreach (var payload in new[] { "{\"apiPath\":123}", "{\"apiPath\":\"editor/state\",\"body\":{}}", "{\"apiPath\":\"editor/state\",\"body\":null}" }) {
                var result = Http("queue/submit", payload, out int status); Require(status == 400 && (bool)result["requestAccepted"] == false, "Wrong envelope type returned " + status);
            }
            return true;
        });
        Check("Guarded submission rejects malformed input before consuming its identity", () => {
            string requestId = Guid.NewGuid().ToString("N");
            long expiresAtMs = MCPRequestQueue.SessionTimeMs + MCPRequestQueue.RetryWindowMs - 1000;
            var invalid = new { apiPath = "gameobject/create", body = "{\"name\":\"" + ObjectName + "\"} trailing", requestId, queueSessionId = MCPRequestQueue.SessionId, expiresAtMs };
            var rejected = Http("queue/submit-once", MiniJson.Serialize(invalid), out int rejectedStatus);
            Require(rejectedStatus == 400 && !rejected.ContainsKey("ticketId") && GameObject.Find(ObjectName) == null, "Invalid guarded request was accepted");
            var valid = new { invalid.apiPath, body = "{\"name\":\"" + ObjectName + "\"}", requestId, invalid.queueSessionId, expiresAtMs };
            var accepted = Http("queue/submit-once", MiniJson.Serialize(valid), out int acceptedStatus);
            Require(acceptedStatus == 202 && GameObject.Find(ObjectName) != null, "Corrected guarded request returned " + acceptedStatus + ": " + MiniJson.Serialize(accepted));
            var duplicate = Http("queue/submit-once", MiniJson.Serialize(valid), out int duplicateStatus);
            Require(duplicateStatus == 202 && accepted["ticketId"].ToString() == duplicate["ticketId"].ToString(), "Duplicate changed ticket identity");
            return new { rejectedStatus, acceptedStatus, duplicateStatus };
        });
        Check("Actual chunked Unicode body cannot exceed the 32 MiB byte limit", () => {
            string large = "{\"v\":\"" + new string('\u4e2d', 12 * 1024 * 1024) + "\"}";
            var result = Http("queue/info", large, out int status, true);
            Require(status == 413 && (string)result["code"] == "request_too_large" && (bool)result["requestAccepted"] == false, "Chunked Unicode limit returned " + status);
            return new { status, sentEntityBytes = 32 * 1024 * 1024 + 1, preparedCharacters = large.Length, preparedBytes = Encoding.UTF8.GetByteCount(large) };
        });
        Check("Origin rejection still precedes parsing", () => {
            var result = Http("queue/submit", "{} trailing", out int status, false, "https://example.invalid"); Require(status == 403, "Origin guard returned " + status); return result;
        });
        bool passed = Checks.All(c => (bool)c.GetType().GetProperty("passed").GetValue(c));
        File.WriteAllText("Library/UnityMcpRequestInputValidation.json", MiniJson.Serialize(new { unityVersion = Application.unityVersion, passed, checks = Checks }));
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
