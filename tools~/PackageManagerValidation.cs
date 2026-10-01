using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;

public static class UnityMcpPackageManagerValidation
{
    [Serializable]
    private class Report
    {
        public string unityVersion;
        public bool passed;
        public List<string> checks = new List<string>();
        public string error;
    }

    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type Commands = typeof(MCPPackageManagerCommands);
    private static readonly Action<Func<Func<bool>>, Func<object>, Action<object>, Func<bool>> Schedule =
        (Action<Func<Func<bool>>, Func<object>, Action<object>, Func<bool>>)Delegate.CreateDelegate(
            typeof(Action<Func<Func<bool>>, Func<object>, Action<object>, Func<bool>>), Commands.GetMethod("Schedule", PrivateStatic));
    private static readonly Action Progress = (Action)Delegate.CreateDelegate(typeof(Action), Commands.GetMethod("Progress", PrivateStatic));
    private static readonly Action Clear = (Action)Delegate.CreateDelegate(typeof(Action), Commands.GetMethod("Clear", PrivateStatic));

    public static void Run()
    {
        var report = new Report { unityVersion = Application.unityVersion };
        try
        {
            ValidateScheduling();
            report.checks.Add("Pending package operations return immediately and run sequentially while other agent reads execute");
            ValidateExpiration();
            report.checks.Add("Expired pending requests never start; expired active requests retain their slot until completion");
            ValidateFailures();
            report.checks.Add("Start and result failures resolve once and release the next request");
            ValidateTicketExpiration();
            report.checks.Add("The queue execution deadline skips pending package changes and keeps terminal outcomes stable");
            ValidateLegacyWaiter();
            report.checks.Add("Legacy synchronous workers receive deferred results without blocking the main thread");
            ValidateRoutes();
            report.checks.Add("All five package routes and test discovery preserve category gates and input validation");
            ValidateCleanup();
            report.checks.Add("Idle polling unsubscribes; reload cleanup abandons pending callbacks");
            report.passed = true;
        }
        catch (Exception error)
        {
            report.error = error.ToString();
            UnityEngine.Debug.LogException(error);
        }
        finally { Clear(); }
        File.WriteAllText("Library/UnityMcpPackageManagerValidation.json", JsonUtility.ToJson(report, true));
        EditorApplication.Exit(report.passed ? 0 : 1);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void ValidateScheduling()
    {
        bool complete = false;
        int starts = 0, resolves = 0;
        Schedule(() => { starts++; return () => complete; }, () => "first", value =>
        {
            Check((string)value == "first", "First result changed");
            resolves++;
        }, () => true);
        Schedule(() => { starts++; return () => true; }, () => "second", value => resolves++, () => true);
        Check(starts == 0 && resolves == 0, "Scheduling blocked or executed immediately");
        Progress();
        Progress();
        Check(starts == 1 && resolves == 0, "Package operations overlapped");
        var read = MCPRequestQueue.SubmitRequest("package-other-agent", "editor/state", () => "responsive");
        MCPRequestQueue.ProcessNextRequests();
        Check((string)MCPRequestQueue.GetTicketStatus(read.TicketId)["result"] == "responsive", "Unrelated reads were blocked");
        complete = true;
        Progress();
        Check(starts == 2 && resolves == 1, "Second request did not follow the first");
        Progress();
        Progress();
        Check(resolves == 2, "Completion was duplicated or lost");
    }

    private static void ValidateExpiration()
    {
        bool complete = false, active = true, pending = true;
        int starts = 0, resolves = 0, results = 0;
        Schedule(() => { starts++; return () => complete; }, () => { results++; return "active"; }, value => resolves++, () => active);
        Schedule(() => { starts++; return () => true; }, () => "expired", value => resolves++, () => pending);
        Progress();
        pending = false;
        active = false;
        Schedule(() => { starts++; return () => true; }, () => "survivor", value => resolves++, () => true);
        Progress();
        Check(starts == 1 && resolves == 0, "Expired active operation released its native slot too early");
        complete = true;
        Progress();
        Check(starts == 2 && resolves == 0 && results == 0, "Expired request started or produced a late result");
        Progress();
        Check(resolves == 1, "Surviving request did not complete");
    }

    private static void ValidateFailures()
    {
        int failures = 0, successes = 0;
        Action<object> error = value =>
        {
            Check(MiniJson.Serialize(value).Contains("fixture failure"), "Failure detail was lost");
            failures++;
        };
        Schedule(() => throw new InvalidOperationException("fixture failure: start"), () => null, error, () => true);
        Schedule(() => () => true, () => throw new InvalidOperationException("fixture failure: result"), error, () => true);
        Schedule(() => () => true, () => "ok", value => successes++, () => true);
        for (int i = 0; i < 8; i++) Progress();
        Check(failures == 2 && successes == 1, "Failure blocked later work or resolved more than once");
    }

    private static void ValidateTicketExpiration()
    {
        bool nativeComplete = false;
        int starts = 0;
        var running = MCPRequestQueue.SubmitDeferredRequest("package-timeout-active", "packages/add", (resolve, isActive) =>
            Schedule(() => { starts++; return () => nativeComplete; }, () => new { success = true }, resolve, isActive));
        MCPRequestQueue.ProcessNextRequests();
        Progress();
        var waiting = MCPRequestQueue.SubmitDeferredRequest("package-timeout-waiting", "packages/remove", (resolve, isActive) =>
            Schedule(() => { starts++; return () => true; }, () => new { success = true }, resolve, isActive));
        MCPRequestQueue.ProcessNextRequests();
        long old = System.Diagnostics.Stopwatch.GetTimestamp() - 121L * System.Diagnostics.Stopwatch.Frequency;
        var timestamp = typeof(MCPRequestQueue.RequestTicket).GetField("StartedTimestamp", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        timestamp.SetValue(running, (long?)old);
        timestamp.SetValue(waiting, (long?)old);
        typeof(MCPRequestQueue).GetMethod("RunCleanup", PrivateStatic).Invoke(null, null);
        Progress();
        Check(starts == 1, "Expired pending package mutation started");
        nativeComplete = true;
        Progress();
        Check((string)MCPRequestQueue.GetTicketStatus(running.TicketId)["status"] == "TimedOut", "Late result overwrote timeout");
        Check((string)MCPRequestQueue.GetTicketStatus(waiting.TicketId)["status"] == "TimedOut", "Pending timeout disappeared");
        Check(starts == 1, "Expired pending request was replayed");
    }

    private static void ValidateLegacyWaiter()
    {
        object result = null;
        Exception failure = null;
        bool started = false, complete = false;
        var worker = new Thread(() =>
        {
            try
            {
                result = MCPRequestQueue.ExecuteDeferredWithTracking("legacy-packages", "packages/list", (resolve, isActive) =>
                    Schedule(() => { started = true; return () => complete; }, () => new { count = 7 }, resolve, isActive));
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        worker.Start();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!started && watch.ElapsedMilliseconds < 5000)
        {
            MCPRequestQueue.ProcessNextRequests();
            Progress();
            Thread.Sleep(1);
        }
        Check(started && worker.IsAlive, "Legacy worker did not wait for the asynchronous operation");
        complete = true;
        Progress();
        Check(worker.Join(5000), "Legacy worker did not receive completion");
        Check(failure == null && MiniJson.Serialize(result).Contains("\"count\":7"), "Legacy response payload changed");
    }

    private static void ValidateRoutes()
    {
        var route = typeof(MCPBridgeServer).GetMethod("RouteDeferredRequest", PrivateStatic);
        bool enabled = MCPSettingsManager.IsCategoryEnabled("packagemanager");
        bool testing = MCPSettingsManager.IsCategoryEnabled("testing");
        try
        {
            MCPSettingsManager.SetCategoryEnabled("packagemanager", false);
            MCPSettingsManager.SetCategoryEnabled("testing", false);
            foreach (string path in new[] { "packages/list", "packages/add", "packages/remove", "packages/search", "packages/info", "testing/list-tests" })
            {
                object result = null;
                route.Invoke(null, new object[] { path, new Dictionary<string, object>(), (Action<object>)(value => result = value), (Func<bool>)(() => true) });
                Check(MiniJson.Serialize(result).Contains("disabled"), "Deferred route bypassed category settings: " + path);
            }
            MCPSettingsManager.SetCategoryEnabled("packagemanager", true);
            foreach (string path in new[] { "packages/add", "packages/remove", "packages/search", "packages/info" })
            {
                foreach (string body in new[] { "{}", "{\"identifier\":null,\"name\":null,\"query\":null}" })
                {
                    object result = null;
                    route.Invoke(null, new object[] { path, MiniJson.Deserialize(body), (Action<object>)(value => result = value), (Func<bool>)(() => true) });
                    Check(MiniJson.Serialize(result).Contains("required"), "Missing argument was not rejected: " + path);
                }
            }
        }
        finally
        {
            MCPSettingsManager.SetCategoryEnabled("packagemanager", enabled);
            MCPSettingsManager.SetCategoryEnabled("testing", testing);
        }
    }

    private static void ValidateCleanup()
    {
        Check(!(bool)Commands.GetField("_subscribed", PrivateStatic).GetValue(null), "Idle scheduler retained its update callback");
        int starts = 0;
        Schedule(() => { starts++; return () => true; }, () => "abandoned", value => { throw new Exception("Reload callback survived"); }, () => true);
        Clear();
        Progress();
        Check(starts == 0 && !(bool)Commands.GetField("_subscribed", PrivateStatic).GetValue(null), "Reload cleanup retained work");
    }
}
