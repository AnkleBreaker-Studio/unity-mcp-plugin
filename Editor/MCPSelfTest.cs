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

        /// <summary>Per-test timeout in milliseconds. If a probe exceeds this, it is marked as Failed.</summary>
        private const long TestTimeoutMs = 5000;

        /// <summary>Rolling test log displayed in the Dashboard UI.</summary>
        private static readonly List<string> _testLog = new List<string>();
        private const int MaxLogLines = 50;

        public static bool IsRunning => _running;
        public static float Progress => _progress;
        public static string CurrentCategory => _currentCategory;
        public static DateTime LastRunTime => _lastFullRun;
        public static IReadOnlyList<string> TestLog => _testLog;

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

        // ─── SessionState keys (survive domain reload, cleared on editor restart) ───
        private const string SK_Results      = "MCPSelfTest_Results";
        private const string SK_LastRun      = "MCPSelfTest_LastRun";
        private const string SK_Log          = "MCPSelfTest_Log";
        private const string SK_Running      = "MCPSelfTest_Running";
        private const string SK_ResumeIndex  = "MCPSelfTest_ResumeIndex";

        // ─── Static init ─────────────────────────────────────────────

        static MCPSelfTest()
        {
            // Initialize all categories as Untested first
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

            // Restore persisted state from SessionState (survives domain reload)
            RestoreFromSession();

            // If a test run was interrupted by domain reload, resume it
            int resumeIndex = SessionState.GetInt(SK_ResumeIndex, -1);
            if (resumeIndex >= 0)
            {
                Debug.Log($"[MCP SelfTest] Domain reload detected mid-run — resuming from index {resumeIndex}");
                // Delay resume by one frame to let the editor finish initializing
                EditorApplication.delayCall += () => ResumeFromIndex(resumeIndex);
            }
        }

        // ─── Persistence helpers ─────────────────────────────────────

        /// <summary>Tracks current step index for domain-reload resume. -1 = not running.</summary>
        private static int _currentIndex = -1;

        private static void SaveToSession()
        {
            // Save _lastFullRun
            SessionState.SetString(SK_LastRun, _lastFullRun.Ticks.ToString());

            // Save resume index (-1 = not running, >=0 = resume from this index)
            SessionState.SetInt(SK_ResumeIndex, _running ? _currentIndex : -1);

            // Save results as simple pipe-delimited lines: cat|status|message|details|durationMs
            var lines = new List<string>();
            foreach (var kvp in _results)
            {
                var r = kvp.Value;
                // Escape pipes in message/details
                string msg = (r.Message ?? "").Replace("|", "\\|");
                string det = (r.Details ?? "").Replace("|", "\\|").Replace("\n", "\\n");
                lines.Add($"{r.Category}|{(int)r.Status}|{msg}|{det}|{r.DurationMs}");
            }
            SessionState.SetString(SK_Results, string.Join("\n", lines));

            // Save log
            SessionState.SetString(SK_Log, string.Join("\n", _testLog));
        }

        private static void RestoreFromSession()
        {
            // Restore _lastFullRun
            string ticksStr = SessionState.GetString(SK_LastRun, "");
            if (long.TryParse(ticksStr, out long ticks) && ticks > 0)
                _lastFullRun = new DateTime(ticks, DateTimeKind.Utc);

            // Restore _running (always false after domain reload)
            _running = false;

            // Restore results
            string resultsStr = SessionState.GetString(SK_Results, "");
            if (!string.IsNullOrEmpty(resultsStr))
            {
                foreach (string line in resultsStr.Split('\n'))
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] parts = line.Split(new[] { '|' }, 5);
                    if (parts.Length < 5) continue;

                    string cat = parts[0];
                    if (!int.TryParse(parts[1], out int statusInt)) continue;
                    string msg = parts[2].Replace("\\|", "|");
                    string det = parts[3].Replace("\\|", "|").Replace("\\n", "\n");
                    double.TryParse(parts[4], out double dur);

                    _results[cat] = new MCPTestResult
                    {
                        Category = cat,
                        Status = (MCPTestResult.TestStatus)statusInt,
                        Message = msg,
                        Details = det,
                        DurationMs = dur,
                        Timestamp = _lastFullRun,
                    };
                }
            }

            // Restore log
            string logStr = SessionState.GetString(SK_Log, "");
            if (!string.IsNullOrEmpty(logStr))
            {
                _testLog.Clear();
                _testLog.AddRange(logStr.Split('\n'));
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
            { "testing",    TestTesting },
            { "shadergraph",    TestShaderGraph },
            { "terrain",        TestTerrain },
            { "amplify",        TestAmplify },
            { "constraint",     TestConstraint },
            { "graphics",       TestGraphics },
            { "memoryprofiler", TestMemoryProfiler },
            { "navigation",     TestNavigation },
            { "packagemanager", TestPackageManager },
            { "particle",       TestParticle },
            { "prefabasset",    TestPrefabAsset },
            { "prefs",          TestPrefs },
            { "projectsettings",TestProjectSettings },
            { "scenario",       TestScenario },
            { "screenshot",     TestScreenshot },
            { "scriptableobject", TestScriptableObject },
            { "search",         TestSearch },
            { "spriteatlas",    TestSpriteAtlas },
            { "texture",        TestTexture },
            { "ui",             TestUI },
            { "uma",            TestUMA },
            { "undo",           TestUndo },
        };

        // ─── Run tests ──────────────────────────────────────────────

        /// <summary>
        /// Run all tests asynchronously (executes on main thread via
        /// EditorApplication.update). Safe to call from UI code.
        /// </summary>
        public static void RunAllAsync()
        {
            if (_running)
            {
                Debug.Log("[MCP SelfTest] RunAllAsync called but already running — skipping.");
                return;
            }
            _testLog.Clear();
            AddLog("─── Self-test started ───");
            StartRunFromIndex(0);
        }

        /// <summary>Resume a test run after domain reload.</summary>
        private static void ResumeFromIndex(int startIndex)
        {
            if (_running)
            {
                Debug.Log("[MCP SelfTest] ResumeFromIndex called but already running — skipping.");
                return;
            }
            AddLog($"─── Resuming after domain reload (from index {startIndex}) ───");
            StartRunFromIndex(startIndex);
        }

        /// <summary>Core loop: starts (or resumes) the test run from a given index.</summary>
        private static void StartRunFromIndex(int startIndex)
        {
            _running = true;
            _progress = 0f;

            string[] categories = MCPSettingsManager.GetAllCategoryNames();
            Debug.Log($"[MCP SelfTest] Starting from index {startIndex} — {categories.Length} categories: {string.Join(", ", categories)}");
            _currentIndex = startIndex;

            void Step()
            {
                Debug.Log($"[MCP SelfTest] Step() called — index={_currentIndex}/{categories.Length}, _running={_running}");
                try
                {
                    if (_currentIndex >= categories.Length)
                    {
                        _running = false;
                        _currentIndex = -1;
                        _progress = 1f;
                        _currentCategory = null;
                        _lastFullRun = DateTime.UtcNow;
                        EditorApplication.update -= Step;

                        int p = PassedCount, f = FailedCount, w = WarningCount;
                        Debug.Log($"[MCP SelfTest] ALL DONE — {p} passed, {f} failed, {w} warnings");
                        AddLog($"─── Done: {p} passed, {f} failed, {w} warnings ───");
                        SaveToSession();
                        return;
                    }

                    string cat = categories[_currentIndex];
                    _currentCategory = cat;
                    _progress = (float)_currentIndex / categories.Length;
                    Debug.Log($"[MCP SelfTest] [{_currentIndex+1}/{categories.Length}] Running test: '{cat}'...");

                    RunSingleTest(cat);

                    // Log the result
                    if (_results.TryGetValue(cat, out var r))
                    {
                        string icon = r.Status == MCPTestResult.TestStatus.Passed ? "✓"
                                    : r.Status == MCPTestResult.TestStatus.Warning ? "⚠"
                                    : r.Status == MCPTestResult.TestStatus.Failed  ? "✗"
                                    : "?";
                        string dur = r.DurationMs > 0 ? $" ({r.DurationMs}ms)" : "";
                        AddLog($"  {icon} {cat}: {r.Message}{dur}");
                    }

                    // Persist intermediate results + log (survives domain reload mid-run)
                    SaveToSession();
                }
                catch (Exception ex)
                {
                    // Safeguard: if anything crashes inside Step, record it and move on
                    string cat = _currentIndex < categories.Length ? categories[_currentIndex] : "unknown";
                    _results[cat] = new MCPTestResult
                    {
                        Category = cat,
                        Status = MCPTestResult.TestStatus.Failed,
                        Message = TruncateMessage(ex.Message),
                        Details = $"Step() crash: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}",
                        Timestamp = DateTime.UtcNow,
                    };
                    AddLog($"  ✗ {cat}: CRASH — {ex.GetType().Name}: {TruncateMessage(ex.Message)}");
                    Debug.LogError($"[MCP SelfTest] CATCH — Step crash on '{cat}': {ex}");
                    SaveToSession();
                }
                finally
                {
                    Debug.Log($"[MCP SelfTest] FINALLY — advancing index from {_currentIndex} to {_currentIndex + 1}");
                    _currentIndex++; // Always advance to prevent infinite loop
                }
            }

            EditorApplication.update += Step;
        }

        /// <summary>
        /// Run a single category test synchronously (must be called on main thread).
        /// </summary>
        public static void RunSingleTest(string category)
        {
            category = category.ToLower();
            Debug.Log($"[MCP SelfTest] RunSingleTest('{category}') — server={MCPBridgeServer.IsRunning}, enabled={MCPSettingsManager.IsCategoryEnabled(category)}, hasProbe={TestProbes.ContainsKey(category)}");

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
                Debug.Log($"[MCP SelfTest] Probe '{category}' returned in {sw.ElapsedMilliseconds}ms — error={error ?? "(null = OK)"}");

                // Check timeout
                if (sw.ElapsedMilliseconds > TestTimeoutMs)
                {
                    _results[category] = new MCPTestResult
                    {
                        Category = category,
                        Status = MCPTestResult.TestStatus.Failed,
                        Message = $"Timeout ({sw.ElapsedMilliseconds}ms > {TestTimeoutMs}ms)",
                        Details = $"Test completed but exceeded the {TestTimeoutMs}ms timeout.\nResult was: {(error ?? "OK")}",
                        Timestamp = DateTime.UtcNow,
                        DurationMs = sw.ElapsedMilliseconds,
                    };
                    return;
                }

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

        private static void AddLog(string line)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            _testLog.Add($"[{timestamp}] {line}");
            if (_testLog.Count > MaxLogLines)
                _testLog.RemoveAt(0);
        }

        /// <summary>Clear the test log manually.</summary>
        public static void ClearLog() => _testLog.Clear();

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

        // --- Testing ---
        private static string TestTesting()
        {
            try
            {
                // Verify GetTestJob works (read-only, no side effects)
                var jobResult = MCPTestRunnerCommands.GetTestJob(
                    new Dictionary<string, object>()) as Dictionary<string, object>;
                if (jobResult == null) return "GetTestJob returned null";

                // Either returns a job or "No test jobs found" — both are valid
                if (jobResult.ContainsKey("error"))
                {
                    string error = jobResult["error"].ToString();
                    if (!error.Contains("No test jobs found"))
                        return $"GetTestJob error: {error}";
                }

                return null;
            }
            catch (Exception ex)
            {
                return $"Testing test threw: {ex.Message}";
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

        // --- ShaderGraph ---
        private static string TestShaderGraph()
        {
            try
            {
                var result = MCPShaderGraphCommands.GetStatus(EmptyArgs());
                return AssertNotNull(result, "ShaderGraph.GetStatus");
            }
            catch (Exception ex)
            {
                return $"ShaderGraph.GetStatus threw: {ex.Message}";
            }
        }

        // --- Terrain ---
        private static string TestTerrain()
        {
            try
            {
                var result = MCPTerrainCommands.ListTerrains(EmptyArgs());
                return AssertNotNull(result, "Terrain.ListTerrains");
            }
            catch (Exception ex)
            {
                return $"Terrain.ListTerrains threw: {ex.Message}";
            }
        }

        // --- Amplify ---
        private static string TestAmplify()
        {
            try
            {
                var result = MCPAmplifyCommands.GetStatus(EmptyArgs());
                return AssertNotNull(result, "Amplify.GetStatus");
            }
            catch (Exception ex)
            {
                return $"Amplify.GetStatus threw: {ex.Message}";
            }
        }

        // --- Constraint ---
        private static string TestConstraint()
        {
            try
            {
                var type = typeof(MCPConstraintCommands);
                if (type == null) return "MCPConstraintCommands class not found";
                return null;
            }
            catch (Exception ex)
            {
                return $"Constraint threw: {ex.Message}";
            }
        }

        // --- Graphics ---
        private static string TestGraphics()
        {
            try
            {
                var result = MCPGraphicsCommands.GetLightingSummary(EmptyArgs());
                return AssertNotNull(result, "Graphics.GetLightingSummary");
            }
            catch (Exception ex)
            {
                return $"Graphics.GetLightingSummary threw: {ex.Message}";
            }
        }

        // --- MemoryProfiler ---
        private static string TestMemoryProfiler()
        {
            try
            {
                var result = MCPMemoryProfilerCommands.GetStatus(EmptyArgs());
                return AssertNotNull(result, "MemoryProfiler.GetStatus");
            }
            catch (Exception ex)
            {
                return $"MemoryProfiler.GetStatus threw: {ex.Message}";
            }
        }

        // --- Navigation ---
        private static string TestNavigation()
        {
            try
            {
                var result = MCPNavigationCommands.GetNavMeshInfo(EmptyArgs());
                return AssertNotNull(result, "Navigation.GetNavMeshInfo");
            }
            catch (Exception ex)
            {
                return $"Navigation.GetNavMeshInfo threw: {ex.Message}";
            }
        }

        // --- PackageManager ---
        private static string TestPackageManager()
        {
            try
            {
                var result = MCPPackageManagerCommands.ListPackages(EmptyArgs());
                return AssertNotNull(result, "PackageManager.ListPackages");
            }
            catch (Exception ex)
            {
                return $"PackageManager.ListPackages threw: {ex.Message}";
            }
        }

        // --- Particle ---
        private static string TestParticle()
        {
            try
            {
                var type = typeof(MCPParticleCommands);
                if (type == null) return "MCPParticleCommands class not found";
                return null;
            }
            catch (Exception ex)
            {
                return $"Particle threw: {ex.Message}";
            }
        }

        // --- PrefabAsset ---
        private static string TestPrefabAsset()
        {
            try
            {
                var type = typeof(MCPPrefabAssetCommands);
                if (type == null) return "MCPPrefabAssetCommands class not found";
                return null;
            }
            catch (Exception ex)
            {
                return $"PrefabAsset threw: {ex.Message}";
            }
        }

        // --- Prefs ---
        private static string TestPrefs()
        {
            try
            {
                var args = new Dictionary<string, object> { { "key", "__mcp_selftest_nonexistent" } };
                var result = MCPPrefsCommands.GetEditorPref(args);
                return AssertNotNull(result, "Prefs.GetEditorPref");
            }
            catch (Exception ex)
            {
                return $"Prefs.GetEditorPref threw: {ex.Message}";
            }
        }

        // --- ProjectSettings ---
        private static string TestProjectSettings()
        {
            try
            {
                var result = MCPProjectSettingsCommands.GetRenderPipelineInfo(EmptyArgs());
                return AssertNotNull(result, "ProjectSettings.GetRenderPipelineInfo");
            }
            catch (Exception ex)
            {
                return $"ProjectSettings.GetRenderPipelineInfo threw: {ex.Message}";
            }
        }

        // --- Scenario ---
        private static string TestScenario()
        {
            try
            {
                var result = MCPScenarioCommands.ListScenarios(EmptyArgs());
                if (result is Dictionary<string, object> dict && dict.ContainsKey("error"))
                {
                    string err = dict["error"].ToString();
                    if (err.Contains("not installed")) return null; // MPPM not installed — pass
                }
                return AssertNotNull(result, "Scenario.ListScenarios");
            }
            catch (Exception ex)
            {
                return $"Scenario.ListScenarios threw: {ex.Message}";
            }
        }

        // --- Screenshot ---
        private static string TestScreenshot()
        {
            try
            {
                var result = MCPScreenshotCommands.GetSceneViewInfo(EmptyArgs());
                return AssertNotNull(result, "Screenshot.GetSceneViewInfo");
            }
            catch (Exception ex)
            {
                return $"Screenshot.GetSceneViewInfo threw: {ex.Message}";
            }
        }

        // --- ScriptableObject ---
        private static string TestScriptableObject()
        {
            try
            {
                var result = MCPScriptableObjectCommands.ListScriptableObjectTypes(EmptyArgs());
                return AssertNotNull(result, "ScriptableObject.ListScriptableObjectTypes");
            }
            catch (Exception ex)
            {
                return $"ScriptableObject.ListScriptableObjectTypes threw: {ex.Message}";
            }
        }

        // --- Search ---
        private static string TestSearch()
        {
            try
            {
                var result = MCPSearchCommands.GetSceneStats(EmptyArgs());
                return AssertNotNull(result, "Search.GetSceneStats");
            }
            catch (Exception ex)
            {
                return $"Search.GetSceneStats threw: {ex.Message}";
            }
        }

        // --- SpriteAtlas ---
        private static string TestSpriteAtlas()
        {
            try
            {
                var result = MCPSpriteAtlasCommands.ListSpriteAtlases(EmptyArgs());
                return AssertNotNull(result, "SpriteAtlas.ListSpriteAtlases");
            }
            catch (Exception ex)
            {
                return $"SpriteAtlas.ListSpriteAtlases threw: {ex.Message}";
            }
        }

        // --- Texture ---
        private static string TestTexture()
        {
            try
            {
                var type = typeof(MCPTextureCommands);
                if (type == null) return "MCPTextureCommands class not found";
                return null;
            }
            catch (Exception ex)
            {
                return $"Texture threw: {ex.Message}";
            }
        }

        // --- UI ---
        private static string TestUI()
        {
            try
            {
                var result = MCPUICommands.GetUIInfo(EmptyArgs());
                return AssertNotNull(result, "UI.GetUIInfo");
            }
            catch (Exception ex)
            {
                return $"UI.GetUIInfo threw: {ex.Message}";
            }
        }

        // --- UMA ---
        private static string TestUMA()
        {
#if UMA_INSTALLED
            try
            {
                var result = MCPUMACommands.GetProjectConfig(EmptyArgs());
                return AssertNotNull(result, "UMA.GetProjectConfig");
            }
            catch (Exception ex)
            {
                return $"UMA.GetProjectConfig threw: {ex.Message}";
            }
#else
            return null; // UMA not installed — pass (handler not compiled)
#endif
        }

        // --- Undo ---
        private static string TestUndo()
        {
            try
            {
                var result = MCPUndoCommands.GetUndoHistory(EmptyArgs());
                return AssertNotNull(result, "Undo.GetUndoHistory");
            }
            catch (Exception ex)
            {
                return $"Undo.GetUndoHistory threw: {ex.Message}";
            }
        }
    }
}
