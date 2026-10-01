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

public static class UnityMcpQueueAdmissionValidation
{
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly List<object> Checks = new List<object>();
    private static readonly Action<HttpListenerContext> Handle = (Action<HttpListenerContext>)Delegate.CreateDelegate(
        typeof(Action<HttpListenerContext>), typeof(MCPBridgeServer).GetMethod("HandleRequest", Hidden));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static Dictionary<string, object> Metrics() => MCPRequestQueue.GetQueueInfo().TryGetValue("httpCommands", out var value)
        ? (Dictionary<string, object>)value : new Dictionary<string, object>();
    private static long Metric(string name) => Metrics().TryGetValue(name, out var value) ? Convert.ToInt64(value) : -1;
    private static int Queued => Convert.ToInt32(MCPRequestQueue.GetQueueInfo()["totalQueued"]);
    private static void Drain()
    {
        for (int index = 0; Queued > 0 && index < 2000; index++) MCPRequestQueue.ProcessNextRequests();
        Require(Queued == 0, "Fixture queue did not drain");
    }
    private static void Check(string name, Func<object> action)
    {
        try { Checks.Add(new { name, passed = true, evidence = action() }); }
        catch (Exception error) { Checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
        finally { Drain(); }
    }

    private sealed class Exchange : IDisposable
    {
        internal int Status;
        internal Dictionary<string, object> Result;
        private readonly HttpListener listener = new HttpListener();
        private Thread client, worker;
        private Exception failure;
        internal Exchange(string route, string body, bool process = false)
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            string address = "http://127.0.0.1:" + port + "/";
            listener.Prefixes.Add(address); listener.Start();
            var pending = listener.BeginGetContext(null, null);
            client = new Thread(() => {
                try {
                    var request = WebRequest.CreateHttp(address + "api/" + route);
                    request.Proxy = null; request.Method = "POST"; request.Timeout = 10000;
                    request.KeepAlive = false;
                    request.ServicePoint.Expect100Continue = false;
                    request.ContentType = "application/json; charset=utf-8";
                    byte[] bytes = Encoding.UTF8.GetBytes(body); request.ContentLength = bytes.Length;
                    using (var output = request.GetRequestStream()) output.Write(bytes, 0, bytes.Length);
                    HttpWebResponse response;
                    try { response = (HttpWebResponse)request.GetResponse(); }
                    catch (WebException error) { response = (HttpWebResponse)error.Response; if (response == null) throw; }
                    using (response) using (var reader = new StreamReader(response.GetResponseStream())) {
                        Status = (int)response.StatusCode;
                        Result = (Dictionary<string, object>)MiniJson.Deserialize(reader.ReadToEnd());
                    }
                } catch (Exception error) { failure = error; }
            }) { IsBackground = true };
            client.Start();
            Require(pending.AsyncWaitHandle.WaitOne(5000), "Owned listener did not receive " + route + ": " + failure);
            var context = listener.EndGetContext(pending);
            worker = new Thread(() => { try { Handle(context); } catch (Exception error) { failure = error; } }) { IsBackground = true };
            worker.Start();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!worker.Join(5) && watch.ElapsedMilliseconds < 8000) {
                if (process || watch.ElapsedMilliseconds > 250) MCPRequestQueue.ProcessNextRequests();
            }
            Require(!worker.IsAlive && client.Join(3000), "Fixture exchange did not finish");
            if (failure != null) throw failure;
        }
        public void Dispose() { listener.Close(); }
    }

    private static object Envelope(string body = "{}", string requestId = null, long expires = 0) => requestId == null
        ? (object)new { apiPath = "editor/state", body, agentId = "admission-wire" }
        : new { apiPath = "editor/state", body, agentId = "admission-wire", requestId, queueSessionId = MCPRequestQueue.SessionId, expiresAtMs = expires };
    private static long Submit(object envelope, out int status)
    {
        using (var exchange = new Exchange("queue/submit", MiniJson.Serialize(envelope))) {
            status = exchange.Status;
            return exchange.Result.TryGetValue("ticketId", out var id) ? Convert.ToInt64(id) : -1;
        }
    }
    private static void Fill(int count)
    {
        for (int index = 0; index < count; index++) {
            Submit(Envelope(), out int status); Require(status == 202, "Capacity reached before the documented limit");
        }
    }

    private static MCPRequestQueue.RequestTicket Native(Func<object> action, long cost)
        => (MCPRequestQueue.RequestTicket)typeof(MCPRequestQueue).GetMethod("SubmitHttpRequest", Hidden)
            .Invoke(null, new object[] { "admission-native", "editor/state", action, cost });
    private static MCPRequestQueue.RequestTicket Deferred(Action<Action<object>, Func<bool>> action, long cost)
        => (MCPRequestQueue.RequestTicket)typeof(MCPRequestQueue).GetMethod("SubmitHttpDeferredRequest", Hidden)
            .Invoke(null, new object[] { "admission-deferred", "editor/state", action, cost });
    private static void Finish(MCPRequestQueue.RequestTicket ticket, MCPRequestQueue.RequestStatus status)
        => typeof(MCPRequestQueue).GetMethod("TryCompleteTicket", Hidden).Invoke(null, new object[] { ticket, status, null, "Fixture terminal outcome", -1, null });
    private static void AssertBusy(Action submit)
    {
        try { submit(); throw new InvalidOperationException("Admission unexpectedly succeeded"); }
        catch (TargetInvocationException error) {
            Require(error.InnerException.GetType().Name == "RequestInputException"
                && error.InnerException.Message.Contains("No command was accepted"), "Unexpected refusal: " + error.InnerException);
        }
    }

    public static void Run()
    {
        Require(File.Exists(".unity-mcp-validation"), "Use a marked disposable validation project");
        Check("Ordinary queued and legacy reads preserve their response forms", () => {
            long ticket = Submit(Envelope(), out int status); Require(status == 202 && ticket > 0, "Queue control failed"); Drain();
            using (var legacy = new Exchange("editor/state", "{}", true)) Require(legacy.Status == 200 && legacy.Result.ContainsKey("projectPath"), "Legacy control failed");
            return true;
        });
        Check("HTTP command count is bounded after body readers have finished", () => {
            Fill(256);
            using (var rejected = new Exchange("queue/submit", MiniJson.Serialize(Envelope()))) {
                Require(rejected.Status == 503 && rejected.Result["code"].ToString() == "command_queue_busy"
                    && (bool)rejected.Result["requestAccepted"] == false, "The 257th command was accepted");
                Require(Queued == 256 && Metric("activeCount") == 256, "Count limit changed admitted work");
                var http = (Dictionary<string, object>)MCPRequestQueue.GetQueueInfo()["http"];
                Require(Convert.ToInt64(http["activeBodyReaders"]) == 0, "Body readers remained active after replies");
                return Metrics();
            }
        });
        Check("Queue pressure refuses legacy dispatch without a ticket or side effects", () => {
            Fill(256);
            using (var rejected = new Exchange("editor/state", "{}")) {
                Require(rejected.Status == 503 && (bool)rejected.Result["requestAccepted"] == false, "Legacy command bypassed admission");
                Require(Queued == 256, "Legacy refusal changed queued work");
                var waiters = (System.Collections.IDictionary)typeof(MCPRequestQueue).GetField("_waiters", Hidden).GetValue(null);
                Require(waiters.Count == 0, "Legacy refusal retained a synchronous waiter"); return rejected.Result;
            }
        });
        Check("Protected replay succeeds at capacity and a refused identity remains reusable", () => {
            string id = Guid.NewGuid().ToString("N"); long expires = MCPRequestQueue.SessionTimeMs + 119000;
            var original = Envelope(requestId: id, expires: expires);
            long ticket = Submit(original, out int status); Require(status == 202, "First protected submission failed"); Fill(255);
            Require(Submit(original, out status) == ticket && status == 202, "Replay needed a second admission slot");
            string nextId = Guid.NewGuid().ToString("N"); var next = Envelope(requestId: nextId, expires: expires);
            Submit(next, out status); Require(status == 503, "Protected overflow was accepted");
            MCPRequestQueue.ProcessNextRequests();
            long accepted = Submit(next, out status); Require(status == 202 && accepted != ticket, "Refusal consumed the protected identity");
            return new { originalTicket = ticket, acceptedAfterCapacity = accepted };
        });
        Check("Completed work releases its count and argument budget", () => {
            Submit(Envelope("{\"values\":[1,true,null,{\"text\":\"\\u4e2d\"}]}"), out int status);
            Require(status == 202 && Metric("activeCount") == 1 && Metric("argumentCostBytes") > 0, "Missing admitted argument accounting");
            long bytes = Metric("argumentCostBytes"); Drain();
            Require(Metric("activeCount") == 0 && Metric("argumentCostBytes") == 0, "Completed arguments stayed charged");
            return new { releasedCostBytes = bytes };
        });
        Check("Queued and legacy deferred routes share the admission limit", () => {
            Fill(256);
            foreach (string endpoint in new[] { "queue/submit", "packages/list" }) {
                string body = endpoint == "queue/submit" ? MiniJson.Serialize(new { apiPath = "packages/list", body = "{}" }) : "{}";
                using (var rejected = new Exchange(endpoint, body))
                    Require(rejected.Status == 503 && (bool)rejected.Result["requestAccepted"] == false, "Deferred route bypassed admission");
            }
            Require(Queued == 256 && Metric("activeCount") == 256, "Deferred refusal changed admitted work"); return true;
        });
        Check("Decoded value count contributes to the argument budget", () => {
            string body = "{\"values\":[" + string.Join(",", Enumerable.Repeat("null", 10000)) + "]}";
            Submit(Envelope(body), out int status);
            Require(status == 202 && Metric("argumentCostBytes") > 640000, "Decoded nodes were charged only as text");
            return new { sourceCharacters = body.Length, accountedBytes = Metric("argumentCostBytes") };
        });
        Check("Ordinary multi-megabyte input remains admissible and releases after execution", () => {
            string body = "{\"text\":\"" + new string('x', 4 * 1024 * 1024) + "\"}";
            Submit(Envelope(body), out int status);
            Require(status == 202 && Metric("argumentCostBytes") >= 8 * 1024 * 1024, "Large input was refused or unaccounted");
            long accounted = Metric("argumentCostBytes"); Drain();
            Require(Metric("activeCount") == 0 && Metric("argumentCostBytes") == 0, "Large input stayed charged");
            return new { sourceCharacters = body.Length, accountedBytes = accounted };
        });
        Check("An exact byte-budget fit passes and one additional byte fails without creating a ticket", () => {
            long limit = Metric("maxArgumentCostBytes"); Require(limit > 0, "No byte budget advertised");
            var ticket = Native(() => true, limit);
            try {
                AssertBusy(() => Native(() => true, 1));
                Require(Metric("activeCount") == 1 && Metric("argumentCostBytes") == limit && Queued == 1, "Byte refusal changed admitted state");
                return new { accountedLimit = limit, syntheticAccountingOnly = true };
            } finally { Finish(ticket, MCPRequestQueue.RequestStatus.TimedOut); }
        });
        Check("Concurrent reservations never exceed the aggregate argument budget", () => {
            long cost = Metric("maxArgumentCostBytes") / 16;
            var threads = new Thread[32]; var accepted = new bool[32]; var failures = new Exception[32];
            for (int index = 0; index < threads.Length; index++) {
                int slot = index;
                threads[index] = new Thread(() => { try { Native(() => true, cost); accepted[slot] = true; }
                    catch (TargetInvocationException error) { if (error.InnerException.GetType().Name != "RequestInputException") failures[slot] = error; }
                    catch (Exception error) { failures[slot] = error; } });
                threads[index].Start();
            }
            foreach (var thread in threads) Require(thread.Join(5000), "Reservation worker did not finish");
            Require(failures.All(error => error == null), "Reservation failed unexpectedly");
            Require(accepted.Count(value => value) == 16 && Metric("activeCount") == 16
                && Metric("argumentCostBytes") == Metric("maxArgumentCostBytes"), "Concurrent admission exceeded or lost the budget");
            return new { accepted = 16, rejected = 16, syntheticAccountingOnly = true };
        });
        Check("Deferred work keeps its reservation while executing and releases once", () => {
            Action<object> resolve = null;
            var ticket = Deferred((complete, active) => resolve = complete, 12345);
            try {
                Drain();
                Require(ticket.Status == MCPRequestQueue.RequestStatus.Executing && Metric("argumentCostBytes") == 12345, "Deferred work released its reservation too soon");
                resolve(true); resolve(false);
                Require(Metric("activeCount") == 0 && Metric("argumentCostBytes") == 0 && (bool)ticket.Result, "Late callback changed the outcome or double-released admission");
                return true;
            } finally { Finish(ticket, MCPRequestQueue.RequestStatus.TimedOut); }
        });
        Check("Queued timeout releases admission without executing its action", () => {
            bool ran = false; var ticket = Native(() => ran = true, 5678);
            Finish(ticket, MCPRequestQueue.RequestStatus.TimedOut); Drain();
            Require(!ran && Metric("activeCount") == 0 && Metric("argumentCostBytes") == 0, "Expired queued command retained admission or ran");
            return true;
        });
        Check("Deferred timeout releases admission and a late callback cannot release it again", () => {
            Action<object> resolve = null;
            var ticket = Deferred((complete, active) => resolve = complete, 91011); Drain();
            typeof(MCPRequestQueue.RequestTicket).GetField("StartedTimestamp", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(ticket, System.Diagnostics.Stopwatch.GetTimestamp() - 121L * System.Diagnostics.Stopwatch.Frequency);
            typeof(MCPRequestQueue).GetMethod("RunCleanup", Hidden).Invoke(null, null);
            Require(Metric("activeCount") == 0 && Metric("argumentCostBytes") == 0, "Timed-out work stayed charged");
            resolve(true);
            Require(Metric("activeCount") == 0 && Metric("argumentCostBytes") == 0 && ticket.Status == MCPRequestQueue.RequestStatus.TimedOut, "Late callback corrupted accounting");
            return true;
        });
        Check("Failed work releases the same admission as completed work", () => {
            var ticket = Native(() => true, 4321);
            Finish(ticket, MCPRequestQueue.RequestStatus.Failed); Drain();
            Require(Metric("activeCount") == 0 && Metric("argumentCostBytes") == 0, "Failed command retained admission"); return true;
        });
        Check("Polling and queue information remain usable while command admission is full", () => {
            long ticket = Submit(Envelope(), out int status); Fill(255);
            using (var info = new Exchange("queue/info", "{}")) Require(info.Status == 200 && info.Result.ContainsKey("httpCommands"), "Diagnostics required another command slot");
            using (var poll = new Exchange("queue/status?ticketId=" + ticket, "{}")) Require(poll.Status == 200 && poll.Result["status"].ToString() == "Queued", "Polling could not observe full queue");
            Require(Metric("activeCount") == 256 && Queued == 256, "Diagnostic reads changed command admission"); return true;
        });
        bool passed = Checks.All(check => (bool)check.GetType().GetProperty("passed").GetValue(check));
        File.WriteAllText("Library/UnityMcpQueueAdmissionValidation.json", MiniJson.Serialize(new { unityVersion = Application.unityVersion, passed, checks = Checks }));
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
