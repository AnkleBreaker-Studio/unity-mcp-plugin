using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UnityMCP.Editor;

namespace UnityMcpRenderFixture
{
    public sealed class ImguiPatternWindow : EditorWindow
    {
        private void OnGUI()
        {
            float halfWidth = position.width / 2, halfHeight = position.height / 2;
            EditorGUI.DrawRect(new Rect(0, 0, halfWidth, halfHeight), Color.red);
            EditorGUI.DrawRect(new Rect(halfWidth, 0, halfWidth, halfHeight), Color.green);
            EditorGUI.DrawRect(new Rect(0, halfHeight, halfWidth, halfHeight), Color.blue);
            EditorGUI.DrawRect(new Rect(halfWidth, halfHeight, halfWidth, halfHeight), new Color(1, 1, 0));
        }
    }

    public sealed class PatternWindow : EditorWindow
    {
        public void CreateGUI()
        {
            rootVisualElement.Clear();
            foreach (var colors in new[] { new[] { Color.red, Color.green }, new[] { Color.blue, new Color(1, 1, 0) } })
            {
                var row = new VisualElement(); row.style.flexDirection = FlexDirection.Row; row.style.flexGrow = 1;
                foreach (var color in colors) { var cell = new VisualElement(); cell.style.flexGrow = 1; cell.style.backgroundColor = color; row.Add(cell); }
                rootVisualElement.Add(row);
            }
        }
    }

    public static class UnityMcpEditorRenderValidation
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly List<EditorWindow> Owned = new List<EditorWindow>();
        private static readonly Dictionary<string, bool?> Preferences = new Dictionary<string, bool?>();
        private static EditorWindow PreviousDockTab;
        private const string SessionId = "__McpRender_" + "LongAgentNameForDashboardValidation_" + "012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789";

