using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityMCP.Editor;

public static class UnityMcpTestResultsValidation
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type Commands = typeof(MCPTestRunnerCommands);
    private static readonly List<object> Checks = new List<object>();
    private static FieldInfo Field(string name) => Commands.GetField(name, PrivateStatic);
    private static object Invoke(string name, params object[] args) => Commands.GetMethod(name, PrivateStatic).Invoke(null, args);
    private static void Set(object instance, string name, object value) => instance.GetType().GetField("<" + name + ">k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value);
    private static ITestAdaptor Node(string name, bool suite, params ITestAdaptor[] children)
    {
        var node = (ITestAdaptor)FormatterServices.GetUninitializedObject(typeof(TestRunnerApi).Assembly.GetType("UnityEditor.TestTools.TestRunner.Api.TestAdaptor", true));
        Set(node, "Name", name); Set(node, "FullName", name); Set(node, "IsSuite", suite);
        Set(node, "HasChildren", children.Length > 0); Set(node, "Children", children); Set(node, "Categories", Array.Empty<string>());
        Set(node, "TestCaseCount", suite ? children.Length : 1);
        return node;
    }
    private static ITestResultAdaptor Result(ITestAdaptor test, TestStatus status, params ITestResultAdaptor[] children)
    {
        var result = (ITestResultAdaptor)FormatterServices.GetUninitializedObject(typeof(TestRunnerApi).Assembly.GetType("UnityEditor.TestTools.TestRunner.Api.TestResultAdaptor", true));
        Set(result, "Test", test); Set(result, "TestStatus", status); Set(result, "Children", children);
        Set(result, "HasChildren", children.Length > 0); Set(result, "Name", test.Name); Set(result, "FullName", test.FullName);
        Set(result, "Message", status == TestStatus.Failed ? "Controlled result failure" : "");
        Set(result, "StackTrace", status == TestStatus.Failed ? "Controlled fixture stack" : "");
        int passed = 0, failed = 0, skipped = 0, inconclusive = 0;
        if (!test.IsSuite) { passed = status == TestStatus.Passed ? 1 : 0; failed = status == TestStatus.Failed ? 1 : 0; skipped = status == TestStatus.Skipped ? 1 : 0; inconclusive = status == TestStatus.Inconclusive ? 1 : 0; }
        foreach (var child in children) { passed += child.PassCount; failed += child.FailCount; skipped += child.SkipCount; inconclusive += child.InconclusiveCount; }
        Set(result, "PassCount", passed); Set(result, "FailCount", failed); Set(result, "SkipCount", skipped); Set(result, "InconclusiveCount", inconclusive);
        return result;
    }
    private static void Check(string name, Func<object> action)
    {
        try { Checks.Add(new { name, passed = true, evidence = action() }); }
        catch (Exception error) { Checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
        finally { Reset(); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static ICallbacks Begin()
    {
        Invoke("EnsureCallbacksRegistered");
        var api = (TestRunnerApi)Field("_testRunnerApi").GetValue(null);
        typeof(TestRunnerApi).GetField("ScheduleJob", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(api, (Func<ExecutionSettings, string>)(settings => "controlled-results"));
        MCPTestRunnerCommands.RunTests(new Dictionary<string, object>());
        return (ICallbacks)Field("_callbacks").GetValue(null);
    }
    public static void Run()
    {
        Check("Empty suites are not discovered as tests", () => {
            var list = new List<Dictionary<string, object>>();
            Invoke("CollectLeafTests", Node("EmptySuite", true), list, null, 10);
            Require(list.Count == 0, "Empty suite returned " + list.Count + " test(s)"); return new { count = list.Count };
        });
        Check("Empty suites contribute no progress or result entries", () => {
            var callback = Begin(); var root = Node("EmptySuite", true);
            callback.RunStarted(root); callback.TestStarted(root); callback.TestFinished(Result(root, TestStatus.Passed)); callback.RunFinished(Result(root, TestStatus.Passed));
            var job = (Dictionary<string, object>)MCPTestRunnerCommands.GetTestJob(new Dictionary<string, object> { { "includeDetails", true } });
            var summary = (Dictionary<string, object>)job["summary"];
            Require(Convert.ToInt32(summary["total"]) == 0 && Convert.ToInt32(summary["passed"]) == 0 && ((IList)job["tests"]).Count == 0, MiniJson.Serialize(job)); return job;
        });
        Check("Final tree reconciles missed and duplicate callbacks", () => {
            var callback = Begin(); var nodes = new[] { Node("Pass", false), Node("Fail", false), Node("Skip", false), Node("Inconclusive", false) };
            var root = Node("Suite", true, nodes); var results = new[] { Result(nodes[0], TestStatus.Passed), Result(nodes[1], TestStatus.Failed), Result(nodes[2], TestStatus.Skipped), Result(nodes[3], TestStatus.Inconclusive) };
            callback.RunStarted(root); callback.TestFinished(results[0]); callback.TestFinished(results[0]); callback.RunFinished(Result(root, TestStatus.Failed, results));
            var job = (Dictionary<string, object>)MCPTestRunnerCommands.GetTestJob(new Dictionary<string, object> { { "includeDetails", true } }); var summary = (Dictionary<string, object>)job["summary"]; var progress = (Dictionary<string, object>)job["progress"];
            Require((string)job["status"] == "failed" && Convert.ToInt32(summary["total"]) == 4 && Convert.ToInt32(summary["passed"]) == 1 && Convert.ToInt32(summary["failed"]) == 1 && Convert.ToInt32(summary["skipped"]) == 2 && Convert.ToInt32(progress["completed"]) == 4 && ((IList)job["tests"]).Count == 4, MiniJson.Serialize(job));
            var details = (IList)job["tests"];
            for (int index = 0; index < results.Length; index++) {
                var detail = (Dictionary<string, object>)details[index];
                Require((string)detail["fullName"] == results[index].FullName && (string)detail["status"] == results[index].TestStatus.ToString() && (string)detail["message"] == results[index].Message, MiniJson.Serialize(detail));
            }
            var failures = (IList)progress["failuresSoFar"];
            Require(failures.Count == 1 && (string)((Dictionary<string, object>)failures[0])["fullName"] == "Fail", MiniJson.Serialize(failures)); return job;
        });
        Check("Suite-level failure remains failed without leaf failures", () => {
            var callback = Begin(); var root = Node("EmptyFailedSuite", true); callback.RunStarted(root); callback.RunFinished(Result(root, TestStatus.Failed));
            var job = (Dictionary<string, object>)MCPTestRunnerCommands.GetTestJob(new Dictionary<string, object>());
            Require((string)job["status"] == "failed", MiniJson.Serialize(job)); return job;
        });
        Check("Normal success retains its counts and result", () => {
            var callback = Begin(); var leaf = Node("Pass", false); var root = Node("Suite", true, leaf); var result = Result(leaf, TestStatus.Passed); callback.RunStarted(root); callback.TestFinished(result); callback.RunFinished(Result(root, TestStatus.Passed, result));
            var job = (Dictionary<string, object>)MCPTestRunnerCommands.GetTestJob(new Dictionary<string, object> { { "includeDetails", true } }); var summary = (Dictionary<string, object>)job["summary"];
            Require((string)job["status"] == "succeeded" && Convert.ToInt32(summary["passed"]) == 1 && ((IList)job["tests"]).Count == 1, MiniJson.Serialize(job)); return job;
        });
        Check("Discovery reports truncation only for another matching test", () => {
            var root = Node("Suite", true, Node("other", false), Node("MATCH.one", false), Node("Empty", true), Node("match.two", false), Node("OtherAgain", false), Node("Match.three", false));
            for (int limit = 1; limit <= 4; limit++) {
                var list = new List<Dictionary<string, object>>();
                bool truncated = (bool)Invoke("CollectLeafTests", root, list, "match.", limit);
                Require(list.Count == Math.Min(3, limit) && truncated == (limit < 3), "Wrong matching count or truncation at " + limit);
            }
            return new { limits = new[] { 1, 2, 3, 4 } };
        });
        Check("Discovery stops after the first matching overflow", () => {
            var root = Node("Suite", true); Set(root, "Children", BoundedChildren()); Set(root, "HasChildren", true);
            var list = new List<Dictionary<string, object>>();
            Require((bool)Invoke("CollectLeafTests", root, list, null, 2) && list.Count == 2, "Bounded traversal did not report truncation");
            return new { returned = 2, examined = 3 };
        });
        Check("Invalid discovery limits fail before native discovery", () => {
            foreach (var limit in new object[] { null, 0, -1, 10001, 1.5, true, "invalid", double.NaN, double.PositiveInfinity }) {
                object response = null; MCPTestRunnerCommands.ListTests(new Dictionary<string, object> { { "maxResults", limit } }, result => response = result);
                Require(response != null && MiniJson.Serialize(response).Contains("maxResults must be an integer"), "Accepted invalid limit: " + limit);
            }
            Require(Field("_testRunnerApi").GetValue(null) == null, "Invalid discovery created native API state");
            return new { rejected = 9 };
        });
        Check("Null discovery mode returns a command error", () => {
            object response = null; MCPTestRunnerCommands.ListTests(new Dictionary<string, object> { { "mode", null } }, result => response = result);
            Require(response != null && MiniJson.Serialize(response).Contains("Unknown test mode"), "Null mode did not return an actionable error"); return response;
        });
        Check("Missing native result details are explicitly incomplete", () => {
            var callback = Begin(); var root = Node("Suite", true); var result = Result(root, TestStatus.Passed); Set(result, "PassCount", 3);
            callback.RunStarted(root); callback.RunFinished(result);
            var job = (Dictionary<string, object>)MCPTestRunnerCommands.GetTestJob(new Dictionary<string, object> { { "includeDetails", true } });
            Require(Convert.ToInt32(((Dictionary<string,object>)job["summary"])["passed"]) == 3 && !(bool)job["resultsComplete"] && ((IList)job["tests"]).Count == 0, MiniJson.Serialize(job)); return job;
        });
        bool passed = true;
        foreach (var check in Checks) passed &= (bool)check.GetType().GetProperty("passed").GetValue(check);
        File.WriteAllText("Library/UnityMcpTestResultsValidation.json", MiniJson.Serialize(new { unityVersion = Application.unityVersion, passed, nativeTestsLaunched = false, checks = Checks }));
        EditorApplication.Exit(passed ? 0 : 1);
    }
    private static IEnumerable<ITestAdaptor> BoundedChildren()
    {
        yield return Node("One", false); yield return Node("Two", false); yield return Node("Overflow", false);
        throw new InvalidOperationException("Discovery traversed beyond the first overflow");
    }
    private static void Reset()
    {
        Invoke("RestorePlayModeOptions");
        var api = Field("_testRunnerApi").GetValue(null) as TestRunnerApi;
        var callbacks = Field("_callbacks").GetValue(null) as ICallbacks;
        if (api != null && callbacks != null) api.UnregisterCallbacks(callbacks);
        Field("_callbacks").SetValue(null, null); Field("_testRunnerApi").SetValue(null, null);
        if (api != null) UnityEngine.Object.DestroyImmediate(api);
        ((IDictionary)Field("_jobs").GetValue(null)).Clear(); Field("_currentJobId").SetValue(null, null);
        SessionState.EraseString("MCPTestRunner_Jobs"); SessionState.EraseString("MCPTestRunner_CurrentJobId");
    }
}
