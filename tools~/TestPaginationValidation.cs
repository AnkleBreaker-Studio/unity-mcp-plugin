using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;
using Unity.Profiling;

public static class UnityMcpTestPaginationValidation
{
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Commands = typeof(MCPTestRunnerCommands);
    private static readonly Type JobType = Commands.GetNestedType("TestJob", BindingFlags.NonPublic);
    private static readonly Type ResultType = Commands.GetNestedType("TestResult", BindingFlags.NonPublic);
    private static IDictionary Jobs => (IDictionary)Commands.GetField("_jobs", Hidden).GetValue(null);
    private static readonly List<string> Owned = new List<string>();
    private static void Set(object target, string name, object value) => target.GetType().GetField(name).SetValue(target, value);
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static object Field(object target, string name) => target.GetType().GetField(name).GetValue(target);
    public static string CreateJob(int count, bool running = false, bool mixed = false)
    {
        Require(File.Exists(".unity-mcp-validation"), "Use a marked disposable validation project");
        var job = Activator.CreateInstance(JobType); string id = "pagination-fixture-" + Guid.NewGuid().ToString("N");
        Set(job, "JobId", id); Set(job, "Status", Enum.Parse(JobType.GetField("Status").FieldType, running ? "Running" : "Succeeded"));
        Set(job, "StartedAt", DateTime.UtcNow); if (!running) Set(job, "CompletedAt", (DateTime?)DateTime.UtcNow);
        Set(job, "TotalTests", count); Set(job, "CompletedTests", count); Set(job, "SnapshotDirty", false);
        var results = (IList)Field(job, "AllResults");
        for (int i = 0; i < count; i++)
        {
            var item = Activator.CreateInstance(ResultType);
            Set(item, "Name", "Case" + i); Set(item, "FullName", "Pagination.Case" + i);
            Set(item, "Status", mixed ? new[] { "Passed", "Failed", "Inconclusive", "Skipped" }[i % 4] : "Passed");
            Set(item, "Message", i == 0 ? "Unicode \u754c \ud83d\ude80" : "message-" + i);
            Set(item, "StackTrace", "stack-" + i); Set(item, "Duration", i / 1000.0); results.Add(item);
        }
        Jobs.Add(id, job); Owned.Add(id); return id;
    }
    public static void RemoveJob(string id) { Jobs.Remove(id); Owned.Remove(id); }
    private static Dictionary<string, object> Read(string id, params object[] pairs)
    {
        var args = new Dictionary<string, object> { { "jobId", id } };
        for (int i = 0; i < pairs.Length; i += 2) args.Add((string)pairs[i], pairs[i + 1]);
        return (Dictionary<string, object>)MCPTestRunnerCommands.GetTestJob(args);
    }
    private static Dictionary<string, object> Page(Dictionary<string, object> result)
    {
        Require(result.ContainsKey("resultPage"), "Missing resultPage metadata"); return (Dictionary<string, object>)result["resultPage"];
    }
    private static IList Tests(Dictionary<string, object> result) => (IList)result["tests"];
    private static ProfilerRecorder Allocations() => ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1,
        ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
    private static object Measure(int count)
    {
        string id = CreateJob(count); for (int i = 0; i < 5; i++) Read(id, "includeDetails", true, "resultLimit", 20);
        var times = new List<double>(); var counts = new List<long>(); int returned = 0;
        for (int i = 0; i < 11; i++)
        {
            var watch = new Stopwatch();
            using (var recorder = Allocations())
            {
                watch.Start(); var result = Read(id, "includeDetails", true, "resultLimit", 20); watch.Stop(); recorder.Stop();
                Require(recorder.Valid, "Allocation recorder unavailable");
                counts.Add(recorder.Count > 0 ? recorder.GetSample(0).Count : 0); returned = Tests(result).Count;
            }
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        times.Sort(); counts.Sort(); RemoveJob(id);
        return new { stored = count, requested = 20, returned, medianAllocationEvents = counts[5], medianMilliseconds = times[5] };
    }
    public static object Validate()
    {
        Require(File.Exists(".unity-mcp-validation"), "Use a marked disposable validation project");
        var checks = new List<object>(); int failures = 0;
        Action<string, Func<object>> check = (name, action) => {
            try { checks.Add(new { name, passed = true, evidence = action() }); }
            catch (Exception error) { failures++; checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
            finally { foreach (string id in Owned.ToArray()) RemoveJob(id); }
        };
        check("Unpaged callers retain full details and summary-only responses", () => {
            string id = CreateJob(35); var full = Read(id, "includeDetails", true); var summary = Read(id);
            Require(Tests(full).Count == 35 && !full.ContainsKey("resultPage") && !summary.ContainsKey("tests"), "Legacy response changed");
            Require((string)((Dictionary<string, object>)Tests(full)[0])["message"] == "Unicode \u754c \ud83d\ude80", "Diagnostic text changed"); return true;
        });
        check("Requested result limit bounds materialized details", () => {
            string id = CreateJob(5000); var result = Read(id, "includeDetails", true, "resultLimit", 20);
            Require(Tests(result).Count == 20, "Returned " + Tests(result).Count + " details for a 20-result page");
            var page = Page(result); Require((int)page["total"] == 5000 && (int)page["nextOffset"] == 20 && (bool)page["hasMore"] && (bool)page["stable"], "Page metadata disagrees");
            Require((bool)result["resultsComplete"], "Paging changed native completeness"); return page;
        });
        check("Successive pages enumerate every result once with unchanged records", () => {
            string id = CreateJob(23); var all = Tests(Read(id, "includeDetails", true)); int offset = 0, visited = 0;
            do {
                var result = Read(id, "resultOffset", offset, "resultLimit", 7); var page = Page(result);
                foreach (var item in Tests(result)) Require(MiniJson.Serialize(item) == MiniJson.Serialize(all[visited++]), "Page reordered or altered a test result");
                if (!(bool)page["hasMore"]) { Require(page["nextOffset"] == null, "Terminal page retained continuation"); break; }
                offset = (int)page["nextOffset"];
            } while (visited < 30);
            Require(visited == 23, "A result was missed or repeated"); return new { visited };
        });
        check("Failure filtering applies before page offsets", () => {
            string id = CreateJob(20, mixed: true); var result = Read(id, "includeFailedOnly", true, "resultOffset", 3, "resultLimit", 2);
            var tests = Tests(result); var page = Page(result);
            Require(tests.Count == 2 && (string)((Dictionary<string, object>)tests[0])["fullName"] == "Pagination.Case6"
                && (string)((Dictionary<string, object>)tests[1])["fullName"] == "Pagination.Case9" && (int)page["total"] == 10, "Filtered offsets or total changed"); return page;
        });
        check("Empty, exact-end and extreme offsets have no continuation", () => {
            foreach (int count in new[] { 0, 20 }) foreach (int offset in new[] { count, int.MaxValue }) {
                var result = Read(CreateJob(count), "resultOffset", offset, "resultLimit", 20); var page = Page(result);
                Require(Tests(result).Count == 0 && !(bool)page["hasMore"] && page["nextOffset"] == null, "Past-end page overflowed");
            }
            var exact = Page(Read(CreateJob(20), "resultLimit", 20)); Require(!(bool)exact["hasMore"] && exact["nextOffset"] == null, "Exact limit was truncated"); return true;
        });
        check("Offset alone opts into the default 200-result page", () => {
            var result = Read(CreateJob(250), "resultOffset", 0); var page = Page(result);
            Require(Tests(result).Count == 200 && (int)page["limit"] == 200 && (int)page["nextOffset"] == 200, "Missing default result page"); return page;
        });
        check("Invalid pagination returns command errors", () => {
            string id = CreateJob(1); int rejected = 0;
            foreach (string key in new[] { "resultOffset", "resultLimit" }) foreach (object value in new object[] { null, -1, true, 1.5, "bad", double.NaN, double.PositiveInfinity, 2147483648L }) {
                var result = Read(id, key, value); Require(result.ContainsKey("error") && !result.ContainsKey("tests"), "Accepted invalid " + key + ": " + value); rejected++;
            }
            foreach (int value in new[] { 0, 10001 }) { Require(Read(id, "resultLimit", value).ContainsKey("error"), "Accepted invalid limit"); rejected++; }
            return new { rejected };
        });
        check("Running and incomplete results keep their separate meanings", () => {
            string id = CreateJob(5, running: true); var result = Read(id, "resultLimit", 2);
            Require(!(bool)Page(result)["stable"] && (bool)result["resultsComplete"] && (string)result["status"] == "running", "Running page claimed stability");
            Set(Jobs[id], "CompletedTests", 7); result = Read(id, "resultLimit", 2);
            Require(!(bool)result["resultsComplete"] && (int)Page(result)["total"] == 5, "Unavailable details were counted as returned records"); return true;
        });
        var measurements = new List<object>(); long controlCount;
        using (var recorder = Allocations()) { var control = new byte[1024]; recorder.Stop(); GC.KeepAlive(control); controlCount = recorder.Count > 0 ? recorder.GetSample(0).Count : 0; }
        foreach (int count in new[] { 100, 1000, 10000 }) measurements.Add(Measure(count));
        Require(controlCount > 0, "Allocation positive control failed");
        return new { unityVersion = Application.unityVersion, passed = failures == 0, checks, measurements, allocationControlEvents = controlCount,
            measurement = "Median of 11 warmed-up GetTestJob calls; result construction only, not JSON serialization, native discovery or the whole editor frame. GC.Alloc sample Count measures allocation events, not bytes." };
    }
    public static void Run()
    {
        var report = Validate(); File.WriteAllText("Library/UnityMcpTestPaginationValidation.json", MiniJson.Serialize(report));
        EditorApplication.Exit((bool)report.GetType().GetProperty("passed").GetValue(report) ? 0 : 1);
    }
}