        private static string Id(EditorWindow window) => (string)typeof(MCPBridgeServer).Assembly.GetType("UnityMCP.Editor.MCPObjectId").GetMethod("Get").Invoke(null, new object[] { window });
        private static EditorWindow Resolve(string id) => Owned.Single(window => window != null && Id(window) == id);
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        static UnityMcpEditorRenderValidation()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
            EditorApplication.quitting += Cleanup;
        }

        private static void Cleanup() => Close();

        private static void RequireProject()
        {
            Require(Application.platform == RuntimePlatform.WindowsEditor, "Rendered capture validation requires Windows");
            Require(File.Exists(".unity-mcp-validation"), "Use a marked disposable project");
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Exit Play Mode before running the fixture");
        }

        public static object Open(string kind, int width, int height)
        {
            RequireProject();
            Require(kind == "pattern" || kind == "dashboard", "Unknown fixture kind");
            IntPtr foreground = GetForegroundWindow();
            EditorWindow window = kind == "pattern" ? (EditorWindow)ScriptableObject.CreateInstance<PatternWindow>() : ScriptableObject.CreateInstance<MCPDashboardWindow>();
            Owned.Add(window);
            window.titleContent = new GUIContent("__McpRender_" + kind);
            window.minSize = new Vector2(kind == "dashboard" ? 360 : 100, kind == "dashboard" ? 500 : 100);
            window.position = new Rect(120, 120, width, height);
            if (kind == "dashboard")
            {
                foreach (var title in new[] { "Request Queue", "Active Agent Sessions", "HTTP Activity", "Recent Actions", "Project Context", "Feature Categories", "AnkleBreaker News", "Settings" })
                {
                    string key = "UnityMCP_Dashboard_" + Application.dataPath + "_" + title;
                    if (!Preferences.ContainsKey(key)) Preferences[key] = EditorPrefs.HasKey(key) ? (bool?)EditorPrefs.GetBool(key) : null;
                    EditorPrefs.SetBool(key, title == "Request Queue" || title == "Active Agent Sessions" || title == "HTTP Activity");
                }
                var session = new MCPAgentSession { AgentId = SessionId, ConnectedAt = DateTime.UtcNow };
                session.LogAction("validation/long-request-name-" + new string('x', 240));
                session.IncrementQueuedRequest();
                var queueType = typeof(MCPRequestQueue);
                object gate = queueType.GetField("_queueLock", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                lock (gate) ((IDictionary)queueType.GetField("_sessions", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null))[SessionId] = session;
            }
            var show = typeof(EditorWindow).GetMethod("ShowPopupWithMode", InstanceFlags);
            Require(show != null, "Background editor-window creation is unavailable");
            var mode = Enum.Parse(show.GetParameters()[0].ParameterType, "Utility");
            show.Invoke(window, new[] { mode, (object)false });
            SendBehindExistingWindows(window.titleContent.text);
            return new { id = Id(window), kind, foregroundPreserved = GetForegroundWindow() == foreground, visible = window.hasFocus };
        }

        public static object OpenDocked(bool imgui = false)
        {
            RequireProject();
            Require(PreviousDockTab == null, "A fixture dock is already open");
            var previous = Resources.FindObjectsOfTypeAll<SceneView>().FirstOrDefault(view => view.docked && view.hasFocus);
            Require(previous != null, "The disposable editor needs an existing selected Scene tab");
            object dock = typeof(EditorWindow).GetField("m_Parent", InstanceFlags).GetValue(previous);
            var add = dock.GetType().GetMethod("AddTab", new[] { typeof(EditorWindow), typeof(bool) });
            Require(add != null, "DockArea.AddTab is unavailable");
            IntPtr foreground = GetForegroundWindow();
            EditorWindow window = imgui ? (EditorWindow)ScriptableObject.CreateInstance<ImguiPatternWindow>() : ScriptableObject.CreateInstance<PatternWindow>();
            Owned.Add(window); PreviousDockTab = previous;
            window.titleContent = new GUIContent("__McpRender_docked_pattern");
            add.Invoke(dock, new object[] { window, true });
            return new { id = Id(window), foregroundPreserved = GetForegroundWindow() == foreground, selected = window.hasFocus, docked = window.docked };
        }

        public static object SelectPreviousDockTab()
        {
            Require(PreviousDockTab != null, "No fixture dock is open");
            IntPtr foreground = GetForegroundWindow(); PreviousDockTab.ShowTab();
            return new { foregroundPreserved = GetForegroundWindow() == foreground, selected = PreviousDockTab.hasFocus };
        }

        public static object Inspect(string id)
        {
            var window = Resolve(id); var root = window.rootVisualElement;
            return new { id, visible = window.hasFocus, width = window.position.width, height = window.position.height,
                panelAttached = root.panel != null, rootBounds = RectInfo(root.worldBound),
                labels = root.Query<TextElement>().ToList().Where(label => label.worldBound.width > 0 && label.worldBound.height > 0 && label.visible).Select(label => new {
                    text = label.text, tooltip = label.tooltip, x = label.worldBound.x, y = label.worldBound.y, width = label.worldBound.width, height = label.worldBound.height,
                    naturalWidth = label.MeasureTextSize(label.text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x,
                    naturalHeight = label.MeasureTextSize(label.text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).y,
                    whiteSpace = label.resolvedStyle.whiteSpace.ToString(), overflow = label.resolvedStyle.textOverflow.ToString() }).ToArray() };
        }

        public static object Sections(string id, string title)
        {
            var window = Resolve(id);
            foreach (var foldout in window.rootVisualElement.Query<Foldout>().ToList()) foldout.value = foldout.text == title;
            return Scroll(id, 0);
        }

        public static object Resize(string id, int width, int height)
        {
            var window = Resolve(id); IntPtr foreground = GetForegroundWindow();
            window.position = new Rect(window.position.x, window.position.y, width, height); window.Repaint();
            return new { foregroundPreserved = GetForegroundWindow() == foreground };
        }

        public static object Scroll(string id, float value)
        {
            var window = Resolve(id); var scroll = window.rootVisualElement.Q<ScrollView>();
            Require(scroll != null, "Window has no ScrollView"); scroll.scrollOffset = new Vector2(0, value); window.Repaint();
            return true;
        }

        public static object Capture(string id, string fileName, bool activateTab = false)
        {
            Require(Path.GetFileName(fileName) == fileName && fileName.EndsWith(".png"), "Use a fixture PNG basename");
            var window = Resolve(id); IntPtr foreground = GetForegroundWindow(); bool selected = window.hasFocus;
            bool previousSelected = PreviousDockTab != null && PreviousDockTab.hasFocus;
            var result = MCPScreenshotCommands.CaptureEditorWindow(new Dictionary<string, object> {
                { "window", "id:" + id }, { "activateTab", activateTab }, { "path", Path.GetFullPath(Path.Combine("Library/UnityMcpEditorRender", fileName)) } });
            return new { capture = result, foregroundPreserved = GetForegroundWindow() == foreground, selectedTabPreserved = window.hasFocus == selected,
                previousDockTabPreserved = (PreviousDockTab != null && PreviousDockTab.hasFocus) == previousSelected };
        }

        public static object CheckPattern(string fileName, bool contentOnly = false)
        {
            var texture = new Texture2D(2, 2);
            try
            {
                Require(texture.LoadImage(File.ReadAllBytes(Path.Combine("Library/UnityMcpEditorRender", fileName))), "Invalid PNG");
                var pixels = texture.GetPixels32(); var counts = new int[4]; var sumX = new double[4]; var sumY = new double[4];
                for (int i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    int color = p.r > 240 && p.g < 15 && p.b < 15 ? 0 : p.r < 15 && p.g > 240 && p.b < 15 ? 1
                        : p.r < 15 && p.g < 15 && p.b > 240 ? 2 : p.r > 240 && p.g > 240 && p.b < 15 ? 3 : -1;
                    if (color < 0) continue;
                    counts[color]++; sumX[color] += i % texture.width; sumY[color] += i / texture.width;
                }
                Require(counts.All(count => count > 1000), "Missing or incorrect UI color quadrants: " + string.Join(",", counts));
                Require(sumX[0] / counts[0] < sumX[1] / counts[1] && sumY[0] / counts[0] > sumY[2] / counts[2]
                    && sumX[2] / counts[2] < sumX[3] / counts[3], "UI pixels were flipped or channels swapped");
                double coverage = (double)counts.Sum() / pixels.Length;
                if (contentOnly)
                {
                    Require(coverage > 0.99, "Docked capture includes chrome or clips content: " + coverage);
                    Require(Math.Abs((double)counts[0] / counts[2] - 1) < 0.02 && Math.Abs((double)counts[1] / counts[3] - 1) < 0.02,
                        "Docked capture crops unequal top and bottom quadrants");
                }
                return new { width = texture.width, height = texture.height, quadrantPixelCounts = counts, contentCoverage = coverage };
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
        }

        public static object Close()
        {
            IntPtr foreground = GetForegroundWindow();
            int count = Owned.Count;
            if (PreviousDockTab != null) PreviousDockTab.ShowTab();
            foreach (var window in Owned) if (window != null) window.Close(); Owned.Clear();
            bool dockRestored = PreviousDockTab == null || PreviousDockTab.hasFocus;
            PreviousDockTab = null;
            foreach (var preference in Preferences)
            {
                if (preference.Value.HasValue) EditorPrefs.SetBool(preference.Key, preference.Value.Value);
                else EditorPrefs.DeleteKey(preference.Key);
            }
            Preferences.Clear();
            object gate = typeof(MCPRequestQueue).GetField("_queueLock", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            lock (gate) ((IDictionary)typeof(MCPRequestQueue).GetField("_sessions", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Remove(SessionId);
            return new { closed = count, preferencesRestored = true, dockRestored, foregroundPreserved = GetForegroundWindow() == foreground };
        }

        private static object RectInfo(Rect value) => new { x = value.x, y = value.y, width = value.width, height = value.height };
        private static void SendBehindExistingWindows(string title)
        {
            uint pid;
            using (var process = System.Diagnostics.Process.GetCurrentProcess()) pid = (uint)process.Id;
            EnumWindows((handle, data) => {
                GetWindowThreadProcessId(handle, out uint owner);
                if (owner != pid) return true;
                var caption = new System.Text.StringBuilder(256); GetWindowText(handle, caption, caption.Capacity);
                if (caption.ToString() == title) SetWindowPos(handle, new IntPtr(1), 0, 0, 0, 0, 0x13);
                return true;
            }, IntPtr.Zero);
        }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr data);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, System.Text.StringBuilder text, int capacity);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr order, int x, int y, int width, int height, uint flags);
        private delegate bool EnumProc(IntPtr handle, IntPtr data);
    }
}
