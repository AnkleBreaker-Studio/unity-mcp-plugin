using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Editor window providing an overview of AB Unity MCP status, feature categories,
    /// server controls, queue monitoring, settings, and active agent sessions.
    /// Accessible via Window > AB Unity MCP.
    /// </summary>
    public class MCPDashboardWindow : EditorWindow
    {
        private Vector2 _scrollPosition;
        private bool _settingsFoldout = false;
        private bool _agentsFoldout = true;
        private bool _categoriesFoldout = true;
        private bool _queueFoldout = true;
        private bool _contextFoldout = true;
        private bool _testsFoldout = true;
        private bool _recentActionsFoldout = true;
        private string _expandedTestCategory = null;

        private static readonly Color ColorGreen  = new Color(0.2f, 0.8f, 0.2f);
        private static readonly Color ColorRed    = new Color(0.9f, 0.2f, 0.2f);
        private static readonly Color ColorYellow = new Color(0.9f, 0.8f, 0.1f);
        private static readonly Color ColorGrey   = new Color(0.5f, 0.5f, 0.5f);
        private static readonly Color ColorBlue   = new Color(0.4f, 0.7f, 1.0f);

        private GUIStyle _headerStyle;
        private GUIStyle _subHeaderStyle;
        private GUIStyle _dotStyle;
        private bool _stylesInitialized;

        [MenuItem("Window/AB Unity MCP")]
        public static void ShowWindow()
        {
            var window = GetWindow<MCPDashboardWindow>("AB Unity MCP");
            window.minSize = new Vector2(340, 500);
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;

            _headerStyle = new GUIStyle(EditorStyles.largeLabel)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
            };

            _subHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12,
            };

            _dotStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleCenter,
                fixedWidth = 22,
            };

            _stylesInitialized = true;
        }

        private void OnInspectorUpdate()
        {
            // Repaint periodically for live status
            Repaint();
        }

        private void OnGUI()
        {
            InitStyles();
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            DrawHeader();
            EditorGUILayout.Space(6);
            DrawConnectionStatus();
            EditorGUILayout.Space(4);
            DrawServerControls();
            EditorGUILayout.Space(8);
            DrawQueueStatus();
            EditorGUILayout.Space(8);
            DrawProjectContext();
            EditorGUILayout.Space(8);
            DrawAgentSessions();
            EditorGUILayout.Space(8);
            DrawRecentActions();
            EditorGUILayout.Space(8);
            DrawCategoryStatus();
            EditorGUILayout.Space(8);
            DrawSettings();
            EditorGUILayout.Space(8);
            DrawVersionInfo();

            EditorGUILayout.EndScrollView();
        }

        // ─── Header ───

        private void DrawHeader()
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField("AnkleBreaker Unity MCP", _headerStyle, GUILayout.Height(28));
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        // ─── Connection Status ───

        private void DrawConnectionStatus()
        {
            bool running = MCPBridgeServer.IsRunning;

            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            // Status dot
            var prevColor = GUI.color;
            GUI.color = running ? ColorGreen : ColorRed;
            GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
            GUI.color = prevColor;

            EditorGUILayout.LabelField(
                running ? "Server Running" : "Server Stopped",
                EditorStyles.boldLabel);

            GUILayout.FlexibleSpace();

            // Show actual active port when running, settings port when stopped
            int displayPort = running ? MCPBridgeServer.ActivePort : MCPSettingsManager.Port;
            string portLabel = running && !MCPSettingsManager.UseManualPort
                ? $"Port {displayPort} (auto)"
                : $"Port {displayPort}";
            EditorGUILayout.LabelField(portLabel, GUILayout.Width(100));

            // Cache values once per event to prevent Layout/Repaint mismatch.
            // Using local bools ensures the same controls exist in both passes.
            int agents = MCPRequestQueue.ActiveSessionCount;
            int queued = MCPRequestQueue.TotalQueuedCount;
            bool showAgents = agents > 0;
            bool showQueued = queued > 0;

            // Always draw the same number of controls regardless of state —
            // hide them with alpha when inactive to avoid IMGUI control count mismatch.
            var savedAlpha = GUI.color.a;

            // Agent count indicator
            GUI.color = showAgents ? ColorGreen : new Color(0, 0, 0, 0);
            GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
            GUI.color = showAgents ? new Color(prevColor.r, prevColor.g, prevColor.b, savedAlpha) : new Color(0, 0, 0, 0);
            EditorGUILayout.LabelField(showAgents ? $"{agents} agent{(agents > 1 ? "s" : "")}" : "", GUILayout.Width(65));

            // Queue count indicator
            GUI.color = showQueued ? ColorYellow : new Color(0, 0, 0, 0);
            GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
            GUI.color = showQueued ? new Color(prevColor.r, prevColor.g, prevColor.b, savedAlpha) : new Color(0, 0, 0, 0);
            EditorGUILayout.LabelField(showQueued ? $"{queued} queued" : "", GUILayout.Width(65));

            GUI.color = prevColor;

            EditorGUILayout.EndHorizontal();

            // ParrelSync clone indicator (shown below the main status bar)
            if (MCPInstanceRegistry.IsParrelSyncClone())
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(24);
                var cloneStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = ColorBlue },
                    fontStyle = FontStyle.Italic,
                };
                int cloneIdx = MCPInstanceRegistry.GetParrelSyncCloneIndex();
                EditorGUILayout.LabelField(
                    $"\u2937 ParrelSync Clone #{cloneIdx}",
                    cloneStyle);
                EditorGUILayout.EndHorizontal();
            }
        }

        // ─── Server Controls ───

        private void DrawServerControls()
        {
            EditorGUILayout.BeginHorizontal();

            bool running = MCPBridgeServer.IsRunning;

            GUI.enabled = !running;
            if (GUILayout.Button("Start", GUILayout.Height(24)))
                MCPBridgeServer.Start();

            GUI.enabled = running;
            if (GUILayout.Button("Stop", GUILayout.Height(24)))
                MCPBridgeServer.Stop();

            GUI.enabled = true;
            if (GUILayout.Button("Restart", GUILayout.Height(24)))
            {
                MCPBridgeServer.Stop();
                EditorApplication.delayCall += () => MCPBridgeServer.Start();
            }

            EditorGUILayout.EndHorizontal();
        }

        // ─── Queue Status (Multi-Agent) ───

        private void DrawQueueStatus()
        {
            _queueFoldout = EditorGUILayout.Foldout(_queueFoldout, "Request Queue", true, EditorStyles.foldoutHeader);
            if (!_queueFoldout) return;

            var queueInfo = MCPRequestQueue.GetQueueInfo();

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Summary row
            EditorGUILayout.BeginHorizontal();

            int totalQueued = 0;
            if (queueInfo.ContainsKey("totalQueued"))
                int.TryParse(queueInfo["totalQueued"].ToString(), out totalQueued);

            int activeAgents = 0;
            if (queueInfo.ContainsKey("activeAgents"))
                int.TryParse(queueInfo["activeAgents"].ToString(), out activeAgents);

            int cacheSize = 0;
            if (queueInfo.ContainsKey("completedCacheSize"))
                int.TryParse(queueInfo["completedCacheSize"].ToString(), out cacheSize);

            var prevColor = GUI.color;
            GUI.color = totalQueued > 0 ? ColorYellow : ColorGreen;
            GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
            GUI.color = prevColor;

            string statusText = totalQueued > 0
                ? $"{totalQueued} pending  |  {activeAgents} agents  |  {cacheSize} cached"
                : $"Idle  |  {activeAgents} agents  |  {cacheSize} cached";
            EditorGUILayout.LabelField(statusText, EditorStyles.miniLabel);

            EditorGUILayout.EndHorizontal();

            // Per-agent breakdown (if any queued)
            if (queueInfo.ContainsKey("perAgentQueued") && queueInfo["perAgentQueued"] is Dictionary<string, object> perAgent)
            {
                if (perAgent.Count > 0)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Per-Agent Queue Depth:", EditorStyles.miniLabel);

                    foreach (var kvp in perAgent)
                    {
                        EditorGUILayout.BeginHorizontal();
                        GUILayout.Space(24);

                        int depth = 0;
                        int.TryParse(kvp.Value.ToString(), out depth);

                        var agentColor = depth > 0 ? ColorYellow : ColorGreen;
                        GUI.color = agentColor;
                        GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
                        GUI.color = prevColor;

                        EditorGUILayout.LabelField(kvp.Key, GUILayout.Width(160));
                        EditorGUILayout.LabelField($"{depth} pending", GUILayout.Width(80));

                        EditorGUILayout.EndHorizontal();
                    }
                }
            }

            EditorGUILayout.EndVertical();
        }

        // ─── Project Context ───

        private void DrawProjectContext()
        {
            _contextFoldout = EditorGUILayout.Foldout(_contextFoldout, "Project Context", true, EditorStyles.foldoutHeader);
            if (!_contextFoldout) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Enabled toggle
            EditorGUILayout.BeginHorizontal();
            bool enabled = EditorGUILayout.Toggle("Enable Context", MCPSettingsManager.ContextEnabled);
            if (enabled != MCPSettingsManager.ContextEnabled)
                MCPSettingsManager.ContextEnabled = enabled;
            GUILayout.FlexibleSpace();

            // Buttons
            if (GUILayout.Button("Create Templates", GUILayout.Width(110), GUILayout.Height(18)))
            {
                int created = MCPContextManager.CreateDefaultTemplates();
                if (created > 0)
                    EditorUtility.DisplayDialog("Templates Created",
                        $"Created {created} template file(s) in:\n{MCPSettingsManager.ContextPath}", "OK");
                else
                    EditorUtility.DisplayDialog("Templates Exist",
                        "All template files already exist.", "OK");
            }

            if (GUILayout.Button("Open Folder", GUILayout.Width(90), GUILayout.Height(18)))
            {
                string folderPath = MCPContextManager.GetContextFolderPath();
                if (System.IO.Directory.Exists(folderPath))
                    EditorUtility.RevealInFinder(folderPath);
                else
                    EditorUtility.DisplayDialog("Folder Not Found",
                        $"Context folder does not exist yet.\nClick 'Create Templates' to set it up.\n\n{folderPath}", "OK");
            }

            EditorGUILayout.EndHorizontal();

            if (!enabled)
            {
                EditorGUILayout.HelpBox("Project context is disabled. Agents will not receive project documentation.", MessageType.Info);
                EditorGUILayout.EndVertical();
                return;
            }

            // Path display
            EditorGUILayout.LabelField("Path:", MCPSettingsManager.ContextPath, EditorStyles.miniLabel);

            // File list
            var files = MCPContextManager.GetContextFileList();
            bool anyFiles = false;

            foreach (var file in files)
            {
                if (!file.IsStandard && !file.Exists) continue; // Don't show missing custom files

                anyFiles = true;
                EditorGUILayout.BeginHorizontal();

                var prevColor = GUI.color;
                if (file.Exists && file.SizeBytes > 0)
                    GUI.color = ColorGreen;
                else if (file.Exists)
                    GUI.color = ColorYellow;
                else
                    GUI.color = ColorGrey;

                GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
                GUI.color = prevColor;

                string displayName = file.Category;
                EditorGUILayout.LabelField(displayName, GUILayout.MinWidth(140));

                if (file.Exists)
                {
                    string sizeLabel = file.SizeBytes > 1024
                        ? $"{file.SizeBytes / 1024f:0.#} KB"
                        : $"{file.SizeBytes} B";
                    EditorGUILayout.LabelField(
                        file.SizeBytes == 0 ? "empty" : sizeLabel,
                        EditorStyles.miniLabel, GUILayout.Width(60));
                }
                else
                {
                    EditorGUILayout.LabelField("not created", EditorStyles.miniLabel, GUILayout.Width(60));
                }

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }

            if (!anyFiles)
            {
                EditorGUILayout.HelpBox(
                    "No context files found. Click 'Create Templates' to get started.",
                    MessageType.Info);
            }

            EditorGUILayout.EndVertical();
        }

        // ─── Recent Actions ───

        private void DrawRecentActions()
        {
            _recentActionsFoldout = EditorGUILayout.Foldout(_recentActionsFoldout, "Recent Actions", true, EditorStyles.foldoutHeader);
            if (!_recentActionsFoldout) return;

            var recent = MCPActionHistory.GetRecent(8);

            if (recent.Count == 0)
            {
                EditorGUILayout.HelpBox("No actions recorded yet.", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Show newest first
            for (int i = recent.Count - 1; i >= 0; i--)
            {
                var r = recent[i];
                EditorGUILayout.BeginHorizontal();

                // Status dot
                var prevColor = GUI.color;
                Color dotColor;
                switch (r.Status)
                {
                    case "Completed": dotColor = ColorGreen; break;
                    case "Failed":    dotColor = ColorRed;   break;
                    default:          dotColor = ColorYellow; break;
                }
                GUI.color = dotColor;
                GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
                GUI.color = prevColor;

                // Timestamp
                EditorGUILayout.LabelField(r.Timestamp.ToString("HH:mm:ss"),
                    EditorStyles.miniLabel, GUILayout.Width(55));

                // Agent (short)
                string agent = r.AgentId ?? "?";
                if (agent.Length > 10) agent = agent.Substring(0, 8) + "..";
                prevColor = GUI.color;
                GUI.color = ColorBlue;
                EditorGUILayout.LabelField(agent, EditorStyles.miniLabel, GUILayout.Width(65));
                GUI.color = prevColor;

                // Action command
                string cmd = MCPActionRecord.ExtractCommand(r.ActionName);
                EditorGUILayout.LabelField(cmd, EditorStyles.miniLabel, GUILayout.Width(100));

                // Target (truncated)
                string target = r.TargetPath ?? "";
                if (target.Length > 25)
                    target = ".." + target.Substring(target.Length - 23);
                EditorGUILayout.LabelField(target, EditorStyles.miniLabel);

                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
            }

            // Open full history button
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            string btnLabel = MCPActionHistory.Count > 8
                ? $"Open Full History ({MCPActionHistory.Count} actions)"
                : "Open Full History";
            if (GUILayout.Button(btnLabel, GUILayout.Width(200), GUILayout.Height(20)))
            {
                MCPActionHistoryWindow.ShowWindow();
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        // ─── Feature Categories + Test Status ───

        private void DrawCategoryStatus()
        {
            _categoriesFoldout = EditorGUILayout.Foldout(_categoriesFoldout, "Feature Categories", true, EditorStyles.foldoutHeader);
            if (!_categoriesFoldout) return;

            // Test controls bar
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);

            // Summary
            int passed = MCPSelfTest.PassedCount;
            int failed = MCPSelfTest.FailedCount;
            int warnings = MCPSelfTest.WarningCount;
            int total = MCPSettingsManager.GetAllCategoryNames().Length;

            if (MCPSelfTest.IsRunning)
            {
                EditorGUILayout.LabelField(
                    $"Testing: {MCPSelfTest.CurrentCategory}...",
                    EditorStyles.miniLabel);
                var rect = GUILayoutUtility.GetRect(100, 16, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(rect, MCPSelfTest.Progress, $"{(int)(MCPSelfTest.Progress * 100)}%");
            }
            else if (MCPSelfTest.LastRunTime > System.DateTime.MinValue)
            {
                string summary = "";
                if (failed > 0)
                    summary += $"<color=#E63333>{failed} failed</color>  ";
                if (warnings > 0)
                    summary += $"<color=#E6CC11>{warnings} warn</color>  ";
                summary += $"<color=#33CC33>{passed}/{total} passed</color>";

                var richStyle = new GUIStyle(EditorStyles.miniLabel) { richText = true };
                EditorGUILayout.LabelField(summary, richStyle, GUILayout.ExpandWidth(true));
            }
            else
            {
                EditorGUILayout.LabelField("No tests run yet", EditorStyles.miniLabel);
            }

            GUILayout.FlexibleSpace();

            GUI.enabled = !MCPSelfTest.IsRunning && MCPBridgeServer.IsRunning;
            if (GUILayout.Button("Run Tests", GUILayout.Width(80), GUILayout.Height(20)))
            {
                MCPSelfTest.RunAllAsync();
            }
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            // Category rows
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            string[] categories = MCPSettingsManager.GetAllCategoryNames();
            foreach (var cat in categories)
            {
                bool enabled = MCPSettingsManager.IsCategoryEnabled(cat);
                var testResult = MCPSelfTest.GetResult(cat);

                EditorGUILayout.BeginHorizontal();

                // Status dot — reflects test status when available, else enabled/disabled
                var prevColor = GUI.color;
                Color dotColor = GetCategoryDotColor(enabled, testResult);
                GUI.color = dotColor;
                GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
                GUI.color = prevColor;

                // Pretty name
                string displayName = char.ToUpper(cat[0]) + cat.Substring(1);
                EditorGUILayout.LabelField(displayName, GUILayout.Width(100));

                // Test status label — always draw both controls to avoid IMGUI control count mismatch
                bool hasTested = testResult != null && testResult.Status != MCPTestResult.TestStatus.Untested;
                bool hasDetails = hasTested && (testResult.Status == MCPTestResult.TestStatus.Failed ||
                    testResult.Status == MCPTestResult.TestStatus.Warning);

                if (hasTested)
                {
                    string statusLabel = GetTestStatusText(testResult);
                    var statusStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        normal = { textColor = dotColor },
                    };
                    EditorGUILayout.LabelField(statusLabel, statusStyle, GUILayout.Width(90));
                }
                else
                {
                    EditorGUILayout.LabelField("\u2014", EditorStyles.miniLabel, GUILayout.Width(90));
                }

                // Always draw the details button to keep control count stable
                if (hasDetails)
                {
                    if (GUILayout.Button("?", GUILayout.Width(20), GUILayout.Height(16)))
                    {
                        _expandedTestCategory = _expandedTestCategory == cat ? null : cat;
                    }
                }
                else
                {
                    // Invisible placeholder — same control, no visual
                    var transparent = GUI.color;
                    GUI.color = new Color(0, 0, 0, 0);
                    GUILayout.Button("", GUILayout.Width(20), GUILayout.Height(16));
                    GUI.color = transparent;
                }

                GUILayout.FlexibleSpace();

                bool newEnabled = EditorGUILayout.Toggle(enabled, GUILayout.Width(30));
                if (newEnabled != enabled)
                    MCPSettingsManager.SetCategoryEnabled(cat, newEnabled);

                EditorGUILayout.EndHorizontal();

                // Expanded error details
                if (_expandedTestCategory == cat && testResult != null &&
                    !string.IsNullOrEmpty(testResult.Details))
                {
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.SelectableLabel(
                        testResult.Details,
                        EditorStyles.wordWrappedMiniLabel,
                        GUILayout.MinHeight(36));
                    EditorGUILayout.EndVertical();
                }
            }

            EditorGUILayout.EndVertical();
        }

        private Color GetCategoryDotColor(bool enabled, MCPTestResult result)
        {
            if (!enabled) return ColorGrey;
            if (result == null || result.Status == MCPTestResult.TestStatus.Untested)
                return enabled ? ColorGreen : ColorGrey;

            switch (result.Status)
            {
                case MCPTestResult.TestStatus.Passed:  return ColorGreen;
                case MCPTestResult.TestStatus.Warning: return ColorYellow;
                case MCPTestResult.TestStatus.Failed:  return ColorRed;
                default: return ColorGrey;
            }
        }

        private string GetTestStatusText(MCPTestResult result)
        {
            switch (result.Status)
            {
                case MCPTestResult.TestStatus.Passed:
                    return $"\u2713 {result.DurationMs:0}ms";
                case MCPTestResult.TestStatus.Warning:
                    return $"\u26A0 {result.Message}";
                case MCPTestResult.TestStatus.Failed:
                    return $"\u2717 {result.Message}";
                default:
                    return "\u2014";
            }
        }

        // ─── Agent Sessions ───

        private void DrawAgentSessions()
        {
            _agentsFoldout = EditorGUILayout.Foldout(_agentsFoldout, "Active Agent Sessions", true, EditorStyles.foldoutHeader);
            if (!_agentsFoldout) return;

            var sessions = MCPRequestQueue.GetActiveSessions();

            if (sessions.Count == 0)
            {
                EditorGUILayout.HelpBox("No active agent sessions.", MessageType.Info);
                return;
            }

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            foreach (var session in sessions)
            {
                EditorGUILayout.BeginHorizontal();

                var prevColor = GUI.color;
                GUI.color = ColorGreen;
                GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
                GUI.color = prevColor;

                string agentId = session.ContainsKey("agentId") ? session["agentId"].ToString() : "?";
                string action = session.ContainsKey("currentAction") ? session["currentAction"].ToString() : "idle";
                object totalObj = session.ContainsKey("totalActions") ? session["totalActions"] : 0;
                object queuedObj = session.ContainsKey("queuedRequests") ? session["queuedRequests"] : 0;
                object completedObj = session.ContainsKey("completedRequests") ? session["completedRequests"] : 0;
                object avgMs = session.ContainsKey("averageResponseTimeMs") ? session["averageResponseTimeMs"] : 0;

                EditorGUILayout.LabelField(agentId, EditorStyles.boldLabel, GUILayout.Width(160));
                EditorGUILayout.LabelField(action, GUILayout.MinWidth(80));
                GUILayout.FlexibleSpace();

                // Queue + completed stats
                int queuedInt = 0;
                int.TryParse(queuedObj.ToString(), out queuedInt);

                var richStyle = new GUIStyle(EditorStyles.miniLabel) { richText = true };
                string stats = $"{totalObj} total";
                if (queuedInt > 0)
                    stats += $"  <color=#E6CC11>{queuedInt}q</color>";
                stats += $"  <color=#33CC33>{completedObj}ok</color>";
                stats += $"  {avgMs}ms";

                EditorGUILayout.LabelField(stats, richStyle, GUILayout.Width(170));

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
        }

        // ─── Settings ───

        private void DrawSettings()
        {
            _settingsFoldout = EditorGUILayout.Foldout(_settingsFoldout, "Settings", true, EditorStyles.foldoutHeader);
            if (!_settingsFoldout) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Auto-start
            bool autoStart = EditorGUILayout.Toggle("Auto-start on Editor Load", MCPSettingsManager.AutoStart);
            if (autoStart != MCPSettingsManager.AutoStart)
                MCPSettingsManager.AutoStart = autoStart;

            EditorGUILayout.Space(2);

            // Port mode toggle
            bool useManual = EditorGUILayout.Toggle("Use Manual Port", MCPSettingsManager.UseManualPort);
            if (useManual != MCPSettingsManager.UseManualPort)
                MCPSettingsManager.UseManualPort = useManual;

            if (useManual)
            {
                // Manual port entry
                EditorGUILayout.BeginHorizontal();
                int port = EditorGUILayout.IntField("Server Port", MCPSettingsManager.Port);
                if (port != MCPSettingsManager.Port && port > 1024 && port < 65536)
                {
                    MCPSettingsManager.Port = port;
                }
                EditorGUILayout.EndHorizontal();

                if (MCPBridgeServer.IsRunning && MCPBridgeServer.ActivePort != MCPSettingsManager.Port)
                    EditorGUILayout.HelpBox("Restart server to apply port change.", MessageType.Info);
            }
            else
            {
                // Auto-select info
                string autoInfo = MCPBridgeServer.IsRunning
                    ? $"Auto-selected port {MCPBridgeServer.ActivePort} (range: {MCPInstanceRegistry.PortRangeStart}-{MCPInstanceRegistry.PortRangeEnd})"
                    : $"Will auto-select from range {MCPInstanceRegistry.PortRangeStart}-{MCPInstanceRegistry.PortRangeEnd}";
                EditorGUILayout.HelpBox(autoInfo, MessageType.None);
            }

            EditorGUILayout.Space(4);

            // Reset button
            if (GUILayout.Button("Reset All Settings to Defaults"))
            {
                if (EditorUtility.DisplayDialog("Reset Settings",
                    "Reset all MCP settings to defaults?", "Reset", "Cancel"))
                {
                    MCPSettingsManager.ResetToDefaults();
                }
            }

            EditorGUILayout.EndVertical();
        }

        // ─── Version Info ───

        private void DrawVersionInfo()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Plugin Version: 2.15.0", GUILayout.Width(155));
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Check for Updates", GUILayout.Width(130)))
            {
                MCPUpdateChecker.CheckForUpdates((hasUpdate, latestVersion) =>
                {
                    if (hasUpdate)
                    {
                        EditorUtility.DisplayDialog("Update Available",
                            $"A new version ({latestVersion}) is available.\n" +
                            "Update via Unity Package Manager.",
                            "OK");
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("Up to Date",
                            "You are running the latest version.", "OK");
                    }
                });
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}
