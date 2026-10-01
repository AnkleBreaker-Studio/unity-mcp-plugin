using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;

public static class UnityMcpSerializationValidation
{
    private static readonly List<object> Checks = new List<object>();
    private static readonly MethodInfo SerializeResult = typeof(MCPEditorCommands).GetMethod("SerializeResult", BindingFlags.NonPublic | BindingFlags.Static);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Check(string name, Func<object> body)
    {
        try { Checks.Add(new { name, passed = true, evidence = body() }); }
        catch (Exception error) { Checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
    }
    private sealed class GetterFailure
    {
        public int Before => 1;
        public int Broken => throw new InvalidOperationException("Controlled getter failure");
        public int After => 2;
    }
    private sealed class CountingGetter
    {
        public int Reads;
        public int Value => 7;
        public int After { get { Reads++; return 2; } }
    }
    private sealed class EnumerationFailure : ArrayList
    {
        public override IEnumerator GetEnumerator() { yield return 1; throw new InvalidOperationException("Controlled enumeration failure"); }
    }
    private sealed class CyclicList : ArrayList
    {
        public int Enumerations;
        public override IEnumerator GetEnumerator()
        {
            if (++Enumerations > 32) throw new InvalidOperationException("Controlled cycle probe stopped at 32 enumerations");
            return base.GetEnumerator();
        }
    }
    private sealed class Branching : IEnumerable
    {
        public static int Enumerations, Values;
        private readonly int _depth;
        public Branching(int depth) { _depth = depth; }
        public IEnumerator GetEnumerator()
        {
            Enumerations++;
            for (int i = 0; i < 1000; i++)
            {
                if (++Values > 120000) throw new InvalidOperationException("Controlled expansion probe stopped at 120000 values");
                yield return _depth == 0 ? (object)i : new Branching(_depth - 1);
            }
        }
    }
    private sealed class LargeList : ArrayList
    {
        public int Values;
        public override IEnumerator GetEnumerator()
        {
            string value = new string('x', 4 * 1024 * 1024);
            for (int i = 0; i < 8; i++) { Values++; yield return value; }
        }
    }
    private static Dictionary<string, object> HttpResult(object payload, out int status)
    {
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        string url = "http://127.0.0.1:" + port + "/";
        var listener = new HttpListener(); listener.Prefixes.Add(url); listener.Start();
        string json = null, failure = null; int resultStatus = 0;
        var client = new Thread(() => {
            try {
                var request = WebRequest.CreateHttp(url); request.Timeout = 10000; request.Proxy = null;
                HttpWebResponse response;
                try { response = (HttpWebResponse)request.GetResponse(); } catch (WebException error) { response = (HttpWebResponse)error.Response; }
                using (response) using (var reader = new StreamReader(response.GetResponseStream())) { resultStatus = (int)response.StatusCode; json = reader.ReadToEnd(); }
            } catch (Exception error) { failure = error.Message; }
        }) { IsBackground = true };
        try {
            var pending = listener.BeginGetContext(null, null); client.Start();
            Require(pending.AsyncWaitHandle.WaitOne(5000), "Owned HTTP request did not connect");
            var context = listener.EndGetContext(pending);
            typeof(MCPBridgeServer).GetMethod("SendJson", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { context.Response, 200, payload });
            Require(client.Join(12000) && failure == null, "Owned HTTP request failed: " + failure);
            status = resultStatus;
            return (Dictionary<string, object>)MiniJson.Deserialize(json);
        } finally { listener.Close(); if (client.IsAlive) client.Join(12000); }
    }
    public static void Run()
    {
        Check("Finite primitive and getter-failure shapes remain compatible", () => {
            string json = MiniJson.Serialize(new GetterFailure());
            Require(json == "{\"Before\":1,\"Broken\":null,\"After\":2}", json); return new { json };
        });
        Check("Non-finite values produce valid JSON strings", () => {
            string json = MiniJson.Serialize(new object[] { double.NaN, double.PositiveInfinity, float.NegativeInfinity });
            Require(json == "[\"NaN\",\"Infinity\",\"-Infinity\"]", json); return new { json };
        });
        Check("Integers ignore the editor culture", () => {
            var previous = CultureInfo.CurrentCulture;
            try {
                var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone(); culture.NumberFormat.NegativeSign = "~"; CultureInfo.CurrentCulture = culture;
                string json = MiniJson.Serialize(new object[] { -1, -2L, -3.25m });
                Require(json == "[-1,-2,-3.25]", json); return new { json };
            } finally { CultureInfo.CurrentCulture = previous; }
        });
        Check("Nested enumeration failure cannot leave partial JSON", () => {
            string json = null; Exception failure = null;
            try { json = MiniJson.Serialize(new { Values = new EnumerationFailure() }); } catch (Exception error) { failure = error; }
            Require(failure != null, "Serializer returned malformed partial JSON: " + json);
            return new { errorType = failure.GetType().Name, message = failure.Message };
        });
        Check("Reference cycles stop before recursive enumeration", () => {
            var list = new CyclicList(); list.Add(list); Exception failure = null;
            try { MiniJson.Serialize(list); } catch (Exception error) { failure = error; }
            Require(failure != null && list.Enumerations == 1, "Cycle was enumerated " + list.Enumerations + " times");
            return new { enumerations = list.Enumerations, message = failure.Message };
        });
        Check("Shared references are serialized independently", () => {
            var shared = new Dictionary<string, object> { { "value", 7 } };
            string json = MiniJson.Serialize(new[] { shared, shared });
            Require(json == "[{\"value\":7},{\"value\":7}]", json); return new { json };
        });
        Check("Deep trees are rejected before stack exhaustion", () => {
            object tree = 1; for (int i = 0; i < 100; i++) tree = new object[] { tree };
            Exception failure = null; try { MiniJson.Serialize(tree); } catch (Exception error) { failure = error; }
            Require(failure != null, "A 100-level tree was accepted"); return new { message = failure.Message };
        });
        Check("Bounded serialization counts escaped UTF-8 before allocation", () => {
            var bounded = typeof(MiniJson).GetMethod("Serialize", new[] { typeof(object), typeof(int) });
            Require(bounded != null, "No bounded serializer is available");
            string source = "Quote \" \u4e2d \ud83d\ude00 \ud800";
            string json = MiniJson.Serialize(source); int bytes = Encoding.UTF8.GetByteCount(json);
            Require((string)bounded.Invoke(null, new object[] { source, bytes }) == json, "Exact byte limit was rejected");
            Exception failure = null; try { bounded.Invoke(null, new object[] { source, bytes - 1 }); } catch (Exception error) { failure = error.GetBaseException(); }
            Require(failure != null, "Over-limit string was accepted"); return new { bytes, json, message = failure.Message };
        });
        Check("Exhausted output budget does not evaluate the next property getter", () => {
            var value = new CountingGetter(); Exception failure = null;
            try { MiniJson.Serialize(value, 10); } catch (Exception error) { failure = error; }
            Require(failure is MiniJson.SerializationException && value.Reads == 0, "A getter ran after the output budget was exhausted");
            return new { laterGetterReads = value.Reads };
        });
        Check("Execution result expansion has a global work budget", () => {
            Branching.Enumerations = 0; Branching.Values = 0; object result = null; Exception failure = null;
            try { result = SerializeResult.Invoke(null, new object[] { new Branching(3) }); } catch (Exception error) { failure = error.GetBaseException(); }
            Require(Branching.Values <= 100000, "Result expansion visited " + Branching.Values + " values despite per-container limits");
            Require(failure != null || MiniJson.Serialize(result).Contains("serialization"), "Budget exhaustion was not reported");
            return new { values = Branching.Values, enumerations = Branching.Enumerations, message = failure?.Message };
        });
        Check("Reflected execution results cannot swallow global budget exhaustion", () => {
            Branching.Values = 0; Branching.Enumerations = 0;
            var result = (Dictionary<string, object>)SerializeResult.Invoke(null, new object[] { new { Rows = new Branching(2) } });
            Require((string)result["code"] == "execution_result_limit" && (bool)result["executionCompleted"], "Nested budget exhaustion was hidden");
            Require(Convert.ToInt32(result["serializedValues"]) == 100000, "Global budget counter differs");
            return new { values = Branching.Values, code = result["code"], executionCompleted = result["executionCompleted"] };
        });
        Check("Execution enumeration failures report that code already ran", () => {
            var result = (Dictionary<string, object>)SerializeResult.Invoke(null, new object[] { new EnumerationFailure() });
            Require((string)result["code"] == "execution_result_serialization_failed" && (bool)result["executionCompleted"], "Execution completion was not retained");
            Require(((string)result["stackTrace"]).Contains("EnumerationFailure"), "Existing exception diagnostics were lost");
            return result;
        });
        Check("Legacy list counts and per-container truncation remain compatible", () => {
            var result = (Dictionary<string, object>)SerializeResult.Invoke(null, new object[] { new int[1001] });
            var items = (List<object>)result["result"];
            Require(Convert.ToInt32(result["count"]) == 1001 && items.Count == 1001 && ((string)items[1000]).Contains("truncated at 1000"), "List contract changed");
            return new { count = result["count"], last = items[1000] };
        });
        Check("Wide generic serialization stops at its total value bound", () => {
            Exception failure = null;
            try { MiniJson.Serialize(new object[1000000]); } catch (Exception error) { failure = error; }
            Require(failure is MiniJson.SerializationException && ((MiniJson.SerializationException)failure).Reason == "value_limit", "Wide serialization was not bounded");
            return new { message = failure.Message };
        });
        Check("HTTP writer stops oversized traversal and returns a bounded 413", () => {
            var values = new LargeList(); var result = HttpResult(values, out var status);
            Require(status == 413 && (string)result["error"] == "response_too_large" && (bool)result["sizeIsLowerBound"], "Oversized HTTP contract changed");
            Require(values.Values <= 4 && (bool)result["outcomeUnknown"], "Oversized result was fully traversed or write outcome was inferred");
            return new { status, valuesVisited = values.Values, result };
        });
        Check("HTTP writer returns explicit cycle failure instead of invalid JSON", () => {
            var values = new CyclicList(); values.Add(values); var result = HttpResult(values, out var status);
            Require(status == 500 && (string)result["error"] == "response_serialization_failed" && (string)result["reason"] == "reference_cycle", "Cyclic HTTP payload was not reported");
            Require(values.Enumerations == 1, "HTTP writer walked the cycle");
            return new { status, result };
        });
        bool passed = true; foreach (var check in Checks) passed &= (bool)check.GetType().GetProperty("passed").GetValue(check);
        File.WriteAllText("Library/UnityMcpSerializationValidation.json", MiniJson.Serialize(new { unityVersion = Application.unityVersion, passed, checks = Checks }));
        EditorApplication.Exit(passed ? 0 : 1);
    }
}
