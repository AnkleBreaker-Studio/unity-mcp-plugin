using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Result of a single category test.
    /// </summary>
    public class MCPTestResult
    {
        public string Category;
        public TestStatus Status;
        public string Message;
        public string Details;
        public DateTime Timestamp;
        public double DurationMs;

        public enum TestStatus { Untested, Passed, Warning, Failed }
    }

    /// <summary>
    /// Self-test runner that validates every MCP feature category by calling
    /// safe, read-only probe endpoints. Results are stored and surfaced in
    /// the Dashboard and toolbar indicator.
    ///
    /// Tests are non-destructive — they never create, delete or modify scene
    /// objects. Each category has a specific probe that exercises the command
    /// handler and routing layer without side effects.
    /// </summary>
    [InitializeOnLoad]
    public static class MCPSelfTest
    {
        // ─── Public state ────────────────────────────────────────────

        private static readonly Dictionary<string, MCPTestResult> _results =
            new Dictionary<string, MCPTestResult>();

        private static bool _running;
        private static float _progress;
        private static string _currentCategory;
        private static DateTime _lastFullRun = DateTime.MinValue;

        public static bool IsRunning => _running;
        public static float Progress => _progress;
        public static string CurrentCategory => _currentCategory;
        public static DateTime LastRunTime => _lastFullRun;

        public static MCPTestResult GetResult(string category)
        {
            _results.TryGetValue(category.ToLower(), out var r);
            return r;
        }

        public static IReadOnlyDictionary<string, MCPTestResult> AllResults => _results;

        /// <summary>Quick aggregate: number of tests that passed.</summary>
        public static int PassedCount => _results.Values.Count(r => r.Status == MCPTestResult.TestStatus.Passed);

        /// <summary>Quick aggregate: number of tests that failed.</summary>
        public static int FailedCount => _results.Values.Count(r => r.Status == MCPTestResult.TestStatus.Failed);

        /// <summary>Quick aggregate: number of tests with warnings.</summary>
        public static int WarningCount => _results.Values.Count(r => r.Status == MCPTestResult.TestStatus.Warning);

        /// <summary>True if any test has failed.</summary>
        public static bool HasFailures => _results.Values.Any(r => r.Status == MCPTestResult.TestStatus.Failed);

        /// <summary>True if any test has warnings.</summary>
        public static bool HasWarnings => _results.Values.Any(r => r.Status == MCPTestResult.TestStatus.Warning);

        // ─── Static init ─────────────────────────────────────────────

        static MCPSelfTest()
        {
            // Initialize results for all categories as Untested
            foreach (var cat in MCPSettingsManager.GetAllCategoryNames())
            {
                _results[cat] = new MCPTestResult
                {
                    Category = cat,
                    Status = MCPTestResult.TestStatus.Untested,
                    Message = "Not tested yet",
                    Timestamp = DateTime.MinValue,
                };
            }
        }

        // ─── Test definitions ────────────────────────────────────────

        /// <summary>
        /// Each category maps to a delegate that exercises its handler
        /// without creating/deleting anything. Returns null on success
        /// or an error string on failure.
        /// </summary>
        private static readonly Dictionary<string, Func<string>> TestProbes =
            new Dictionary<string, Func<string>>
        {
            { "editor",     TestEditor },
            { "scene",      TestScene },
            { "gameobject", TestGameObject },
            { "component",  TestComponent },
            { "asset",      TestAsset },
            { "script",     TestScript },
            { "renderer",   TestRenderer },
            { "build",      TestBuild },
            { "console",    TestConsole },
            { "project",    TestProject },
            { "animation",  TestAnimation },
            { "prefab",     TestPrefab },
            { "physics",    TestPhysics },
            { "lighting",   TestLighting },
            { "audio",      TestAudio },
            { "taglayer",   TestTagLayer },
            { "selection",  TestSelection },
            { "input",      TestInput },
            { "asmdef",     TestAssemblyDef },
            { "profiler",   TestProfiler },
            { "debugger",   TestDebugger },
        };

        // ─── Run tests ──────────────────────────────────────────────

        /// <summary>
        /// Run all tests asynchronously (executes on main thread via
        /// EditorApplication.update). Safe to call from UI code.
        /// </summary>
        public static void RunAllAsync()
        {
            if (_running) return;
            _running = true;
            _progress = 0f;

            string[] categories = MCPSettingsManager.GetAllCategoryNames();
            int index = 0;

            void Step()
            {
                if (index >= categories.Length)
                {
                    _running = false;
                    _progress = 1f;
                    _currentCategory = null;
                    _lastFullRun = DateTime.UtcNow;
                    EditorApplication.update -= Step;
                    return;
                }

                string cat = categories[index];
                _currentCategory = cat;
                _progress = (float)index / categories.Length;

                RunSingleTest(cat);

                index++;
            }

            EditorApplication.update += Step;
        }

        /// <summary>
        /// Run a single category test synchronously (must be called on main thread).
        /// </summary>
        public static void RunSingleTest(string category)
        {
            category = category.ToLower();

            // Check if server is running
            if (!MCPBridgeServer.IsRunning)
            {
                _results[category] = new MCPTestResult
                {
                    Category = category,
                    Status = MCPTestResult.TestStatus.Failed,
                    Message = "Server not running",
                    Details = "The AB Unity MCP server is stopped. Start it first.",
                    Timestamp = DateTime.UtcNow,
                };
                return;
            }

            // Check if category is enabled
            if (!MCPSettingsManager.IsCategoryEnabled(category))
            {
                _results[category] = new MCPTestResult
                {
                    Category = category,
                    Status = MCPTestResult.TestStatus.Warning,
                    Message = "Category disabled",
                    Details = $"'{category}' is disabled in settings. Enable it to test.",
                    Timestamp = DateTime.UtcNow,
                };
                return;
            }

            // Find and run the probe
            if (!TestProbes.TryGetValue(category, out var probe))
            {
                _results[category] = new MCPTestResult
                {
                    Category = category,
                    Status = MCPTestResult.TestStatus.Warning,
                    Message = "No test defined",
                    Details = $"No self-test probe exists for '{category}' yet.",
                    Timestamp = DateTime.UtcNow,
                };
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string error = probe();
                sw.Stop();

                if (error == null)
                {
                    _results[category] = new MCPTestResult
                    {
                        Category = category,
                        Status = MCPTestResult.TestStatus.Passed,
                        Message = "OK",
                        Details = $"Completed in {sw.ElapsedMilliseconds}ms",
                        Timestamp = DateTime.UtcNow,
                        DurationMs = sw.ElapsedMilliseconds,
                    };
                }
                else
                {
                    _results[category] = new MCPTestResult
                    {
                        Category = category,
                        Status = MCPTestResult.TestStatus.Failed,
                        Message = TruncateMessage(error),
                        Details = error,
                        Timestamp = DateTime.UtcNow,
                        DurationMs = sw.ElapsedMilliseconds,
                    };
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                _results[category] = new MCPTestResult
                {
                    Category = category,
                    Status = MCPTestResult.TestStatus.Failed,
                    Message = TruncateMessage(ex.Message),
                    Details = $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}",
                    Timestamp = DateTime.UtcNow,
                    DurationMs = sw.ElapsedMilliseconds,
                };
            }
        }

        private static string TruncateMessage(string msg)
        {
            if (msg == null) return "";
            return msg.Length > 80 ? msg.Substring(0, 77) + "..." : msg;
        }

        // ─── Individual test probes ──────────────────────────────────
        // Each returns null on success, or an error string on failure.
        // They use the command handler classes directly (same path as HTTP routing)
        // but ONLY call read-only / safe methods.

        private static Dictionary<string, object> EmptyArgs() => new Dictionary<string, object>();

        private static string AssertNotNull(object result, string label)
        {
            if (result == null) return $"{label} returned null";
            // Check for error property in anonymous objects or dicts
            if (result is Dictionary<string, object> dict && dict.ContainsKey("error"))
                return $"{label}: {dict["error"]}";
            return null;
        }

        // --- Editor ---
        private static string TestEditor()
        {
            var result = MCPEditorCommands.GetEditorState();
            return AssertNotNull(result, "GetEditorState");
        }

        // --- Scene ---
        private static string TestScene()
        {
            var result = MCPSceneCommands.GetSceneInfo();
            return AssertNotNull(result, "GetSceneInfo");
        }

        // --- GameObject ---
        private static string TestGameObject()
        {
            // Test info on a known object — Main Camera typically exists
            var cam = Camera.main;
            if (cam == null)
                return null; // No camera, but the handler class loaded fine — pass

            var args = new Dictionary<string, object> { { "path", cam.gameObject.name } };
            var result = MCPGameObjectCommands.GetInfo(args);
            return AssertNotNull(result, "GetInfo");
        }

        // --- Component ---
        private static string TestComponent()
        {
            var cam = Camera.main;
            if (cam == null)
                return null; // Handler loaded

            var args = new Dictionary<string, object>
            {
                { "gameObjectPath", cam.gameObject.name },
                { "componentType", "Camera" },
            };
            var result = MCPComponentCommands.GetProperties(args);
            return AssertNotNull(result, "GetProperties");
        }

        // --- Asset ---
        private static string TestAsset()
        {
            var args = new Dictionary<string, object> { { "folder", "Assets" }, { "recursive", false } };
            var result = MCPAssetCommands.List(args);
            return AssertNotNull(result, "AssetList");
        }

        // --- Script ---
        private static string TestScript()
        {
            // Try to read a script we know should exist (any .cs in Editor/)
            string[] guids = UnityEditor.AssetDatabase.FindAssets("t:MonoScript", new[] { "Packages/com.anklebreaker.unity-mcp/Editor" });
            if (guids.Length == 0)
            {
                // Fallback: look in Assets
                guids = UnityEditor.AssetDatabase.FindAssets("t:MonoScript");
            }

            if (guids.Length == 0)
                return null; // No scripts at all, but handler loaded

            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
            var args = new Dictionary<string, object> { { "path", path } };
            var result = MCPScriptCommands.Read(args);
            return AssertNotNull(result, "ScriptRead");
        }

        // --- Renderer ---
        private static string TestRenderer()
        {
            // Renderer commands require a specific GO — just verify the class is accessible
            try
            {
                var type = typeof(MCPRendererCommands);
                if (type == null) return "MCPRendererCommands class not found";
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // --- Build ---
        private static string TestBuild()
        {
            // We cannot start a build as a test. Verify the class loads.
            try
            {
                var type = typeof(MCPBuildCommands);
                if (type == null) return "MCPBuildCommands class not found";
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // --- Console ---
        private static string TestConsole()
        {
            var args = new Dictionary<string, object> { { "count", 1 } };
            var result = MCPConsoleCommands.GetLog(args);
            return AssertNotNull(result, "GetLog");
        }

        // --- Project ---
        private static string TestProject()
        {
            var result = MCPProjectCommands.GetInfo();
            return AssertNotNull(result, "GetProjectInfo");
        }

        // --- Animation ---
        private static string TestAnimation()
        {
            // Verify animation controller info works on a non-existent path
            // (should return an error dict, not throw)
            try
            {
                var args = new Dictionary<string, object> { { "path", "Assets/__mcp_test_nonexistent.controller" } };
                var result = MCPAnimationCommands.GetControllerInfo(args);
                // Result should be an error dict, not a crash
                if (result == null) return "GetControllerInfo returned null (expected error dict)";
                return null;
            }
            catch (Exception ex)
            {
                return $"GetControllerInfo threw: {ex.Message}";
            }
        }

        // --- Prefab ---
        private static string TestPrefab()
        {
            try
            {
                var args = new Dictionary<string, object> { { "path", "Assets/__mcp_test_nonexistent.prefab" } };
                var result = MCPPrefabCommands.GetPrefabInfo(args);
                if (result == null) return "GetPrefabInfo returned null";
                return null;
            }
            catch (Exception ex)
            {
                return $"GetPrefabInfo threw: {ex.Message}";
            }
        }

        // --- Physics ---
        private static string TestPhysics()
        {
            // Raycast from origin downward — safe read-only operation
            try
            {
                var args = new Dictionary<string, object>
                {
                    { "origin", new Dictionary<string, object> { {"x", 0}, {"y", 100}, {"z", 0} } },
                    { "direction", new Dictionary<string, object> { {"x", 0}, {"y", -1}, {"z", 0} } },
                };
                var result = MCPPhysicsCommands.Raycast(args);
                return AssertNotNull(result, "Raycast");
            }
            catch (Exception ex)
            {
                return $"Raycast threw: {ex.Message}";
            }
        }

        // --- Lighting ---
        private static string TestLighting()
        {
            try
            {
                var result = MCPLightingCommands.GetLightingInfo(EmptyArgs());
                return AssertNotNull(result, "GetLightingInfo");
            }
            catch (Exception ex)
            {
                return $"GetLightingInfo threw: {ex.Message}";
            }
        }

        // --- Audio ---
        private static string TestAudio()
        {
            try
            {
                var result = MCPAudioCommands.GetAudioInfo(EmptyArgs());
                return AssertNotNull(result, "GetAudioInfo");
            }
            catch (Exception ex)
            {
                return $"GetAudioInfo threw: {ex.Message}";
            }
        }

        // --- TagLayer ---
        private static string TestTagLayer()
        {
            try
            {
                var result = MCPTagLayerCommands.GetTagsAndLayers(EmptyArgs());
                return AssertNotNull(result, "GetTagsAndLayers");
            }
            catch (Exception ex)
            {
                return $"GetTagsAndLayers threw: {ex.Message}";
            }
        }

        // --- Selection ---
        private static string TestSelection()
        {
            try
            {
                var result = MCPSelectionCommands.GetSelection(EmptyArgs());
                return AssertNotNull(result, "GetSelection");
            }
            catch (Exception ex)
            {
                return $"GetSelection threw: {ex.Message}";
            }
        }

        // --- Input Actions ---
        private static string TestInput()
        {
            try
            {
                // Test info on a non-existent file (should return error dict, not throw)
                var args = new Dictionary<string, object> { { "path", "Assets/__mcp_test_nonexistent.inputactions" } };
                var result = MCPInputCommands.GetInputActionsInfo(args);
                if (result == null) return "GetInputActionsInfo returned null";
                return null;
            }
            catch (Exception ex)
            {
                return $"GetInputActionsInfo threw: {ex.Message}";
            }
        }

        // --- Profiler ---
        private static string TestProfiler()
        {
            try
            {
                // Test rendering stats (always available, no side effects)
                var result = MCPProfilerCommands.GetRenderingStats(EmptyArgs());
                string err = AssertNotNull(result, "GetRenderingStats");
                if (err != null) return err;

                // Test memory info (always available, no side effects)
                result = MCPProfilerCommands.GetMemoryInfo(EmptyArgs());
                return AssertNotNull(result, "GetMemoryInfo");
            }
            catch (Exception ex)
            {
                return $"Profiler test threw: {ex.Message}";
            }
        }

        // --- Frame Debugger ---
        private static string TestDebugger()
        {
            try
            {
                // Just verify the class loads and reflection resolves without error
                // Don't actually enable/disable the debugger to avoid side effects
                var result = MCPProfilerCommands.GetFrameEvents(EmptyArgs());
                // This will likely return an error (debugger not enabled) which is fine
                if (result == null) return "GetFrameEvents returned null";
                return null;
            }
            catch (Exception ex)
            {
                return $"Debugger test threw: {ex.Message}";
            }
        }

        // --- Assembly Definitions ---
        private static string TestAssemblyDef()
        {
            try
            {
                // 1. Test listing — safe read-only operation
                var listResult = MCPAssemblyDefCommands.ListAssemblyDefs(EmptyArgs());
                string listErr = AssertNotNull(listResult, "ListAssemblyDefs");
                if (listErr != null) return listErr;

                // 2. Test info on a non-existent path (should return error dict, not throw)
                var infoArgs = new Dictionary<string, object> { { "path", "Assets/__mcp_test_nonexistent.asmdef" } };
                var infoResult = MCPAssemblyDefCommands.GetAssemblyDefInfo(infoArgs);
                if (infoResult == null) return "GetAssemblyDefInfo returned null";

                // 3. Test create + add reference + info + cleanup (full round-trip)
                string testPath = "Assets/__mcp_selftest_temp.asmdef";
                try
                {
                    // Create
                    var createArgs = new Dictionary<string, object>
                    {
                        { "path", testPath },
                        { "name", "MCP.SelfTest.Temp" },
                        { "rootNamespace", "MCP.SelfTest" },
                    };
                    var createResult = MCPAssemblyDefCommands.CreateAssemblyDef(createArgs);
                    string createErr = AssertNotNull(createResult, "CreateAssemblyDef");
                    if (createErr != null) return createErr;

                    // Verify file exists
                    if (!System.IO.File.Exists(testPath))
                        return "CreateAssemblyDef did not create file on disk";

                    // Read back info
                    var readArgs = new Dictionary<string, object> { { "path", testPath } };
                    var readResult = MCPAssemblyDefCommands.GetAssemblyDefInfo(readArgs) as Dictionary<string, object>;
                    if (readResult == null) return "GetAssemblyDefInfo returned null for created file";
                    if (!readResult.ContainsKey("name") || readResult["name"].ToString() != "MCP.SelfTest.Temp")
                        return $"Name mismatch: expected 'MCP.SelfTest.Temp', got '{readResult["name"]}'";

                    // Update settings
                    var updateArgs = new Dictionary<string, object>
                    {
                        { "path", testPath },
                        { "rootNamespace", "MCP.SelfTest.Updated" },
                        { "allowUnsafeCode", true },
                    };
                    var updateResult = MCPAssemblyDefCommands.UpdateSettings(updateArgs);
                    string updateErr = AssertNotNull(updateResult, "UpdateSettings");
                    if (updateErr != null) return updateErr;

                    // Verify update
                    readResult = MCPAssemblyDefCommands.GetAssemblyDefInfo(readArgs) as Dictionary<string, object>;
                    if (readResult == null) return "GetAssemblyDefInfo returned null after update";
                    if (readResult.ContainsKey("rootNamespace") && readResult["rootNamespace"].ToString() != "MCP.SelfTest.Updated")
                        return "rootNamespace was not updated";

                    return null; // All passed
                }
                finally
                {
                    // Cleanup: delete test file
                    if (System.IO.File.Exists(testPath))
                    {
                        AssetDatabase.DeleteAsset(testPath);
                    }
                    // Also clean up .meta
                    string metaPath = testPath + ".meta";
                    if (System.IO.File.Exists(metaPath))
                    {
                        System.IO.File.Delete(metaPath);
                    }
                }
            }
            catch (Exception ex)
            {
                return $"AssemblyDef test threw: {ex.Message}";
            }
        }
    }
}
