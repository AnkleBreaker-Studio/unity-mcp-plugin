using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEditor;
using Unity.Profiling;
using UnityEngine;
using UnityMCP.Editor;

public static class UnityMcpHistoryNotificationsValidation
{
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags InstanceHidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Checks = new List<object>();
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static bool Owned(Delegate callback) => callback.Method.DeclaringType == typeof(MCPActionHistory)
        || callback.Method.DeclaringType?.DeclaringType == typeof(MCPActionHistory);
    private static Delegate[] Delayed() => (EditorApplication.delayCall?.GetInvocationList() ?? Array.Empty<Delegate>()).Where(Owned).ToArray();
    private static int Scheduled() => Delayed().Length + (EditorApplication.update?.GetInvocationList() ?? Array.Empty<Delegate>()).Count(Owned);
    private static void Drain()
    {
        var dispatch = typeof(MCPActionHistory).GetMethod("DispatchNotifications", Hidden);
        for (int i = 0; i < 120 && Scheduled() > 0; i++)
        {
            if (dispatch != null) dispatch.Invoke(null, null);
            else foreach (var callback in Delayed()) { EditorApplication.delayCall -= (EditorApplication.CallbackFunction)callback; callback.DynamicInvoke(); }
        }
        Require(Scheduled() == 0, "Notification callback remained scheduled");
    }
    private static void Clear()
    {
        MCPActionHistory.Clear();
        foreach (var callback in Delayed()) EditorApplication.delayCall -= (EditorApplication.CallbackFunction)callback;
    }
    private static MCPActionRecord Record(string name = "editor/state", string agent = "history-fixture")
    {
        var record = new MCPActionRecord { AgentId = agent, ActionName = name, Category = MCPActionRecord.ExtractCategory(name), Status = "Completed", Timestamp = DateTime.UtcNow };
        MCPActionHistory.RecordAction(record); return record;
    }
    private static long Metric(string key)
    {
        var info = MCPRequestQueue.GetQueueInfo();
        if (!info.TryGetValue("historyNotifications", out var value)) return -1;
        return Convert.ToInt64(((Dictionary<string, object>)value)[key]);
    }
    private static void Check(string name, Func<object> test)
    {
        try { Checks.Add(new { name, passed = true, evidence = test() }); }
        catch (Exception error) { Checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
        finally { Clear(); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LargeRecord()
    {
        var record = new MCPActionRecord { AgentId = "history-fixture", ActionName = "editor/state", ErrorMessage = new string('x', 524288) };
        MCPActionHistory.RecordAction(record); return new WeakReference(record);
    }
    private static object Field(object instance, string name) => instance.GetType().GetField(name, InstanceHidden).GetValue(instance);
    private static void Refresh(MCPActionHistoryWindow window)
    {
        typeof(MCPActionHistoryWindow).GetField("_lastRefreshTime", InstanceHidden).SetValue(window, -100d);
        typeof(MCPActionHistoryWindow).GetMethod("OnInspectorUpdate", InstanceHidden).Invoke(window, null);
    }
    private static ProfilerRecorder Allocations() => ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1,
        ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
    public static void Run()
    {
        Require(File.Exists(".unity-mcp-validation"), "Use a marked disposable validation project");
        int oldLimit = MCPSettingsManager.ActionHistoryMaxEntries;
        bool oldPersistence = MCPSettingsManager.ActionHistoryPersistence;
        const string path = "Library/MCPActionHistory.json";
        byte[] oldFile = File.Exists(path) ? File.ReadAllBytes(path) : null;
        MCPSettingsManager.ActionHistoryPersistence = false;
        MCPSettingsManager.ActionHistoryMaxEntries = 8;
        Clear();
        try
        {
            Check("No observers schedule no callbacks through 2000 records", () => {
                for (int i = 0; i < 2000; i++) Record();
                int scheduled = Scheduled(); Require(MCPActionHistory.Count == 8, "History limit changed");
                Require(scheduled == 0, "Scheduled callbacks without observers: " + scheduled);
                return new { recorded = 2000, retained = MCPActionHistory.Count, scheduled };
            });
            Check("Clear releases an undispatched large record", () => {
                var weak = LargeRecord(); MCPActionHistory.Clear(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                Require(!weak.IsAlive, "A cleared record remains alive before editor callbacks drain");
                return new { collected = true, recordTextBytes = 1048576 };
            });
            Check("Observers retain deferred ordered exactly-once delivery", () => {
                var delivered = new List<long>(); Action<MCPActionRecord> observer = r => delivered.Add(r.Id);
                MCPActionHistory.OnActionRecorded += observer;
                try {
                    var expected = new List<long>(); for (int i = 0; i < 230; i++) expected.Add(Record().Id);
                    Require(delivered.Count == 0, "Event became synchronous"); int scheduled = Scheduled();
                    Drain(); Require(delivered.SequenceEqual(expected), "Notification order or count changed");
                    Require(scheduled == 1, "One callback per record remains: " + scheduled);
                    return new { delivered = delivered.Count, scheduled, deferred = true };
                } finally { MCPActionHistory.OnActionRecorded -= observer; }
            });
            Check("Clear cancels queued observer notifications", () => {
                int count = 0; Action<MCPActionRecord> observer = r => count++;
                MCPActionHistory.OnActionRecorded += observer;
                try { Record(); MCPActionHistory.Clear(); Drain(); Require(count == 0, "Cleared history was still notified"); return new { count }; }
                finally { MCPActionHistory.OnActionRecorded -= observer; }
            });
            Check("Each editor update drains at most 100 notifications", () => {
                int count = 0; Action<MCPActionRecord> observer = r => count++;
                MCPActionHistory.OnActionRecorded += observer;
                try {
                    for (int i = 0; i < 230; i++) Record();
                    var dispatch = typeof(MCPActionHistory).GetMethod("DispatchNotifications", Hidden);
                    Require(dispatch != null, "Missing grouped dispatcher"); dispatch.Invoke(null, null);
                    Require(count == 100 && Metric("pendingCount") == 130 && Scheduled() == 1, "Editor update allowance changed");
                    Drain(); Require(count == 230, "Later updates lost notifications"); return new { firstUpdate = 100, total = count };
                } finally { MCPActionHistory.OnActionRecorded -= observer; }
            });
            Check("Reentrant clear defers new records and cancels the previous batch", () => {
                var delivered = new List<string>();
                Action<MCPActionRecord> observer = record => { delivered.Add(record.ActionName); if (record.ActionName == "first") { MCPActionHistory.Clear(); Record("new"); } };
                MCPActionHistory.OnActionRecorded += observer;
                try {
                    Record("first"); Record("cancelled");
                    var dispatch = typeof(MCPActionHistory).GetMethod("DispatchNotifications", Hidden);
                    Require(dispatch != null, "Missing grouped dispatcher"); dispatch.Invoke(null, null);
                    Require(delivered.SequenceEqual(new[] { "first" }), "Reentrant notifications ran synchronously or survived clear");
                    Drain(); Require(delivered.SequenceEqual(new[] { "first", "new" }), "New generation was not delivered"); return new { delivered };
                } finally { MCPActionHistory.OnActionRecorded -= observer; }
            });
            Check("Unsubscribing releases pending notifications on the next update", () => {
                int calls = 0; Action<MCPActionRecord> observer = record => calls++;
                MCPActionHistory.OnActionRecorded += observer;
                try { for (int i = 0; i < 110; i++) Record(); }
                finally { MCPActionHistory.OnActionRecorded -= observer; }
                Drain(); Require(calls == 0 && Metric("pendingCount") == 0, "Unsubscribed notifications remained retained");
                return new { calls, pending = Metric("pendingCount") };
            });
            Check("One failing observer does not suppress later observers or records", () => {
                int count = 0;
                Action<MCPActionRecord> failing = record => throw new InvalidOperationException("Expected history notification fixture failure");
                Action<MCPActionRecord> observer = record => count++;
                MCPActionHistory.OnActionRecorded += failing; MCPActionHistory.OnActionRecorded += observer;
                try { Record(); Record(); Drain(); Require(count == 2, "Observer failure suppressed delivery"); return new { count }; }
                finally { MCPActionHistory.OnActionRecorded -= failing; MCPActionHistory.OnActionRecorded -= observer; }
            });
            Check("Notification pressure has an explicit bound", () => {
                int count = 0; Action<MCPActionRecord> observer = r => count++;
                MCPActionHistory.OnActionRecorded += observer;
                try {
                    long before = Metric("dropped"); for (int i = 0; i < 10032; i++) Record();
                    long pending = Metric("pendingCount"), dropped = Metric("dropped") - before;
                    Require(pending == 10000 && dropped == 32, "Notification pressure is not bounded or observable");
                    Drain(); Require(count == 10000, "Retained notifications were not delivered"); return new { pending, dropped, delivered = count };
                } finally { MCPActionHistory.OnActionRecorded -= observer; }
            });
            Check("Window coalesces bursts and reuses an idle list", () => {
                MCPSettingsManager.ActionHistoryMaxEntries = 500;
                var window = ScriptableObject.CreateInstance<MCPActionHistoryWindow>();
                try {
                    var before = Field(window, "_filteredRecords");
                    long control;
                    using (var recorder = Allocations()) { var bytes = new byte[1024]; recorder.Stop(); GC.KeepAlive(bytes); control = recorder.Count > 0 ? recorder.GetSample(0).Count : 0; }
                    Require(control > 0, "Allocation recorder failed its positive control");
                    var clock = System.Diagnostics.Stopwatch.StartNew(); long allocations, rawRecorderValue; string rawRecorderUnit; bool coalesced;
                    using (var recorder = Allocations()) {
                        for (int i = 0; i < 500; i++) Record(agent: "history-agent-" + (i % 20));
                        Drain(); coalesced = ReferenceEquals(before, Field(window, "_filteredRecords"));
                        Refresh(window); recorder.Stop(); clock.Stop(); allocations = recorder.Count > 0 ? recorder.GetSample(0).Count : 0;
                        rawRecorderValue = recorder.Count > 0 ? recorder.GetSample(0).Value : 0; rawRecorderUnit = recorder.UnitType.ToString();
                    }
                    var refreshed = Field(window, "_filteredRecords");
                    Require(((List<MCPActionRecord>)refreshed).Count == 500, "Window did not observe the burst");
                    Refresh(window); bool idleReused = ReferenceEquals(refreshed, Field(window, "_filteredRecords"));
                    Checks.Add(new { name = "500-record window cost observation", passed = true, evidence = new { allocations, rawRecorderValue, rawRecorderUnit, elapsedMs = clock.Elapsed.TotalMilliseconds, profilerControlAllocations = control, coalesced, idleReused } });
                    Require(coalesced && idleReused, "History window rebuilds per notification or while idle");
                    MCPActionHistory.Clear(); Refresh(window); Require(((List<MCPActionRecord>)Field(window, "_filteredRecords")).Count == 0, "Window missed a clear without an event");
                    return new { coalesced, idleReused, clearObserved = true };
                } finally { UnityEngine.Object.DestroyImmediate(window); }
            });
            Check("Window filters retain values when earlier options expire", () => {
                MCPSettingsManager.ActionHistoryMaxEntries = 3;
                Record("asset/info", "A"); Record("editor/state", "B"); Record("editor/state", "B");
                var window = ScriptableObject.CreateInstance<MCPActionHistoryWindow>();
                try {
                    typeof(MCPActionHistoryWindow).GetField("_agentFilterIndex", InstanceHidden).SetValue(window, 2);
                    typeof(MCPActionHistoryWindow).GetField("_categoryFilterIndex", InstanceHidden).SetValue(window, 2);
                    Record("editor/state", "B"); Drain(); Refresh(window);
                    string agent = ((string[])Field(window, "_agentOptions"))[(int)Field(window, "_agentFilterIndex")];
                    string category = ((string[])Field(window, "_categoryOptions"))[(int)Field(window, "_categoryFilterIndex")];
                    Require(agent == "B" && category == "editor", "Retention silently changed a selected filter");
                    return new { agent, category };
                } finally { UnityEngine.Object.DestroyImmediate(window); }
            });
            Check("Window selection follows its record and releases an evicted record", () => {
                MCPSettingsManager.ActionHistoryMaxEntries = 3; var first = Record();
                var window = ScriptableObject.CreateInstance<MCPActionHistoryWindow>();
                try {
                    typeof(MCPActionHistoryWindow).GetField("_selectedRecord", InstanceHidden).SetValue(window, first);
                    typeof(MCPActionHistoryWindow).GetField("_selectedIndex", InstanceHidden).SetValue(window, 0);
                    Record(); Drain(); Refresh(window);
                    Require((int)Field(window, "_selectedIndex") == 1 && ReferenceEquals(first, Field(window, "_selectedRecord")), "New action moved the highlight to a different record");
                    MCPSettingsManager.ActionHistoryMaxEntries = 1; Record(); Drain(); Refresh(window);
                    Require(Field(window, "_selectedRecord") == null && (int)Field(window, "_selectedIndex") == -1, "Evicted selection remained retained");
                    return new { insertionPreservedSelection = true, evictedSelectionReleased = true };
                } finally { UnityEngine.Object.DestroyImmediate(window); }
            });
        }
        finally
        {
            Clear(); MCPSettingsManager.ActionHistoryMaxEntries = oldLimit; MCPSettingsManager.ActionHistoryPersistence = oldPersistence;
            if (oldFile != null) File.WriteAllBytes(path, oldFile); else if (File.Exists(path)) File.Delete(path);
        }
        bool passed = Checks.All(check => (bool)check.GetType().GetProperty("passed").GetValue(check));
        File.WriteAllText("Library/UnityMcpHistoryNotificationsValidation.json", MiniJson.Serialize(new { unityVersion = Application.unityVersion, passed, checks = Checks }));
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
