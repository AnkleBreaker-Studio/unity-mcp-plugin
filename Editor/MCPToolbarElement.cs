using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Injects an MCP status indicator and dropdown into Unity's main toolbar,
    /// next to the Asset Store / AI / Unity VCS buttons.
    /// Uses reflection to access the internal Toolbar VisualElement tree.
    /// </summary>
    [InitializeOnLoad]
    public static class MCPToolbarElement
    {
        // Reflection handles
        private static readonly Type kToolbarType =
            typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.Toolbar");

        private static ScriptableObject _toolbar;
        private static VisualElement _mcpRoot;
        private static VisualElement _statusDot;
        private static Label _statusLabel;
        private static Label _agentCountLabel;
        private static bool _injected;

        // Colors
        private static readonly Color kRunningColor  = new Color(0.30f, 0.85f, 0.40f);
        private static readonly Color kStoppedColor   = new Color(0.90f, 0.25f, 0.25f);
        private static readonly Color kAgentColor     = new Color(0.40f, 0.75f, 1.00f);

        static MCPToolbarElement()
        {
            EditorApplication.update += TryInject;
        }

        // ─── Injection ───────────────────────────────────────────────

        private static void TryInject()
        {
            if (_injected) return;
            if (kToolbarType == null) return;

            // Find the Toolbar ScriptableObject instance
            var toolbars = Resources.FindObjectsOfTypeAll(kToolbarType);
            if (toolbars == null || toolbars.Length == 0) return;
            _toolbar = (ScriptableObject)toolbars[0];

            // Get rootVisualElement via reflection
            var rootProp = _toolbar.GetType().GetProperty(
                "rootVisualElement",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (rootProp == null) return;

            var root = rootProp.GetValue(_toolbar) as VisualElement;
            if (root == null) return;

            // Schedule injection after layout is built
            root.schedule.Execute(() => Inject(root)).StartingIn(200);
        }

        private static void Inject(VisualElement root)
        {
            if (_injected) return;

            // Find the right-aligned zone in the toolbar
            // Unity 6 uses "ToolbarZoneRightAlign" or similar
            var target = FindTargetContainer(root);
            if (target == null)
            {
                Debug.LogWarning("[MCP Toolbar] Could not find toolbar container. Toolbar element not injected.");
                return;
            }

            // Build the MCP toolbar element
            _mcpRoot = BuildToolbarElement();

            // Insert at the beginning of the right zone (before Asset Store, AI, etc.)
            target.Insert(0, _mcpRoot);

            _injected = true;
            EditorApplication.update -= TryInject;

            // Start periodic status refresh
            _mcpRoot.schedule.Execute(RefreshStatus).Every(1000);
        }

        /// <summary>
        /// Walk the toolbar visual tree to find the container where existing buttons live.
        /// Tries several selectors for compatibility across Unity 6 subversions.
        /// </summary>
        private static VisualElement FindTargetContainer(VisualElement root)
        {
            // Strategy 1: Direct name query
            var target = root.Q("ToolbarZoneRightAlign");
            if (target != null) return target;

            // Strategy 2: USS class
            target = root.Q(className: "unity-toolbar-zone-right-align");
            if (target != null) return target;

            // Strategy 3: Walk children for a right-aligned flex container
            target = root.Q("unity-editor-toolbar-right");
            if (target != null) return target;

            // Strategy 4: Broad search — find the container that holds known toolbar buttons
            // Look for a VisualElement whose children include things like "Asset Store", "AI" text
            target = FindContainerByContent(root);
            if (target != null) return target;

            return null;
        }

        /// <summary>
        /// Fallback: walk the tree to find the parent of known toolbar buttons.
        /// </summary>
        private static VisualElement FindContainerByContent(VisualElement root)
        {
            // Find any element containing "Asset Store" text — its parent is our target
            var found = FindElementRecursive(root, el =>
            {
                if (el is Label label && label.text != null &&
                    label.text.Contains("Asset Store"))
                    return true;
                if (el is TextElement txt && txt.text != null &&
                    txt.text.Contains("Asset Store"))
                    return true;
                return false;
            });

            return found?.parent?.parent; // Button > container > zone
        }

        private static VisualElement FindElementRecursive(VisualElement root, Func<VisualElement, bool> predicate)
        {
            if (predicate(root)) return root;
            foreach (var child in root.Children())
            {
                var result = FindElementRecursive(child, predicate);
                if (result != null) return result;
            }
            return null;
        }

        // ─── Build UI ────────────────────────────────────────────────

        private static VisualElement BuildToolbarElement()
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

            // Make it look like the other toolbar buttons
            container.style.borderLeftWidth = 1;
            container.style.borderLeftColor = new Color(0.15f, 0.15f, 0.15f, 0.4f);

            // Clickable — show dropdown on click
            container.RegisterCallback<ClickEvent>(evt => ShowDropdown(container));

            // Hover cursor
            container.RegisterCallback<MouseEnterEvent>(evt =>
            {
                container.style.backgroundColor = new Color(1f, 1f, 1f, 0.06f);
            });
            container.RegisterCallback<MouseLeaveEvent>(evt =>
            {
                container.style.backgroundColor = Color.clear;
            });

            // Status dot
            _statusDot = new VisualElement();
            _statusDot.name = "mcp-status-dot";
            _statusDot.style.width = 8;
            _statusDot.style.height = 8;
            _statusDot.style.borderTopLeftRadius = 4;
            _statusDot.style.borderTopRightRadius = 4;
            _statusDot.style.borderBottomLeftRadius = 4;
            _statusDot.style.borderBottomRightRadius = 4;
            _statusDot.style.marginRight = 5;
            _statusDot.style.backgroundColor = kStoppedColor;
            container.Add(_statusDot);

            // "MCP" label
            _statusLabel = new Label("MCP");
            _statusLabel.name = "mcp-status-label";
            _statusLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _statusLabel.style.fontSize = 11;
            _statusLabel.style.color = new Color(0.78f, 0.78f, 0.78f);
            _statusLabel.style.marginRight = 2;
            container.Add(_statusLabel);

            // Agent count badge (hidden when 0)
            _agentCountLabel = new Label();
            _agentCountLabel.name = "mcp-agent-count";
            _agentCountLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _agentCountLabel.style.fontSize = 9;
            _agentCountLabel.style.color = Color.white;
            _agentCountLabel.style.backgroundColor = kAgentColor;
            _agentCountLabel.style.borderTopLeftRadius = 6;
            _agentCountLabel.style.borderTopRightRadius = 6;
            _agentCountLabel.style.borderBottomLeftRadius = 6;
            _agentCountLabel.style.borderBottomRightRadius = 6;
            _agentCountLabel.style.paddingLeft = 4;
            _agentCountLabel.style.paddingRight = 4;
            _agentCountLabel.style.paddingTop = 1;
            _agentCountLabel.style.paddingBottom = 1;
            _agentCountLabel.style.marginLeft = 3;
            _agentCountLabel.style.marginRight = 2;
            _agentCountLabel.style.display = DisplayStyle.None;
            container.Add(_agentCountLabel);

            // Dropdown arrow
            var arrow = new Label("\u25BE"); // ▾
            arrow.style.fontSize = 10;
            arrow.style.color = new Color(0.6f, 0.6f, 0.6f);
            arrow.style.marginLeft = 2;
            arrow.style.unityTextAlign = TextAnchor.MiddleCenter;
            container.Add(arrow);

            return container;
        }

        // ─── Status Refresh ──────────────────────────────────────────

        private static void RefreshStatus()
        {
            if (_mcpRoot == null || !_injected) return;

            bool running = MCPBridgeServer.IsRunning;

            // Status dot
            _statusDot.style.backgroundColor = running ? kRunningColor : kStoppedColor;

            // Status label tooltip
            _mcpRoot.tooltip = running
                ? $"MCP Bridge — Running on port {MCPSettingsManager.Port}"
                : "MCP Bridge — Stopped";

            // Agent count badge
            int agents = MCPRequestQueue.ActiveSessionCount;
            if (agents > 0)
            {
                _agentCountLabel.text = agents.ToString();
                _agentCountLabel.style.display = DisplayStyle.Flex;
                _agentCountLabel.tooltip = $"{agents} active agent{(agents > 1 ? "s" : "")}";
            }
            else
            {
                _agentCountLabel.style.display = DisplayStyle.None;
            }
        }

        // ─── Dropdown Menu ───────────────────────────────────────────

        private static void ShowDropdown(VisualElement anchor)
        {
            var menu = new GenericMenu();
            bool running = MCPBridgeServer.IsRunning;

            // Status header (disabled, just for info)
            menu.AddDisabledItem(new GUIContent(
                running
                    ? $"\u25CF  Running — Port {MCPSettingsManager.Port}"
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

            // Quick toggles for categories
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

            // Individual category toggles
            foreach (var cat in MCPSettingsManager.GetAllCategoryNames())
            {
                bool enabled = MCPSettingsManager.IsCategoryEnabled(cat);
                string displayName = char.ToUpper(cat[0]) + cat.Substring(1);
                string catCapture = cat; // Capture for closure
                menu.AddItem(new GUIContent($"Categories/{displayName}"), enabled, () =>
                {
                    MCPSettingsManager.SetCategoryEnabled(catCapture, !enabled);
                });
            }

            menu.AddSeparator("");

            // Settings
            menu.AddItem(
                new GUIContent("Auto-Start on Load"),
                MCPSettingsManager.AutoStart,
                () => MCPSettingsManager.AutoStart = !MCPSettingsManager.AutoStart);

            menu.AddSeparator("");

            // Open full dashboard
            menu.AddItem(new GUIContent("Open Dashboard..."), false, () =>
            {
                MCPDashboardWindow.ShowWindow();
            });

            // Check for updates
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

            // Show the menu anchored below the toolbar element
            var rect = anchor.worldBound;
            menu.DropDown(new Rect(rect.x, rect.yMax, 0, 0));
        }

        // ─── Cleanup ─────────────────────────────────────────────────

        /// <summary>
        /// Re-inject after domain reload (static state is lost).
        /// The [InitializeOnLoad] constructor handles this automatically.
        /// </summary>
        internal static void ForceReinject()
        {
            _injected = false;
            _mcpRoot = null;
            _statusDot = null;
            _statusLabel = null;
            _agentCountLabel = null;
            _toolbar = null;
            EditorApplication.update += TryInject;
        }
    }
}
