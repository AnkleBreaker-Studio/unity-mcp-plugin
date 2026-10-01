using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityMCP.Editor;

public static class UnityMcpTestRunnerValidation
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type Commands = typeof(MCPTestRunnerCommands);
    private static object Invoke(string name, params object[] args)
    {
        var method = Commands.GetMethod(name, PrivateStatic);
        if (name == "OnRunFinished" && method.GetParameters().Length == args.Length + 1)
        {
            var expanded = new object[args.Length + 1];
            expanded[0] = Field("_currentJobId").GetValue(null);
            Array.Copy(args, 0, expanded, 1, args.Length);
            args = expanded;
        }
        return method.Invoke(null, args);
    }
    private static FieldInfo Field(string name) => Commands.GetField(name, PrivateStatic);
    private static readonly List<object> Results = new List<object>();

    public static void Run()
    {
        bool enabled = EditorSettings.enterPlayModeOptionsEnabled;
        var options = EditorSettings.enterPlayModeOptions;
        bool completed = false;
        bool allPassed = true;
        try
        {
            for (int optionMask = 0; optionMask < 4; optionMask++)
            foreach (string scenario in new[] { "startup-exception", "framework-error", "clear-stuck", "normal-completion" })
            {
                Reset();
                EditorSettings.enterPlayModeOptionsEnabled = false;
                EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)optionMask;
                bool expectedEnabled = EditorSettings.enterPlayModeOptionsEnabled;
                var expectedOptions = EditorSettings.enterPlayModeOptions;
                Invoke("EnsureCallbacksRegistered");
                var api = (TestRunnerApi)Field("_testRunnerApi").GetValue(null);
                var schedule = typeof(TestRunnerApi).GetField("ScheduleJob", BindingFlags.NonPublic | BindingFlags.Instance);
                var originalSchedule = schedule.GetValue(api);
                string thrown = null;
                object response = null;
                try
                {
                    schedule.SetValue(api, (Func<ExecutionSettings, string>)(settings =>
                    {
                        if (scenario == "startup-exception") throw new InvalidOperationException("Controlled startup failure");
                        return "validation-native-run";
                    }));
                    try { response = MCPTestRunnerCommands.RunTests(new Dictionary<string, object> { { "mode", "PlayMode" } }); }
                    catch (Exception error) { thrown = error.Message; }
                    if (scenario == "framework-error")
                    {
                        var delegator = typeof(TestRunnerApi).Assembly.GetType("UnityEditor.TestTools.TestRunner.Api.CallbacksDelegator", true);
                        var instance = delegator.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null);
                        delegator.GetMethod("RunFailed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Invoke(instance, new object[] { "Controlled prebuild failure" });
                    }
                    if (scenario == "clear-stuck") response = MCPTestRunnerCommands.RunTests(new Dictionary<string, object> { { "clearStuck", true } });
                    if (scenario == "normal-completion") Invoke("OnRunFinished", 0, 0, 0, 0, 0.1);
                    var state = MCPTestRunnerCommands.GetTestJob(new Dictionary<string, object>()) as Dictionary<string, object>;
                    bool restored = EditorSettings.enterPlayModeOptionsEnabled == expectedEnabled && EditorSettings.enterPlayModeOptions == expectedOptions && !SessionState.GetBool("MCPTestRunner_PlayModeGuard", false);
                    bool released = Field("_currentJobId").GetValue(null) == null;
                    bool casePassed = restored && released && Field("_callbacks").GetValue(null) == null && (string)state["status"] == (scenario == "normal-completion" ? "succeeded" : "failed");
                    allPassed &= casePassed;
                    Results.Add(new { scenario, optionMask, expectedEnabled, expectedOptions = (int)expectedOptions, actualEnabled = EditorSettings.enterPlayModeOptionsEnabled, actualOptions = (int)EditorSettings.enterPlayModeOptions, settingsRestored = restored, currentJobReleased = released, status = state?["status"], thrown, response, passed = casePassed });
                }
                finally { schedule.SetValue(api, originalSchedule); }
            }
            ValidateEmptyClear();
            ValidateLateCallbackAndReload();
            ValidateRestoredTimestamps();
            completed = true;
        }
        catch (Exception error) { Results.Add(new { harnessError = error.ToString() }); }
        finally
        {
            Reset();
            EditorSettings.enterPlayModeOptionsEnabled = enabled;
            EditorSettings.enterPlayModeOptions = options;
            File.WriteAllText("Library/UnityMcpTestRunnerValidation.json", MiniJson.Serialize(new { unityVersion = Application.unityVersion, passed = completed && allPassed, controlledCases = 16, nativeTestsLaunched = false, results = Results }));
        }
        EditorApplication.Exit(completed && allPassed ? 0 : 1);
    }

    private static void ValidateRestoredTimestamps()
    {
        Reset();
        Invoke("EnsureCallbacksRegistered");
        var api = (TestRunnerApi)Field("_testRunnerApi").GetValue(null);
        var schedule = typeof(TestRunnerApi).GetField("ScheduleJob", BindingFlags.NonPublic | BindingFlags.Instance);
        schedule.SetValue(api, (Func<ExecutionSettings, string>)(settings => "validation-native-run"));
        MCPTestRunnerCommands.RunTests(new Dictionary<string, object>());
        string previousId = (string)Field("_currentJobId").GetValue(null);
        Invoke("OnRunFinished", 0, 0, 0, 0, 0.1);
        var saved = (List<object>)MiniJson.Deserialize(SessionState.GetString("MCPTestRunner_Jobs", ""));
        var record = (Dictionary<string, object>)saved[0];
        var previousUtc = DateTime.UtcNow.AddMinutes(-1);
        string offsetTimestamp = new DateTimeOffset(previousUtc).ToOffset(TimeSpan.FromHours(2)).ToString("O");
        record["startedAt"] = offsetTimestamp;
        record["completedAt"] = offsetTimestamp;
        SessionState.SetString("MCPTestRunner_Jobs", MiniJson.Serialize(saved));
        var jobs = (IDictionary)Field("_jobs").GetValue(null);
        jobs.Clear();
        Invoke("RestoreFromSessionState");
        var restored = jobs[previousId];
        var started = (DateTime)restored.GetType().GetField("StartedAt").GetValue(restored);
        var completed = (DateTime)restored.GetType().GetField("CompletedAt").GetValue(restored);
        if (started.Kind != DateTimeKind.Utc || completed.Kind != DateTimeKind.Utc || started != previousUtc || completed != previousUtc)
            throw new InvalidOperationException("Restored timestamps changed their UTC instant or kind");
        MCPTestRunnerCommands.RunTests(new Dictionary<string, object>());
        string newestId = (string)Field("_currentJobId").GetValue(null);
        Invoke("OnRunFinished", 0, 0, 0, 0, 0.1);
        var latest = (Dictionary<string, object>)MCPTestRunnerCommands.GetTestJob(new Dictionary<string, object>());
        if ((string)latest["jobId"] != newestId)
            throw new InvalidOperationException("A restored job sorted ahead of a newer job");
        Results.Add(new { scenario = "restored-timestamps-stay-utc", passed = true });
    }

    private static void ValidateEmptyClear()
    {
        Reset();
        Invoke("EnsureCallbacksRegistered");
        var api = (TestRunnerApi)Field("_testRunnerApi").GetValue(null);
        var schedule = typeof(TestRunnerApi).GetField("ScheduleJob", BindingFlags.NonPublic | BindingFlags.Instance);
        int scheduled = 0;
        schedule.SetValue(api, (Func<ExecutionSettings, string>)(settings => { scheduled++; return "unexpected-run"; }));
        var response = MCPTestRunnerCommands.RunTests(new Dictionary<string, object> { { "clearStuck", true } });
        if (scheduled != 0 || Field("_currentJobId").GetValue(null) != null || ((IDictionary)Field("_jobs").GetValue(null)).Count != 0)
            throw new InvalidOperationException("Clearing an absent job scheduled a test run");
        Results.Add(new { scenario = "clear-without-active-job", scheduled, response, passed = true });
    }

    private static void ValidateLateCallbackAndReload()
    {
        Reset();
        Invoke("EnsureCallbacksRegistered");
        var api = (TestRunnerApi)Field("_testRunnerApi").GetValue(null);
        var schedule = typeof(TestRunnerApi).GetField("ScheduleJob", BindingFlags.NonPublic | BindingFlags.Instance);
        schedule.SetValue(api, (Func<ExecutionSettings, string>)(settings => "validation-native-run"));
        MCPTestRunnerCommands.RunTests(new Dictionary<string, object> { { "mode", "PlayMode" } });
        var abandonedCallback = (IErrorCallbacks)Field("_callbacks").GetValue(null);
        MCPTestRunnerCommands.RunTests(new Dictionary<string, object> { { "clearStuck", true } });
        MCPTestRunnerCommands.RunTests(new Dictionary<string, object> { { "mode", "PlayMode" } });
        string current = (string)Field("_currentJobId").GetValue(null);
        abandonedCallback.OnError("Late error from the preceding run");
        var state = (Dictionary<string, object>)MCPTestRunnerCommands.GetTestJob(new Dictionary<string, object>());
        if ((string)state["jobId"] != current || (string)state["status"] != "running")
            throw new InvalidOperationException("A late callback changed the new job");

        Invoke("BeforeAssemblyReload");
        if (api != null || Field("_callbacks").GetValue(null) != null)
            throw new InvalidOperationException("Reload retained the owned API or callbacks");
        ((IDictionary)Field("_jobs").GetValue(null)).Clear();
        Field("_currentJobId").SetValue(null, null);
        Invoke("RestoreFromSessionState");
        Invoke("EnsureCallbacksRegistered");
        var job = ((IDictionary)Field("_jobs").GetValue(null))[current];
        if ((string)job.GetType().GetField("NativeRunId").GetValue(job) != "validation-native-run")
            throw new InvalidOperationException("Native run identity was lost across session restoration");
        ((IErrorCallbacks)Field("_callbacks").GetValue(null)).OnError("End the restored fixture job");
        if (SessionState.GetBool("MCPTestRunner_PlayModeGuard", false) || Field("_currentJobId").GetValue(null) != null)
            throw new InvalidOperationException("Restored job failure did not release its state");
        Results.Add(new { scenario = "late-callback-and-session-restore", passed = true });
    }

    private static void Reset()
    {
        Invoke("RestorePlayModeOptions");
        var api = Field("_testRunnerApi").GetValue(null) as TestRunnerApi;
        var callbacks = Field("_callbacks").GetValue(null) as ICallbacks;
        if (api != null && callbacks != null) api.UnregisterCallbacks(callbacks);
        Field("_callbacks").SetValue(null, null);
        Field("_testRunnerApi").SetValue(null, null);
        if (api != null) UnityEngine.Object.DestroyImmediate(api);
        ((IDictionary)Field("_jobs").GetValue(null)).Clear();
        Field("_currentJobId").SetValue(null, null);
        SessionState.EraseString("MCPTestRunner_Jobs");
        SessionState.EraseString("MCPTestRunner_CurrentJobId");
    }
}
