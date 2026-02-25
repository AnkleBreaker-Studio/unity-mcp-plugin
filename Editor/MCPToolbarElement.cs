using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

#if UNITY_6000_3_OR_NEWER
using UnityEditor.Toolbars;
#else
using System.Reflection;
using UnityEngine.UIElements;
#endif

namespace UnityMCP.Editor
{
    /// <summary>
    /// Adds MCP status elements to Unity's main editor toolbar.
    /// On Unity 6000.3+ uses the official MainToolbar API.
    /// On older versions, falls back to reflection-based injection.
    /// </summary>
    public static class MCPToolbarElement
    {
        // ─── Shared state ────────────────────────────────────────────────
        internal static bool ServerRunning;
        internal static int ActiveAgents;
        internal static bool HasFailures;
        internal static bool HasWarnings;

        internal static string StatusText
        {
            get
            {
                string prefix = ServerRunning ? "\u25CF" : "\u25CB"; // ● or ○
                string label = $"{prefix} MCP";
                if (ActiveAgents > 0)
                    label += $" [{ActiveAgents}]";
                return label;
            }
        }

        internal static string StatusTooltip
        {
            get
            {
                if (!ServerRunning)
                    return "MCP Bridge \u2014 Stopped\nClick to open Dashboard";

                string tip = $"MCP Bridge \u2014 Running on port {MCPSettingsManager.Port}";
                if (ActiveAgents > 0)
                    tip += $"\n{ActiveAgents} active agent{(ActiveAgents > 1 ? "s" : "")}";
                if (HasFailures)
                    tip += "\nSelf-test failures detected";
                else if (HasWarnings)
                    tip += "\nSelf-test warnings detected";
                tip += "\nClick to open Dashboard";
                return tip;
            }
        }

        // ─── Periodic refresh ────────────────────────────────────────────

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.update += PeriodicRefresh;
        }

        private static double _nextRefreshTime;

        private static void PeriodicRefresh()
        {
            if (EditorApplication.timeSinceStartup < _nextRefreshTime) return;
            _nextRefreshTime = EditorApplication.timeSinceStartup + 1.0;

            bool changed = false;

            bool running = MCPBridgeServer.IsRunning;
            if (running != ServerRunning) { ServerRunning = running; changed = true; }

            int agents = MCPRequestQueue.ActiveSessionCount;
            if (agents != ActiveAgents) { ActiveAgents = agents; changed = true; }

            bool failures = MCPSelfTest.HasFailures;
            if (failures != HasFailures) { HasFailures = failures; changed = true; }

            bool warnings = MCPSelfTest.HasWarnings;
            if (warnings != HasWarnings) { HasWarnings = warnings; changed = true; }

            if (changed)
            {
#if UNITY_6000_3_OR_NEWER
                try
                {
                    MainToolbar.Refresh("MCP/Status");
                    MainToolbar.Refresh("MCP/Actions");
                }
                catch { /* MainToolbar may not be ready yet */ }
#else
                MCPToolbarFallback.RefreshMainToolbar();
#endif
            }
        }

        // ─── Shared dropdown menu builder ────────────────────────────────

        internal static void BuildMenu(GenericMenu menu)
        {
            bool running = MCPBridgeServer.IsRunning;

            // Status header
            menu.AddDisabledItem(new GUIContent(
                running
                    ? $"\u25CF  Running \u2014 Port {MCPSettingsManager.Port}"
                    : "\u25CF  Stopped"));

            menu.AddSeparator("");

            // Server controls
            if (running)
            {
                menu.AddItem(new GUIContent("Stop Server"), false, () => MCPBridgeServer.Stop());
                menu.AddItem(new GUIContent("Restart Server"), false, () =>
                {
                    MCPBridgeServer.Stop();
                    EditorApplication.delayCall += () => MCPBridgeServer.Start();
                });
            }
            else
            {
                menu.AddItem(new GUIContent("Start Server"), false, () => MCPBridgeServer.Start());
            }

            menu.AddSeparator("");

            // Agent sessions
            int agents = MCPRequestQueue.ActiveSessionCount;
            if (agents > 0)
            {
                menu.AddDisabledItem(new GUIContent($"Agents ({agents} active)"));
                var sessions = MCPRequestQueue.GetActiveSessions();
                foreach (var session in sessions)
                {
                    string agentId = session.ContainsKey("agentId") ? session["agentId"].ToString() : "?";
                    string action = session.ContainsKey("currentAction") ? session["currentAction"].ToString() : "idle";
                    menu.AddDisabledItem(new GUIContent($"   {agentId}: {action}"));
                }
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("No active agents"));
            }

            menu.AddSeparator("");

