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

public static class UnityMcpExecutionValidation
{
    public static int Counter;

    public static void Run()
    {
        var report = new Dictionary<string, object> {
            { "unityVersion", Application.unityVersion },
            { "runtime", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription }
        };
        var failures = new List<string>();
        try
        {
            if (!File.Exists(".unity-mcp-validation")) throw new Exception("Unmarked validation project");
            string tempRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../ExecutionTemp"));
            if (!Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/').Equals(tempRoot, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Set TEMP and TMP to the validation project's ExecutionTemp directory before starting Unity");
            string snippetTemp = Path.Combine(tempRoot, "umcp");
            var before = Directory.Exists(snippetTemp) ? Directory.GetFiles(snippetTemp).ToHashSet() : new HashSet<string>();
            var simple = Call("return new { number = 7, flag = true, position = new Vector3(1, 2, 3) };");
            report["simple"] = simple;
            Check(simple.ContainsKey("success") && (bool)simple["success"], "simpleExecution", report, failures);
            var invalid = Call("int first = 1;\nreturn MissingMcpValidationSymbol;");
            report["compilationError"] = invalid;
            Check(invalid.ContainsKey("error") && ((List<object>)invalid["errors"]).Any(item => item.ToString().StartsWith("Line 2:")),
                "diagnosticUsesSnippetLine", report, failures);
            var exception = Call("throw new InvalidOperationException(\"__McpExecutionExpected\");");
            report["runtimeError"] = exception;
            Check(exception.TryGetValue("error", out var error) && error.ToString() == "__McpExecutionExpected", "runtimeExceptionPreserved", report, failures);
            var list = Call("return new [] { 1, 2, 3 };");
            Check(Convert.ToInt32(list["count"]) == 3 && ((List<object>)list["result"]).Count == 3, "listContract", report, failures);
            Counter = 0;
            const int iterations = 20;
            var names = new HashSet<string>();
            var stopwatch = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                var call = Call("UnityMcpExecutionValidation.Counter++; return System.Reflection.Assembly.GetExecutingAssembly().GetName().Name;");
                if (!call.TryGetValue("result", out var result)) throw new Exception(MiniJson.Serialize(call));
                names.Add(result.ToString());
            }
            stopwatch.Stop();
            report["repeatedExecution"] = new { iterations, elapsedMs = stopwatch.Elapsed.TotalMilliseconds, executions = Counter, distinctAssemblies = names.Count };
            Check(Counter == iterations && names.Count == iterations, "everyCallExecutesWithFreshAssembly", report, failures);
            var references = typeof(MCPEditorCommands).GetMethod("GetMetadataReferencesReflection", BindingFlags.NonPublic | BindingFlags.Static);
            var firstReferences = ((IEnumerable)references.Invoke(null, null)).Cast<object>().ToArray();
            var nextReferences = ((IEnumerable)references.Invoke(null, null)).Cast<object>().ToArray();
            int reused = nextReferences.Count(item => firstReferences.Any(previous => ReferenceEquals(previous, item)));
            report["references"] = new { count = nextReferences.Length, reused };
            Check(reused > 0, "metadataReferencesReused", report, failures);
            stopwatch.Restart();
            for (int i = 0; i < 20; i++) references.Invoke(null, null);
            stopwatch.Stop();
            report["twentyReferenceCollectionsMs"] = stopwatch.Elapsed.TotalMilliseconds;
            string[] leftovers = Directory.Exists(snippetTemp) ? Directory.GetFiles(snippetTemp).Where(path => !before.Contains(path)).ToArray() : Array.Empty<string>();
            report["temporaryDllsLeft"] = leftovers.Select(Path.GetFileName).ToArray();
            Check(leftovers.Length == 0, "noTemporaryDllsAfterFailures", report, failures);
            report["editorState"] = MCPEditorCommands.GetEditorState();
            CheckReferenceCache(tempRoot, report, failures);
            report["failures"] = failures;
            report["passed"] = failures.Count == 0;
        }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); }
        File.WriteAllText("Library/UnityMcpExecutionValidation.json", MiniJson.Serialize(report));
        EditorApplication.Exit((bool)report["passed"] ? 0 : 1);
    }

    private static Dictionary<string, object> Call(string code)
        => (Dictionary<string, object>)MiniJson.Deserialize(MiniJson.Serialize(MCPEditorCommands.ExecuteCode(new Dictionary<string, object> { { "code", code } })));

    private static void CheckReferenceCache(string tempRoot, Dictionary<string, object> report, List<string> failures)
    {
        var support = typeof(MCPEditorCommands).Assembly.GetType("UnityMCP.Editor.MCPCodeExecutionSupport");
        var getter = support.GetMethod("GetReference", BindingFlags.NonPublic | BindingFlags.Static);
        var diagnostics = support.GetMethod("GetDiagnostics", BindingFlags.NonPublic | BindingFlags.Static);
        var metadata = AppDomain.CurrentDomain.GetAssemblies().First(assembly => assembly.GetName().Name == "Microsoft.CodeAnalysis")
            .GetType("Microsoft.CodeAnalysis.MetadataReference");
        var create = metadata.GetMethods(BindingFlags.Public | BindingFlags.Static).First(method => method.Name == "CreateFromFile"
            && method.GetParameters().Length > 0 && method.GetParameters()[0].ParameterType == typeof(string));
        Func<string, object> factory = path => {
            var parameters = create.GetParameters();
            var values = new object[parameters.Length];
            values[0] = path;
            for (int i = 1; i < values.Length; i++) values[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
            return create.Invoke(null, values);
        };
        object Reference(string path) => getter.Invoke(null, new object[] { path, factory });
        Dictionary<string, object> Status() => (Dictionary<string, object>)MiniJson.Deserialize(MiniJson.Serialize(diagnostics.Invoke(null, null)));
        string root = Path.GetFullPath(Path.Combine(tempRoot, "References" + Guid.NewGuid().ToString("N")));
        if (!root.StartsWith(Path.GetFullPath(tempRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Invalid reference fixture path");
        Directory.CreateDirectory(root);
        try
        {
            string source = typeof(UnityMcpExecutionValidation).Assembly.Location;
            string first = Path.Combine(root, "First.dll");
            File.Copy(source, first);
            object original = Reference(first);
            Check(ReferenceEquals(original, Reference(first)), "unchangedReferenceReused", report, failures);
            File.SetLastWriteTimeUtc(first, File.GetLastWriteTimeUtc(first).AddSeconds(5));
            object changed = Reference(first);
            Check(!ReferenceEquals(original, changed), "changedReferenceInvalidated", report, failures);
            int capacity = Convert.ToInt32(Status()["maxMetadataCacheEntries"]);
            for (int i = 0; i < capacity + 1; i++)
            {
                string path = Path.Combine(root, "Reference" + i + ".dll");
                File.Copy(source, path);
                Reference(path);
            }
            var afterFill = Status();
            Check(Convert.ToInt32(afterFill["metadataCacheEntries"]) <= capacity && !ReferenceEquals(changed, Reference(first)),
                "referenceCountBoundAndEviction", report, failures);
            long byteLimit = Convert.ToInt64(afterFill["maxMetadataReferenceImageBytes"]);
            string large = Path.Combine(root, "Large.dll");
            File.Copy(source, large);
            using (var stream = new FileStream(large, FileMode.Open, FileAccess.Write)) stream.SetLength(byteLimit + 1);
            var beforeLarge = Status();
            object largeReference = Reference(large);
            var afterLarge = Status();
            Check(largeReference != null && Convert.ToInt64(afterLarge["metadataReferenceImageBytes"]) <= byteLimit
                && Convert.ToInt64(beforeLarge["metadataReferenceImageBytes"]) == Convert.ToInt64(afterLarge["metadataReferenceImageBytes"]),
                "oversizedReferenceNotRetained", report, failures);
            report["cacheAfterPressure"] = afterLarge;
            var recovered = Call("return UnityMcpExecutionValidation.Counter;");
            Check(recovered.ContainsKey("result") && Convert.ToInt32(recovered["result"]) == Counter, "executionAfterCacheEviction", report, failures);
        }
        finally
        {
            Directory.Delete(root, true);
            report["referenceFixtureRemoved"] = !Directory.Exists(root);
        }
    }

    private static void Check(bool passed, string name, Dictionary<string, object> report, List<string> failures)
    {
        report[name] = passed;
        if (!passed) failures.Add(name);
    }
}
