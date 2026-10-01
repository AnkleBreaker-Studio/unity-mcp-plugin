using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityMCP.Editor;
#if UNITY_6000_3_OR_NEWER
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
#endif

public static class UnityMcpToolbarValidation
{
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly List<object> Checks = new List<object>();
    private static readonly Type Toolbar = typeof(MCPToolbarElement);
    private static object Call(string name) => Toolbar.GetMethod(name, Hidden).Invoke(null, null);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Check(string name, Action action)
    {
        try { action(); Checks.Add(new { name, passed = true }); }
        catch (Exception ex) { Checks.Add(new { name, passed = false, error = ex.GetBaseException().Message }); }
    }
    private static void Tick()
    {
        Toolbar.GetField("_nextRefreshTime", Hidden).SetValue(null, 0d);
        Call("PeriodicRefresh");
    }
    private static Dictionary<string, object> MenuItems()
    {
        var menu = (GenericMenu)Call("BuildMenu");
        var entries = (IEnumerable)typeof(GenericMenu).GetField("m_MenuItems", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(menu);
        var result = new Dictionary<string, object>();
        foreach (var entry in entries)
        {
            var content = (GUIContent)entry.GetType().GetField("content").GetValue(entry);
            if (!string.IsNullOrEmpty(content.text)) result[content.text] = entry;
        }
        return result;
    }

    public static object Run()
    {
        Require(File.Exists(".unity-mcp-validation") && !EditorApplication.isPlayingOrWillChangePlaymode, "Use a marked disposable editor outside Play Mode");
        Checks.Clear();
        Check("Both menu roots resolve all primary destinations without duplicate registrations", () =>
        {
            var paths = new Dictionary<string, int>();
            foreach (var method in TypeCache.GetMethodsWithAttribute<MenuItem>())
                foreach (MenuItem item in method.GetCustomAttributes(typeof(MenuItem), false))
                    if (!item.validate) paths[item.menuItem] = paths.TryGetValue(item.menuItem, out int count) ? count + 1 : 1;
            foreach (string root in new[] { "Window/AB Unity MCP", "Tools/AnkleBreaker/Unity MCP" })
                foreach (string leaf in new[] { "Dashboard", "Action History", "Welcome", "MCP Menu", "Settings", "Show Toolbar Status", "Run Self-Tests", "Documentation" })
                    Require(paths.TryGetValue(root + "/" + leaf, out int count) && count == 1, "Missing or ambiguous menu: " + root + "/" + leaf);
            Require(paths.ContainsKey("Tools/AnkleBreaker/MCP For Unity/Dashboard"), "Upstream CLICKME dashboard target is unavailable");
        });
        Check("Shared menu provides actionable navigation including the canonical Welcome", () =>
        {
            var items = MenuItems();
            foreach (string label in new[] { "Open Dashboard...", "Action History...", "Welcome...", "Documentation...", "Settings/Open Settings...", "Show Toolbar Status", "Dashboard/Project Context", "Dashboard/Feature Categories", "Dashboard/HTTP Activity" })
            {
                Require(items.TryGetValue(label, out object item), "Missing destination: " + label);
                Require(item.GetType().GetField("func").GetValue(item) is Delegate, "Destination has no action: " + label);
            }
        });
        Check("Dashboard retains one keyboard-focusable navigation control after rebuilding", () =>
        {
            var window = ScriptableObject.CreateInstance<MCPDashboardWindow>();
            try
            {
                window.CreateGUI(); window.CreateGUI();
                var buttons = window.rootVisualElement.Query<Button>("mcp-navigation-menu").ToList();
                Require(buttons.Count == 1 && buttons[0].focusable && buttons[0].enabledInHierarchy, "Navigation is duplicated or inaccessible");
                Require(window.rootVisualElement.Query<Foldout>().ToList().Any(f => f.text == "Settings"), "Settings destination missing");
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        });
#if UNITY_6000_3_OR_NEWER
        var find = typeof(MainToolbar).GetMethod("TryGetOverlay", Hidden);
        object[] args = { "MCP/Status", null };
        Require(find != null && (bool)find.Invoke(null, args), "Native MCP toolbar overlay unavailable");
        var overlay = (Overlay)args[1];
        string key = (string)Toolbar.GetProperty("VisibilityKey", Hidden).GetValue(null);
        bool originalShown = overlay.displayed, hadKey = EditorPrefs.HasKey(key), originalKey = EditorPrefs.GetBool(key);
        bool manual = MCPSettingsManager.UseManualPort;
        try
        {
            Check("First install restores the MCP element in a layout that hides it", () =>
            {
                overlay.displayed = false; EditorPrefs.DeleteKey(key); Call("Initialize"); Tick();
                Require(overlay.displayed && EditorPrefs.GetBool(key), "Install did not restore and record visibility");
            });
            Check("Subsequent initialization preserves a deliberate Hide choice", () =>
            {
                overlay.displayed = false; Call("Initialize"); Tick();
                Require(!overlay.displayed, "A later initialization overwrote the user's layout");
            });
            Check("Explicit restore reuses the same native overlay", () =>
            {
                Call("ShowToolbar"); Call("ShowToolbar");
                object[] repeated = { "MCP/Status", null };
                Require((bool)find.Invoke(null, repeated) && ReferenceEquals(overlay, repeated[1]) && overlay.displayed, "Restore duplicated or failed to show the overlay");
            });
            Check("Live toolbar tooltip follows port-mode changes without agent or running-state changes", () =>
            {
                MCPSettingsManager.UseManualPort = false; Tick();
                Require(overlay.rootVisualElement.Query<VisualElement>().ToList().Any(v => v.tooltip != null && v.tooltip.Contains("(auto)")), "Automatic port mode missing");
                MCPSettingsManager.UseManualPort = true; Tick();
                Require(overlay.rootVisualElement.Query<VisualElement>().ToList().Any(v => v.tooltip != null && v.tooltip.Contains("Running on port") && !v.tooltip.Contains("(auto)")), "Tooltip remained stale after mode change");
            });
        }
        finally
        {
            MCPSettingsManager.UseManualPort = manual;
            overlay.displayed = originalShown;
            if (hadKey) EditorPrefs.SetBool(key, originalKey); else EditorPrefs.DeleteKey(key);
            Tick();
        }
#endif
        bool passed = Checks.All(c => (bool)c.GetType().GetProperty("passed").GetValue(c));
        var report = new { unityVersion = Application.unityVersion, passed, checks = Checks };
        File.WriteAllText("Library/UnityMcpToolbarValidation.json", MiniJson.Serialize(report));
        return report;
    }
}
