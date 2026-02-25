using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityMCP.Editor
{
    // ─── Overlay: shows "MCP" toolbar strip in the Scene View ───────────

    /// <summary>
    /// Registers an MCP overlay toolbar that appears in the Scene View.
    /// Uses Unity's official Overlay / EditorToolbar APIs (2021.2+).
    /// Users can dock, float, or collapse the overlay like any other.
    /// </summary>
    [Overlay(typeof(SceneView), OverlayId, "MCP")]
    [Icon("d_Profiler.NetworkMessages")]
    public class MCPToolbarOverlay : ToolbarOverlay
    {
        public const string OverlayId = "unity-mcp-toolbar-overlay";

        MCPToolbarOverlay() : base(
            MCPStatusElement.Id,
            MCPDropdownElement.Id
        )
        { }
    }

    // ─── Status element: green/red dot + "MCP" label ────────────────────

    [EditorToolbarElement(Id, typeof(SceneView))]
    public class MCPStatusElement : EditorToolbarButton
    {
        public const string Id = "UnityMCP/Status";

        private readonly VisualElement _dot;
        private readonly Label _agentBadge;

        private static readonly Color kRunning = new Color(0.30f, 0.85f, 0.40f);
        private static readonly Color kStopped = new Color(0.90f, 0.25f, 0.25f);
        private static readonly Color kWarning = new Color(0.90f, 0.80f, 0.10f);
        private static readonly Color kBadgeBg = new Color(0.40f, 0.75f, 1.00f);

        public MCPStatusElement()
        {
            // Remove default button text / icon
            text = null;
            icon = null;

            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.paddingLeft = 6;
            style.paddingRight = 6;

            // Status dot
            _dot = new VisualElement();
            _dot.style.width = 8;
            _dot.style.height = 8;
            _dot.style.borderTopLeftRadius = 4;
            _dot.style.borderTopRightRadius = 4;
            _dot.style.borderBottomLeftRadius = 4;
            _dot.style.borderBottomRightRadius = 4;
            _dot.style.marginRight = 5;
            _dot.style.backgroundColor = kStopped;
            Add(_dot);

            // "MCP" label
            var label = new Label("MCP");
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.fontSize = 11;
            label.style.color = new Color(0.78f, 0.78f, 0.78f);
            Add(label);

            // Agent count badge (hidden by default)
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
            Add(_agentBadge);

            // Click opens dashboard
            clicked += () => MCPDashboardWindow.ShowWindow();

            // Tooltip
            tooltip = "MCP Bridge Status — Click to open Dashboard";

            // Periodic refresh
            schedule.Execute(Refresh).Every(1000);
        }

        private void Refresh()
        {
            bool running = MCPBridgeServer.IsRunning;

            // Dot color
            Color c;
            if (!running)
                c = kStopped;
            else if (MCPSelfTest.HasFailures)
                c = kStopped;
            else if (MCPSelfTest.HasWarnings)
                c = kWarning;
            else
                c = kRunning;
            _dot.style.backgroundColor = c;

            // Tooltip
            string tip = running
                ? $"MCP Bridge — Running on port {MCPSettingsManager.Port}"
                : "MCP Bridge — Stopped";

            if (running && MCPSelfTest.LastRunTime > DateTime.MinValue)
            {
                int p = MCPSelfTest.PassedCount;
                int f = MCPSelfTest.FailedCount;
                int w = MCPSelfTest.WarningCount;
                tip += $"\nTests: {p} passed";
                if (f > 0) tip += $", {f} failed";
                if (w > 0) tip += $", {w} warnings";
            }
            tooltip = tip;

            // Agent badge
            int agents = MCPRequestQueue.ActiveSessionCount;
            if (agents > 0)
            {
                _agentBadge.text = agents.ToString();
                _agentBadge.style.display = DisplayStyle.Flex;
                _agentBadge.tooltip = $"{agents} active agent{(agents > 1 ? "s" : "")}";
            }
            else
            {
                _agentBadge.style.display = DisplayStyle.None;
            }
        }
    }

    // ─── Dropdown element: ▾ button with GenericMenu ────────────────────

    [EditorToolbarElement(Id, typeof(SceneView))]
    public class MCPDropdownElement : EditorToolbarButton
    {
        public const string Id = "UnityMCP/Dropdown";

        public MCPDropdownElement()
        {
            text = "\u25BE"; // ▾
            style.fontSize = 12;
            style.paddingLeft = 2;
            style.paddingRight = 4;
            style.unityTextAlign = TextAnchor.MiddleCenter;
            tooltip = "MCP Actions";

            clicked += ShowMenu;
        }

        private void ShowMenu()
        {
            var menu = new GenericMenu();
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

            menu.ShowAsContext();
        }
    }

    // ─── Main toolbar injection (legacy fallback) ───────────────────────

    /// <summary>
    /// Attempts to also inject a status indicator into Unity's main top toolbar
    /// (next to Asset Store / AI / VCS tabs) via reflection.
    /// This is a best-effort approach since Unity doesn't expose a public API for it.
    /// If injection fails, the Overlay toolbar in Scene View is the primary UI.
    /// </summary>
    [InitializeOnLoad]
    public static class MCPMainToolbarInjector
    {
        private static bool _injected;
        private static VisualElement _mcpRoot;
        private static VisualElement _statusDot;
        private static Label _agentBadge;
        private static int _retryCount;
        private const int MaxRetries = 30; // Try for ~3 seconds

        private static readonly Color kRunning = new Color(0.30f, 0.85f, 0.40f);
        private static readonly Color kStopped = new Color(0.90f, 0.25f, 0.25f);
        private static readonly Color kWarning = new Color(0.90f, 0.80f, 0.10f);
        private static readonly Color kBadgeBg = new Color(0.40f, 0.75f, 1.00f);

        static MCPMainToolbarInjector()
        {
            EditorApplication.update += TryInject;
        }

        private static void TryInject()
        {
            if (_injected || _retryCount >= MaxRetries)
            {
                EditorApplication.update -= TryInject;
                if (!_injected && _retryCount >= MaxRetries)
                    Debug.Log("[MCP Toolbar] Main toolbar injection skipped — Overlay toolbar in Scene View is available.");
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

                // Get rootVisualElement
                var rootProp = toolbarType.GetProperty("rootVisualElement",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (rootProp == null) return;

                var root = rootProp.GetValue(toolbar) as VisualElement;
                if (root == null || root.childCount == 0) return;

                // Try to find the right-aligned zone
                var target = root.Q("ToolbarZoneRightAlign")
                    ?? root.Q(className: "unity-toolbar-zone-align-right")
                    ?? root.Q(className: "unity-editor-toolbar-container__right")
                    ?? FindRightZone(root);

                if (target == null)
                {
                    // Log the tree structure for debugging (only on last retry)
                    if (_retryCount >= MaxRetries)
                    {
                        Debug.Log("[MCP Toolbar] Could not find toolbar container. Tree dump:");
                        DumpTree(root, 0, 3);
                    }
                    return;
                }

                // Build and inject
                _mcpRoot = BuildElement();
                target.Insert(0, _mcpRoot);
                _injected = true;
                EditorApplication.update -= TryInject;

                // Periodic refresh
                _mcpRoot.schedule.Execute(RefreshMainToolbar).Every(1000);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP Toolbar] Injection attempt {_retryCount} failed: {ex.Message}");
            }
        }

        private static VisualElement FindRightZone(VisualElement root)
        {
            // Walk the tree looking for a container that holds known buttons
            // In Unity 6, look for containers with specific patterns
            return FindRecursive(root, el =>
            {
                // Look for flex-row containers in the right portion of the toolbar
                if (el.childCount >= 2 && el.resolvedStyle.flexDirection == FlexDirection.Row)
                {
                    // Check if any child contains text like "Account", "Cloud", or known toolbar items
                    foreach (var child in el.Children())
                    {
                        if (child is TextElement te)
                        {
                            string t = te.text ?? "";
                            if (t.Contains("Account") || t.Contains("Cloud") || t.Contains("Collab")
                                || t.Contains("Services") || t.Contains("Asset Store"))
                                return true;
                        }
                    }
                }
                return false;
            });
        }

        private static VisualElement FindRecursive(VisualElement root, Func<VisualElement, bool> match)
        {
            if (match(root)) return root;
            foreach (var child in root.Children())
            {
                var found = FindRecursive(child, match);
                if (found != null) return found;
            }
            return null;
        }

        private static void DumpTree(VisualElement el, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;
            string indent = new string(' ', depth * 2);
            string name = string.IsNullOrEmpty(el.name) ? "" : $" name='{el.name}'";
            string text = el is TextElement te2 && !string.IsNullOrEmpty(te2.text) ? $" text='{te2.text}'" : "";
            Debug.Log($"[MCP Toolbar] {indent}{el.GetType().Name}{name}{text} children={el.childCount}");
            foreach (var child in el.Children())
                DumpTree(child, depth + 1, maxDepth);
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

            var label = new Label("MCP");
            label.style.unityTextAlign = TextAnchor.MiddleCenter;
            label.style.fontSize = 11;
            label.style.color = new Color(0.78f, 0.78f, 0.78f);
            container.Add(label);

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

        private static void RefreshMainToolbar()
        {
            if (_mcpRoot == null || !_injected) return;

            bool running = MCPBridgeServer.IsRunning;
            Color c = !running ? kStopped
                : MCPSelfTest.HasFailures ? kStopped
                : MCPSelfTest.HasWarnings ? kWarning
                : kRunning;
            _statusDot.style.backgroundColor = c;

            int agents = MCPRequestQueue.ActiveSessionCount;
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
}
