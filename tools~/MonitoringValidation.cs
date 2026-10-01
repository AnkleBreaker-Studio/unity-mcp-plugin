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

public static class UnityMcpMonitoringValidation
{
    [Serializable]
    private class Report
    {
        public string unityVersion;
        public bool passed;
        public int resultCases;
        public int classificationMismatches;
        public bool rawResultsPreserved = true;
        public int commandErrors;
        public int exceptions;
        public int duplicateCallbacks = 20;
        public int deferredHistoryRecords;
        public int timedOutHistoryRecords;
        public bool lateCallbackIgnored;
        public bool legacyResultPreserved;
        public bool dashboardShowsCommandError;
        public bool dashboardShowsExceptionCount;
        public bool dashboardErrorRow;
        public bool historyEndpointError;
        public bool diagnosticBounded;
        public bool undoPreserved;
        public bool undoWithHistoryBacklog;
        public bool pendingHistorySavedBeforeReload;
        public bool clearIncludesPendingHistory;
        public bool historyRoundTrip;
        public bool oldHistoryLoads;
        public int maximumPendingHistory;
        public long historyRecordsDropped;
        public bool backlogDrained;
        public string error;
    }

    private class AutoResult { public bool success { get; set; } = false; public string error { get; set; } = "automatic property"; }
    private class FieldResult { public bool ok = false; public string error = "public field"; }
    private class GetterResult { public bool success => throw new InvalidOperationException("Monitoring called a user getter"); }
    private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        var report = new Report { unityVersion = Application.unityVersion };
        int previousLimit = MCPSettingsManager.ActionHistoryMaxEntries;
        bool previousPersistence = MCPSettingsManager.ActionHistoryPersistence;
        const string historyPath = "Library/MCPActionHistory.json";
        byte[] previousFile = File.Exists(historyPath) ? File.ReadAllBytes(historyPath) : null;
        try
        {
            Check(File.Exists(".unity-mcp-validation"), "This suite requires a disposable validation project");
            MCPSettingsManager.ActionHistoryMaxEntries = 500;
            MCPSettingsManager.ActionHistoryPersistence = false;
            MCPActionHistory.Clear();
            ValidateResults(report);
            ValidateCallbacks(report);
            ValidateLegacy(report);
            ValidateUndo(report);
            ValidateDiagnostic(report);
            ValidateDashboard(report);
            ValidatePersistence(report, historyPath);
            ValidateClear(report);
            ValidateBacklog(report);
            ValidateUndoBacklog(report);
            Check(report.classificationMismatches == 0, "Command errors are not classified consistently with MCP responses");
            Check(report.rawResultsPreserved && report.legacyResultPreserved, "A legacy result or completed ticket status changed");
            Check(report.commandErrors == 7 && report.exceptions == 1, "Command errors and exceptions are not counted separately");
            Check(report.deferredHistoryRecords == 1 && report.timedOutHistoryRecords == 1 && report.lateCallbackIgnored,
                "Deferred completion, timeout or duplicate callbacks corrupted history");
            Check(report.dashboardShowsCommandError && report.dashboardShowsExceptionCount, "Dashboard omits command errors or exceptions");
            Check(report.dashboardErrorRow && report.historyEndpointError, "Recent actions or the history endpoint conceal a command error");
            Check(report.undoPreserved && report.diagnosticBounded, "Undo behavior or diagnostic bounds regressed");
            Check(report.undoWithHistoryBacklog, "Pending history hid the newest edit from undo/last");
            Check(report.pendingHistorySavedBeforeReload, "Reload persistence omitted a completed callback awaiting an editor update");
            Check(report.clearIncludesPendingHistory, "Cleared history reappeared on the next editor update");
            Check(report.historyRoundTrip && report.oldHistoryLoads, "History persistence lost an outcome or rejected an old record");
            Check(report.maximumPendingHistory == 10000 && report.historyRecordsDropped == 1 && report.backlogDrained,
                "The history backlog is unbounded or loses terminal accounting");
            report.passed = true;
        }
        catch (Exception error)
        {
            report.error = error.ToString();
            UnityEngine.Debug.LogException(error);
        }
        finally
        {
            MCPActionHistory.Clear();
            MCPSettingsManager.ActionHistoryMaxEntries = previousLimit;
            MCPSettingsManager.ActionHistoryPersistence = previousPersistence;
            if (previousFile != null) File.WriteAllBytes(historyPath, previousFile);
        }
        File.WriteAllText("Library/UnityMcpMonitoringValidation.json", JsonUtility.ToJson(report, true));
        EditorApplication.Exit(report.passed ? 0 : 1);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static long Number(Dictionary<string, object> value, string key) =>
        value.TryGetValue(key, out var number) ? Convert.ToInt64(number) : 0;

