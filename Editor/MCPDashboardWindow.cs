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

        private static readonly Color ColorGreen  = new Color(0.2f, 0.8f, 0.2f);
        private static readonly Color ColorRed    = new Color(0.9f, 0.2f, 0.2f);
        private static readonly Color ColorYellow = new Color(0.9f, 0.8f, 0.1f);
        private static readonly Color ColorGrey   = new Color(0.5f, 0.5f, 0.5f);

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

        // ─── Feature Categories ───

        private void DrawCategoryStatus()
        {
            _categoriesFoldout = EditorGUILayout.Foldout(_categoriesFoldout, "Feature Categories", true, EditorStyles.foldoutHeader);
            if (!_categoriesFoldout) return;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            string[] categories = MCPSettingsManager.GetAllCategoryNames();
            foreach (var cat in categories)
            {
                bool enabled = MCPSettingsManager.IsCategoryEnabled(cat);

                EditorGUILayout.BeginHorizontal();

                var prevColor = GUI.color;
                GUI.color = enabled ? ColorGreen : ColorGrey;
                GUILayout.Label("\u25CF", _dotStyle, GUILayout.Width(22));
                GUI.color = prevColor;

                // Pretty name
                string displayName = char.ToUpper(cat[0]) + cat.Substring(1);
                EditorGUILayout.LabelField(displayName, GUILayout.Width(120));

                GUILayout.FlexibleSpace();

                bool newEnabled = EditorGUILayout.Toggle(enabled, GUILayout.Width(30));
                if (newEnabled != enabled)
                    MCPSettingsManager.SetCategoryEnabled(cat, newEnabled);

                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndVertical();
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
