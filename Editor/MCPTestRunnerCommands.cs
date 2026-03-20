using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// MCP command handler for running Unity Test Runner tests.
    /// Uses an async job-based pattern: start a test run (returns job ID),
    /// then poll for status/results via the job ID.
    /// </summary>
    [InitializeOnLoad]
    public static class MCPTestRunnerCommands
    {
        // ─── Job Tracking ────────────────────────────────────────────

        private static readonly Dictionary<string, TestJob> _jobs = new Dictionary<string, TestJob>();
        private static string _currentJobId;
        private static TestRunnerApi _testRunnerApi;
        private static MCPTestCallbacks _callbacks;

        private const int MaxFailuresTracked = 50;
        private const double StuckThresholdSeconds = 120.0;
        private const double JobExpiryMinutes = 30.0;

        // ─── PlayMode Domain Reload Guard ────────────────────────────
        private const string PlayModeGuardKey = "MCPTestRunner_PlayModeGuard";
        private const string PlayModeOriginalEnabledKey = "MCPTestRunner_OriginalPMOEnabled";
        private const string PlayModeOriginalOptionsKey = "MCPTestRunner_OriginalPMOOptions";

        static MCPTestRunnerCommands()
        {
            // Restore state after domain reload
            RestoreFromSessionState();

            // Re-register callbacks if a test job is in progress
            // (handles domain reload during PlayMode tests if guard failed)
            if (_currentJobId != null && _jobs.TryGetValue(_currentJobId, out var job)
                && job.Status == TestJobStatus.Running)
            {
                EnsureCallbacksRegistered();
                Debug.Log($"[MCP TestRunner] Re-registered callbacks after domain reload for job {_currentJobId}");
            }

            // Restore PlayMode options if test run completed during Play Mode
            // but OnRunFinished didn't fire (crash recovery)
            if (SessionState.GetBool(PlayModeGuardKey, false) && _currentJobId == null)
            {
                RestorePlayModeOptions();
            }
        }

        // ─── Public API ──────────────────────────────────────────────

        /// <summary>
        /// Start a test run. Returns a job ID immediately.
        /// Route: testing/run-tests
        /// </summary>
        public static object RunTests(Dictionary<string, object> args)
        {
            // Check if a test run is already in progress
            if (_currentJobId != null && _jobs.TryGetValue(_currentJobId, out var existing)
                && existing.Status == TestJobStatus.Running)
            {
                // Allow force-clear of stuck jobs
                bool clearStuck = args.ContainsKey("clearStuck") && Convert.ToBoolean(args["clearStuck"]);
                if (clearStuck)
                {
                    existing.Status = TestJobStatus.Failed;
                    existing.Error = "Force-cleared by user";
                    existing.CompletedAt = DateTime.UtcNow;
                    _currentJobId = null;
                    SaveToSessionState();
                    return new Dictionary<string, object>
                    {
                        { "success", true },
                        { "message", "Stuck job cleared" },
                        { "clearedJobId", existing.JobId }
                    };
                }

                return new Dictionary<string, object>
                {
                    { "error", "A test run is already in progress" },
                    { "currentJobId", _currentJobId },
                    { "hint", "Use clearStuck=true to force-clear if the job appears stuck" }
                };
            }

            // Check for Play Mode — don't run tests while playing
            if (EditorApplication.isPlaying)
            {
                return new Dictionary<string, object>
                {
                    { "error", "Cannot run tests while Play Mode is active. Stop the scene first." }
                };
            }

            // Check for compilation
            if (EditorApplication.isCompiling)
            {
                return new Dictionary<string, object>
                {
                    { "error", "Cannot run tests while scripts are compiling. Wait for compilation to finish." }
                };
            }

            // Parse mode
            string modeStr = args.ContainsKey("mode") ? args["mode"].ToString() : "EditMode";
            TestMode testMode;
            switch (modeStr.ToLowerInvariant())
            {
                case "editmode":
                case "edit":
                    testMode = TestMode.EditMode;
                    break;
                case "playmode":
                case "play":
                    testMode = TestMode.PlayMode;
                    break;
                default:
                    return new Dictionary<string, object>
                    {
                        { "error", $"Unknown test mode: {modeStr}. Use 'EditMode' or 'PlayMode'." }
                    };
            }

            // Parse filters
            string[] testNames = ParseStringArray(args, "testNames");
            string[] testCategories = ParseStringArray(args, "categories");
            string[] assemblyNames = ParseStringArray(args, "assemblies");
            string[] groupNames = ParseStringArray(args, "groupNames");

            // Create the job
            var job = new TestJob
            {
                JobId = Guid.NewGuid().ToString("N").Substring(0, 12),
                Mode = testMode,
                Status = TestJobStatus.Running,
                StartedAt = DateTime.UtcNow,
                TestNames = testNames,
                Categories = testCategories,
                Assemblies = assemblyNames,
            };

            _jobs[job.JobId] = job;
            _currentJobId = job.JobId;

            // Clean up old jobs
            CleanupExpiredJobs();
            SaveToSessionState();

            // Build the filter
            var filter = new Filter
            {
                testMode = testMode,
            };

            if (testNames != null && testNames.Length > 0)
                filter.testNames = testNames;
            if (testCategories != null && testCategories.Length > 0)
                filter.categoryNames = testCategories;
            if (assemblyNames != null && assemblyNames.Length > 0)
                filter.assemblyNames = assemblyNames;
            if (groupNames != null && groupNames.Length > 0)
                filter.groupNames = groupNames;

            // For PlayMode tests, disable domain reload to preserve callbacks
            if (testMode == TestMode.PlayMode)
            {
                SaveAndDisableDomainReload();
            }

            // Start the test run
            EnsureCallbacksRegistered();

            var executionSettings = new ExecutionSettings(filter);
            _testRunnerApi.Execute(executionSettings);

            Debug.Log($"[MCP TestRunner] Started test job {job.JobId} (mode={testMode})");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "jobId", job.JobId },
                { "status", "running" },
                { "mode", testMode.ToString() }
            };
        }

        /// <summary>
        /// Get the status/results of a test job.
        /// Route: testing/get-job
        /// </summary>
        public static object GetTestJob(Dictionary<string, object> args)
        {
            string jobId = args.ContainsKey("jobId") ? args["jobId"].ToString() : null;
            if (string.IsNullOrEmpty(jobId))
            {
                // If no jobId, return the current/latest job
                if (_currentJobId != null)
                    jobId = _currentJobId;
                else if (_jobs.Count > 0)
                    jobId = _jobs.Values.OrderByDescending(j => j.StartedAt).First().JobId;
                else
                    return new Dictionary<string, object> { { "error", "No test jobs found" } };
            }

            if (!_jobs.TryGetValue(jobId, out var job))
            {
                return new Dictionary<string, object>
                {
                    { "error", $"Job '{jobId}' not found" },
                    { "availableJobs", _jobs.Keys.ToArray() }
                };
            }

            bool includeDetails = args.ContainsKey("includeDetails") && Convert.ToBoolean(args["includeDetails"]);
            bool includeFailedOnly = args.ContainsKey("includeFailedOnly") && Convert.ToBoolean(args["includeFailedOnly"]);

            return SerializeJob(job, includeDetails, includeFailedOnly);
        }

        /// <summary>
        /// List available tests (discovery).
        /// Route: testing/list-tests
        /// </summary>
        public static object ListTests(Dictionary<string, object> args)
        {
            string modeStr = args.ContainsKey("mode") ? args["mode"].ToString() : "EditMode";
            TestMode testMode;
            switch (modeStr.ToLowerInvariant())
            {
                case "editmode":
                case "edit":
                    testMode = TestMode.EditMode;
                    break;
                case "playmode":
                case "play":
                    testMode = TestMode.PlayMode;
                    break;
                default:
                    return new Dictionary<string, object>
                    {
                        { "error", $"Unknown test mode: {modeStr}. Use 'EditMode' or 'PlayMode'." }
                    };
            }

            string nameFilter = args.ContainsKey("nameFilter") ? args["nameFilter"].ToString() : null;
            int maxResults = args.ContainsKey("maxResults") ? Convert.ToInt32(args["maxResults"]) : 200;

            EnsureCallbacksRegistered();

            // Use RetrieveTestList to get available tests
            ITestAdaptor rootTest = null;
            _testRunnerApi.RetrieveTestList(testMode, (root) => { rootTest = root; });

            // Callback fires synchronously on main thread
            if (rootTest == null)
            {
                return new Dictionary<string, object>
                {
                    { "error", "Failed to retrieve test list. The callback may not have fired synchronously." },
                    { "mode", testMode.ToString() }
                };
            }

            var tests = new List<Dictionary<string, object>>();
            CollectLeafTests(rootTest, tests, nameFilter, maxResults);

            return new Dictionary<string, object>
            {
                { "mode", testMode.ToString() },
                { "totalTests", tests.Count },
                { "truncated", tests.Count >= maxResults },
                { "tests", tests }
            };
        }

        // ─── Test Runner Callbacks ───────────────────────────────────

        private static void EnsureCallbacksRegistered()
        {
            if (_testRunnerApi == null)
            {
                _testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            }

            if (_callbacks == null)
            {
                _callbacks = new MCPTestCallbacks();
                _testRunnerApi.RegisterCallbacks(_callbacks);
            }
        }

        internal static void OnRunStarted(int totalTests)
        {
            if (_currentJobId == null || !_jobs.TryGetValue(_currentJobId, out var job))
                return;

            job.TotalTests = totalTests;
            job.CompletedTests = 0;
            SaveToSessionState();
            Debug.Log($"[MCP TestRunner] Job {job.JobId}: Run started, {totalTests} tests to execute");
        }

        internal static void OnTestStarted(string testFullName)
        {
            if (_currentJobId == null || !_jobs.TryGetValue(_currentJobId, out var job))
                return;

            job.CurrentTestName = testFullName;
            job.CurrentTestStartedAt = DateTime.UtcNow;
        }

        internal static void OnTestFinished(string testFullName, string testName, TestStatus resultStatus,
            double durationSeconds, string message, string stackTrace)
        {
            if (_currentJobId == null || !_jobs.TryGetValue(_currentJobId, out var job))
                return;

            job.CompletedTests++;
            job.CurrentTestName = null;
            job.CurrentTestStartedAt = null;
            job.LastUpdatedAt = DateTime.UtcNow;

            var result = new TestResult
            {
                FullName = testFullName,
                Name = testName,
                Status = resultStatus.ToString(),
                Duration = durationSeconds,
                Message = message,
                StackTrace = stackTrace
            };

            job.AllResults.Add(result);

            if (resultStatus == TestStatus.Failed)
            {
                job.FailedCount++;
                if (job.FailuresSoFar.Count < MaxFailuresTracked)
                    job.FailuresSoFar.Add(result);
            }
            else if (resultStatus == TestStatus.Passed)
            {
                job.PassedCount++;
            }
            else
            {
                job.SkippedCount++;
            }
        }

        internal static void OnRunFinished(int totalPassed, int totalFailed, int totalSkipped,
            int totalInconclusive, double totalDuration)
        {
            if (_currentJobId == null || !_jobs.TryGetValue(_currentJobId, out var job))
                return;

            job.Status = totalFailed > 0 ? TestJobStatus.Failed : TestJobStatus.Succeeded;
            job.CompletedAt = DateTime.UtcNow;
            job.TotalDuration = totalDuration;
            job.CurrentTestName = null;
            job.CurrentTestStartedAt = null;

            // Restore PlayMode options if we disabled domain reload
            if (job.Mode == TestMode.PlayMode)
            {
                RestorePlayModeOptions();
            }

            _currentJobId = null;
            SaveToSessionState();

            Debug.Log($"[MCP TestRunner] Job {job.JobId}: Finished — " +
                      $"{totalPassed} passed, {totalFailed} failed, {totalSkipped} skipped " +
                      $"({totalDuration:F1}s)");
        }

        // ─── Serialization ───────────────────────────────────────────

        private static Dictionary<string, object> SerializeJob(TestJob job, bool includeDetails, bool includeFailedOnly)
        {
            var result = new Dictionary<string, object>
            {
                { "jobId", job.JobId },
                { "status", job.Status.ToString().ToLowerInvariant() },
                { "mode", job.Mode.ToString() },
                { "startedAt", job.StartedAt.ToString("O") },
            };

            // Progress info
            var progress = new Dictionary<string, object>
            {
                { "completed", job.CompletedTests },
                { "total", job.TotalTests },
                { "passed", job.PassedCount },
                { "failed", job.FailedCount },
                { "skipped", job.SkippedCount },
            };

            if (job.CurrentTestName != null)
            {
                progress["currentTest"] = job.CurrentTestName;
                if (job.CurrentTestStartedAt.HasValue)
                {
                    double elapsed = (DateTime.UtcNow - job.CurrentTestStartedAt.Value).TotalSeconds;
                    progress["currentTestElapsed"] = Math.Round(elapsed, 1);
                    progress["stuckSuspected"] = elapsed > StuckThresholdSeconds;
                }
            }

            if (job.FailuresSoFar.Count > 0)
            {
                progress["failuresSoFar"] = job.FailuresSoFar.Select(f => new Dictionary<string, object>
                {
                    { "name", f.Name },
                    { "fullName", f.FullName },
                    { "message", f.Message ?? "" },
                }).ToList();
            }

            // Blocked reason detection
            if (job.Status == TestJobStatus.Running)
            {
                if (EditorApplication.isCompiling)
                    progress["blockedReason"] = "compiling";
                else if (!UnityEditorInternal.InternalEditorUtility.isApplicationActive)
                    progress["blockedReason"] = "editor_unfocused";
            }

            result["progress"] = progress;

            if (job.CompletedAt.HasValue)
            {
                result["completedAt"] = job.CompletedAt.Value.ToString("O");
                result["totalDuration"] = Math.Round(job.TotalDuration, 2);
            }

            if (job.Error != null)
                result["error"] = job.Error;

            // Summary
            result["summary"] = new Dictionary<string, object>
            {
                { "total", job.TotalTests },
                { "passed", job.PassedCount },
                { "failed", job.FailedCount },
                { "skipped", job.SkippedCount },
                { "duration", Math.Round(job.TotalDuration, 2) }
            };

            // Detailed results
            if (includeDetails || includeFailedOnly)
            {
                var tests = job.AllResults;
                if (includeFailedOnly)
                    tests = tests.Where(t => t.Status == "Failed" || t.Status == "Inconclusive").ToList();

                result["tests"] = tests.Select(t => new Dictionary<string, object>
                {
                    { "name", t.Name },
                    { "fullName", t.FullName },
                    { "status", t.Status },
                    { "duration", Math.Round(t.Duration, 3) },
                    { "message", t.Message ?? "" },
                    { "stackTrace", t.StackTrace ?? "" }
                }).ToList();
            }

            return result;
        }

        // ─── Test Discovery Helpers ──────────────────────────────────

        private static void CollectLeafTests(ITestAdaptor test, List<Dictionary<string, object>> results,
            string nameFilter, int maxResults)
        {
            if (results.Count >= maxResults) return;

            if (!test.HasChildren)
            {
                // Leaf test
                if (nameFilter != null && !test.FullName.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                    return;

                results.Add(new Dictionary<string, object>
                {
                    { "name", test.Name },
                    { "fullName", test.FullName },
                    { "categories", test.Categories?.ToArray() ?? Array.Empty<string>() },
                    { "runState", test.RunState.ToString() }
                });
            }
            else
            {
                foreach (var child in test.Children)
                {
                    CollectLeafTests(child, results, nameFilter, maxResults);
                    if (results.Count >= maxResults) break;
                }
            }
        }

        // ─── Session State Persistence ───────────────────────────────

        private const string SessionKey = "MCPTestRunner_Jobs";
        private const string SessionCurrentKey = "MCPTestRunner_CurrentJobId";

        private static void SaveToSessionState()
        {
            try
            {
                // Serialize minimal state for surviving domain reloads
                var jobList = new List<Dictionary<string, object>>();
                foreach (var kv in _jobs)
                {
                    var j = kv.Value;
                    jobList.Add(new Dictionary<string, object>
                    {
                        { "jobId", j.JobId },
                        { "mode", j.Mode.ToString() },
                        { "status", j.Status.ToString() },
                        { "startedAt", j.StartedAt.ToString("O") },
                        { "completedAt", j.CompletedAt?.ToString("O") ?? "" },
                        { "totalTests", j.TotalTests },
                        { "completedTests", j.CompletedTests },
                        { "passedCount", j.PassedCount },
                        { "failedCount", j.FailedCount },
                        { "skippedCount", j.SkippedCount },
                        { "totalDuration", j.TotalDuration },
                        { "error", j.Error ?? "" }
                    });
                }

                string json = MiniJson.Serialize(jobList);
                SessionState.SetString(SessionKey, json);
                SessionState.SetString(SessionCurrentKey, _currentJobId ?? "");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP TestRunner] Failed to save session state: {ex.Message}");
            }
        }

        private static void RestoreFromSessionState()
        {
            try
            {
                string json = SessionState.GetString(SessionKey, "");
                if (string.IsNullOrEmpty(json)) return;

                _currentJobId = SessionState.GetString(SessionCurrentKey, null);
                if (string.IsNullOrEmpty(_currentJobId)) _currentJobId = null;

                var jobList = MiniJson.Deserialize(json) as List<object>;
                if (jobList == null) return;

                foreach (var obj in jobList)
                {
                    var dict = obj as Dictionary<string, object>;
                    if (dict == null) continue;

                    var job = new TestJob
                    {
                        JobId = dict["jobId"].ToString(),
                        Mode = Enum.TryParse<TestMode>(dict["mode"].ToString(), out var m) ? m : TestMode.EditMode,
                        Status = Enum.TryParse<TestJobStatus>(dict["status"].ToString(), out var s)
                            ? s
                            : TestJobStatus.Failed,
                        TotalTests = Convert.ToInt32(dict["totalTests"]),
                        CompletedTests = Convert.ToInt32(dict["completedTests"]),
                        PassedCount = Convert.ToInt32(dict["passedCount"]),
                        FailedCount = Convert.ToInt32(dict["failedCount"]),
                        SkippedCount = Convert.ToInt32(dict["skippedCount"]),
                        TotalDuration = Convert.ToDouble(dict["totalDuration"]),
                    };

                    if (DateTime.TryParse(dict["startedAt"].ToString(), out var started))
                        job.StartedAt = started;
                    if (!string.IsNullOrEmpty(dict["completedAt"]?.ToString()) &&
                        DateTime.TryParse(dict["completedAt"].ToString(), out var completed))
                        job.CompletedAt = completed;
                    if (!string.IsNullOrEmpty(dict["error"]?.ToString()))
                        job.Error = dict["error"].ToString();

                    // If job was running but survived a domain reload, mark as failed
                    if (job.Status == TestJobStatus.Running)
                    {
                        var elapsed = (DateTime.UtcNow - job.StartedAt).TotalMinutes;
                        if (elapsed > 5)
                        {
                            job.Status = TestJobStatus.Failed;
                            job.Error = "Job became stale after domain reload";
                            job.CompletedAt = DateTime.UtcNow;
                            if (_currentJobId == job.JobId)
                                _currentJobId = null;
                        }
                    }

                    _jobs[job.JobId] = job;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP TestRunner] Failed to restore session state: {ex.Message}");
            }
        }

        private static void CleanupExpiredJobs()
        {
            var expired = _jobs.Values
                .Where(j => j.Status != TestJobStatus.Running
                            && j.CompletedAt.HasValue
                            && (DateTime.UtcNow - j.CompletedAt.Value).TotalMinutes > JobExpiryMinutes)
                .Select(j => j.JobId)
                .ToList();

            foreach (var id in expired)
                _jobs.Remove(id);
        }

        // ─── PlayMode Domain Reload Guard ────────────────────────────

        /// <summary>
        /// Save current EnterPlayModeOptions and disable domain reload.
        /// This prevents Unity from destroying our callbacks when entering Play Mode.
        /// </summary>
        private static void SaveAndDisableDomainReload()
        {
            // Save original settings
            SessionState.SetBool(PlayModeOriginalEnabledKey, EditorSettings.enterPlayModeOptionsEnabled);
            SessionState.SetInt(PlayModeOriginalOptionsKey, (int)EditorSettings.enterPlayModeOptions);
            SessionState.SetBool(PlayModeGuardKey, true);

            // Enable enter play mode options with domain reload disabled
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EditorSettings.enterPlayModeOptions | EnterPlayModeOptions.DisableDomainReload;

            Debug.Log("[MCP TestRunner] Disabled domain reload for PlayMode tests");
        }

        /// <summary>
        /// Restore original EnterPlayModeOptions after PlayMode tests complete.
        /// </summary>
        private static void RestorePlayModeOptions()
        {
            if (!SessionState.GetBool(PlayModeGuardKey, false))
                return;

            bool originalEnabled = SessionState.GetBool(PlayModeOriginalEnabledKey, false);
            int originalOptions = SessionState.GetInt(PlayModeOriginalOptionsKey, 0);

            EditorSettings.enterPlayModeOptionsEnabled = originalEnabled;
            EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)originalOptions;

            SessionState.SetBool(PlayModeGuardKey, false);

            Debug.Log("[MCP TestRunner] Restored original EnterPlayModeOptions");
        }

        // ─── Helpers ─────────────────────────────────────────────────

        private static string[] ParseStringArray(Dictionary<string, object> args, string key)
        {
            if (!args.ContainsKey(key)) return null;

            var value = args[key];
            if (value is string str)
            {
                if (string.IsNullOrWhiteSpace(str)) return null;
                return str.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            }

            if (value is List<object> list)
                return list.Select(o => o.ToString()).Where(s => s.Length > 0).ToArray();

            return null;
        }

        // ─── Data Types ──────────────────────────────────────────────

        internal enum TestJobStatus
        {
            Running,
            Succeeded,
            Failed
        }

        internal class TestJob
        {
            public string JobId;
            public TestMode Mode;
            public TestJobStatus Status;
            public DateTime StartedAt;
            public DateTime? CompletedAt;
            public DateTime LastUpdatedAt;
            public string Error;

            // Filters used
            public string[] TestNames;
            public string[] Categories;
            public string[] Assemblies;

            // Progress
            public int TotalTests;
            public int CompletedTests;
            public int PassedCount;
            public int FailedCount;
            public int SkippedCount;
            public double TotalDuration;

            // Current test being executed
            public string CurrentTestName;
            public DateTime? CurrentTestStartedAt;

            // Results
            public List<TestResult> AllResults = new List<TestResult>();
            public List<TestResult> FailuresSoFar = new List<TestResult>();
        }

        internal class TestResult
        {
            public string FullName;
            public string Name;
            public string Status;
            public double Duration;
            public string Message;
            public string StackTrace;
        }
    }

    /// <summary>
    /// Test Runner API callbacks that forward events to MCPTestRunnerCommands.
    /// </summary>
    internal class MCPTestCallbacks : ICallbacks
    {
        public void RunStarted(ITestAdaptor testsToRun)
        {
            int leafCount = CountLeafTests(testsToRun);
            MCPTestRunnerCommands.OnRunStarted(leafCount);
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            int passed = 0, failed = 0, skipped = 0, inconclusive = 0;
            double totalDuration = result.Duration;

            CountResults(result, ref passed, ref failed, ref skipped, ref inconclusive);

            MCPTestRunnerCommands.OnRunFinished(passed, failed, skipped, inconclusive, totalDuration);
        }

        public void TestStarted(ITestAdaptor test)
        {
            if (!test.HasChildren)
            {
                MCPTestRunnerCommands.OnTestStarted(test.FullName);
            }
        }

        public void TestFinished(ITestResultAdaptor result)
        {
            if (!result.Test.HasChildren)
            {
                MCPTestRunnerCommands.OnTestFinished(
                    result.Test.FullName,
                    result.Test.Name,
                    result.TestStatus,
                    result.Duration,
                    result.Message,
                    result.StackTrace
                );
            }
        }

        private static int CountLeafTests(ITestAdaptor test)
        {
            if (!test.HasChildren) return 1;
            int count = 0;
            foreach (var child in test.Children)
                count += CountLeafTests(child);
            return count;
        }

        private static void CountResults(ITestResultAdaptor result,
            ref int passed, ref int failed, ref int skipped, ref int inconclusive)
        {
            if (!result.Test.HasChildren)
            {
                switch (result.TestStatus)
                {
                    case TestStatus.Passed:
                        passed++;
                        break;
                    case TestStatus.Failed:
                        failed++;
                        break;
                    case TestStatus.Skipped:
                        skipped++;
                        break;
                    case TestStatus.Inconclusive:
                        inconclusive++;
                        break;
                }
            }
            else if (result.Children != null)
            {
                foreach (var child in result.Children)
                    CountResults(child, ref passed, ref failed, ref skipped, ref inconclusive);
            }
        }
    }
}
