using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityMCP.Editor;

public static class UnityMcpTestPersistenceValidation
{
    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type Commands = typeof(MCPTestRunnerCommands);
    private static readonly List<object> Checks = new List<object>();
    private static readonly List<object> NativeEntries = new List<object>();
    private static FieldInfo Field(string name) => Commands.GetField(name, Hidden);
    private static object Invoke(string name, params object[] args) => Commands.GetMethod(name, Hidden).Invoke(null, args);
    private static IDictionary Jobs => (IDictionary)Field("_jobs").GetValue(null);
    private static IList NativeRuns
    {
        get
        {
            var type = typeof(TestRunnerApi).Assembly.GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder", true);
            var instance = type.GetProperty("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy).GetValue(null);
            return (IList)type.GetField("TestRuns").GetValue(instance);
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Check(string name, Func<object> body)
    {
        try { Checks.Add(new { name, passed = true, evidence = body() }); }
        catch (Exception error) { Checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
        finally { Reset(); }
    }
    private static string Begin(bool nativeActive = false)
    {
        Invoke("EnsureCallbacksRegistered");
        var api = (TestRunnerApi)Field("_testRunnerApi").GetValue(null);
        string nativeId = Guid.NewGuid().ToString();
        typeof(TestRunnerApi).GetField("ScheduleJob", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(api, (Func<ExecutionSettings, string>)(settings => nativeId));
        MCPTestRunnerCommands.RunTests(new Dictionary<string, object> {
            { "testNames", new List<object> { "Fixture.Pass", "Fixture.Fail" } },
            { "categories", new List<object> { "Persistence" } },
            { "assemblies", new List<object> { "Validation" } },
            { "groupNames", new List<object> { "Fixture.*" } }
        });
        string id = (string)Field("_currentJobId").GetValue(null);
        Require(id != null, "Controlled job did not start");
        if (nativeActive)
        {
            var type = typeof(TestRunnerApi).Assembly.GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobData", true);
            var data = Activator.CreateInstance(type, new object[] { new ExecutionSettings(new Filter { testMode = TestMode.EditMode }) });
            type.GetField("guid").SetValue(data, nativeId);
            type.GetField("isRunning").SetValue(data, true);
            NativeRuns.Add(data); NativeEntries.Add(data);
        }
        return id;
    }
    private static Dictionary<string, object> Read(string id) => (Dictionary<string, object>)MCPTestRunnerCommands.GetTestJob(new Dictionary<string, object> { { "jobId", id }, { "includeDetails", true } });
    private static void AddResults(string id)
    {
        Invoke("OnRunStarted", id, 3);
        Invoke("OnTestFinished", id, "Fixture.Pass", "Pass", TestStatus.Passed, 0.125, "", "");
        Invoke("OnTestFinished", id, "Fixture.Fail", "Fail", TestStatus.Failed, 0.25, "Failure \u00e9 \"quoted\"\nnext line", "Frame.One\nFrame.Two");
    }
    private static void Reload()
    {
        Invoke("BeforeAssemblyReload");
        Jobs.Clear(); Field("_currentJobId").SetValue(null, null);
        Invoke("RestoreFromSessionState");
    }
    public static void Run()
    {
        Check("Completed details and failure diagnostics survive reload", () => {
            string id = Begin(); AddResults(id); Invoke("OnRunFinished", id, 1, 1, 0, 0, 1.25);
            var before = Read(id); Reload(); var after = Read(id);
            Require((bool)after["resultsComplete"] && MiniJson.Serialize(before["tests"]) == MiniJson.Serialize(after["tests"]), "Detailed results were lost or altered");
            Require(MiniJson.Serialize(before["progress"]) == MiniJson.Serialize(after["progress"]), "Failure diagnostics changed");
            return new { detailCount = ((IList)after["tests"]).Count, unicodeAndStackTracePreserved = true };
        });
        Check("Running test name, timer and filters survive reload", () => {
            string id = Begin(true); AddResults(id); Invoke("OnTestStarted", id, "Fixture.Slow");
            var job = Jobs[id]; var started = DateTime.UtcNow.AddSeconds(-20);
            job.GetType().GetField("CurrentTestStartedAt").SetValue(job, (DateTime?)started);
            Reload(); var after = Read(id); var progress = (Dictionary<string, object>)after["progress"];
            Require((string)after["status"] == "running" && progress.ContainsKey("currentTest") && (string)progress["currentTest"] == "Fixture.Slow", "Running test identity was lost");
            Require(Convert.ToDouble(progress["currentTestElapsed"]) >= 20 && Convert.ToDouble(progress["currentTestElapsed"]) < 30, "Running timer was lost");
            var restored = Jobs[id]; var filters = (string[])restored.GetType().GetField("TestNames").GetValue(restored);
            Require(filters != null && filters.Length == 2, "Filters were lost");
            return new { status = after["status"], currentTest = progress["currentTest"], elapsed = progress["currentTestElapsed"] };
        });
        Check("A long-running native job is not failed by its age", () => {
            string id = Begin(true); var job = Jobs[id]; job.GetType().GetField("StartedAt").SetValue(job, DateTime.UtcNow.AddMinutes(-10));
            Reload(); var after = Read(id);
            Require((string)after["status"] == "running", "Live native job was marked " + after["status"]); return new { status = after["status"] };
        });
        Check("An orphaned native job becomes a terminal failure", () => {
            string id = Begin(); Reload(); var after = Read(id);
            Require((string)after["status"] == "failed" && Field("_currentJobId").GetValue(null) == null, "Orphaned job remained running"); return new { status = after["status"], error = after["error"] };
        });
        Check("A damaged summary does not hide later valid jobs", () => {
            string id = Begin(); AddResults(id); Invoke("OnRunFinished", id, 1, 1, 0, 0, 1.25);
            var saved = (List<object>)MiniJson.Deserialize(SessionState.GetString("MCPTestRunner_Jobs", ""));
            saved.Insert(0, new Dictionary<string, object> { { "jobId", "damaged" }, { "mode", "EditMode" }, { "status", "Succeeded" }, { "totalTests", "invalid" } });
            SessionState.SetString("MCPTestRunner_Jobs", MiniJson.Serialize(saved));
            Jobs.Clear(); Field("_currentJobId").SetValue(null, null); Invoke("RestoreFromSessionState");
            var after = Read(id); Require(after.ContainsKey("jobId") && (string)after["jobId"] == id, "A damaged summary prevented valid restoration"); return new { restored = id };
        });
        Check("Legacy summaries remain readable", () => {
            string id = Begin(); Invoke("OnRunFinished", id, 1, 0, 0, 0, 0.2);
            var saved = (List<object>)MiniJson.Deserialize(SessionState.GetString("MCPTestRunner_Jobs", ""));
            var summary = (Dictionary<string, object>)saved[0]; summary.Remove("snapshotVersion"); summary.Remove("snapshotBytes");
            SessionState.EraseString("MCPTestRunner_Job_" + id); SessionState.SetString("MCPTestRunner_Jobs", MiniJson.Serialize(saved));
            Jobs.Clear(); Field("_currentJobId").SetValue(null, null); Invoke("RestoreFromSessionState");
            var after = Read(id); Require((string)after["status"] == "succeeded" && Convert.ToInt32(((Dictionary<string,object>)after["summary"])["passed"]) == 1 && !(bool)after["resultsComplete"], "Legacy summary changed"); return new { status = after["status"], resultsComplete = after["resultsComplete"] };
        });
        Check("A damaged snapshot preserves the summary and reports missing details", () => {
            string id = Begin(); AddResults(id); Invoke("OnRunFinished", id, 1, 1, 0, 0, 1.25);
            SessionState.SetString("MCPTestRunner_Job_" + id, "{invalid");
            Jobs.Clear(); Field("_currentJobId").SetValue(null, null); Invoke("RestoreFromSessionState");
            var result = Read(id);
            Require((string)result["status"] == "failed" && !(bool)result["resultsComplete"] && result.ContainsKey("persistenceWarning"), "Snapshot corruption lost the summary or was hidden");
            return new { warning = result["persistenceWarning"] };
        });
        Check("A legacy active job with unknown native identity stays explicit", () => {
            string id = Begin(); var job = Jobs[id]; job.GetType().GetField("NativeRunId").SetValue(job, null);
            Reload(); var result = Read(id);
            Require((string)result["status"] == "running" && result.ContainsKey("recoveryWarning"), "Unknown native identity was treated as definite failure");
            return new { warning = result["recoveryWarning"] };
        });
        Check("A resumed RunStarted retains progress and all filters", () => {
            string id = Begin(true); AddResults(id); Reload(); Invoke("OnRunStarted", id, 3);
            var result = Read(id); var job = Jobs[id];
            foreach (var field in new[] { "TestNames", "Categories", "Assemblies", "GroupNames" })
                Require(((string[])job.GetType().GetField(field).GetValue(job)).Length > 0, field + " was lost");
            Require(Convert.ToInt32(((Dictionary<string,object>)result["progress"])["completed"]) == 2 && (bool)result["resultsComplete"], "Resumed RunStarted reset progress");
            return new { completed = 2, filterFamilies = 4 };
        });
        Check("Count retention evicts old snapshots and preserves the latest job", () => {
            string oldest = null, latest = null;
            for (int i = 0; i < 130; i++) {
                latest = Begin(); if (oldest == null) oldest = latest;
                Invoke("OnRunFinished", latest, 0, 0, 0, 0, 0.1);
            }
            Require(Jobs.Count == 128 && !Jobs.Contains(oldest) && Jobs.Contains(latest), "Job count retention failed");
            Require(SessionState.GetString("MCPTestRunner_Job_" + oldest, "missing") == "missing", "Evicted snapshot survived");
            Reload(); Require(Jobs.Count == 128 && Jobs.Contains(latest), "Evicted jobs returned after reload");
            return new { retained = Jobs.Count };
        });
        Check("Expired history is removed on read without starting another run", () => {
            string id = Begin(); Invoke("OnRunFinished", id, 0, 0, 0, 0, 0.1);
            var job = Jobs[id]; job.GetType().GetField("CompletedAt").SetValue(job, (DateTime?)DateTime.UtcNow.AddMinutes(-31));
            var result = Read(id); Require(!result.ContainsKey("jobId") && !Jobs.Contains(id), "Read retained an expired job");
            Require(SessionState.GetString("MCPTestRunner_Job_" + id, "missing") == "missing", "Expired snapshot survived");
            return new { error = result["error"] };
        });
        Check("Snapshot byte pressure evicts old jobs but keeps oversized newest results", () => {
            string first = Begin(); Invoke("OnTestFinished", first, "Fixture.Large", "Large", TestStatus.Failed, 0.1, new string('x', 20 * 1024 * 1024), "");
            Invoke("OnRunFinished", first, 0, 1, 0, 0, 0.1);
            string newest = Begin(); Invoke("OnTestFinished", newest, "Fixture.Large", "Large", TestStatus.Failed, 0.1, new string('y', 34 * 1024 * 1024), "");
            Invoke("OnRunFinished", newest, 0, 1, 0, 0, 0.1);
            Require(!Jobs.Contains(first) && Jobs.Contains(newest), "Byte budget did not evict oldest completed history");
            var result = Read(newest); var retention = (Dictionary<string, object>)MiniJson.Deserialize(MiniJson.Serialize(result["historyRetention"]));
            Require((bool)retention["overBudget"] && (bool)result["resultsComplete"], "Protected oversized snapshot was silently truncated");
            Reload(); result = Read(newest);
            var entry = (Dictionary<string, object>)((IList)result["tests"])[0];
            Require(((string)entry["message"]).Length == 34 * 1024 * 1024, "Oversized latest snapshot lost details");
            return new { retained = Jobs.Count, snapshotBytes = retention["snapshotBytes"], overBudget = retention["overBudget"] };
        });
        bool passed = true; foreach (var check in Checks) passed &= (bool)check.GetType().GetProperty("passed").GetValue(check);
        File.WriteAllText("Library/UnityMcpTestPersistenceValidation.json", MiniJson.Serialize(new { unityVersion = Application.unityVersion, passed, nativeTestsLaunched = false, checks = Checks }));
        EditorApplication.Exit(passed ? 0 : 1);
    }
    private static void Reset()
    {
        foreach (var entry in NativeEntries) NativeRuns.Remove(entry); NativeEntries.Clear();
        Invoke("RestorePlayModeOptions");
        var api = Field("_testRunnerApi").GetValue(null) as TestRunnerApi; var callback = Field("_callbacks").GetValue(null) as ICallbacks;
        if (api != null && callback != null) api.UnregisterCallbacks(callback);
        Field("_callbacks").SetValue(null, null); Field("_testRunnerApi").SetValue(null, null);
        if (api != null) UnityEngine.Object.DestroyImmediate(api);
        foreach (var id in Jobs.Keys) SessionState.EraseString("MCPTestRunner_Job_" + id);
        Jobs.Clear(); Field("_currentJobId").SetValue(null, null);
        SessionState.EraseString("MCPTestRunner_Jobs"); SessionState.EraseString("MCPTestRunner_CurrentJobId");
        SessionState.EraseInt("MCPTestRunner_HistoryEvictions");
    }
}
