using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityMCP.Editor;

public static class UnityMcpValidation
{
    [Serializable]
    private class Report
    {
        public string unityVersion;
        public bool passed;
        public List<string> checks = new List<string>();
        public double polling100Ms;
        public double polling10000Ms;
        public string error;
    }

    public static void Run()
    {
        var report = new Report { unityVersion = Application.unityVersion };
        try
        {
            ValidateIdentity();
            report.checks.Add("Object identity round-trip without JSON number conversion");
            ValidateScheduling();
            report.checks.Add("Agent FIFO, round-robin writes, read batching and ticket results");
            ValidateDeferred();
            report.checks.Add("Deferred completion is atomic, single-shot and stable after timeout");
            ValidateSync();
            report.checks.Add("Legacy synchronous callers are signaled without lost wakeups");
            ValidateSyncTimeouts();
            report.checks.Add("Legacy timeout preserves running outcomes and skips expired batched and queued work");
            ValidateDashboard();
            report.checks.Add("Dashboard shows running-only work and separates failures from finished requests");
            report.polling100Ms = MeasurePolling(100);
            report.polling10000Ms = MeasurePolling(9900);
            report.checks.Add("1000 status polls at queue depths 100 and 10000");
            report.passed = true;
        }
        catch (Exception error)
        {
            report.error = error.ToString();
            UnityEngine.Debug.LogException(error);
        }
        File.WriteAllText("Library/UnityMcpValidation.json", JsonUtility.ToJson(report, true));
        EditorApplication.Exit(report.passed ? 0 : 1);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ValidateIdentity()
    {
        var go = new GameObject("__mcp_validation_identity");
        try
        {
            var helper = typeof(MCPRequestQueue).Assembly.GetType("UnityMCP.Editor.MCPObjectId", true);
            var id = (string)helper.GetMethod("Get").Invoke(null, new object[] { go });
            var resolved = helper.GetMethod("ToObject").Invoke(null, new object[] { id });
            Check(ReferenceEquals(go, resolved), "Object ID did not round-trip");
            Check(!string.IsNullOrEmpty(id), "Empty object ID");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    private static void ValidateScheduling()
    {
        var order = new List<string>();
        var tickets = new List<MCPRequestQueue.RequestTicket>();
        for (int agent = 0; agent < 3; agent++)
        {
            for (int i = 0; i < 2; i++)
            {
                string value = agent + ":" + i;
                tickets.Add(MCPRequestQueue.SubmitRequest("validation-" + agent, "validation/write", () =>
                {
                    order.Add(value);
                    return value;
                }));
            }
        }
        foreach (var ticket in tickets)
            Check((string)MCPRequestQueue.GetTicketStatus(ticket.TicketId)["status"] == "Queued", "Pending ticket missing");
        for (int i = 0; i < 6; i++) MCPRequestQueue.ProcessNextRequests();
        Check(string.Join(",", order) == "0:0,1:0,2:0,0:1,1:1,2:1", "Write scheduling changed");
        foreach (var ticket in tickets)
        {
            var status = MCPRequestQueue.GetTicketStatus(ticket.TicketId);
            Check((string)status["status"] == "Completed", "Write did not complete");
            Check((double)status["queueWaitMs"] >= 0 && (double)status["processingTimeMs"] >= 0, "Negative timing");
            Check(status.ContainsKey("executionTimeMs") && status.ContainsKey("startedAt"), "Timing compatibility lost");
        }
        int reads = 0;
        for (int i = 0; i < 6; i++) MCPRequestQueue.SubmitRequest("reads", "editor/state", () => ++reads);
        MCPRequestQueue.ProcessNextRequests();
        Check(reads == 5, "Read batch size changed");
        MCPRequestQueue.ProcessNextRequests();
        Check(reads == 6, "Remaining read was lost");
    }

    private static void ValidateDeferred()
    {
        Action<object> complete = null;
        var ticket = MCPRequestQueue.SubmitDeferredRequest("deferred", "testing/list-tests", callback => complete = callback);
        MCPRequestQueue.ProcessNextRequests();
        Check((string)MCPRequestQueue.GetTicketStatus(ticket.TicketId)["status"] == "Executing", "Deferred ticket lost");
        Check(complete != null, "Deferred callback not invoked");
        complete("done");
        var status = MCPRequestQueue.GetTicketStatus(ticket.TicketId);
        Check((string)status["result"] == "done", "Deferred result changed");
        Check((double)status["processingTimeMs"] >= 0, "Deferred timing missing");
        Parallel.For(0, 40, i => complete("duplicate"));
        Check((string)MCPRequestQueue.GetTicketStatus(ticket.TicketId)["result"] == "done", "Duplicate callback replaced the result");
        Check((int)Session("deferred")["completedRequests"] == 1, "Duplicate callbacks inflated completion count");

        Action<object> late = null;
        var expired = MCPRequestQueue.SubmitDeferredRequest("expired", "testing/list-tests", callback => late = callback);
        MCPRequestQueue.ProcessNextRequests();
        SetTimestamp(expired, "StartedTimestamp", Stopwatch.GetTimestamp() - 121L * Stopwatch.Frequency);
        Cleanup();
        Check((string)MCPRequestQueue.GetTicketStatus(expired.TicketId)?["status"] == "TimedOut", "Expired deferred ticket disappeared instead of reporting timeout");
        late("too late");
        Check((string)MCPRequestQueue.GetTicketStatus(expired.TicketId)["status"] == "TimedOut", "Late callback revived timed-out ticket");
        Check((int)Session("expired")["queuedRequests"] == 0, "Timeout left session requests outstanding");
        Check((int)Session("expired")["timedOutRequests"] == 1, "Timeout was not counted exactly once");
        Check(typeof(MCPRequestQueue.RequestTicket).GetProperty("DeferredAction", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(expired) == null,
            "Expired ticket retained its work closure");
        SetTimestamp(expired, "CompletedTimestamp", Stopwatch.GetTimestamp() - 31L * Stopwatch.Frequency);
        Cleanup();
        Check(MCPRequestQueue.GetTicketStatus(expired.TicketId) == null, "Timed-out result did not expire");
        late("after eviction");
        Check(MCPRequestQueue.GetTicketStatus(expired.TicketId) == null, "Late callback restored an evicted ticket");

        var waited = MCPRequestQueue.SubmitDeferredRequest("long-wait", "testing/list-tests", callback => late = callback);
        waited.SubmittedAt = DateTime.UtcNow.AddSeconds(-130);
        SetTimestamp(waited, "SubmittedTimestamp", Stopwatch.GetTimestamp() - 130L * Stopwatch.Frequency);
        MCPRequestQueue.ProcessNextRequests();
        Cleanup();
        Check((string)MCPRequestQueue.GetTicketStatus(waited.TicketId)?["status"] == "Executing", "Queue wait consumed the execution timeout");
        late("finished after waiting");

        var failed = MCPRequestQueue.SubmitDeferredRequest("deferred-failure", "testing/list-tests", callback =>
            { throw new InvalidOperationException("Expected validation failure"); });
        MCPRequestQueue.ProcessNextRequests();
        Check((string)MCPRequestQueue.GetTicketStatus(failed.TicketId)["status"] == "Failed", "Deferred exception was lost");
        Check((int)Session("deferred-failure")["queuedRequests"] == 0, "Deferred exception left requests outstanding");
        Check((int)Session("deferred-failure")["completedRequests"] == 1, "Deferred failure was not counted exactly once");
        Check((int)Session("deferred-failure")["failedRequests"] == 1, "Deferred failure metric missing");
    }

    private static Dictionary<string, object> Session(string agentId) =>
        MCPRequestQueue.GetActiveSessions().Find(session => (string)session["agentId"] == agentId);

    private static void SetTimestamp(MCPRequestQueue.RequestTicket ticket, string field, long timestamp) =>
        typeof(MCPRequestQueue.RequestTicket).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(ticket, timestamp);

    private static void Cleanup() => typeof(MCPRequestQueue).GetMethod("RunCleanup", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);

    private static void ValidateSync()
    {
        for (int i = 0; i < 50; i++)
        {
            var request = Task.Run(() => MCPRequestQueue.ExecuteWithTracking("sync", "editor/state", () => "sync-result"));
            var deadline = Stopwatch.StartNew();
            while (!request.IsCompleted && deadline.ElapsedMilliseconds < 5000)
            {
                MCPRequestQueue.ProcessNextRequests();
                Thread.Yield();
            }
            Check(request.IsCompleted, "Synchronous waiter missed completion");
            Check((string)request.GetAwaiter().GetResult() == "sync-result", "Sync result changed");
        }
    }

    private static double MeasurePolling(int additionalRequests)
    {
        MCPRequestQueue.RequestTicket last = null;
        for (int i = 0; i < additionalRequests; i++)
            last = MCPRequestQueue.SubmitRequest("benchmark", "editor/state", () => null);
        for (int i = 0; i < 100; i++) MCPRequestQueue.GetTicketStatus(last.TicketId);
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
            Check((string)MCPRequestQueue.GetTicketStatus(last.TicketId)["status"] == "Queued", "Queued status changed");
        return timer.Elapsed.TotalMilliseconds;
    }

    private static void ValidateSyncTimeouts()
    {
        int abandonedRuns = 0;
        using (var finishedWaiters = new CountdownEvent(3))
        {
            Func<string, Func<object>, Task<object>> submit = (path, action) => Task.Run(() =>
            {
                try { return MCPRequestQueue.ExecuteWithTracking("sync-timeout", path, action); }
                finally { finishedWaiters.Signal(); }
            });
            var running = submit("editor/state", () =>
            {
                Check(finishedWaiters.Wait(MCPRequestQueue.SyncTimeoutMs + 5000), "Waiters did not expire while action ran");
                return "late result";
            });
            WaitForQueued(1);
            var batched = submit("editor/state", () => ++abandonedRuns);
            WaitForQueued(2);
            var queued = submit("validation/write", () => ++abandonedRuns);
            WaitForQueued(3);

            MCPRequestQueue.ProcessNextRequests();
            Check(Task.WaitAll(new[] { running, batched, queued }, 5000), "Legacy tasks did not finish");
            foreach (var request in new[] { running, batched, queued })
            {
                Check(request.IsCompleted, "Legacy waiter did not return after timeout");
                var response = request.GetAwaiter().GetResult() as Dictionary<string, object>;
                Check(response != null && response.ContainsKey("error"), "Legacy timeout response changed");
                var ticket = MCPRequestQueue.GetTicketStatus((long)response["ticketId"]);
                Check((string)ticket["status"] == "TimedOut", "Running completion overwrote the timeout");
                Check(ticket.ContainsKey("startedAt") == ReferenceEquals(request, running), "Expired work started after its waiter left");
            }
            MCPRequestQueue.ProcessNextRequests();
            Check(abandonedRuns == 0, "Expired batch or queue work executed");
            Check(MCPRequestQueue.TotalQueuedCount == 0, "Expired work remained in the queue");
            Check((int)MCPRequestQueue.GetQueueInfo()["executingCount"] == 0, "Expired work remained executing");
            var session = Session("sync-timeout");
            Check((int)session["queuedRequests"] == 0 && (int)session["completedRequests"] == 3
                && (int)session["timedOutRequests"] == 3, "Legacy timeout counters are inconsistent");
            Check((double)session["averageQueueWaitMs"] >= 0 && (double)session["averageProcessingTimeMs"] >= 0,
                "Session timing aggregates missing");
        }
    }

    private static void WaitForQueued(int count)
    {
        var deadline = Stopwatch.StartNew();
        while (MCPRequestQueue.TotalQueuedCount < count && deadline.ElapsedMilliseconds < 5000) Thread.Yield();
        Check(MCPRequestQueue.TotalQueuedCount == count, "Legacy request did not enter queue");
    }

    private static void ValidateDashboard()
    {
        Action<object> complete = null;
        MCPRequestQueue.SubmitDeferredRequest("dashboard", "testing/list-tests", callback => complete = callback);
        MCPRequestQueue.ProcessNextRequests();
        var window = ScriptableObject.CreateInstance<MCPDashboardWindow>();
        try
        {
            var queueRows = new VisualElement();
            var agentRows = new VisualElement();
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            typeof(MCPDashboardWindow).GetField("_queueRows", flags).SetValue(window, queueRows);
            typeof(MCPDashboardWindow).GetField("_agentRows", flags).SetValue(window, agentRows);
            typeof(MCPDashboardWindow).GetMethod("RefreshQueue", flags).Invoke(window, null);
            string queueText = LabelText(queueRows);
            Check(queueText.Contains("1 running") && !queueText.Contains("Idle"), "Dashboard reported idle while a deferred operation was running");
            typeof(MCPDashboardWindow).GetMethod("RefreshAgents", flags).Invoke(window, null);
            string agentText = LabelText(agentRows);
            Check(agentText.Contains("1 failed") && agentText.Contains("3 timed out"), "Dashboard omitted terminal failures");
            Check(agentText.Contains("ms waiting") && agentText.Contains("ms processing"), "Dashboard omitted timing breakdown");
            complete("done");
            typeof(MCPDashboardWindow).GetMethod("RefreshQueue", flags).Invoke(window, null);
            Check(LabelText(queueRows).Contains("Idle"), "Dashboard did not refresh after execution finished");
        }
        finally { UnityEngine.Object.DestroyImmediate(window); }
    }

    private static string LabelText(VisualElement root)
    {
        var labels = new List<string>();
        root.Query<Label>().ForEach(label => labels.Add(label.text));
        return string.Join("\n", labels);
    }
}