            // Category toggles
            menu.AddItem(new GUIContent("Categories/Enable All"), false, () =>
            {
                foreach (var cat in MCPSettingsManager.GetAllCategoryNames())
                    MCPSettingsManager.SetCategoryEnabled(cat, true);
            });
            menu.AddItem(new GUIContent("Categories/Disable All"), false, () =>
            {
                foreach (var cat in MCPSettingsManager.GetAllCategoryNames())
                    MCPSettingsManager.SetCategoryEnabled(cat, false);
            });
            menu.AddSeparator("Categories/");
            foreach (var cat in MCPSettingsManager.GetAllCategoryNames())
            {
                bool enabled = MCPSettingsManager.IsCategoryEnabled(cat);
                string displayName = char.ToUpper(cat[0]) + cat.Substring(1);
                string catCapture = cat;
                menu.AddItem(new GUIContent($"Categories/{displayName}"), enabled, () =>
                {
                    MCPSettingsManager.SetCategoryEnabled(catCapture, !enabled);
                });
            }

            menu.AddSeparator("");

            // Tests
            if (running && !MCPSelfTest.IsRunning)
            {
                string testLabel = "Run Tests";
                if (MCPSelfTest.LastRunTime > DateTime.MinValue)
                {
                    int f = MCPSelfTest.FailedCount;
                    int p = MCPSelfTest.PassedCount;
                    testLabel = f > 0 ? $"Run Tests  ({f} failed)" : $"Run Tests  ({p} passed)";
                }
                menu.AddItem(new GUIContent(testLabel), false, () => MCPSelfTest.RunAllAsync());
            }
            else if (MCPSelfTest.IsRunning)
            {
                menu.AddDisabledItem(new GUIContent("Tests running..."));
            }

            menu.AddSeparator("");

            // Settings
            menu.AddItem(
                new GUIContent("Auto-Start on Load"),
                MCPSettingsManager.AutoStart,
                () => MCPSettingsManager.AutoStart = !MCPSettingsManager.AutoStart);

            menu.AddSeparator("");

            // Dashboard & Updates
            menu.AddItem(new GUIContent("Open Dashboard..."), false, () => MCPDashboardWindow.ShowWindow());
            menu.AddItem(new GUIContent("Check for Updates..."), false, () =>
            {
                MCPUpdateChecker.CheckForUpdates((hasUpdate, latestVersion) =>
                {
                    EditorUtility.DisplayDialog(
                        hasUpdate ? "Update Available" : "Up to Date",
                        hasUpdate
                            ? $"A new version ({latestVersion}) is available.\nUpdate via Unity Package Manager."
                            : "You are running the latest version.",
                        "OK");
                });
            });
        }

#if UNITY_6000_3_OR_NEWER
        // ═══════════════════════════════════════════════════════════════════
        // Unity 6000.3+: Official MainToolbar API
        // ═══════════════════════════════════════════════════════════════════

        [MainToolbarElement("MCP/Status",
            defaultDockPosition = MainToolbarDockPosition.Right)]
        public static MainToolbarElement CreateStatusButton()
        {
            ServerRunning = MCPBridgeServer.IsRunning;
            ActiveAgents = MCPRequestQueue.ActiveSessionCount;
            HasFailures = MCPSelfTest.HasFailures;
            HasWarnings = MCPSelfTest.HasWarnings;

            var content = new MainToolbarContent(StatusText, tooltip: StatusTooltip);
            return new MainToolbarButton(content, () => MCPDashboardWindow.ShowWindow());
        }

        [MainToolbarElement("MCP/Actions",
            defaultDockPosition = MainToolbarDockPosition.Right)]
        public static MainToolbarElement CreateActionsDropdown()
        {
            var content = new MainToolbarContent("\u25BE", tooltip: "MCP Actions");
            return new MainToolbarDropdown(content, ShowMenuFromRect);
        }

        private static void ShowMenuFromRect(Rect buttonRect)
        {
            var menu = new GenericMenu();
            BuildMenu(menu);
            menu.ShowAsContext();
        }
#endif
    }