    private static bool Failed(Dictionary<string, object> value) =>
        value.TryGetValue("commandFailed", out var failed) && failed is bool flag && flag;

    private static Dictionary<string, object> Session(string id) =>
        MCPRequestQueue.GetActiveSessions().Find(value => (string)value["agentId"] == id);

    private static void ValidateResults(Report report)
    {
        var cases = new List<KeyValuePair<object, bool>>
        {
            new KeyValuePair<object, bool>(new { error = "anonymous error" }, true),
            new KeyValuePair<object, bool>(new Dictionary<string, object> { { "success", false }, { "error", "dictionary error" } }, true),
            new KeyValuePair<object, bool>(new { ok = false }, true),
            new KeyValuePair<object, bool>(new { error = new { message = "structured error" } }, true),
            new KeyValuePair<object, bool>(new AutoResult(), true),
            new KeyValuePair<object, bool>(new FieldResult(), true),
            new KeyValuePair<object, bool>(new { error = new { message = "" } }, true),
            new KeyValuePair<object, bool>(new { success = true, error = "unrelated diagnostic" }, false),
            new KeyValuePair<object, bool>(new { ok = true, success = false }, false),
            new KeyValuePair<object, bool>(new { result = new { error = "nested data" } }, false),
            new KeyValuePair<object, bool>(new[] { new { error = "list entry" } }, false),
            new KeyValuePair<object, bool>(new GetterResult(), false),
            new KeyValuePair<object, bool>(new { error = "" }, false),
            new KeyValuePair<object, bool>(new { success = "false" }, false),
            new KeyValuePair<object, bool>(null, false),
        };
        foreach (var item in cases)
        {
            var ticket = MCPRequestQueue.SubmitRequest("monitor-results", "editor/state", () => item.Key);
            MCPRequestQueue.ProcessNextRequests();
            var status = MCPRequestQueue.GetTicketStatus(ticket.TicketId);
            report.resultCases++;
            if (Failed(status) != item.Value) report.classificationMismatches++;
            report.rawResultsPreserved &= ticket.Status == MCPRequestQueue.RequestStatus.Completed
                && ReferenceEquals(status["result"], item.Key) && string.IsNullOrEmpty(ticket.ErrorMessage);
        }
        MCPRequestQueue.SubmitRequest("monitor-results", "editor/state", () => throw new InvalidOperationException("expected exception"));
        MCPRequestQueue.ProcessNextRequests();
        report.commandErrors = (int)Number(Session("monitor-results"), "commandErrors");
        report.exceptions = (int)Number(Session("monitor-results"), "failedRequests");
    }

    private static void ValidateCallbacks(Report report)
    {
        Action<object> complete = null;
        var ticket = MCPRequestQueue.SubmitDeferredRequest("monitor-callback", "testing/list-tests", callback => complete = callback);
        MCPRequestQueue.ProcessNextRequests();
        Parallel.For(0, report.duplicateCallbacks, _ => complete(new { error = "deferred error" }));
        MCPRequestQueue.ProcessNextRequests();
        report.deferredHistoryRecords = MCPActionHistory.GetFiltered("monitor-callback", null, null).Count;
        Check(Number(Session("monitor-callback"), "completedRequests") == 1, "Duplicate callback changed completion count");

        Action<object> late = null;
        var expired = MCPRequestQueue.SubmitDeferredRequest("monitor-timeout", "testing/list-tests", callback => late = callback);
        MCPRequestQueue.ProcessNextRequests();
        typeof(MCPRequestQueue.RequestTicket).GetField("StartedTimestamp", InstancePrivate)
            .SetValue(expired, Stopwatch.GetTimestamp() - 121L * Stopwatch.Frequency);
        typeof(MCPRequestQueue).GetMethod("RunCleanup", StaticPrivate).Invoke(null, null);
        late(new { error = "too late" });
        MCPRequestQueue.ProcessNextRequests();
        report.timedOutHistoryRecords = MCPActionHistory.GetFiltered("monitor-timeout", null, null).Count;
        report.lateCallbackIgnored = expired.Status == MCPRequestQueue.RequestStatus.TimedOut
            && Number(Session("monitor-timeout"), "completedRequests") == 1
            && Number(Session("monitor-timeout"), "timedOutRequests") == 1
            && Number(Session("monitor-timeout"), "commandErrors") == 0;
    }

    private static void ValidateLegacy(Report report)
    {
        var expected = new Dictionary<string, object> { { "error", "legacy result" } };
        var response = Task.Run(() => MCPRequestQueue.ExecuteWithTracking("monitor-legacy", "editor/state", () => expected));
        var watch = Stopwatch.StartNew();
        while (!response.IsCompleted && watch.ElapsedMilliseconds < 5000)
        {
            MCPRequestQueue.ProcessNextRequests();
            Thread.Sleep(1);
        }
        Check(response.IsCompleted, "Legacy request did not complete");
        report.legacyResultPreserved = ReferenceEquals(response.Result, expected);
    }

