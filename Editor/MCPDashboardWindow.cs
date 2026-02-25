using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Editor window providing an overview of MCP Bridge status, feature categories,
    /// server controls, settings, and active agent sessions.
    /// Accessible via Window > MCP Dashboard.
    /// </summary>
    public class MCPDashboardWindow : EditorWindow
    {
        private Vector2 _scrollPosition;
        private bool _settingsFoldout = false;
        private bool _agentsFoldout = true;
        private bool _categoriesFoldout = true;
        private bool _testsFoldout = true;
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

        [MenuItem("Window/MCP Dashboard")]
        public static void ShowWindow()
        {
            var window = GetWindow<MCPDashboardWindow>("MCP Dashboard");
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
            DrawCategoryStatus();
            EditorGUILayout.Space(8);
            DrawAgentSessions();
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
            EditorGUILayout.LabelField("Unity MCP Dashboard", _headerStyle, GUILayout.Height(28));
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
            EditorGUILayout.LabelField($"Port {MCPSettingsManager.Port}", GUILayout.Width(70));

            int agents = MCPRequestQueue.ActiveSessionCount;
            if (agents > 0)
            {
                GUI.color = ColorGreen;
                GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
                GUI.color = prevColor;
                EditorGUILayout.LabelField($"{agents} agent{(agents > 1 ? "s" : "")}", GUILayout.Width(65));
            }

            EditorGUILayout.EndHorizontal();
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

                // Test status label
                if (testResult != null && testResult.Status != MCPTestResult.TestStatus.Untested)
                {
                    string statusText = GetTestStatusText(testResult);
                    var statusStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        normal = { textColor = dotColor },
                    };
                    EditorGUILayout.LabelField(statusText, statusStyle, GUILayout.Width(90));

                    // Details button if there's something to show
                    if (testResult.Status == MCPTestResult.TestStatus.Failed ||
                        testResult.Status == MCPTestResult.TestStatus.Warning)
                    {
                        if (GUILayout.Button("?", GUILayout.Width(20), GUILayout.Height(16)))
                        {
                            _expandedTestCategory = _expandedTestCategory == cat ? null : cat;
                        }
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("—", EditorStyles.miniLabel, GUILayout.Width(90));
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
                    return "—";
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

                EditorGUILayout.LabelField(agentId, EditorStyles.boldLabel, GUILayout.Width(140));
                EditorGUILayout.LabelField(action, GUILayout.MinWidth(100));
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField($"{totalObj} actions", GUILayout.Width(70));

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

            // Port
            EditorGUILayout.BeginHorizontal();
            int port = EditorGUILayout.IntField("Server Port", MCPSettingsManager.Port);
            if (port != MCPSettingsManager.Port && port > 1024 && port < 65536)
            {
                MCPSettingsManager.Port = port;
                EditorGUILayout.HelpBox("Restart server to apply.", MessageType.Info);
            }
            EditorGUILayout.EndHorizontal();

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
            EditorGUILayout.LabelField("Plugin Version: 2.0.0", GUILayout.Width(150));
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
