using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;

public static class UnityMcpResultRetentionValidation
{
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly List<object> Checks = new List<object>();
    private static readonly MethodInfo Complete = typeof(MCPRequestQueue).GetMethod("TryCompleteTicket", Hidden);
    private static readonly MethodInfo Cleanup = typeof(MCPRequestQueue).GetMethod("RunCleanup", Hidden);
    private static readonly FieldInfo Timestamp = typeof(MCPRequestQueue.RequestTicket).GetField("CompletedTimestamp", BindingFlags.Instance | BindingFlags.NonPublic);
    private static IDictionary Cached => (IDictionary)typeof(MCPRequestQueue).GetField("_completedTickets", Hidden).GetValue(null);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static MCPRequestQueue.RequestTicket Finish(object value, string route = "editor/state")
    {
        var ticket = MCPRequestQueue.SubmitRequest("result-retention-fixture", route, () => null);
        Complete.Invoke(null, new object[] { ticket, MCPRequestQueue.RequestStatus.Completed, value, null, -1, null });
        return ticket;
    }
    private static void Expire()
    {
        foreach (MCPRequestQueue.RequestTicket ticket in Cached.Values)
            Timestamp.SetValue(ticket, System.Diagnostics.Stopwatch.GetTimestamp() - 121L * System.Diagnostics.Stopwatch.Frequency);
        Cleanup.Invoke(null, null);
    }
    private static Dictionary<string, object> Metrics() => MCPRequestQueue.GetQueueInfo().TryGetValue("completedResults", out var value)
        ? (Dictionary<string, object>)value : new Dictionary<string, object>();
    private static long Metric(string name) => Metrics().TryGetValue(name, out var value) ? Convert.ToInt64(value) : -1;
    private static void Check(string name, Func<object> action)
    {
        try { Checks.Add(new { name, passed = true, evidence = action() }); }
        catch (Exception error) { Checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
        finally { Expire(); MCPActionHistory.Clear(); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompletePayload()
    {
        var payload = new byte[1024 * 1024];
        var weak = new WeakReference(payload);
        Finish(new Dictionary<string, object> { { "instanceId", "9223372036854775806" }, { "path", "Root/Target" }, { "unrelatedPayload", payload } }, "gameobject/create");
        Expire();
        return weak;
    }
    private sealed class DangerousResult
    {
        public readonly string Stored = "kept";
        public string Value => throw new InvalidOperationException("Getter was invoked");
        public override int GetHashCode() => throw new InvalidOperationException("HashCode was invoked");
        public override bool Equals(object other) => throw new InvalidOperationException("Equals was invoked");
        public override string ToString() => throw new InvalidOperationException("ToString was invoked");
    }
    private sealed class CustomComparer : IEqualityComparer<string>
    {
        internal bool Reject;
        public bool Equals(string left, string right) => left == right;
        public int GetHashCode(string value) => Reject ? throw new InvalidOperationException("Custom comparer invoked") : value.GetHashCode();
    }
    public static void Run()
    {
        Require(File.Exists(".unity-mcp-validation"), "Use a marked disposable validation project");
        Check("Ordinary terminal polling preserves the result and transport status", () => {
            var result = new Dictionary<string, object> { { "success", true }, { "value", 42 } };
            var ticket = Finish(result);
            var status = MCPRequestQueue.GetTicketStatus(ticket.TicketId);
            Require(status["status"].ToString() == "Completed" && ReferenceEquals(status["result"], result), "Result or status changed");
            return true;
        });
        Check("Completed polling cache remains bounded during a burst", () => {
            long first = 0, last = 0;
            for (int i = 0; i < 4100; i++) { last = Finish(true).TicketId; if (i == 0) first = last; }
            int count = Cached.Count;
            Require(count <= 4096, "Retained " + count + " terminal tickets; expected at most 4096");
            Require(MCPRequestQueue.GetTicketStatus(first) == null && MCPRequestQueue.GetTicketStatus(last) != null, "Oldest-first eviction changed");
            return new { retained = count, metrics = Metrics() };
        });
        Check("Weighted result pressure evicts old results before the count ceiling", () => {
            string sharedPayload = new string('x', 8 * 1024 * 1024);
            long first = 0, last = 0;
            for (int i = 0; i < 20; i++) { last = Finish(sharedPayload).TicketId; if (i == 0) first = last; }
            Require(Metric("costBytes") >= 0 && Metric("costBytes") <= 256L * 1024 * 1024, "Missing or exceeded result-cost budget");
            Require(Cached.Count < 20 && MCPRequestQueue.GetTicketStatus(first) == null && MCPRequestQueue.GetTicketStatus(last) != null, "Weighted pressure did not evict oldest results");
            return new { metrics = Metrics(), sharedPayload = true, actualHeapMeasurement = false };
        });
        Check("Pending history does not keep unrelated expired result payloads alive", () => {
            var weak = CompletePayload();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Require(!weak.IsAlive, "An expired 1 MiB payload is still rooted by pending history");
            typeof(MCPRequestQueue).GetMethod("FlushCompletedHistory", Hidden).Invoke(null, new object[] { 100 });
            var history = MCPActionHistory.GetFiltered("result-retention-fixture", null, null);
            Require(history.Count == 1 && history[0].TargetPath == "Root/Target" && history[0].TargetInstanceId == "9223372036854775806", "Target metadata was lost with payload release");
            return true;
        });
        Check("Expiry releases cache accounting and order links", () => {
            for (int i = 0; i < 10; i++) Finish(new string('a', 1024));
            Expire();
            Require(Cached.Count == 0 && Metric("costBytes") == 0, "Expiry retained count or cost");
            var order = typeof(MCPRequestQueue).GetField("_completedOrder", Hidden).GetValue(null);
            Require((int)order.GetType().GetProperty("Count").GetValue(order) == 0, "Expiry retained ordering nodes");
            return true;
        });
        Check("Protected retry cannot execute an evicted result again", () => {
            string id = Guid.NewGuid().ToString("N"); long deadline = MCPRequestQueue.SessionTimeMs + 119000;
            var first = MCPRequestQueue.SubmitOnce("result-retention-fixture", "editor/state", "{}", id, MCPRequestQueue.SessionId, deadline,
                () => MCPRequestQueue.SubmitRequest("result-retention-fixture", "editor/state", () => true));
            Complete.Invoke(null, new object[] { first.Ticket, MCPRequestQueue.RequestStatus.Completed, true, null, -1, null });
            for (int i = 0; i < 4096; i++) Finish(true);
            bool submitted = false;
            var replay = MCPRequestQueue.SubmitOnce("result-retention-fixture", "editor/state", "{}", id, MCPRequestQueue.SessionId, deadline,
                () => { submitted = true; return Finish(false); });
            Require(!submitted && replay.StatusCode == 410 && replay.Code == "result_expired", "An expired protected request was admitted again");
            return new { replay.StatusCode, replay.Code };
        });
        Check("A synchronous waiter keeps its result after polling-cache eviction", () => {
            var expected = new Dictionary<string, object> { { "value", "sync-result" } };
            var waiting = Task.Run(() => MCPRequestQueue.ExecuteWithTracking("result-retention-sync", "editor/state", () => expected));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (MCPRequestQueue.TotalQueuedCount == 0 && watch.ElapsedMilliseconds < 5000) Thread.Sleep(1);
            Require(MCPRequestQueue.TotalQueuedCount == 1, "Waiter did not submit");
            lock (typeof(MCPRequestQueue).GetField("_queueLock", Hidden).GetValue(null))
            {
                MCPRequestQueue.ProcessNextRequests();
                for (int i = 0; i < 4096; i++) Finish(true);
            }
            Require(waiting.Wait(5000) && ReferenceEquals(waiting.Result, expected), "Eviction changed the synchronous result");
            return true;
        });
        Check("Cost accounting and history never call custom getters or conversions", () => {
            var custom = new DangerousResult();
            var result = new Dictionary<string, object> { { "instanceId", custom }, { "path", custom }, { "payload", custom } };
            var ticket = Finish(result);
            Require(ReferenceEquals(MCPRequestQueue.GetTicketStatus(ticket.TicketId)["result"], result), "Custom result changed");
            typeof(MCPRequestQueue).GetMethod("FlushCompletedHistory", Hidden).Invoke(null, new object[] { 100 });
            var history = MCPActionHistory.GetFiltered("result-retention-fixture", null, null);
            Require(history.Count == 1 && history[0].TargetPath == null, "History invoked an opaque target conversion");
            return true;
        });
        Check("History snapshots bounded target text before later result mutation", () => {
            var result = new Dictionary<string, object> { { "path", new string('p', 20000) }, { "instanceId", "9223372036854775806" } };
            Finish(result, "gameobject/create"); result["path"] = "mutated later";
            typeof(MCPRequestQueue).GetMethod("FlushCompletedHistory", Hidden).Invoke(null, new object[] { 100 });
            var history = MCPActionHistory.GetFiltered("result-retention-fixture", null, null);
            Require(history.Count == 1 && history[0].TargetPath.Length == 4096 && history[0].TargetPath[0] == 'p'
                && history[0].TargetInstanceId == "9223372036854775806", "Target snapshot changed or grew without a bound");
            return true;
        });
        Check("A custom result comparer cannot interrupt terminal publication", () => {
            var comparer = new CustomComparer();
            var result = new Dictionary<string, object>(comparer) { { "path", "custom" } }; comparer.Reject = true;
            var ticket = Finish(result);
            Require(ticket.Status == MCPRequestQueue.RequestStatus.Completed && ReferenceEquals(MCPRequestQueue.GetTicketStatus(ticket.TicketId)["result"], result), "Custom comparer interrupted completion");
            typeof(MCPRequestQueue).GetMethod("FlushCompletedHistory", Hidden).Invoke(null, new object[] { 100 });
            var history = MCPActionHistory.GetFiltered("result-retention-fixture", null, null);
            Require(history.Count == 1 && history[0].TargetPath == null, "Opaque-comparer metadata should be skipped");
            return true;
        });
        Check("Cycles and deep graphs terminate accounting without invoking serialization", () => {
            var cycle = new Dictionary<string, object>(); cycle["self"] = cycle;
            var cyclic = Finish(cycle);
            Require(ReferenceEquals(MCPRequestQueue.GetTicketStatus(cyclic.TicketId)["result"], cycle), "Cycle was transformed");
            object deep = true;
            for (int i = 0; i < 100; i++) deep = new List<object> { deep };
            var ticket = Finish(deep);
            Require(Metric("costBytes") == 256L * 1024 * 1024 && Cached.Count == 1
                && ReferenceEquals(MCPRequestQueue.GetTicketStatus(ticket.TicketId)["result"], deep), "Uncertain graph accounting was not conservative");
            return Metrics();
        });
        Check("A dense 5,000-node hierarchy is measured and leaves unrelated results pollable", () => {
            var control = Finish(true);
            var roots = new List<object>();
            for (int i = 0; i < 5000; i++) roots.Add(new Dictionary<string, object> {
                { "name", "Node " + i }, { "instanceId", "GlobalObjectId_V1-2-fixture-" + i },
                { "components", new List<string> { "MeshRenderer", "MeshFilter" } },
                { "position", new Dictionary<string, object> { { "x", 1f }, { "y", 2f }, { "z", 3f } } } });
            var hierarchy = Finish(new Dictionary<string, object> { { "hierarchy", roots }, { "returnedNodes", 5000 } }, "scene/hierarchy");
            var later = Finish(true);
            Require(Metric("costBytes") < 256L * 1024 * 1024 / 4, "A 5,000-node hierarchy was charged as an uncertain full-budget result");
            Require(MCPRequestQueue.GetTicketStatus(control.TicketId) != null && MCPRequestQueue.GetTicketStatus(hierarchy.TicketId) != null
                && MCPRequestQueue.GetTicketStatus(later.TicketId) != null, "A large result or an unrelated small result was evicted");
            return Metrics();
        });
        Check("Oversized accounting does not evict an unrelated usable result", () => {
            var control = Finish(true); long before = Metric("oversizedNotCached");
            string shared = new string('s', 8 * 1024 * 1024);
            var result = Enumerable.Repeat<object>(shared, 20).ToList();
            var oversized = Finish(result);
            Require(MCPRequestQueue.GetTicketStatus(oversized.TicketId) == null && ReferenceEquals(oversized.Result, result)
                && MCPRequestQueue.GetTicketStatus(control.TicketId) != null && Metric("oversizedNotCached") == before + 1, "Oversized result damaged a usable cache entry or raw native result");
            return new { metrics = Metrics(), sharedPayload = true };
        });
        Check("Reserved collection capacity contributes to accounting", () => {
            Finish(new List<byte>(1024 * 1024));
            Require(Metric("costBytes") >= 1024 * 1024, "Empty list capacity was ignored");
            Expire(); Finish(new Dictionary<string, object>(100000));
            Require(Metric("costBytes") >= 3200000, "Empty dictionary capacity was ignored");
            return Metrics();
        });
        Check("Duplicate deferred completion cannot change result-cache accounting", () => {
            Action<object> callback = null;
            var ticket = MCPRequestQueue.SubmitDeferredRequest("result-retention-fixture", "editor/state", done => callback = done);
            MCPRequestQueue.ProcessNextRequests(); callback("original");
            long cost = Metric("costBytes"); callback(new string('x', 8 * 1024 * 1024));
            Require(Metric("costBytes") == cost && (string)ticket.Result == "original" && Cached.Count == 1, "Late completion changed cache accounting");
            return true;
        });
        bool passed = Checks.All(check => (bool)check.GetType().GetProperty("passed").GetValue(check));
        File.WriteAllText("Library/UnityMcpResultRetentionValidation.json", MiniJson.Serialize(new { unityVersion = Application.unityVersion, passed, checks = Checks }));
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