    private static void ValidateDashboard(Report report)
    {
        var window = ScriptableObject.CreateInstance<MCPDashboardWindow>();
        try
        {
            var rows = new VisualElement();
            typeof(MCPDashboardWindow).GetField("_agentRows", InstancePrivate).SetValue(window, rows);
            typeof(MCPDashboardWindow).GetMethod("RefreshAgents", InstancePrivate).Invoke(window, null);
            var labels = new List<string>();
            rows.Query<Label>().ForEach(label => labels.Add(label.text));
            string text = string.Join("\n", labels);
            report.dashboardShowsCommandError = text.Contains("7 command errors");
            report.dashboardShowsExceptionCount = text.Contains("1 exception");
            var actions = new VisualElement();
            typeof(MCPDashboardWindow).GetField("_actionRows", InstancePrivate).SetValue(window, actions);
            typeof(MCPDashboardWindow).GetMethod("RefreshActions", InstancePrivate).Invoke(window, null);
            bool errorLabel = false;
            actions.Query<Label>().ForEach(label => errorLabel |= label.text == "Command error");
            report.dashboardErrorRow = errorLabel && actions.Q(className: "ab-dash__dot--red") != null;
        }
        finally { UnityEngine.Object.DestroyImmediate(window); }
    }

    private static void ValidatePersistence(Report report, string path)
    {
        Action<object> callback = null;
        MCPRequestQueue.SubmitDeferredRequest("monitor-reload", "testing/list-tests", complete => callback = complete);
        MCPRequestQueue.ProcessNextRequests();
        Task.Run(() => callback(new { error = "reload error" })).Wait();
        MCPSettingsManager.ActionHistoryPersistence = true;
        try { typeof(MCPActionHistory).GetMethod("OnBeforeReload", StaticPrivate).Invoke(null, null); }
        finally { MCPSettingsManager.ActionHistoryPersistence = false; }
        string saved = File.ReadAllText(path);
        report.pendingHistorySavedBeforeReload = saved.Contains("monitor-reload") && saved.Contains("reload error")
            && Number(MCPRequestQueue.GetQueueInfo(), "pendingHistoryRecords") == 0;
        MCPActionHistory.Clear();
        File.WriteAllText(path, saved);
        typeof(MCPActionHistory).GetMethod("LoadFromDisk", StaticPrivate).Invoke(null, null);
        var records = MCPActionHistory.GetFiltered("monitor-callback", null, null);
        report.historyRoundTrip = records.Count == 1 && Failed(records[0].ToDict()) && records[0].ErrorMessage == "deferred error";
        File.WriteAllText(path, "{\"records\":[{\"id\":900,\"agentId\":\"legacy-history\",\"status\":\"Completed\",\"actionName\":\"editor/state\",\"undoGroup\":-1}]}");
        typeof(MCPActionHistory).GetMethod("LoadFromDisk", StaticPrivate).Invoke(null, null);
        records = MCPActionHistory.GetAll();
        report.oldHistoryLoads = records.Count == 1 && records[0].Id == 900 && !Failed(records[0].ToDict());
        MCPActionHistory.Clear();
    }

    private static void ValidateDiagnostic(Report report)
    {
        var result = new Dictionary<string, object> { { "error", new string('x', 10000) } };
        var ticket = MCPRequestQueue.SubmitRequest("monitor-diagnostic", "editor/state", () => result);
        MCPRequestQueue.ProcessNextRequests();
        var status = MCPRequestQueue.GetTicketStatus(ticket.TicketId);
        report.diagnosticBounded = status.TryGetValue("commandError", out var error) && ((string)error).Length == 2048
            && ReferenceEquals(ticket.Result, result) && ((string)result["error"]).Length == 10000;
        var history = (Dictionary<string, object>)MCPUndoCommands.GetUndoHistory(new Dictionary<string, object> { { "agentId", "monitor-diagnostic" } });
        var actions = (List<Dictionary<string, object>>)history["actions"];
        report.historyEndpointError = actions.Count == 1 && Failed(actions[0]) && (string)actions[0]["status"] == "Completed";
    }

