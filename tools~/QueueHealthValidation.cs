using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;
using Unity.Profiling;

public static class UnityMcpQueueHealthValidation
{
    [Serializable]
    private class Report
    {
        public string unityVersion;
        public bool passed;
        public int idleIterations = 100000;
        public long idleAllocatedBytes;
        public long allocationControlBytes;
        public bool allocationCounterAvailable;
        public long profilerControlAllocations;
        public long profilerIdleAllocations;
        public bool idleProfilerRecorderValid;
        public double idleMilliseconds;
        public int staleSessionsBeforeCleanup;
        public int staleSessionsAfterCleanup;
        public long staleSessionsRetainedBytes;
        public long staleSessionsReleasedBytes;
        public int inactiveSessionsAfterLimit;
        public bool activeSessionRetained;
        public bool busySessionRetained;
        public bool busySessionVisible;
        public bool returningAgentStartsNewSession;
        public bool newestInactiveSessionRetained;
        public bool oldestInactiveSessionEvicted;
        public long reportedEvictions;
        public List<string> singleWriteCounts = new List<string>();
        public int compilationReadBatch;
        public int existingReadBatch;
        public string error;
    }

    private static Dictionary<string, MCPAgentSession> Sessions =>
        (Dictionary<string, MCPAgentSession>)typeof(MCPRequestQueue).GetField("_sessions", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

    public static void Run()
    {
        var report = new Report { unityVersion = Application.unityVersion };
        try
        {
            MeasureIdle(report);
            MeasureRetention(report);
            foreach (string route in new[] { "profiler/enable", "profiler/memory-snapshot", "debugger/enable", "debugger/event-details", "profiler/future-write" })
            {
                int completed = ProcessBatch(route, 2);
                report.singleWriteCounts.Add(route + ":" + completed);
            }
            report.compilationReadBatch = ProcessBatch("compilation/errors", 6);
            report.existingReadBatch = ProcessBatch("editor/state", 6);
            Check(report.singleWriteCounts.TrueForAll(value => value.EndsWith(":1", StringComparison.Ordinal)), "A state-changing or unknown route was batched as a read");
            Check(report.compilationReadBatch == 5 && report.existingReadBatch == 5, "Known reads did not use the five-request batch");
            Check(report.staleSessionsAfterCleanup == 0, "Expired sessions remain retained");
            Check(report.inactiveSessionsAfterLimit <= 256, "Inactive session retention is unbounded");
            Check(report.activeSessionRetained && report.busySessionRetained && report.busySessionVisible, "Active or queued work was evicted or hidden");
            Check(report.returningAgentStartsNewSession, "Returning agent reused expired session statistics");
            Check(report.newestInactiveSessionRetained && report.oldestInactiveSessionEvicted, "Inactive capacity did not retain the most recent sessions");
            Check(report.profilerControlAllocations > 0, "ProfilerRecorder did not capture the control allocation");
            Check(report.idleProfilerRecorderValid, "Idle allocation recorder is invalid");
            Check(report.profilerIdleAllocations == 0, "Empty queue updates allocate after warmup");
            Check(report.reportedEvictions == 5344, "Queue-info eviction count does not match cleanup");
            report.passed = true;
        }
        catch (Exception error)
        {
            report.error = error.ToString();
            UnityEngine.Debug.LogException(error);
        }
        File.WriteAllText("Library/UnityMcpQueueHealthValidation.json", JsonUtility.ToJson(report, true));
        EditorApplication.Exit(report.passed ? 0 : 1);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Cleanup() =>
        typeof(MCPRequestQueue).GetMethod("RunCleanup", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);

    private static void MeasureIdle(Report report)
    {
        long controlBefore = GC.GetAllocatedBytesForCurrentThread();
        var control = new byte[1024 * 1024];
        report.allocationControlBytes = GC.GetAllocatedBytesForCurrentThread() - controlBefore;
        GC.KeepAlive(control);
        report.allocationCounterAvailable = report.allocationControlBytes >= 1024 * 1024;
        using (var recorder = AllocationRecorder())
        {
            var profilerControl = new byte[1024 * 1024];
            recorder.Stop();
            GC.KeepAlive(profilerControl);
            report.profilerControlAllocations = recorder.Valid && recorder.Count > 0 ? recorder.GetSample(0).Count : 0;
        }
        for (int i = 0; i < 1000; i++) MCPRequestQueue.ProcessNextRequests();
        var watch = new Stopwatch();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        watch.Start();
        for (int i = 0; i < report.idleIterations; i++) MCPRequestQueue.ProcessNextRequests();
        watch.Stop();
        report.idleAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        report.idleMilliseconds = watch.Elapsed.TotalMilliseconds;
        using (var recorder = AllocationRecorder())
        {
            for (int i = 0; i < report.idleIterations; i++) MCPRequestQueue.ProcessNextRequests();
            recorder.Stop();
            report.idleProfilerRecorderValid = recorder.Valid;
            report.profilerIdleAllocations = recorder.Valid && recorder.Count > 0 ? recorder.GetSample(0).Count : 0;
        }
    }

    private static ProfilerRecorder AllocationRecorder() => ProfilerRecorder.StartNew(ProfilerCategory.Internal,
        "GC.Alloc", 1, ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.CollectOnlyOnCurrentThread);

    private static MCPAgentSession AddSession(string id, int idleSeconds)
    {
        var session = new MCPAgentSession { AgentId = id, ConnectedAt = DateTime.UtcNow.AddSeconds(-idleSeconds - 60) };
        for (int i = 0; i < 10; i++) session.LogAction("validation/retention-" + i);
        session.LastActivityAt = DateTime.UtcNow.AddSeconds(-idleSeconds);
        var timestamp = typeof(MCPAgentSession).GetField("LastActivityTimestamp", BindingFlags.Instance | BindingFlags.NonPublic);
        timestamp?.SetValue(session, Stopwatch.GetTimestamp() - idleSeconds * (long)Stopwatch.Frequency);
        Sessions.Add(id, session);
        return session;
    }

    private static int CountPrefix(string prefix)
    {
        int count = 0;
        foreach (string id in Sessions.Keys) if (id.StartsWith(prefix, StringComparison.Ordinal)) count++;
        return count;
    }

    private static void MeasureRetention(Report report)
    {
        long before = GC.GetTotalMemory(true);
        for (int i = 0; i < 5000; i++) AddSession("health-stale-" + i, 3600);
        long retained = GC.GetTotalMemory(true);
        report.staleSessionsRetainedBytes = retained - before;
        report.staleSessionsBeforeCleanup = CountPrefix("health-stale-");
        Cleanup();
        report.staleSessionsAfterCleanup = CountPrefix("health-stale-");
        report.staleSessionsReleasedBytes = retained - GC.GetTotalMemory(true);
        MCPRequestQueue.SubmitRequest("health-stale-0", "editor/state", () => true);
        MCPRequestQueue.ProcessNextRequests();
        report.returningAgentStartsNewSession = Sessions["health-stale-0"].TotalActions == 1;
        for (int i = 0; i < 5000; i++) Sessions.Remove("health-stale-" + i);

        for (int i = 0; i < 600; i++) AddSession("health-idle-" + i, 301 + i);
        AddSession("health-active", 0);
        var busy = AddSession("health-busy", 3600);
        busy.IncrementQueuedRequest();
        Cleanup();
        report.inactiveSessionsAfterLimit = CountPrefix("health-idle-");
        report.newestInactiveSessionRetained = Sessions.ContainsKey("health-idle-0");
        report.oldestInactiveSessionEvicted = !Sessions.ContainsKey("health-idle-599");
        report.activeSessionRetained = Sessions.ContainsKey("health-active");
        report.busySessionRetained = Sessions.ContainsKey("health-busy");
        report.busySessionVisible = MCPRequestQueue.GetActiveSessions().Exists(value => (string)value["agentId"] == "health-busy");
        report.reportedEvictions = Convert.ToInt64(MCPRequestQueue.GetQueueInfo()["evictedSessions"]);
        for (int i = 0; i < 600; i++) Sessions.Remove("health-idle-" + i);
        Sessions.Remove("health-active");
        Sessions.Remove("health-busy");
    }

    private static int ProcessBatch(string route, int count)
    {
        int executed = 0;
        for (int i = 0; i < count; i++) MCPRequestQueue.SubmitRequest("health-batch", route, () => ++executed);
        MCPRequestQueue.ProcessNextRequests();
        int firstUpdate = executed;
        while (MCPRequestQueue.TotalQueuedCount > 0) MCPRequestQueue.ProcessNextRequests();
        Check(executed == count, "A scheduled operation was lost");
        return firstUpdate;
    }
}
