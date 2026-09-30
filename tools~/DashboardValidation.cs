using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityMCP.Editor;

public static class UnityMcpDashboardValidation
{
    private const BindingFlags HiddenInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run()
    {
        var report = new Dictionary<string, object> { { "unityVersion", Application.unityVersion } };
        var failures = new List<string>();
        var ids = new List<string>();
        var savedFoldouts = new Dictionary<string, bool?>();
        MCPDashboardWindow window = null;
        var sessions = (Dictionary<string, MCPAgentSession>)typeof(MCPRequestQueue).GetField("_sessions", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        try
        {
            if (!File.Exists(".unity-mcp-validation")) throw new Exception("Unmarked validation project");
            window = ScriptableObject.CreateInstance<MCPDashboardWindow>();
            window.CreateGUI();
            var refresh = (Action)Delegate.CreateDelegate(typeof(Action), window, typeof(MCPDashboardWindow).GetMethod("RefreshAll", HiddenInstance));
            refresh();
            report["idle"] = Measure(refresh);
            for (int i = 0; i < 20; i++)
            {
                string id = "__McpDashboard" + Guid.NewGuid().ToString("N");
                ids.Add(id);
                var session = new MCPAgentSession { AgentId = id, ConnectedAt = DateTime.UtcNow };
                session.LogAction("validation/last-submitted-request");
                sessions.Add(id, session);
            }
            refresh();
            report["twentyStableAgents"] = Measure(refresh);
            var rows = (VisualElement)typeof(MCPDashboardWindow).GetField("_agentRows", HiddenInstance).GetValue(window);
            var first = rows[0];
            sessions[ids[0]].LastActivityAt = DateTime.UtcNow.AddSeconds(1);
            refresh();
            Check(ReferenceEquals(first, rows[0]), "unshownActivityDoesNotRebuild", report, failures);
            sessions[ids[0]].IncrementQueuedRequest();
            refresh();
            Check(rows.Query<Label>().ToList().Any(label => label.text.Contains("1 outstanding")), "visibleMetricsRefresh", report, failures);
            var unchanged = rows[1];
            report["changingAgents"] = Measure(() => { sessions[ids[0]].IncrementCompletedRequest(2); refresh(); });
            Check(ReferenceEquals(first, rows[0]) && ReferenceEquals(unchanged, rows[1]), "changedAndUnchangedCardsReused", report, failures);
            var directRefresh = (Action)Delegate.CreateDelegate(typeof(Action), window, typeof(MCPDashboardWindow).GetMethod("RefreshAgents", HiddenInstance));
            var timestamp = typeof(MCPAgentSession).GetField("LastActivityTimestamp", HiddenInstance);
            sessions[ids[0]].IncrementQueuedRequest();
            timestamp.SetValue(sessions[ids[0]], Stopwatch.GetTimestamp() - 301L * Stopwatch.Frequency);
            directRefresh();
            Check(ReferenceEquals(first, rows[0]), "oldOutstandingSessionVisible", report, failures);
            sessions[ids[0]].IncrementCompletedRequest(0);
            directRefresh();
            Check(first.parent == null && rows.childCount == 19 && ReferenceEquals(unchanged, rows[0]), "idleSessionExpiresWithoutRebuildingOthers", report, failures);
            sessions[ids[0]].LogAction("validation/returned-request");
            directRefresh();
            Check(rows.childCount == 20 && rows.Query<Label>().ToList().Any(label => label.text == "Latest request: validation/returned-request"), "returningSessionVisible", report, failures);
            foreach (string id in ids) sessions.Remove(id);
            directRefresh();
            Check(rows.childCount == 1 && rows[0] is Label empty && empty.text == "No active agent sessions.", "emptyStateRestored", report, failures);
            sessions.Add(ids[0], new MCPAgentSession { AgentId = ids[0] });
            directRefresh();
            Check(rows.childCount == 1 && rows[0].ClassListContains("ab-dash__agent"), "newAgentReplacesEmptyState", report, failures);
            int rootsBefore = window.rootVisualElement.Query<ScrollView>().ToList().Count;
            foreach (var foldout in window.rootVisualElement.Query<Foldout>().ToList())
            {
                string key = "UnityMCP_Dashboard_" + Application.dataPath + "_" + foldout.text;
                savedFoldouts[key] = EditorPrefs.HasKey(key) ? (bool?)EditorPrefs.GetBool(key) : null;
            }
            var queueFoldout = window.rootVisualElement.Query<Foldout>().ToList().Single(f => f.text == "Request Queue");
            bool desiredQueueState = !queueFoldout.value;
            EditorPrefs.SetBool("UnityMCP_Dashboard_" + Application.dataPath + "_Request Queue", desiredQueueState);
            window.CreateGUI();
            refresh();
            int rootsAfter = window.rootVisualElement.Query<ScrollView>().ToList().Count;
            report["scrollRootsBefore"] = rootsBefore;
            report["scrollRootsAfter"] = rootsAfter;
            Check(rootsAfter == rootsBefore, "recreateGuiDoesNotDuplicateControls", report, failures);
            Check(window.rootVisualElement.Query<Foldout>().ToList().Single(f => f.text == "Request Queue").value == desiredQueueState,
                "savedFoldoutStateRestored", report, failures);
            report["failures"] = failures;
            report["passed"] = failures.Count == 0;
        }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); }
        finally
        {
            if (window != null) UnityEngine.Object.DestroyImmediate(window);
            foreach (string id in ids) sessions.Remove(id);
            foreach (var saved in savedFoldouts)
            {
                if (saved.Value.HasValue) EditorPrefs.SetBool(saved.Key, saved.Value.Value);
                else EditorPrefs.DeleteKey(saved.Key);
            }
            report["fixtureSessionsRemoved"] = ids.All(id => !sessions.ContainsKey(id));
        }
        File.WriteAllText("Library/UnityMcpDashboardValidation.json", MiniJson.Serialize(report));
        EditorApplication.Exit((bool)report["passed"] ? 0 : 1);
    }

    private static object Measure(Action refresh)
    {
        for (int i = 0; i < 5; i++) refresh();
        long control;
        using (var recorder = Recorder())
        {
            var bytes = new byte[1024 * 1024];
            recorder.Stop();
            GC.KeepAlive(bytes);
            control = recorder.Valid && recorder.Count > 0 ? recorder.GetSample(0).Count : 0;
        }
        if (control == 0) throw new Exception("Allocation recorder failed its positive control");
        var watch = Stopwatch.StartNew();
        long allocations;
        long allocatedBytes;
        using (var recorder = Recorder())
        {
            for (int i = 0; i < 100; i++) refresh();
            recorder.Stop();
            watch.Stop();
            allocations = recorder.Valid && recorder.Count > 0 ? recorder.GetSample(0).Count : 0;
            allocatedBytes = recorder.Valid && recorder.Count > 0 ? recorder.GetSample(0).Value : 0;
        }
        return new { iterations = 100, elapsedMs = watch.Elapsed.TotalMilliseconds, allocations, allocatedBytes, profilerControlAllocations = control };
    }

    private static ProfilerRecorder Recorder() => ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1,
        ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.CollectOnlyOnCurrentThread);

    private static void Check(bool passed, string name, Dictionary<string, object> report, List<string> failures)
    {
        report[name] = passed;
        if (!passed) failures.Add(name);
    }
}
