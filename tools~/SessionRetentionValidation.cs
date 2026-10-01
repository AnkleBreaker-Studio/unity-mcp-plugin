using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;

public static class UnityMcpSessionRetentionValidation
{
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private const string Prefix = "session-retention-fixture-";
    private static readonly MethodInfo Complete = typeof(MCPRequestQueue).GetMethod("TryCompleteTicket", Hidden);
    private static readonly MethodInfo Cleanup = typeof(MCPRequestQueue).GetMethod("RunCleanup", Hidden);
    private static readonly MethodInfo Flush = typeof(MCPRequestQueue).GetMethod("FlushCompletedHistory", Hidden);
    private static readonly FieldInfo Activity = typeof(MCPAgentSession).GetField("LastActivityTimestamp", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly List<MCPRequestQueue.RequestTicket> Owned = new List<MCPRequestQueue.RequestTicket>();
    private static Dictionary<string, MCPAgentSession> Sessions => (Dictionary<string, MCPAgentSession>)typeof(MCPRequestQueue).GetField("_sessions", Hidden).GetValue(null);
    private static List<MCPActionRecord> History => (List<MCPActionRecord>)typeof(MCPActionHistory).GetField("_history", Hidden).GetValue(null);
    private static string Id(string name) => Prefix + name;
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static long Metric(string name) => MCPRequestQueue.GetQueueInfo().TryGetValue(name, out var value) ? Convert.ToInt64(value) : -1;
    private static MCPRequestQueue.RequestTicket Submit(string name)
    {
        var ticket = MCPRequestQueue.SubmitRequest(Id(name), "editor/state", () => true);
        Owned.Add(ticket); return ticket;
    }
    private static bool Finish(MCPRequestQueue.RequestTicket ticket, MCPRequestQueue.RequestStatus status = MCPRequestQueue.RequestStatus.Completed) =>
        (bool)Complete.Invoke(null, new object[] { ticket, status, true, null, -1, null });
    private static void Burst(int count, string prefix = "burst-") { for (int i = 0; i < count; i++) Finish(Submit(prefix + i)); }
    private static void Reset()
    {
        long old = System.Diagnostics.Stopwatch.GetTimestamp() - 3601L * System.Diagnostics.Stopwatch.Frequency;
        var completedAt = typeof(MCPRequestQueue.RequestTicket).GetField("CompletedTimestamp", BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (var ticket in Owned) { Finish(ticket, MCPRequestQueue.RequestStatus.TimedOut); completedAt.SetValue(ticket, old); }
        foreach (var session in Sessions.Values) if (session.AgentId.StartsWith(Prefix, StringComparison.Ordinal)) Activity.SetValue(session, old);
        Cleanup.Invoke(null, null); Flush.Invoke(null, new object[] { 10000 });
        History.RemoveAll(record => record.AgentId.StartsWith(Prefix, StringComparison.Ordinal)); Owned.Clear();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference EvictOriginal()
    {
        Finish(Submit("returning")); var weak = new WeakReference(Sessions[Id("returning")]);
        Burst(1024); return weak;
    }
    public static object Validate()
    {
        Require(File.Exists(".unity-mcp-validation"), "Use a marked disposable validation project");
        Require(!EditorApplication.isPlayingOrWillChangePlaymode && !UnityEngine.SceneManagement.SceneManager.GetActiveScene().isDirty, "Validation editor must be clean and outside Play Mode");
        int oldLimit = MCPSettingsManager.ActionHistoryMaxEntries;
        bool oldPersistence = MCPSettingsManager.ActionHistoryPersistence;
        Flush.Invoke(null, new object[] { 10000 });
        var originalHistory = History.ToArray();
        MCPSettingsManager.ActionHistoryPersistence = false; MCPSettingsManager.ActionHistoryMaxEntries = 10000;
        var checks = new List<object>(); int failures = 0;
        Action<string, Func<object>> check = (name, action) => {
            try { checks.Add(new { name, passed = true, evidence = action() }); }
            catch (Exception error) { failures++; checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
            finally { Reset(); }
        };
        try
        {
            check("Recent completed identities are bounded before periodic cleanup", () => {
                long evictions = Metric("pressureEvictedSessions"); Burst(5000);
                int retained = Sessions.Keys.Count(id => id.StartsWith(Prefix, StringComparison.Ordinal));
                Require(retained == 1024, "Retained " + retained + " recent completed sessions; expected 1024");
                Require(!Sessions.ContainsKey(Id("burst-0")) && Sessions.ContainsKey(Id("burst-4999")), "Oldest completion was not evicted first");
                Require(Metric("maxIdleSessions") == 1024 && Metric("idleSessionsTracked") <= 1024, "Idle retention metrics disagree");
                Require(Metric("pressureEvictedSessions") - evictions >= 3976, "Pressure evictions were not counted");
                return new { submitted = 5000, retained, pressureEvictions = Metric("pressureEvictedSessions") - evictions };
            });
            check("Queued and deferred work stays visible under recent identity pressure", () => {
                Action<object> resolve = null;
                var deferred = MCPRequestQueue.SubmitDeferredRequest(Id("deferred"), "validation/deferred", callback => resolve = callback);
                Owned.Add(deferred);
                MCPRequestQueue.ProcessNextRequests();
                Require(resolve != null && deferred.Status == MCPRequestQueue.RequestStatus.Executing, "Deferred request did not start");
                var first = Submit("busy"); var second = Submit("busy");
                Activity.SetValue(Sessions[Id("busy")], System.Diagnostics.Stopwatch.GetTimestamp() - 3601L * System.Diagnostics.Stopwatch.Frequency);
                Burst(1400);
                Require(Sessions[Id("busy")].QueuedRequests == 2 && Sessions.ContainsKey(Id("deferred")), "Outstanding sessions were evicted");
                Require(MCPRequestQueue.GetActiveSessions().Exists(s => (string)s["agentId"] == Id("busy")), "Old outstanding session disappeared from monitoring");
                Finish(first); Burst(1100, "second-wave-");
                Require(Sessions[Id("busy")].QueuedRequests == 1 && Sessions[Id("busy")].CompletedRequests == 1, "Partial completion lost session counters");
                Finish(second); resolve(true);
                Require(Sessions[Id("busy")].CompletedRequests == 2, "Final completion reset counters");
                Require(Metric("idleSessionsTracked") <= 1024, "Completing busy agents exceeded idle capacity");
                return new { queuedProtected = true, partialCompletionProtected = true, deferredProtected = true };
            });
            check("A reused session moves behind older completed identities", () => {
                Burst(1024); Finish(Submit("burst-0")); Finish(Submit("new"));
                Require(Sessions.ContainsKey(Id("burst-0")) && !Sessions.ContainsKey(Id("burst-1")), "Reused session did not refresh eviction order");
                Require(Sessions[Id("burst-0")].CompletedRequests == 2, "Reused session reset counters"); return true;
            });
            check("Native callers may keep more than 1024 busy agents without eviction", () => {
                var pending = new List<MCPRequestQueue.RequestTicket>();
                for (int i = 0; i < 1300; i++) pending.Add(Submit("busy-native-" + i));
                Burst(1200);
                Require(pending.All(ticket => Sessions.ContainsKey(ticket.AgentId) && Sessions[ticket.AgentId].QueuedRequests == 1), "Busy native agent was evicted");
                foreach (var ticket in pending) Finish(ticket);
                Require(Metric("idleSessionsTracked") <= 1024, "Native completion did not return to the idle ceiling");
                return new { busyAgents = 1300, allPreserved = true, afterCompletionIdle = Metric("idleSessionsTracked") };
            });
            check("Expired generations do not attach delayed history to a returning identity", () => {
                Finish(Submit("expired"));
                Activity.SetValue(Sessions[Id("expired")], System.Diagnostics.Stopwatch.GetTimestamp() - 3601L * System.Diagnostics.Stopwatch.Frequency);
                Cleanup.Invoke(null, null); Require(!Sessions.ContainsKey(Id("expired")), "Expired session was retained");
                Finish(Submit("expired")); Flush.Invoke(null, new object[] { 10000 });
                Require(Sessions[Id("expired")].GetStructuredLog().Count == 1, "Expired generation contaminated replacement history");
                Require(History.Count(record => record.AgentId == Id("expired")) == 2, "Global history lost an expired generation"); return true;
            });
            check("Eviction releases the old session while pending history survives", () => {
                var weak = EvictOriginal(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Require(!weak.IsAlive, "Evicted session remains rooted before history drain");
                Finish(Submit("returning")); Flush.Invoke(null, new object[] { 10000 });
                var current = Sessions[Id("returning")];
                Require(current.TotalActions == 1 && current.CompletedRequests == 1, "Returning identity reused evicted counters");
                Require(current.GetStructuredLog().Count == 1, "Old pending record was attached to the replacement session");
                Require(History.Count(record => record.AgentId == Id("returning")) == 2, "Eviction removed global history");
                return new { oldSessionCollected = true, replacementRecords = current.GetStructuredLog().Count, globalRecords = 2 };
            });
            check("Session eviction preserves ticket results and protected replay", () => {
                string key = Guid.NewGuid().ToString("N"); long expires = MCPRequestQueue.SessionTimeMs + 119000;
                var accepted = MCPRequestQueue.SubmitOnce(Id("protected"), "editor/state", "{}", key, MCPRequestQueue.SessionId, expires, () => Submit("protected"));
                Finish(accepted.Ticket); Burst(1024);
                Require(!Sessions.ContainsKey(Id("protected")), "Protected session was not evicted");
                Require((bool)MCPRequestQueue.GetTicketStatus(accepted.Ticket.TicketId)["result"], "Session eviction discarded terminal result");
                var replay = MCPRequestQueue.SubmitOnce(Id("protected"), "editor/state", "{}", key, MCPRequestQueue.SessionId, expires, () => throw new Exception("Protected request executed twice"));
                Require(ReferenceEquals(accepted.Ticket, replay.Ticket), "Protected replay lost its original ticket"); return true;
            });
            check("Concurrent duplicate terminal callbacks enter idle retention once", () => {
                var ticket = Submit("duplicate"); int won = 0;
                Parallel.For(0, 20, i => { if (Finish(ticket)) System.Threading.Interlocked.Increment(ref won); });
                Require(won == 1 && Sessions[Id("duplicate")].CompletedRequests == 1, "Duplicate completion changed counters");
                Flush.Invoke(null, new object[] { 10000 }); Require(Sessions[Id("duplicate")].GetStructuredLog().Count == 1, "Duplicate history recorded");
                return new { callbacks = 20, accepted = won };
            });
            check("Timeout completion releases idle retention protection", () => {
                var ticket = Submit("timeout"); Finish(ticket, MCPRequestQueue.RequestStatus.TimedOut);
                Require(Convert.ToInt32(Sessions[Id("timeout")].ToDict()["timedOutRequests"]) == 1, "Timeout outcome lost");
                Burst(1024); Require(!Sessions.ContainsKey(Id("timeout")), "Timed-out session remained protected"); return true;
            });
        }
        finally
        {
            Reset(); History.Clear(); History.AddRange(originalHistory);
            MCPSettingsManager.ActionHistoryMaxEntries = oldLimit; MCPSettingsManager.ActionHistoryPersistence = oldPersistence;
        }
        return new { unityVersion = Application.unityVersion, passed = failures == 0, checks };
    }
    public static void Run()
    {
        var report = Validate(); string json = MiniJson.Serialize(report);
        File.WriteAllText("Library/UnityMcpSessionRetentionValidation.json", json);
        EditorApplication.Exit((bool)report.GetType().GetProperty("passed").GetValue(report) ? 0 : 1);
    }
}
