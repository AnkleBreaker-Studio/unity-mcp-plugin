using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
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
            report.checks.Add("Deferred ticket remains queryable until its callback completes");
            ValidateSync();
            report.checks.Add("Legacy synchronous callers are signaled without lost wakeups");
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
    }

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
}