#if !UNITY_6000_3_OR_NEWER
    // ═══════════════════════════════════════════════════════════════════════
    // Pre-6000.3 Fallback: Reflection-based main toolbar injection
    // ═══════════════════════════════════════════════════════════════════════

    [InitializeOnLoad]
    internal static class MCPToolbarFallback
    {
        private static bool _injected;
        private static VisualElement _mcpRoot;
        private static VisualElement _statusDot;
        private static Label _statusLabel;
        private static Label _agentBadge;
        private static int _retryCount;
        private const int MaxRetries = 30;

        private static readonly Color kRunning = new Color(0.30f, 0.85f, 0.40f);
        private static readonly Color kStopped = new Color(0.90f, 0.25f, 0.25f);
        private static readonly Color kWarning = new Color(0.90f, 0.80f, 0.10f);
        private static readonly Color kBadgeBg = new Color(0.40f, 0.75f, 1.00f);

        static MCPToolbarFallback()
        {
            EditorApplication.update += TryInject;
        }

        private static void TryInject()
        {
            if (_injected || _retryCount >= MaxRetries)
            {
                EditorApplication.update -= TryInject;
                if (!_injected && _retryCount >= MaxRetries)
                    Debug.Log("[MCP Toolbar] Main toolbar injection not available on this Unity version. Use Unity 6000.3+ for native toolbar support.");
                return;
            }
            _retryCount++;

            try
            {
                var toolbarType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.Toolbar");
                if (toolbarType == null) return;

                var toolbars = Resources.FindObjectsOfTypeAll(toolbarType);
                if (toolbars == null || toolbars.Length == 0) return;

                var toolbar = toolbars[0];

                var rootProp = toolbarType.GetProperty("rootVisualElement",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (rootProp == null) return;

                var root = rootProp.GetValue(toolbar) as VisualElement;
                if (root == null || root.childCount == 0) return;

                var target = root.Q("ToolbarZoneRightAlign")
                    ?? root.Q(className: "unity-toolbar-zone-align-right")
                    ?? root.Q(className: "unity-editor-toolbar-container__right");

                if (target == null) return;

                _mcpRoot = BuildElement();
                target.Insert(0, _mcpRoot);
                _injected = true;
                EditorApplication.update -= TryInject;

                _mcpRoot.schedule.Execute(() => RefreshMainToolbar()).Every(1000);
                Debug.Log("[MCP Toolbar] Injected into main toolbar (legacy mode).");
            }
            catch (Exception ex)
            {
                if (_retryCount >= MaxRetries)
                    Debug.LogWarning($"[MCP Toolbar] Legacy injection failed: {ex.Message}");
            }
        }

        private static VisualElement BuildElement()
        {
            var container = new VisualElement();
            container.name = "mcp-toolbar-element";
            container.style.flexDirection = FlexDirection.Row;
            container.style.alignItems = Align.Center;
            container.style.marginLeft = 4;
            container.style.marginRight = 4;
            container.style.paddingLeft = 6;
            container.style.paddingRight = 6;
            container.style.borderLeftWidth = 1;
            container.style.borderLeftColor = new Color(0.15f, 0.15f, 0.15f, 0.4f);

            container.RegisterCallback<ClickEvent>(evt => MCPDashboardWindow.ShowWindow());
            container.RegisterCallback<MouseEnterEvent>(evt =>
                container.style.backgroundColor = new Color(1f, 1f, 1f, 0.06f));
            container.RegisterCallback<MouseLeaveEvent>(evt =>
                container.style.backgroundColor = Color.clear);

            _statusDot = new VisualElement();
            _statusDot.style.width = 8;
            _statusDot.style.height = 8;
            _statusDot.style.borderTopLeftRadius = 4;
            _statusDot.style.borderTopRightRadius = 4;
            _statusDot.style.borderBottomLeftRadius = 4;
            _statusDot.style.borderBottomRightRadius = 4;
            _statusDot.style.marginRight = 5;
            _statusDot.style.backgroundColor = kStopped;
            container.Add(_statusDot);

            _statusLabel = new Label("MCP");
            _statusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _statusLabel.style.fontSize = 11;
            _statusLabel.style.color = new Color(0.78f, 0.78f, 0.78f);
            container.Add(_statusLabel);

            _agentBadge = new Label();
            _agentBadge.style.unityTextAlign = TextAnchor.MiddleCenter;
            _agentBadge.style.fontSize = 9;
            _agentBadge.style.color = Color.white;
            _agentBadge.style.backgroundColor = kBadgeBg;
            _agentBadge.style.borderTopLeftRadius = 6;
            _agentBadge.style.borderTopRightRadius = 6;
            _agentBadge.style.borderBottomLeftRadius = 6;
            _agentBadge.style.borderBottomRightRadius = 6;
            _agentBadge.style.paddingLeft = 4;
            _agentBadge.style.paddingRight = 4;
            _agentBadge.style.paddingTop = 1;
            _agentBadge.style.paddingBottom = 1;
            _agentBadge.style.marginLeft = 4;
            _agentBadge.style.display = DisplayStyle.None;
            container.Add(_agentBadge);

            return container;
        }

        internal static void RefreshMainToolbar()
        {
            if (_mcpRoot == null || !_injected) return;

            bool running = MCPToolbarElement.ServerRunning;
            Color c = !running ? kStopped
                : MCPToolbarElement.HasFailures ? kStopped
                : MCPToolbarElement.HasWarnings ? kWarning
                : kRunning;
            _statusDot.style.backgroundColor = c;

            int agents = MCPToolbarElement.ActiveAgents;
            if (agents > 0)
            {
                _agentBadge.text = agents.ToString();
                _agentBadge.style.display = DisplayStyle.Flex;
            }
            else
            {
                _agentBadge.style.display = DisplayStyle.None;
            }
        }
    }
#endif
}
