using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static partial class MCPTestRunnerCommands
    {
        private const string SessionKey = "MCPTestRunner_Jobs";
        private const string SessionCurrentKey = "MCPTestRunner_CurrentJobId";
        private const string SnapshotPrefix = "MCPTestRunner_Job_";
        private const string EvictionsKey = "MCPTestRunner_HistoryEvictions";
        private const int MaxRetainedJobs = 128;
        private const long SnapshotBudgetBytes = 32L * 1024 * 1024;
        private static int _historyEvictions;

        [Serializable]
        private sealed class JobSnapshot
        {
            public int Version = 1;
            public string[] TestNames;
            public string[] Categories;
            public string[] Assemblies;
            public string[] GroupNames;
            public string CurrentTestName;
            public string CurrentTestStartedAt;
            public string LastUpdatedAt;
            public TestResult[] Results;
        }

        private static void SaveSnapshot(TestJob job)
        {
            if (!job.SnapshotDirty) return;
            try
            {
                var snapshot = new JobSnapshot
                {
                    TestNames = job.TestNames, Categories = job.Categories,
                    Assemblies = job.Assemblies, GroupNames = job.GroupNames,
                    CurrentTestName = job.CurrentTestName,
                    CurrentTestStartedAt = job.CurrentTestStartedAt?.ToString("O"),
                    LastUpdatedAt = job.LastUpdatedAt.ToString("O"),
                    Results = job.AllResults.ToArray()
                };
                string json = JsonUtility.ToJson(snapshot);
                SessionState.SetString(SnapshotPrefix + job.JobId, json);
                job.SnapshotBytes = Encoding.UTF8.GetByteCount(json);
                job.SnapshotVersion = 1;
                job.SnapshotDirty = false;
                job.PersistenceWarning = null;
            }
            catch (Exception error)
            {
                SessionState.EraseString(SnapshotPrefix + job.JobId);
                job.SnapshotBytes = 0;
                job.SnapshotVersion = 0;
                job.PersistenceWarning = "Detailed results could not be saved for script reload: " + error.GetBaseException().Message;
            }
        }

        private static void SaveToSessionState()
        {
            try
            {
                foreach (var job in _jobs.Values) SaveSnapshot(job);
                CleanupExpiredJobs();
                var summaries = new List<Dictionary<string, object>>(_jobs.Count);
                foreach (var job in _jobs.Values)
                    summaries.Add(new Dictionary<string, object>
                    {
                        { "jobId", job.JobId }, { "nativeRunId", job.NativeRunId ?? "" },
                        { "mode", job.Mode.ToString() }, { "status", job.Status.ToString() },
                        { "startedAt", job.StartedAt.ToString("O") }, { "completedAt", job.CompletedAt?.ToString("O") ?? "" },
                        { "totalTests", job.TotalTests }, { "completedTests", job.CompletedTests },
                        { "passedCount", job.PassedCount }, { "failedCount", job.FailedCount }, { "skippedCount", job.SkippedCount },
                        { "totalDuration", job.TotalDuration }, { "error", job.Error ?? "" },
                        { "snapshotVersion", job.SnapshotVersion }, { "snapshotBytes", job.SnapshotBytes },
                        { "persistenceWarning", job.PersistenceWarning ?? "" }, { "recoveryWarning", job.RecoveryWarning ?? "" }
                    });
                SessionState.SetString(SessionKey, MiniJson.Serialize(summaries));
                SessionState.SetString(SessionCurrentKey, _currentJobId ?? "");
                SessionState.SetInt(EvictionsKey, _historyEvictions);
            }
            catch (Exception error)
            {
                Debug.LogWarning("[MCP TestRunner] Failed to save session state: " + error.GetBaseException().Message);
            }
        }

        private static string ReadText(Dictionary<string, object> data, string key) =>
            data.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value?.ToString()) ? value.ToString() : null;

        private static DateTime? ReadTimestamp(string value) => DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed) ? parsed.ToUniversalTime() : (DateTime?)null;

        private static void RestoreSnapshot(TestJob job, Dictionary<string, object> summary)
        {
            if (!summary.TryGetValue("snapshotVersion", out var version)) return;
            try
            {
                if (Convert.ToInt32(version) == 0) return;
                string json = SessionState.GetString(SnapshotPrefix + job.JobId, "");
                var snapshot = string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<JobSnapshot>(json);
                if (Convert.ToInt32(version) != 1 || snapshot == null || snapshot.Version != 1 || snapshot.Results == null
                    || snapshot.Results.Any(result => result == null || string.IsNullOrEmpty(result.Status)))
                    throw new InvalidOperationException("Missing, damaged or unsupported job snapshot");
                job.TestNames = snapshot.TestNames; job.Categories = snapshot.Categories;
                job.Assemblies = snapshot.Assemblies; job.GroupNames = snapshot.GroupNames;
                job.CurrentTestName = string.IsNullOrEmpty(snapshot.CurrentTestName) ? null : snapshot.CurrentTestName;
                job.CurrentTestStartedAt = ReadTimestamp(snapshot.CurrentTestStartedAt);
                job.LastUpdatedAt = ReadTimestamp(snapshot.LastUpdatedAt) ?? job.StartedAt;
                job.AllResults.AddRange(snapshot.Results);
                job.FailuresSoFar.AddRange(job.AllResults.Where(result => result.Status == "Failed").Take(MaxFailuresTracked));
                job.SnapshotVersion = 1;
                job.SnapshotBytes = Encoding.UTF8.GetByteCount(json);
            }
            catch (Exception error)
            {
                job.PersistenceWarning = "Detailed results could not be restored: " + error.GetBaseException().Message;
                SessionState.EraseString(SnapshotPrefix + job.JobId);
            }
        }

        private static void RestoreFromSessionState()
        {
            _historyEvictions = SessionState.GetInt(EvictionsKey, 0);
            _currentJobId = null;
            try
            {
                var summaries = MiniJson.Deserialize(SessionState.GetString(SessionKey, "")) as List<object>;
                if (summaries == null) return;
                foreach (var record in summaries)
                {
                    try
                    {
                        var data = record as Dictionary<string, object>;
                        if (data == null || string.IsNullOrEmpty(ReadText(data, "jobId"))) continue;
                        var job = new TestJob
                        {
                            JobId = ReadText(data, "jobId"), NativeRunId = ReadText(data, "nativeRunId"),
                            Mode = (TestMode)Enum.Parse(typeof(TestMode), ReadText(data, "mode")),
                            Status = (TestJobStatus)Enum.Parse(typeof(TestJobStatus), ReadText(data, "status")),
                            StartedAt = ReadTimestamp(ReadText(data, "startedAt")) ?? DateTime.MinValue,
                            CompletedAt = ReadTimestamp(ReadText(data, "completedAt")),
                            TotalTests = Convert.ToInt32(data["totalTests"]), CompletedTests = Convert.ToInt32(data["completedTests"]),
                            PassedCount = Convert.ToInt32(data["passedCount"]), FailedCount = Convert.ToInt32(data["failedCount"]),
                            SkippedCount = Convert.ToInt32(data["skippedCount"]), TotalDuration = Convert.ToDouble(data["totalDuration"]),
                            Error = ReadText(data, "error"), PersistenceWarning = ReadText(data, "persistenceWarning"),
                            RecoveryWarning = ReadText(data, "recoveryWarning"), SnapshotDirty = false
                        };
                        RestoreSnapshot(job, data);
                        _jobs[job.JobId] = job;
                    }
                    catch (Exception error)
                    {
                        Debug.LogWarning("[MCP TestRunner] Skipped a damaged job summary: " + error.GetBaseException().Message);
                    }
                }
                string current = SessionState.GetString(SessionCurrentKey, "");
                foreach (var job in _jobs.Values.Where(job => job.Status == TestJobStatus.Running))
                {
                    bool? nativeRunning = IsPersistedNativeRunActive(job.NativeRunId, out var warning);
                    if (job.JobId == current && nativeRunning != false)
                    {
                        _currentJobId = current;
                        job.RecoveryWarning = warning;
                    }
                    else
                    {
                        job.Status = TestJobStatus.Failed;
                        job.Error = job.JobId != current ? "Job lost its active MCP session during script reload"
                            : "Unity Test Runner no longer has the native run after script reload; tests were not restarted";
                        job.CompletedAt = DateTime.UtcNow;
                        job.CurrentTestName = null;
                        job.CurrentTestStartedAt = null;
                        job.SnapshotDirty = true;
                    }
                }
                if (_currentJobId == null) RestorePlayModeOptions();
                SaveToSessionState();
            }
            catch (Exception error)
            {
                Debug.LogWarning("[MCP TestRunner] Failed to restore session state: " + error.GetBaseException().Message);
            }
        }

        private static bool? IsPersistedNativeRunActive(string nativeRunId, out string warning)
        {
            warning = null;
            try
            {
                if (string.IsNullOrEmpty(nativeRunId)) throw new InvalidOperationException("The saved job has no native run ID");
                // Serialized runs exist before Test Framework rebuilds its runner dictionary after reload.
                var holder = typeof(TestRunnerApi).Assembly.GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder", true);
                var instance = holder.GetProperty("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
                var runs = holder.GetField("TestRuns")?.GetValue(instance) as IList;
                if (runs == null) throw new InvalidOperationException("The installed Test Framework does not expose serialized native runs");
                foreach (var run in runs)
                {
                    var id = run?.GetType().GetField("guid");
                    var running = run?.GetType().GetField("isRunning");
                    if (id == null || running == null || running.FieldType != typeof(bool))
                        throw new InvalidOperationException("Unrecognized serialized native run format");
                    if ((string)id.GetValue(run) == nativeRunId) return (bool)running.GetValue(run);
                }
                return false;
            }
            catch (Exception error)
            {
                warning = "Native run recovery is uncertain: " + error.GetBaseException().Message + ". Inspect Unity Test Runner before using clearStuck.";
                return null;
            }
        }

        private static bool CleanupExpiredJobs()
        {
            bool changed = false;
            var now = DateTime.UtcNow;
            var ordered = _jobs.Values.OrderBy(job => job.StartedAt).ToList();
            var newest = ordered.LastOrDefault();
            long bytes = ordered.Sum(job => job.SnapshotBytes);
            foreach (var job in ordered)
            {
                if (job.Status == TestJobStatus.Running) continue;
                bool expired = (now - (job.CompletedAt ?? job.StartedAt)).TotalMinutes > JobExpiryMinutes;
                bool pressure = _jobs.Count > MaxRetainedJobs || bytes > SnapshotBudgetBytes;
                if (!expired && (!pressure || job == newest)) continue;
                _jobs.Remove(job.JobId);
                SessionState.EraseString(SnapshotPrefix + job.JobId);
                bytes -= job.SnapshotBytes;
                if (_historyEvictions < int.MaxValue) _historyEvictions++;
                changed = true;
            }
            return changed;
        }

        private static object SerializeHistoryRetention()
        {
            long bytes = _jobs.Values.Sum(job => job.SnapshotBytes);
            return new { retainedJobs = _jobs.Count, maxJobs = MaxRetainedJobs, expiryMinutes = JobExpiryMinutes,
                snapshotBytes = bytes, snapshotBudgetBytes = SnapshotBudgetBytes, evictions = _historyEvictions,
                overBudget = bytes > SnapshotBudgetBytes };
        }
    }
}
