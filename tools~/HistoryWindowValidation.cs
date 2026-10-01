using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using Unity.Profiling;
using UnityEngine;
using UnityMCP.Editor;

namespace UnityMcpHistoryWindowFixture
{
    public sealed class HistoryProbeTarget : ScriptableObject { }
    public sealed class HistoryProbeWindow : MCPActionHistoryWindow
    {
        public Action Draw;
        public readonly List<long> LayoutAllocations = new List<long>(), RepaintAllocations = new List<long>();
        public readonly List<double> LayoutMs = new List<double>(), RepaintMs = new List<double>();
        public int Layouts, Repaints, Draws;
        public bool ResourceProbe;
        public object ResourcesResult;
        private void OnInspectorUpdate() { }
        private void OnGUI()
        {
            if (Draw == null) return;
            var type = Event.current.type;
            if (type == EventType.Repaint) Draws++;
            if ((type != EventType.Layout && type != EventType.Repaint) || (Layouts >= 24 && Repaints >= 24)) Draw();
            else
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp(); long allocated;
                using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1,
                    ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.CollectOnlyOnCurrentThread))
                {
                    Draw(); recorder.Stop(); allocated = recorder.Count > 0 ? recorder.GetSample(0).Count : 0;
                }
                double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (type == EventType.Layout) { if (++Layouts > 4) { LayoutAllocations.Add(allocated); LayoutMs.Add(elapsed); } }
                else { if (++Repaints > 4) { RepaintAllocations.Add(allocated); RepaintMs.Add(elapsed); } }
            }
            if (ResourceProbe && type == EventType.Repaint) { ResourceProbe = false; ResourcesResult = UnityMcpHistoryWindowValidation.CheckResources(); }
        }
    }

    public static class UnityMcpHistoryWindowValidation
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static HistoryProbeWindow window;
        private static int previousLimit;
        private static bool previousPersistence, running;
        private static byte[] previousFile;
        private const string HistoryPath = "Library/MCPActionHistory.json";
        private static IntPtr foreground;
        private static EditorWindow previousTab;
        private static UnityEngine.Object previousSelection;
        private static HistoryProbeTarget target;
        private static bool foregroundPreserved = true;
        private static readonly List<string> Errors = new List<string>();
        private static object Read(object instance, string name) => typeof(MCPActionHistoryWindow).GetField(name, Hidden)?.GetValue(instance);
        private static void Write(object instance, string name, object value) => typeof(MCPActionHistoryWindow).GetField(name, Hidden).SetValue(instance, value);
        private static void Invoke(object instance, string name) => typeof(MCPActionHistoryWindow).GetMethod(name, Hidden).Invoke(instance, null);
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static Texture2D SelectionTexture(object instance) => (Read(instance, "_rowSelectedStyle") as GUIStyle)?.normal.background;
        static UnityMcpHistoryWindowValidation()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Cleanup;
            EditorApplication.quitting += Cleanup;
            Application.logMessageReceived += (message, stack, type) => {
                if (running && Errors.Count < 20 && (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)) Errors.Add(message);
            };
        }
        private static void Cleanup() { Close(); }
        private static void Tick()
        {
            if (window == null) return;
            if (window.Repaints < 24 || window.Layouts < 24 || window.ResourceProbe) window.Repaint();
        }
        public static object Begin(int count)
        {
            Require(File.Exists(".unity-mcp-validation") && Application.platform == RuntimePlatform.WindowsEditor, "Use a marked Windows validation project");
            Require(!EditorApplication.isPlayingOrWillChangePlaymode, "Exit Play Mode");
            Require(count >= 0 && count <= 5000, "Fixture count outside range");
            Close();
            Errors.Clear();
            previousLimit = MCPSettingsManager.ActionHistoryMaxEntries; previousPersistence = MCPSettingsManager.ActionHistoryPersistence;
            previousFile = File.Exists(HistoryPath) ? File.ReadAllBytes(HistoryPath) : null; running = true;
            MCPSettingsManager.ActionHistoryPersistence = false; MCPSettingsManager.ActionHistoryMaxEntries = 10000; MCPActionHistory.Clear();
            for (int i = 0; i < count; i++) MCPActionHistory.RecordAction(new MCPActionRecord {
                AgentId = "window-agent-" + (i % 8), ActionName = "gameobject/set-transform", Category = "gameobject", Timestamp = DateTime.UtcNow,
                Status = "Completed", ExecutionTimeMs = 15, TargetPath = "Root/Fixture/LongTarget-" + i, TargetInstanceId = "-1", UndoGroup = -1,
            });
            foreground = GetForegroundWindow(); previousTab = EditorWindow.focusedWindow; previousSelection = Selection.activeObject; foregroundPreserved = true;
            window = ScriptableObject.CreateInstance<HistoryProbeWindow>();
            window.titleContent = new GUIContent("__McpHistoryWindowProbe"); window.minSize = new Vector2(500, 400); window.position = new Rect(120, 120, 720, 520);
            window.Draw = (Action)Delegate.CreateDelegate(typeof(Action), window, typeof(MCPActionHistoryWindow).GetMethod("OnGUI", Hidden));
            Invoke(window, "RefreshHistory");
            var show = typeof(EditorWindow).GetMethod("ShowPopupWithMode", Hidden);
            Require(show != null, "Background window creation unavailable");
            show.Invoke(window, new[] { Enum.Parse(show.GetParameters()[0].ParameterType, "Utility"), (object)false });
            SendBehind(); EditorApplication.update += Tick; foregroundPreserved = GetForegroundWindow() == foreground;
            long control; string unit;
            using (var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "GC.Alloc", 1,
                ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.CollectOnlyOnCurrentThread))
            { var bytes = new byte[4096]; recorder.Stop(); GC.KeepAlive(bytes); control = recorder.Count > 0 ? recorder.GetSample(0).Count : 0; unit = recorder.UnitType.ToString(); }
            Require(control > 0, "Allocation recorder failed its positive control");
            return new { count, allocationControlEvents = control, rawRecorderUnit = unit, foregroundPreserved, previousTabPreserved = EditorWindow.focusedWindow == previousTab };
        }
        private static double Median(IEnumerable<double> values) { var sorted = values.OrderBy(x => x).ToArray(); return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2]; }
        public static object Poll() => new {
            ready = window != null && window.Layouts >= 24 && window.Repaints >= 24,
            records = window == null ? 0 : ((List<MCPActionRecord>)Read(window, "_filteredRecords")).Count,
            layouts = window?.Layouts ?? 0, repaints = window?.Repaints ?? 0,
            draws = window?.Draws ?? 0,
            guiErrors = Errors.ToArray(),
            layoutMedianAllocations = window == null ? 0 : Median(window.LayoutAllocations.Select(x => (double)x)),
            repaintMedianAllocations = window == null ? 0 : Median(window.RepaintAllocations.Select(x => (double)x)),
            layoutMedianMs = window == null ? 0 : Median(window.LayoutMs), repaintMedianMs = window == null ? 0 : Median(window.RepaintMs),
            resources = window?.ResourcesResult, foregroundPreserved, previousTabPreserved = EditorWindow.focusedWindow == previousTab,
        };
        public static object Configure(int row, string search, int width = 720, int height = 520)
        {
            Require(window != null, "Open the fixture");
            IntPtr before = GetForegroundWindow();
            Write(window, "_searchText", search); Invoke(window, "RefreshList");
            Write(window, "_listScroll", new Vector2(0, row * (EditorGUIUtility.singleLineHeight + 4)));
            window.position = new Rect(window.position.x, window.position.y, width, height);
            int draws = window.Draws; window.Repaint();
            return new { draws, foregroundPreserved = GetForegroundWindow() == before };
        }
        public static object State() => new {
            count = ((List<MCPActionRecord>)Read(window, "_filteredRecords")).Count,
            selectedIndex = (int)Read(window, "_selectedIndex"), selectedTarget = (Read(window, "_selectedRecord") as MCPActionRecord)?.TargetPath,
            scrollY = ((Vector2)Read(window, "_listScroll")).y, height = window.position.height, width = window.position.width,
            nativeTargetSelected = target != null && Selection.activeObject == target,
        };
        public static object Click(float x, float y)
        {
            IntPtr before = GetForegroundWindow();
            window.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = new Vector2(x, y) });
            window.SendEvent(new Event { type = EventType.MouseUp, button = 0, clickCount = 1, mousePosition = new Vector2(x, y) });
            window.Repaint(); return new { state = State(), foregroundPreserved = GetForegroundWindow() == before };
        }
        public static object Wheel()
        {
            int draws = window.Draws;
            window.SendEvent(new Event { type = EventType.ScrollWheel, mousePosition = new Vector2(100, 60), delta = new Vector2(0, 3) });
            window.Repaint(); return new { draws };
        }
        public static object AttachTarget()
        {
            if (target == null) { target = ScriptableObject.CreateInstance<HistoryProbeTarget>(); target.name = "__mcp_history_window_target"; target.hideFlags = HideFlags.HideAndDontSave; }
            var records = (List<MCPActionRecord>)Read(window, "_filteredRecords");
            var record = records[0];
            record.TargetInstanceId = (string)typeof(MCPBridgeServer).Assembly.GetType("UnityMCP.Editor.MCPObjectId").GetMethod("Get").Invoke(null, new object[] { target });
            window.Repaint(); return true;
        }
        public static object ProbeResources() { Require(window != null, "Open the fixture"); window.ResourceProbe = true; window.Repaint(); return true; }
        internal static object CheckResources()
        {
            int created = 0, surviving = 0;
            for (int i = 0; i < 16; i++)
            {
                var probe = ScriptableObject.CreateInstance<MCPActionHistoryWindow>(); Texture2D texture = null;
                try { Invoke(probe, "InitStyles"); texture = SelectionTexture(probe); if (texture != null) created++; }
                finally { UnityEngine.Object.DestroyImmediate(probe); }
                if (texture != null) { surviving++; UnityEngine.Object.DestroyImmediate(texture); }
            }
            return new { windows = 16, selectionTexturesCreated = created, texturesAliveAfterWindowDestruction = surviving };
        }
        public static object Close()
        {
            EditorApplication.update -= Tick;
            if (!running) return new { closed = true, previouslyRunning = false };
            IntPtr closeForeground = GetForegroundWindow();
            var texture = window == null ? null : SelectionTexture(window);
            if (window != null) window.Close(); window = null;
            bool textureSurvived = texture != null; if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            Selection.activeObject = previousSelection;
            if (target != null) UnityEngine.Object.DestroyImmediate(target); target = null;
            MCPActionHistory.Clear(); MCPSettingsManager.ActionHistoryMaxEntries = previousLimit; MCPSettingsManager.ActionHistoryPersistence = previousPersistence;
            if (previousFile != null) File.WriteAllBytes(HistoryPath, previousFile); else if (File.Exists(HistoryPath)) File.Delete(HistoryPath);
            previousFile = null; running = false;
            return new { closed = true, previouslyRunning = true, textureSurvived, foregroundPreserved = GetForegroundWindow() == closeForeground, previousTabPreserved = EditorWindow.focusedWindow == previousTab };
        }
        private static void SendBehind()
        {
            uint pid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            EnumWindows((handle, data) => {
                GetWindowThreadProcessId(handle, out var owner); if (owner != pid) return true;
                var title = new System.Text.StringBuilder(256); GetWindowText(handle, title, title.Capacity);
                if (title.ToString().Contains("__McpHistoryWindowProbe")) SetWindowPos(handle, new IntPtr(1), 0, 0, 0, 0, 0x0013);
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