    private static void ValidateUndo(Report report)
    {
        GameObject created = null;
        try
        {
            MCPRequestQueue.SubmitRequest("monitor-undo", "gameobject/create", () =>
            {
                created = new GameObject("__mcp_monitoring_undo");
                Undo.RegisterCreatedObjectUndo(created, "monitoring validation");
                return new Dictionary<string, object> { { "name", created.name } };
            });
            MCPRequestQueue.ProcessNextRequests();
            var records = MCPActionHistory.GetFiltered("monitor-undo", null, null);
            bool recorded = records.Count == 1 && records[0].UndoGroup >= 0 && records[0].TargetPath == created.name;
            MCPRequestQueue.SubmitRequest("monitor-undo", "undo/last", () => MCPUndoCommands.UndoLast(
                new Dictionary<string, object> { { "agentId", "monitor-undo" } }));
            MCPRequestQueue.ProcessNextRequests();
            report.undoPreserved = recorded && created == null && records[0].UndoGroup == -1;
        }
        finally { if (created != null) UnityEngine.Object.DestroyImmediate(created); }
    }

    private static void ValidateBacklog(Report report)
    {
        if (!MCPRequestQueue.GetQueueInfo().ContainsKey("pendingHistoryRecords")) return;
        var callbacks = new List<Action<object>>();
        for (int i = 0; i < 10001; i++)
            MCPRequestQueue.SubmitDeferredRequest("monitor-backlog", "editor/state", callback => callbacks.Add(callback));
        while (MCPRequestQueue.TotalQueuedCount > 0) MCPRequestQueue.ProcessNextRequests();
        Parallel.ForEach(callbacks, callback => callback(true));
        var info = MCPRequestQueue.GetQueueInfo();
        report.maximumPendingHistory = (int)Number(info, "pendingHistoryRecords");
        report.historyRecordsDropped = Number(info, "droppedHistoryRecords");
        for (int i = 0; i < 101; i++) MCPRequestQueue.ProcessNextRequests();
        report.backlogDrained = Number(MCPRequestQueue.GetQueueInfo(), "pendingHistoryRecords") == 0
            && Number(Session("monitor-backlog"), "completedRequests") == 10001 && MCPActionHistory.Count == 500;
    }

    private static void ValidateClear(Report report)
    {
        Action<object> callback = null;
        MCPRequestQueue.SubmitDeferredRequest("monitor-clear", "testing/list-tests", complete => callback = complete);
        MCPRequestQueue.ProcessNextRequests();
        Task.Run(() => callback(true)).Wait();
        MCPActionHistory.Clear();
        MCPRequestQueue.ProcessNextRequests();
        report.clearIncludesPendingHistory = MCPActionHistory.Count == 0
            && Number(MCPRequestQueue.GetQueueInfo(), "pendingHistoryRecords") == 0
            && Number(Session("monitor-clear"), "completedRequests") == 1;
    }

    private static void ValidateUndoBacklog(Report report)
    {
        MCPActionHistory.Clear();
        int oldLimit = MCPSettingsManager.ActionHistoryMaxEntries;
        MCPSettingsManager.ActionHistoryMaxEntries = 2000;
        GameObject first = null, second = null;
        try
        {
            MCPRequestQueue.SubmitRequest("monitor-backlog-undo", "gameobject/create", () =>
            {
                first = new GameObject("__mcp_undo_backlog_first");
                Undo.RegisterCreatedObjectUndo(first, "first backlog edit");
                return true;
            });
            MCPRequestQueue.ProcessNextRequests();
            var callbacks = new List<Action<object>>();
            for (int i = 0; i < 1000; i++)
                MCPRequestQueue.SubmitDeferredRequest("monitor-backlog-reads", "editor/state", complete => callbacks.Add(complete));
            while (MCPRequestQueue.TotalQueuedCount > 0) MCPRequestQueue.ProcessNextRequests();
            Parallel.ForEach(callbacks, complete => complete(true));
            MCPRequestQueue.SubmitRequest("monitor-backlog-undo", "gameobject/create", () =>
            {
                second = new GameObject("__mcp_undo_backlog_second");
                Undo.RegisterCreatedObjectUndo(second, "second backlog edit");
                return true;
            });
            MCPRequestQueue.ProcessNextRequests();
            Check(Number(MCPRequestQueue.GetQueueInfo(), "pendingHistoryRecords") > 0, "Fixture did not create a history backlog");
            var undo = MCPRequestQueue.SubmitRequest("monitor-backlog-undo", "undo/last", () => MCPUndoCommands.UndoLast(
                new Dictionary<string, object> { { "agentId", "monitor-backlog-undo" } }));
            MCPRequestQueue.ProcessNextRequests();
            var result = undo.Result as Dictionary<string, object>;
            report.undoWithHistoryBacklog = result != null && result.TryGetValue("success", out var success) && Equals(success, true)
                && second == null && first != null && Number(MCPRequestQueue.GetQueueInfo(), "pendingHistoryRecords") == 0;
        }
        finally
        {
            if (first != null) UnityEngine.Object.DestroyImmediate(first);
            if (second != null) UnityEngine.Object.DestroyImmediate(second);
            MCPSettingsManager.ActionHistoryMaxEntries = oldLimit;
        }
    }
}
