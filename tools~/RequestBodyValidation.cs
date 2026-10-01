using System;
using System.Collections.Generic;
using System.Diagnostics;
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

public static class UnityMcpRequestBodyValidation
{
    private static readonly List<object> Checks = new List<object>();
    private static readonly Action<HttpListenerContext> Handle = (Action<HttpListenerContext>)Delegate.CreateDelegate(typeof(Action<HttpListenerContext>),
        typeof(MCPBridgeServer).GetMethod("HandleRequest", BindingFlags.Static | BindingFlags.NonPublic));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Check(string name, Func<object> action)
    {
        try { Checks.Add(new { name, passed = true, evidence = action() }); }
        catch (Exception error) { Checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
    }
    private static Dictionary<string, object> Metrics() => (Dictionary<string, object>)MCPRequestQueue.GetQueueInfo()["http"];
    private static long Count(string key) => Metrics().TryGetValue(key, out var value) ? Convert.ToInt64(value) : -1;
    private static int Tickets()
    {
        var info = MCPRequestQueue.GetQueueInfo();
        return Convert.ToInt32(info["totalQueued"]) + Convert.ToInt32(info["executingCount"]) + Convert.ToInt32(info["completedCacheSize"]);
    }

    private sealed class Exchange : IDisposable
    {
        internal readonly HttpListener Listener = new HttpListener();
        internal readonly TcpClient Client = new TcpClient();
        internal Thread Worker;
        internal Exception Failure;
        internal readonly Stopwatch Elapsed = Stopwatch.StartNew();
        internal Exchange(string body, long? declared = null, bool chunked = false, string route = "queue/info", string origin = null)
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
            int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
            Listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); Listener.Start();
            var pending = Listener.BeginGetContext(null, null);
            Client.Connect(IPAddress.Loopback, port); Client.ReceiveTimeout = 3000; Client.SendTimeout = 3000;
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            string headers = "POST /api/" + route + " HTTP/1.1\r\nHost: 127.0.0.1:" + port
                + "\r\nContent-Type: application/json; charset=utf-8\r\nConnection: keep-alive\r\n"
                + (origin == null ? "" : "Origin: " + origin + "\r\n")
                + (chunked ? "Transfer-Encoding: chunked\r\n" : "Content-Length: " + (declared ?? bytes.Length) + "\r\n") + "\r\n";
            Write(headers);
            if (chunked) Write(bytes.Length.ToString("x") + "\r\n" + body + "\r\n" + (declared.HasValue ? "" : "0\r\n\r\n"));
            else Write(body);
            Require(pending.AsyncWaitHandle.WaitOne(3000), "Owned listener did not accept headers");
            var context = Listener.EndGetContext(pending);
            Worker = new Thread(() => { try { Handle(context); } catch (Exception error) { Failure = error; } }) { IsBackground = true };
            Worker.Start();
        }
        internal void Write(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text); Client.GetStream().Write(bytes, 0, bytes.Length);
        }
        internal int Status()
        {
            var line = new StringBuilder(); var stream = Client.GetStream();
            while (line.Length < 1024)
            {
                int b = stream.ReadByte(); Require(b >= 0, "Response closed without status");
                if (b == '\n') return int.Parse(line.ToString().Split(' ')[1]);
                line.Append((char)b);
            }
            throw new InvalidOperationException("Unbounded status line");
        }
        public void Dispose()
        {
            Client.Close(); Listener.Close();
            if (Worker != null && !Worker.Join(5000)) { Worker.Abort(); Worker.Join(1000); }
        }
    }

    public static void Run()
    {
        Require(File.Exists(".unity-mcp-validation"), "Use a marked disposable project");
        Check("Complete UTF-8 content-length requests remain accepted", () => {
            using (var request = new Exchange("{\"text\":\"\u4e2d\ud83d\ude00\"}")) {
                Require(request.Status() == 200 && request.Worker.Join(3000) && request.Failure == null, "Valid body failed"); return true;
            }
        });
        Check("Complete chunked requests remain accepted", () => {
            using (var request = new Exchange("{}", chunked: true)) {
                Require(request.Status() == 200 && request.Worker.Join(3000), "Valid chunked body failed"); return true;
            }
        });
        Check("Fragmented final chunk and trailers remain valid", () => {
            using (var request = new Exchange("{}", 1, chunked: true)) {
                foreach (char value in "0\r\nX-Fixture: yes\r\n\r\n") { request.Write(value.ToString()); Thread.Sleep(5); }
                Require(request.Status() == 200 && request.Worker.Join(3000), "Split chunk trailer was rejected"); return true;
            }
        });
        Check("Transport-truncated valid JSON cannot create a ticket", () => {
            int before = Tickets();
            string body = "{\"apiPath\":\"ping\"}";
            using (var request = new Exchange(body, Encoding.UTF8.GetByteCount(body) + 10, route: "queue/submit")) {
                request.Client.Client.Shutdown(SocketShutdown.Send);
                int status = request.Status(); Require(request.Worker.Join(3000), "Worker retained after EOF");
                Require(status == 400 && Tickets() == before, "Truncated entity was accepted with HTTP " + status); return new { status, ticketsBefore = before, ticketsAfter = Tickets() };
            }
        });
        MCPRequestQueue.ProcessNextRequests();
        Check("A truncated chunked entity cannot create a ticket", () => {
            int before = Tickets();
            using (var request = new Exchange("{\"apiPath\":\"ping\"}", 1, chunked: true, route: "queue/submit")) {
                request.Client.Client.Shutdown(SocketShutdown.Send);
                int status = request.Status(); Require(request.Worker.Join(3000), "Chunked reader retained after EOF");
                Require(status == 400 && Tickets() == before, "Incomplete chunk framing was accepted with HTTP " + status); return new { status, ticketsBefore = before, ticketsAfter = Tickets() };
            }
        });
        MCPRequestQueue.ProcessNextRequests();
        Check("Body reader concurrency is bounded and bodyless monitoring remains responsive", () => {
            var requests = new List<Exchange>();
            try {
                for (int i = 0; i < 8; i++) requests.Add(new Exchange("{", 1024 * 1024));
                var watch = Stopwatch.StartNew();
                while (Count("activeBodyReaders") < 8 && watch.ElapsedMilliseconds < 2000) Thread.Sleep(5);
                requests.Add(new Exchange("{", 1024 * 1024));
                Require(requests[8].Worker.Join(2000), "Ninth incomplete request was admitted instead of refused");
                Require(requests[8].Status() == 503, "Reader admission did not return HTTP 503");
                Require(Count("activeBodyReaders") == 8, "Body reader gauge does not match eight held readers");
                using (var monitor = new Exchange("")) {
                    Require(monitor.Worker.Join(2000) && monitor.Status() == 200, "Monitoring blocked behind uploads");
                    return new { activeBodyReaders = Count("activeBodyReaders"), monitoringMs = monitor.Elapsed.Elapsed.TotalMilliseconds };
                }
            } finally { foreach (var request in requests) request.Dispose(); }
        });
        Check("Declared and chunked bodies share a 64 MiB reservation budget", () => {
            var requests = new List<Exchange>();
            try {
                requests.Add(new Exchange("{", 32L * 1024 * 1024));
                requests.Add(new Exchange("{", 1, chunked: true));
                var watch = Stopwatch.StartNew();
                while (Count("activeBodyReaders") < 2 && watch.ElapsedMilliseconds < 2000) Thread.Sleep(5);
                requests.Add(new Exchange("{", 1, chunked: true));
                Require(requests[2].Worker.Join(2000), "Third large reservation was admitted");
                Require(requests[2].Status() == 503, "Byte admission did not return HTTP 503");
                Require(Count("reservedBodyBytes") == 64L * 1024 * 1024, "Reservation gauge mismatch");
                return new { activeBodyReaders = Count("activeBodyReaders"), reservedBodyBytes = Count("reservedBodyBytes") };
            } finally { foreach (var request in requests) request.Dispose(); }
        });
        Check("Stalled and trickling chunked bodies share an absolute 30-second deadline", () => {
            using (var request = new Exchange("{", 1024))
            using (var trickle = new Exchange("{", 1, chunked: true))
            using (var stop = new ManualResetEvent(false)) {
                var upload = new Thread(() => {
                    try { while (!stop.WaitOne(100)) trickle.Write("1\r\n \r\n"); } catch (IOException) { } catch (ObjectDisposedException) { }
                }) { IsBackground = true };
                upload.Start();
                try {
                    Require(request.Worker.Join(34000), "Body reader remains blocked after its 30-second deadline");
                    Require(trickle.Worker.Join(2000), "Trickling input reset the absolute deadline");
                    int status = request.Status(), trickleStatus = trickle.Status();
                    Require(status == 408 && trickleStatus == 408, "Deadline responses were " + status + "/" + trickleStatus);
                    Require(request.Failure == null && trickle.Failure == null, "Deadline escaped request handling");
                    return new { status, trickleStatus, elapsedMs = request.Elapsed.Elapsed.TotalMilliseconds, timeoutCount = Count("bodyReadTimeouts") };
                } finally { stop.Set(); Require(upload.Join(3000), "Trickle upload did not stop"); }
            }
        });
        Check("Unread origin-refused bodies are not drained", () => {
            using (var request = new Exchange("{", 1024, origin: "https://invalid.example")) {
                Require(request.Worker.Join(500), "Origin refusal retained a worker to drain the upload");
                Require(request.Status() == 403, "Origin was not refused"); return true;
            }
        });
        Check("All body reservations and readers are released", () => {
            Require(Count("activeBodyReaders") >= 0 && Count("reservedBodyBytes") >= 0, "Body admission gauges are missing");
            Require(Count("activeBodyReaders") == 0 && Count("reservedBodyBytes") == 0, "Body admission state leaked");
            return Metrics();
        });
        Check("Body read abort releases admission state", () => {
            using (var request = new Exchange("{", 1024)) {
                var watch = Stopwatch.StartNew();
                while (Count("activeBodyReaders") == 0 && watch.ElapsedMilliseconds < 2000) Thread.Sleep(5);
                Require(Count("activeBodyReaders") == 1, "No admitted read to abort");
                request.Worker.Abort(); Require(request.Worker.Join(2000), "Aborted read retained its worker");
                Require(Count("activeBodyReaders") == 0 && Count("reservedBodyBytes") == 0, "Aborted read retained admission"); return true;
            }
        });
        Check("Buffered reader cost comparison preserves decoded content", () => {
            long controlBefore = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(new byte[8192]);
            long controlBytes = GC.GetAllocatedBytesForCurrentThread() - controlBefore;
            var oldRead = (Func<Stream, Encoding, long, string>)Delegate.CreateDelegate(typeof(Func<Stream, Encoding, long, string>), typeof(MCPBridgeServer).GetMethod("ReadRequestBody", BindingFlags.Static | BindingFlags.NonPublic));
            var inputType = typeof(MCPBridgeServer).Assembly.GetType("UnityMCP.Editor.MCPRequestInput");
            var newRead = (Func<Stream, Encoding, long, string>)Delegate.CreateDelegate(typeof(Func<Stream, Encoding, long, string>), inputType.GetMethod("ReadHttpBody", BindingFlags.Static | BindingFlags.NonPublic));
            var measurements = new List<object>();
            foreach (int length in new[] { 1024, 1024 * 1024 }) {
                string expected = new string('x', length); byte[] bytes = Encoding.UTF8.GetBytes(expected);
                foreach (bool updated in new[] { false, true }) {
                    var read = updated ? newRead : oldRead;
                    for (int i = 0; i < 3; i++) using (var stream = new MemoryStream(bytes)) read(stream, Encoding.UTF8, updated ? bytes.Length : 32L * 1024 * 1024);
                    long before = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
                    for (int i = 0; i < 20; i++) using (var stream = new MemoryStream(bytes))
                        Require(read(stream, Encoding.UTF8, updated ? bytes.Length : 32L * 1024 * 1024) == expected, "Decoded content changed");
                    measurements.Add(new { bytes = bytes.Length, updated, iterations = 20, elapsedMs = watch.Elapsed.TotalMilliseconds, callerThreadBytes = GC.GetAllocatedBytesForCurrentThread() - before });
                }
            }
            return new { scope = "MemoryStream only; excludes HTTP and callback-thread allocations", allocationCounterSupported = controlBytes >= 8192, controlBytes, measurements };
        });
        bool passed = Checks.All(check => (bool)check.GetType().GetProperty("passed").GetValue(check));
        File.WriteAllText("Library/UnityMcpRequestBodyValidation.json", MiniJson.Serialize(new { unityVersion = Application.unityVersion, passed, checks = Checks }));
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
