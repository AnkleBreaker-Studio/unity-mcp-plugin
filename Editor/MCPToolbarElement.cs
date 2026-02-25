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
    /// Adds an MCP status dropdown to Unity's main editor toolbar.
    /// On Unity 6000.3+ uses the official MainToolbar API.
    /// On older versions, falls back to reflection-based injection.
    /// Shows a colored status dot + "MCP" label + agent badge, and opens
    /// a dropdown menu with server controls, categories, tests, and settings.
    /// </summary>
    public static class MCPToolbarElement
    {
        // ─── Shared state ────────────────────────────────────────────────
        internal static bool ServerRunning;
        internal static int ActiveAgents;
        internal static bool HasFailures;
        internal static bool HasWarnings;

        // ─── Colored dot icons ───────────────────────────────────────────
        private static Texture2D _greenDot;
        private static Texture2D _redDot;
        private static Texture2D _yellowDot;
        private static Texture2D _greyDot;

        private static Texture2D MakeDot(Color color)
        {
            // Create a small circle texture (16x16 with transparent background)
            int size = 16;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.hideFlags = HideFlags.HideAndDontSave;

            float center = (size - 1) / 2f;
            float radius = size / 2f - 1f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    if (dist <= radius - 0.5f)
                        tex.SetPixel(x, y, color);
                    else if (dist <= radius + 0.5f)
                    {
                        // Anti-alias edge
                        float alpha = 1f - (dist - (radius - 0.5f));
                        tex.SetPixel(x, y, new Color(color.r, color.g, color.b, alpha));
                    }
                    else
                        tex.SetPixel(x, y, Color.clear);
                }
            }

            tex.Apply();
            return tex;
        }

        private static void EnsureDotTextures()
        {
            if (_greenDot == null)
                _greenDot = MakeDot(new Color(0.30f, 0.85f, 0.40f));
            if (_redDot == null)
                _redDot = MakeDot(new Color(0.90f, 0.25f, 0.25f));
            if (_yellowDot == null)
                _yellowDot = MakeDot(new Color(0.90f, 0.80f, 0.10f));
            if (_greyDot == null)
                _greyDot = MakeDot(new Color(0.50f, 0.50f, 0.50f));
        }

        internal static Texture2D CurrentDotIcon
        {
            get
            {
                EnsureDotTextures();
                if (!ServerRunning) return _redDot;
                if (HasFailures) return _redDot;
                if (HasWarnings) return _yellowDot;
                return _greenDot;
            }
        }

        internal static string StatusText
        {
            get
            {
                string label = "MCP";
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
                    return "AB Unity MCP \u2014 Stopped\nClick for options";

                string tip = $"AB Unity MCP \u2014 Running on port {MCPSettingsManager.Port}";
                if (ActiveAgents > 0)
                    tip += $"\n{ActiveAgents} active agent{(ActiveAgents > 1 ? "s" : "")}";
                if (HasFailures)
                    tip += "\nSelf-test failures detected";
                else if (HasWarnings)
                    tip += "\nSelf-test warnings detected";
                tip += "\nClick for options";
                return tip;
            }
        }

        // ─── Periodic refresh ────────────────────────────────────────────

        private const string kElementPath = "MCP/Status";

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
                try { MainToolbar.Refresh(kElementPath); }
                catch { /* MainToolbar may not be ready yet */ }
#else
                MCPToolbarFallback.RefreshMainToolbar();
#endif
            }
        }

        // ─── Dropdown menu builder ───────────────────────────────────────

        internal static void ShowMenu(Rect buttonRect)
        {
            var menu = new GenericMenu();
            bool running = MCPBridgeServer.IsRunning;

            // Status header
            menu.AddDisabledItem(new GUIContent(
                running
                    ? $"\u25CF  Running \u2014 Port {MCPSettingsManager.Port}"
                    : "\u25CB  Stopped"));

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

            menu.DropDown(buttonRect);
        }

#if UNITY_6000_3_OR_NEWER
        // ═══════════════════════════════════════════════════════════════════
        // Unity 6000.3+: Single MainToolbarDropdown with status + menu
        // ═══════════════════════════════════════════════════════════════════

        [MainToolbarElement(kElementPath,
            defaultDockPosition = MainToolbarDockPosition.Right)]
        public static MainToolbarElement CreateMCPDropdown()
        {
            // Snapshot current state
            ServerRunning = MCPBridgeServer.IsRunning;
            ActiveAgents = MCPRequestQueue.ActiveSessionCount;
            HasFailures = MCPSelfTest.HasFailures;
            HasWarnings = MCPSelfTest.HasWarnings;

            var content = new MainToolbarContent(
                StatusText,
                CurrentDotIcon,
                StatusTooltip);

            return new MainToolbarDropdown(content, ShowMenu);
        }
