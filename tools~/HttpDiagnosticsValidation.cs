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

public static class UnityMcpHttpDiagnosticsValidation
{
    private static readonly List<object> Checks = new List<object>();
    private static Dictionary<string, object> Snapshot() => (Dictionary<string, object>)MCPRequestQueue.GetQueueInfo()["http"];
    private static long Count(Dictionary<string, object> snapshot, string key) => Convert.ToInt64(snapshot[key]);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Check(string name, Func<object> action)
    {
        try { Checks.Add(new { name, passed = true, evidence = action() }); }
        catch (Exception error) { Checks.Add(new { name, passed = false, error = error.ToString() }); }
    }

    private static string Line(Stream stream)
    {
        var text = new StringBuilder();
        while (text.Length < 16384)
        {
            int value = stream.ReadByte(); Require(value >= 0, "Unexpected response EOF");
            if (value == '\n') return text.ToString().TrimEnd('\r');
            text.Append((char)value);
        }
        throw new InvalidOperationException("Unbounded response header");
    }

    private static object Exchange(string route, string body, int expectedStatus, string origin = null, string method = "POST", long? expectedInput = null)
    {
        var before = Snapshot();
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var listener = new HttpListener(); string url = "http://127.0.0.1:" + port + "/";
        listener.Prefixes.Add(url); listener.Start();
        Thread worker = null;
        string failure = null, responseBody = null; int status = 0;
        var client = new Thread(() => {
            try {
                using (var socket = new TcpClient()) {
                    socket.Connect(IPAddress.Loopback, port); socket.ReceiveTimeout = 10000;
                    string header = method + " /" + route + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nContent-Type: application/json; charset=utf-8\r\nConnection: close\r\n"
                        + (origin == null ? "" : "Origin: " + origin + "\r\n") + "Content-Length: " + bytes.Length + "\r\n\r\n";
                    byte[] packet = Encoding.UTF8.GetBytes(header).Concat(bytes).ToArray();
                    var stream = socket.GetStream(); stream.Write(packet, 0, packet.Length);
                    status = int.Parse(Line(stream).Split(' ')[1]);
                    int length = -1; string line;
                    while ((line = Line(stream)).Length > 0)
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
                    Require(length >= 0 && length <= 16 * 1024 * 1024, "Invalid response size");
                    byte[] received = new byte[length];
                    for (int offset = 0; offset < length;) {
                        int read = stream.Read(received, offset, length - offset); Require(read > 0, "Incomplete response"); offset += read;
                    }
                    responseBody = Encoding.UTF8.GetString(received);
                }
            } catch (Exception error) { failure = "Client: " + error.Message; }
        }) { IsBackground = true };
        try {
            var pending = listener.BeginGetContext(null, null); client.Start();
            Require(pending.AsyncWaitHandle.WaitOne(5000), "Owned listener did not receive request");
            var context = listener.EndGetContext(pending);
            var handle = (Action<HttpListenerContext>)Delegate.CreateDelegate(typeof(Action<HttpListenerContext>),
                typeof(MCPBridgeServer).GetMethod("HandleRequest", BindingFlags.Static | BindingFlags.NonPublic));
            worker = new Thread(() => { try { handle(context); } catch (Exception error) { failure = "Worker: " + error; } }) { IsBackground = true };
            worker.Start();
            Require(worker.Join(10000) && client.Join(10000) && failure == null, "HTTP failed: " + failure);
            var after = Snapshot();
            Require(status == expectedStatus, "Unexpected status " + status + ": " + responseBody);
            Require(Count(after, "receivedRequests") - Count(before, "receivedRequests") == 1, "Received count");
            Require(Count(after, "completedRequests") - Count(before, "completedRequests") == 1, "Completed count");
            Require(Count(after, "activeRequests") == Count(before, "activeRequests"), "Active count leaked");
            string bucket = "responses" + (status / 100) + "xx";
            Require(Count(after, bucket) - Count(before, bucket) == 1, "Status bucket " + bucket);
            Require(Count(after, "inputBytesRead") - Count(before, "inputBytesRead") == (expectedInput ?? bytes.Length), "UTF-8 input bytes");
            Require(Count(after, "outputBytesWritten") - Count(before, "outputBytesWritten") == Encoding.UTF8.GetByteCount(responseBody), "UTF-8 output bytes");
            return new { status, inputBytes = Count(after, "inputBytesRead") - Count(before, "inputBytesRead"), after };
        } finally {
            listener.Close();
            if (worker != null && worker.IsAlive) { worker.Abort(); worker.Join(1000); }
            client.Join(11000);
        }
    }

