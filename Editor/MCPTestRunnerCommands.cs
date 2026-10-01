using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
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
        private static readonly MethodInfo NativeRunActive = typeof(TestRunnerApi).GetMethod("IsRunActive", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo NativeCancelRun = typeof(TestRunnerApi).GetMethod("CancelTestRun", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(string) }, null);
        private static readonly Type PlayerTestAssemblyProvider = typeof(UnityEngine.TestTools.UnityTestAttribute).Assembly.GetType("UnityEngine.TestTools.Utils.PlayerTestAssemblyProvider");
        private static readonly FieldInfo ReloadClearedTestAssemblyCache = PlayerTestAssemblyProvider?.GetMethod("ResetStaticsOnLoad", BindingFlags.Static | BindingFlags.NonPublic) != null
            ? PlayerTestAssemblyProvider.GetField("m_LoadedAssemblies", BindingFlags.Static | BindingFlags.NonPublic) : null;

        private const int MaxFailuresTracked = 50;
        private const double StuckThresholdSeconds = 120.0;
        private const double JobExpiryMinutes = 30.0;

        // ─── PlayMode Domain Reload Guard ────────────────────────────
        private const string PlayModeGuardKey = "MCPTestRunner_PlayModeGuard";
        private const string PlayModeOriginalEnabledKey = "MCPTestRunner_OriginalPMOEnabled";
        private const string PlayModeOriginalOptionsKey = "MCPTestRunner_OriginalPMOOptions";

        static MCPTestRunnerCommands()
        {
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;
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
            bool clearStuck = args.ContainsKey("clearStuck") && Convert.ToBoolean(args["clearStuck"]);
            // Check if a test run is already in progress
            if (_currentJobId != null && _jobs.TryGetValue(_currentJobId, out var existing)
                && existing.Status == TestJobStatus.Running)
            {
                // Allow force-clear of stuck jobs
                if (clearStuck)
                {
                    FailJob(existing.JobId, "Force-cleared by user");
                    bool cancellationRequested = false;
                    string cancellationError = null;
                    try
                    {
                        if (NativeCancelRun != null && !string.IsNullOrEmpty(existing.NativeRunId))
                            cancellationRequested = (bool)NativeCancelRun.Invoke(null, new object[] { existing.NativeRunId });
                    }
                    catch (Exception error) { cancellationError = error.GetBaseException().Message; }
                    return new Dictionary<string, object>
                    {
                        { "success", true },
                        { "message", "Stuck job cleared" },
                        { "clearedJobId", existing.JobId },
                        { "cancellationRequested", cancellationRequested },
                        { "cancellationError", cancellationError },
                        { "nativeRunMayContinue", IsNativeRunActive(out var ignored) || ignored != null }
                    };
                }

                return new Dictionary<string, object>
                {
                    { "error", "A test run is already in progress" },
                    { "currentJobId", _currentJobId },
                    { "hint", "Use clearStuck=true to force-clear if the job appears stuck" }
                };
            }

            if (clearStuck)
                return new { success = true, message = "No active MCP test job to clear", clearedJobId = (string)null,
                    cancellationRequested = false, cancellationError = (string)null,
                    nativeRunMayContinue = IsNativeRunActive(out var clearStateError) || clearStateError != null };

            // Unity broadcasts test callbacks globally, so another native run must finish cleanup before admission.
            if (IsNativeRunActive(out var nativeStateError) || nativeStateError != null)
                return new { error = nativeStateError ?? "Unity Test Runner is still running or cleaning up a test run. Wait for it to finish before starting another job." };

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
            string modeStr = args.ContainsKey("mode") ? args["mode"]?.ToString() ?? "" : "EditMode";
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

            try
            {
                if (testMode == TestMode.PlayMode)
                {
                    SaveAndDisableDomainReload();
                    ResetPlayModeTestAssemblyCache();
                }
                EnsureCallbacksRegistered();
                var executionSettings = new ExecutionSettings(filter);
                job.NativeRunId = _testRunnerApi.Execute(executionSettings);
                SaveToSessionState();
            }
            catch (Exception error) { FailJob(job.JobId, error.GetBaseException().Message); }

            if (job.Status == TestJobStatus.Failed)
                return new { error = job.Error, jobId = job.JobId };

            Debug.Log($"[MCP TestRunner] Started test job {job.JobId} (mode={testMode})");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "jobId", job.JobId },
                { "status", job.Status.ToString().ToLowerInvariant() },
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

        // RetrieveTestList completes on a future editor update; the caller must keep the main thread free.
        public static void ListTests(Dictionary<string, object> args, Action<object> resolve)
        {
            string modeStr = args.ContainsKey("mode") ? args["mode"]?.ToString() ?? "" : "EditMode";
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
                    resolve(new Dictionary<string, object>
                    {
                        { "error", $"Unknown test mode: {modeStr}. Use 'EditMode' or 'PlayMode'." }
                    });
                    return;
            }

            string nameFilter = args.ContainsKey("nameFilter") ? args["nameFilter"]?.ToString() : null;
            int maxResults = 200;
            if ((args.TryGetValue("maxResults", out var limit)
                && (limit == null || !int.TryParse(Convert.ToString(limit, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out maxResults)))
                || maxResults < 1 || maxResults > 10000)
            {
                resolve(new { error = "maxResults must be an integer from 1 to 10000" });
                return;
            }

            EnsureCallbacksRegistered();

            _testRunnerApi.RetrieveTestList(testMode, root =>
            {
                if (root == null)
                {
                    resolve(new Dictionary<string, object>
                    {
                        { "error", "Unity returned a null test tree." },
                        { "mode", testMode.ToString() }
                    });
                    return;
                }

                var tests = new List<Dictionary<string, object>>();
                bool truncated = CollectLeafTests(root, tests, nameFilter, maxResults);

                resolve(new Dictionary<string, object>
                {
                    { "mode", testMode.ToString() },
                    { "totalTests", tests.Count },
                    { "truncated", truncated },
                    { "tests", tests }
                });
            });
        }

        // ─── Test Runner Callbacks ───────────────────────────────────

        private static void ResetPlayModeTestAssemblyCache()
        {
            // Test Framework 1.8 clears this list on Play entry, but only loads assemblies when it is null.
            if (ReloadClearedTestAssemblyCache != null && typeof(System.Collections.IList).IsAssignableFrom(ReloadClearedTestAssemblyCache.FieldType))
                ReloadClearedTestAssemblyCache.SetValue(null, null);
        }

        private static void EnsureCallbacksRegistered()
        {
            if (_testRunnerApi == null)
            {
                _testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            }

            if (_currentJobId == null || _callbacks?.JobId == _currentJobId) return;
            UnregisterCallbacks();
            _callbacks = new MCPTestCallbacks(_currentJobId);
            _testRunnerApi.RegisterCallbacks(_callbacks);
        }

        private static bool IsNativeRunActive(out string error)
        {
            error = null;
            try
            {
                if (NativeRunActive == null || NativeRunActive.ReturnType != typeof(bool))
                    throw new InvalidOperationException("The installed Test Framework does not expose native run state");
                return (bool)NativeRunActive.Invoke(null, null);
            }
            catch (Exception failure)
            {
                error = "Cannot determine whether Unity Test Runner is busy: " + failure.GetBaseException().Message;
                return false;
            }
        }

        internal static bool IsRunningJob(string jobId) => jobId != null && jobId == _currentJobId
            && _jobs.TryGetValue(jobId, out var job) && job.Status == TestJobStatus.Running;

        private static void UnregisterCallbacks()
        {
            var callbacks = _callbacks;
            _callbacks = null;
            if (_testRunnerApi != null && callbacks != null) _testRunnerApi.UnregisterCallbacks(callbacks);
        }

        private static void ReleaseJob()
        {
            _currentJobId = null;
            UnregisterCallbacks();
            RestorePlayModeOptions();
            SaveToSessionState();
        }

        internal static void FailJob(string jobId, string error)
        {
            if (!IsRunningJob(jobId)) return;
            var job = _jobs[jobId];
            job.Status = TestJobStatus.Failed;
            job.Error = error;
            job.CompletedAt = DateTime.UtcNow;
            job.CurrentTestName = null;
            job.CurrentTestStartedAt = null;
            ReleaseJob();
        }

        private static void BeforeAssemblyReload()
        {
            SaveToSessionState();
            UnregisterCallbacks();
            if (_testRunnerApi != null) UnityEngine.Object.DestroyImmediate(_testRunnerApi);
            _testRunnerApi = null;
        }

        private static void OnEditorQuitting()
        {
            RestorePlayModeOptions();
            BeforeAssemblyReload();
        }

        internal static void OnRunStarted(string jobId, int totalTests)
        {
            if (!IsRunningJob(jobId)) return;
            var job = _jobs[jobId];

            job.TotalTests = totalTests;
            job.CompletedTests = 0;
            SaveToSessionState();
            Debug.Log($"[MCP TestRunner] Job {job.JobId}: Run started, {totalTests} tests to execute");
        }

        internal static void OnTestStarted(string jobId, string testFullName)
        {
            if (!IsRunningJob(jobId)) return;
            var job = _jobs[jobId];

            job.CurrentTestName = testFullName;
            job.CurrentTestStartedAt = DateTime.UtcNow;
        }

        internal static void OnTestFinished(string jobId, string testFullName, string testName, TestStatus resultStatus,
            double durationSeconds, string message, string stackTrace)
        {
            if (!IsRunningJob(jobId)) return;
            var job = _jobs[jobId];

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

        internal static void OnRunFinished(string jobId, int totalPassed, int totalFailed, int totalSkipped,
            int totalInconclusive, double totalDuration)
        {
            if (!IsRunningJob(jobId)) return;
            var job = _jobs[jobId];

            job.PassedCount = totalPassed;
            job.FailedCount = totalFailed;
            job.SkippedCount = totalSkipped + totalInconclusive;
            job.CompletedTests = job.PassedCount + job.FailedCount + job.SkippedCount;
            job.TotalTests = Math.Max(job.TotalTests, job.CompletedTests);
            job.Status = totalFailed > 0 || job.Error != null ? TestJobStatus.Failed : TestJobStatus.Succeeded;
            job.CompletedAt = DateTime.UtcNow;
            job.TotalDuration = totalDuration;
            job.CurrentTestName = null;
            job.CurrentTestStartedAt = null;

            ReleaseJob();

            Debug.Log($"[MCP TestRunner] Job {job.JobId}: Finished — " +
                      $"{totalPassed} passed, {totalFailed} failed, {totalSkipped} skipped " +
                      $"({totalDuration:F1}s)");
        }

        internal static void ReconcileResults(string jobId, ITestResultAdaptor root)
        {
            if (!IsRunningJob(jobId)) return;
            // The final tree can recover results missed during callback delivery or reload.
            var job = _jobs[jobId];
            job.FailuresSoFar.Clear();
            int resultCount = 0;
            CollectResults(job, root, ref resultCount);
            if (resultCount < job.AllResults.Count) job.AllResults.RemoveRange(resultCount, job.AllResults.Count - resultCount);
            job.LastUpdatedAt = DateTime.UtcNow;
            if (root.TestStatus == TestStatus.Failed && root.FailCount == 0)
                job.Error = string.IsNullOrEmpty(root.Message) ? "Unity reported a suite-level failure" : root.Message;
        }

        private static void CollectResults(TestJob job, ITestResultAdaptor result, ref int resultCount)
        {
            if (!result.Test.IsSuite)
            {
                TestResult entry;
                if (resultCount < job.AllResults.Count) entry = job.AllResults[resultCount];
                else { entry = new TestResult(); job.AllResults.Add(entry); }
                resultCount++;
                entry.FullName = result.Test.FullName;
                entry.Name = result.Test.Name;
                entry.Status = result.TestStatus.ToString();
                entry.Duration = result.Duration;
                entry.Message = result.Message;
                entry.StackTrace = result.StackTrace;
                if (result.TestStatus == TestStatus.Failed && job.FailuresSoFar.Count < MaxFailuresTracked)
                    job.FailuresSoFar.Add(entry);
            }
            else if (result.Children != null)
                foreach (var child in result.Children) CollectResults(job, child, ref resultCount);
        }

        private static Dictionary<string, object> SerializeJob(TestJob job, bool includeDetails, bool includeFailedOnly)
        {
            var result = new Dictionary<string, object>
            {
                { "jobId", job.JobId },
                { "status", job.Status.ToString().ToLowerInvariant() },
                { "mode", job.Mode.ToString() },
                { "startedAt", job.StartedAt.ToString("O") },
                { "resultsComplete", job.AllResults.Count == job.CompletedTests },
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

        private static bool CollectLeafTests(ITestAdaptor test, List<Dictionary<string, object>> results,
            string nameFilter, int maxResults)
        {
            if (!test.IsSuite)
            {
                // Leaf test
                if (nameFilter != null && test.FullName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

                if (results.Count >= maxResults) return true;

                results.Add(new Dictionary<string, object>
                {
                    { "name", test.Name },
                    { "fullName", test.FullName },
                    { "categories", test.Categories?.ToArray() ?? Array.Empty<string>() },
                    { "runState", test.RunState.ToString() }
                });
            }
            else if (test.Children != null)
            {
                foreach (var child in test.Children)
                {
                    if (CollectLeafTests(child, results, nameFilter, maxResults)) return true;
                }
            }
            return false;
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
                        { "nativeRunId", j.NativeRunId ?? "" },
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
                        NativeRunId = dict.TryGetValue("nativeRunId", out var nativeRunId) ? nativeRunId?.ToString() : null,
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

                    if (DateTime.TryParse(dict["startedAt"].ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started))
                        job.StartedAt = started.ToUniversalTime();
                    if (!string.IsNullOrEmpty(dict["completedAt"]?.ToString()) &&
                        DateTime.TryParse(dict["completedAt"].ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var completed))
                        job.CompletedAt = completed.ToUniversalTime();
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
            public string NativeRunId;
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
    internal class MCPTestCallbacks : IErrorCallbacks
    {
        internal string JobId { get; }

        internal MCPTestCallbacks(string jobId) { JobId = jobId; }

        public void OnError(string message) => MCPTestRunnerCommands.FailJob(JobId, message);

        public void RunStarted(ITestAdaptor testsToRun)
        {
            if (!MCPTestRunnerCommands.IsRunningJob(JobId)) return;
            int leafCount = CountLeafTests(testsToRun);
            MCPTestRunnerCommands.OnRunStarted(JobId, leafCount);
        }

        public void RunFinished(ITestResultAdaptor result)
        {
            if (!MCPTestRunnerCommands.IsRunningJob(JobId)) return;
            MCPTestRunnerCommands.ReconcileResults(JobId, result);
            MCPTestRunnerCommands.OnRunFinished(JobId, result.PassCount, result.FailCount, result.SkipCount, result.InconclusiveCount, result.Duration);
        }

        public void TestStarted(ITestAdaptor test)
        {
            if (!MCPTestRunnerCommands.IsRunningJob(JobId)) return;
            if (!test.IsSuite)
            {
                MCPTestRunnerCommands.OnTestStarted(JobId, test.FullName);
            }
        }

        public void TestFinished(ITestResultAdaptor result)
        {
            if (!MCPTestRunnerCommands.IsRunningJob(JobId)) return;
            if (!result.Test.IsSuite)
            {
                MCPTestRunnerCommands.OnTestFinished(
                    JobId,
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
            if (!test.IsSuite) return 1;
            int count = 0;
            if (test.Children != null)
                foreach (var child in test.Children) count += CountLeafTests(child);
            return count;
        }
    }
}