#endif
    }

#if !UNITY_6000_3_OR_NEWER
    // ═══════════════════════════════════════════════════════════════════════
    // Pre-6000.3 Fallback: Reflection-based main toolbar injection
    // Uses m_Root field on GUIView base type to access toolbar VisualElement
    // tree, then injects into #ToolbarZoneRightAlign.
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
        private const int MaxRetries = 50;

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
                    Debug.Log("[AB-UMCP] Main toolbar injection not available on this Unity version. Use Unity 6000.3+ for native toolbar support.");
                return;
            }
            _retryCount++;

            try
            {
                // Find the Toolbar instance (it's a ScriptableObject, NOT an EditorWindow)
                var toolbarType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.Toolbar");
                if (toolbarType == null) return;

                var toolbars = Resources.FindObjectsOfTypeAll(toolbarType);
                if (toolbars == null || toolbars.Length == 0) return;

                var toolbar = toolbars[0];

                // Toolbar inherits: Toolbar -> ... -> GUIView -> View -> ScriptableObject
                // The VisualElement root is accessed via the m_Root field on GUIView,
                // NOT via rootVisualElement property (which doesn't exist on Toolbar).
                VisualElement root = null;

                // Try 1: m_Root field on GUIView base type
                var guiViewType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GUIView");
                if (guiViewType != null)
                {
                    var rootField = guiViewType.GetField("m_Root",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (rootField != null)
                        root = rootField.GetValue(toolbar) as VisualElement;
                }

                // Try 2: visualTree property (available on some Unity versions)
                if (root == null && guiViewType != null)
                {
                    var visualTreeProp = guiViewType.GetProperty("visualTree",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    if (visualTreeProp != null)
                        root = visualTreeProp.GetValue(toolbar) as VisualElement;
                }

                // Try 3: rootVisualElement property (older Unity versions)
                if (root == null)
                {
                    var rootProp = toolbarType.GetProperty("rootVisualElement",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    if (rootProp != null)
                        root = rootProp.GetValue(toolbar) as VisualElement;
                }

                if (root == null || root.childCount == 0) return;

                // Find the right-align zone in the toolbar
                var target = root.Q("ToolbarZoneRightAlign")
                    ?? root.Q(className: "unity-editor-toolbar-container__zone")
                    ?? root.Q(className: "unity-toolbar-zone-align-right");

                if (target == null) return;

                _mcpRoot = BuildElement();
                target.Insert(0, _mcpRoot);
                _injected = true;
                EditorApplication.update -= TryInject;

                _mcpRoot.schedule.Execute(() => RefreshMainToolbar()).Every(1000);
                Debug.Log("[AB-UMCP] Injected into main toolbar (legacy mode).");
            }
            catch (Exception ex)
            {
                if (_retryCount >= MaxRetries)
                    Debug.LogWarning($"[AB-UMCP] Legacy injection failed: {ex.Message}");
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
            container.style.height = new StyleLength(StyleKeyword.Auto);
            container.style.borderLeftWidth = 1;
            container.style.borderLeftColor = new Color(0.15f, 0.15f, 0.15f, 0.4f);
            container.tooltip = MCPToolbarElement.StatusTooltip;

            // Click opens the dropdown menu at the element's position
            container.RegisterCallback<ClickEvent>(evt =>
            {
                var worldBound = container.worldBound;
                var menuRect = new Rect(worldBound.x, worldBound.yMax, worldBound.width, 0);
                MCPToolbarElement.ShowMenu(menuRect);
            });
            container.RegisterCallback<MouseEnterEvent>(evt =>
                container.style.backgroundColor = new Color(1f, 1f, 1f, 0.06f));
            container.RegisterCallback<MouseLeaveEvent>(evt =>
                container.style.backgroundColor = Color.clear);

            // Colored status dot
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

            // "MCP" label
            _statusLabel = new Label("MCP");
            _statusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _statusLabel.style.fontSize = 11;
            _statusLabel.style.color = new Color(0.78f, 0.78f, 0.78f);
            container.Add(_statusLabel);

            // Agent count badge
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

            // Dropdown arrow
            var arrow = new Label("\u25BE");
            arrow.style.fontSize = 10;
            arrow.style.color = new Color(0.60f, 0.60f, 0.60f);
            arrow.style.marginLeft = 3;
            arrow.style.unityTextAlign = TextAnchor.MiddleCenter;
            container.Add(arrow);

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

            _mcpRoot.tooltip = MCPToolbarElement.StatusTooltip;

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