    public static void Run()
    {
        Require(File.Exists(".unity-mcp-validation"), "Use a marked disposable project");
        Check("Queue info exposes domain-scoped HTTP counters", () => {
            var value = Snapshot();
            Require(Count(value, "activeRequests") == 0 && DateTime.TryParse((string)value["startedAtUtc"], out _), "Invalid initial snapshot"); return value;
        });
        Check("Successful read counts actual UTF-8 bytes", () => Exchange("api/queue/info", "{\"unused\":\"\u4e2d\ud83d\ude00\"}", 200));
        Check("Malformed request is visible before queue acceptance", () => {
            var before = Snapshot(); var result = Exchange("api/queue/submit", "{} trailing", 400);
            Require(Count(Snapshot(), "inputRejectedRequests") == Count(before, "inputRejectedRequests") + 1, "Missing input rejection"); return result;
        });
        Check("Nested queue body rejection is counted once", () => {
            var before = Snapshot(); var result = Exchange("api/queue/submit", "{\"apiPath\":\"ping\",\"body\":\"[]\"}", 400);
            Require(Count(Snapshot(), "inputRejectedRequests") == Count(before, "inputRejectedRequests") + 1, "Nested rejection doubled or absent"); return result;
        });
        Check("Deep input uses HTTP 413 and input rejection counters", () => {
            var before = Snapshot(); var result = Exchange("api/queue/info", "{\"v\":" + new string('[', 65) + "0" + new string(']', 65) + "}", 413);
            Require(Count(Snapshot(), "inputRejectedRequests") == Count(before, "inputRejectedRequests") + 1, "Depth rejection absent"); return result;
        });
        Check("Origin refusal has no body read or input-parser rejection", () => {
            var before = Snapshot(); var result = Exchange("api/queue/info", "{}", 403, "https://invalid.example", expectedInput: 0);
            Require(Count(Snapshot(), "inputRejectedRequests") == Count(before, "inputRejectedRequests"), "Origin mislabeled as parser rejection"); return result;
        });
        Check("Unknown route is visible in HTTP 4xx", () => Exchange("api/__unknown", "{}", 404));
        Check("Method refusal is visible before dispatch", () => Exchange("api/gameobject/create", "", 405, method: "GET"));
        Check("Missing API prefix is visible before body reading", () => Exchange("missing", "{}", 404, expectedInput: 0));
        Check("Queue acceptance is a completed HTTP 202", () => Exchange("api/queue/submit", "{\"apiPath\":\"ping\"}", 202));
        MCPRequestQueue.ProcessNextRequests();
        Check("Command error remains HTTP 200", () => {
            var ticket = MCPRequestQueue.SubmitRequest("__HttpMetrics", "ping", () => new { error = "controlled command failure" });
            MCPRequestQueue.ProcessNextRequests();
            var before = Snapshot(); var result = Exchange("api/queue/status?ticketId=" + ticket.TicketId, "", 200);
            Require(Count(Snapshot(), "inputRejectedRequests") == Count(before, "inputRejectedRequests"), "Command error labeled input reject"); return result;
        });
        Check("Cyclic response is HTTP 500 with serialization failure", () => {
            var cycle = new Dictionary<string, object>(); cycle["self"] = cycle;
            var ticket = MCPRequestQueue.SubmitRequest("__HttpMetrics", "ping", () => cycle);
            MCPRequestQueue.ProcessNextRequests();
            var before = Snapshot(); var result = Exchange("api/queue/status?ticketId=" + ticket.TicketId, "", 500);
            Require(Count(Snapshot(), "responseSerializationFailures") == Count(before, "responseSerializationFailures") + 1, "Serialization not counted"); return result;
        });
        Check("Oversized response is distinct from rejected input", () => {
            var ticket = MCPRequestQueue.SubmitRequest("__HttpMetrics", "ping", () => new { value = new string('x', 17 * 1024 * 1024) });
            MCPRequestQueue.ProcessNextRequests();
            var before = Snapshot(); var result = Exchange("api/queue/status?ticketId=" + ticket.TicketId, "", 413);
            Require(Count(Snapshot(), "responseSerializationFailures") == Count(before, "responseSerializationFailures") + 1, "Output limit not counted");
            Require(Count(Snapshot(), "inputRejectedRequests") == Count(before, "inputRejectedRequests"), "Output limit counted as input reject"); return result;
        });
        Check("Completed counts partition into response classes and incomplete requests", () => {
            var value = Snapshot();
            Require(Count(value, "receivedRequests") == Count(value, "completedRequests") + Count(value, "activeRequests"), "Request invariant");
            Require(Count(value, "completedRequests") == new[] { "responses2xx", "responses4xx", "responses5xx", "otherResponses", "incompleteRequests" }.Sum(key => Count(value, key)), "Response invariant");
            Require(Convert.ToDouble(value["maxDurationMs"]) >= Convert.ToDouble(value["averageDurationMs"]) && Convert.ToDouble(value["averageDurationMs"]) > 0, "Timing invariant"); return value;
        });
        bool passed = Checks.All(value => (bool)value.GetType().GetProperty("passed").GetValue(value));
        File.WriteAllText("Library/UnityMcpHttpDiagnosticsValidation.json", MiniJson.Serialize(new { unityVersion = Application.unityVersion, passed, checks = Checks }));
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
