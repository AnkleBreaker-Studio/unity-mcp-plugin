using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityMCP.Editor;

namespace UnityMcpCaptureFixture
{
    public class CaptureWindow : EditorWindow { }

    public static class UnityMcpEditorCaptureValidation
    {
        private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly List<object> Checks = new List<object>();
        private static readonly string DirectoryPath = "Library/UnityMcpEditorCapture";
        private const string NativeClassName = "UnityMcpCaptureFixtureWindow";
        private static readonly WindowProc PaintFixture = (window, message, dc, parameter) => {
            if (message == 0x0317 || message == 0x0318) {
                GetClientRect(window, out var rect); FillRect(dc, ref rect, GetStockObject(0)); return IntPtr.Zero;
            }
            if (message == 0x000F) {
                var paintDc = BeginPaint(window, out var paint); FillRect(paintDc, ref paint.rect, GetStockObject(0)); EndPaint(window, ref paint); return IntPtr.Zero;
            }
            return DefWindowProc(window, message, dc, parameter);
        };
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Check(string name, Func<object> action)
        {
            try { Checks.Add(new { name, passed = true, evidence = action() }); }
            catch (Exception error) { Checks.Add(new { name, passed = false, error = error.GetBaseException().Message }); }
        }
        private static (EditorWindow window, int count) Find(string selector)
        {
            var method = typeof(MCPScreenshotCommands).GetMethod("FindWindow", Hidden);
            object[] args = method.GetParameters().Length == 2 ? new object[] { selector, 0 } : new object[] { selector, 0, null };
            return ((EditorWindow)method.Invoke(null, args), (int)args[1]);
        }
        private static string Id(EditorWindow window) => (string)typeof(MCPScreenshotCommands).Assembly.GetType("UnityMCP.Editor.MCPObjectId").GetMethod("Get").Invoke(null, new object[] { window });
        private static Dictionary<string, object> Grab(IntPtr handle, EditorWindow window, string name, bool whole = true, int maxDimension = 8192)
        {
            GetWindowRect(handle, out var rect);
            return (Dictionary<string, object>)typeof(MCPScreenshotCommands).GetMethod("GrabAndEncode", Hidden).Invoke(null,
                new object[] { handle, whole, rect.left + 4, rect.top + 4, 24, 16, Path.Combine(DirectoryPath, name), maxDimension, window, true });
        }
        private static object Pixels(string path, int width, int height)
        {
            Texture2D texture = null;
            try {
                texture = new Texture2D(2, 2);
                Require(texture.LoadImage(File.ReadAllBytes(path)), "Invalid PNG");
                Require(texture.width == width && texture.height == height, "Wrong dimensions");
                var colors = texture.GetPixels32();
                Require(colors.All(c => c.r == 255 && c.g == 255 && c.b == 255 && c.a == 255), "Unexpected pixels");
                return new { width, height, opaqueWhitePixels = colors.Length };
            } finally { if (texture != null) UnityEngine.Object.DestroyImmediate(texture); }
        }
        public static void Run()
        {
            Require(File.Exists(".unity-mcp-validation"), "Use a marked disposable project");
            Directory.CreateDirectory(DirectoryPath);
            var first = ScriptableObject.CreateInstance<CaptureWindow>(); first.titleContent = new GUIContent("__Capture_First");
            var second = ScriptableObject.CreateInstance<CaptureWindow>(); second.titleContent = new GUIContent("__Capture_Second");
            IntPtr native = IntPtr.Zero, foreground = GetForegroundWindow();
            try {
                Check("Duplicate full type names are refused", () => { var r = Find(typeof(CaptureWindow).FullName); Require(r.window == null && r.count == 2, "Arbitrary duplicate window selected"); return r.count; });
                Check("Duplicate simple type names are refused", () => { var r = Find(nameof(CaptureWindow)); Require(r.window == null && r.count == 2, "Simple type ambiguity lost"); return r.count; });
                Check("Unique title retains exact matching", () => { Require(Find("__capture_first").window == first, "Unique title failed"); return true; });
                Check("Ambiguous substrings are refused", () => { var r = Find("__Capture_"); Require(r.window == null && r.count == 2, "Substring ambiguity lost"); return r.count; });
                Check("String object identity selects one duplicate", () => { string id = Id(second); Require(Find("id:" + id).window == second, "Explicit identity not resolved"); return new { id }; });
                Check("Missing identity does not fall back to a different window", () => { Require(Find("id:0").window == null, "Invalid identity fallback"); return true; });
                Check("Ambiguous capture returns usable string identities", () => {
                    var result = (Dictionary<string, object>)MCPScreenshotCommands.CaptureEditorWindow(new Dictionary<string, object> { { "window", typeof(CaptureWindow).FullName } });
                    Require(result.ContainsKey("code") && (string)result["code"] == "ambiguous_window", "Ambiguity was not reported");
                    var candidates = (List<object>)result["candidates"];
                    Require(candidates.Count == 2, "Candidate count changed");
                    foreach (var candidate in candidates) {
                        string selector = (string)candidate.GetType().GetProperty("window").GetValue(candidate);
                        Require(selector.StartsWith("id:") && Find(selector).count == 1, "Candidate identity was truncated or unresolved");
                    }
                    return result;
                });
                Check("Unshown windows fail before native capture", () => {
                    var result = (Dictionary<string, object>)MCPScreenshotCommands.CaptureEditorWindow(new Dictionary<string, object> { { "window", "__Capture_First" }, { "path", Path.Combine(DirectoryPath, "unshown.png") } });
                    Require(result.ContainsKey("code") && (string)result["code"] == "window_not_visible", "Unshown window was not identified safely");
                    Require(!File.Exists(Path.Combine(DirectoryPath, "unshown.png")) && !first.hasFocus, "Unshown window was activated"); return result;
                });
                Check("Capture pixel limits include the full backing window", () => {
                    var validate = typeof(MCPScreenshotCommands).GetMethod("ValidateCaptureSize", Hidden);
                    Require(validate != null, "No bound for the full native bitmap");
                    Func<int, int, int, int, string> error = (w, h, cw, ch) => (string)validate.Invoke(null, new object[] { w, h, cw, ch, 8192 });
                    Require(error(8192, 4096, 32, 32) == null, "Exact full-window budget refused");
                    Require(error(8192, 4097, 32, 32) != null, "Full-window budget ignored for small crop");
                    Require(error(64, 64, 8192, 4097) != null && error(64, 64, 8193, 1) != null, "Crop budget ignored");
                    Require(error(int.MaxValue, int.MaxValue, 1, 1) != null, "Overflow in size guard"); return true;
                });
                Check("Inactive tabs require opt-in and restore the previous tab after native failure", () => {
                    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    var dockType = typeof(EditorWindow).Assembly.GetType("UnityEditor.DockArea", true);
                    var dock = ScriptableObject.CreateInstance(dockType);
                    var parent = typeof(EditorWindow).GetField("m_Parent", flags);
                    var panes = (List<EditorWindow>)dockType.GetField("m_Panes", flags).GetValue(dock);
                    var selected = dockType.GetProperty("selected", flags);
                    try {
                        panes.Add(first); panes.Add(second);
                        parent.SetValue(first, dock); parent.SetValue(second, dock); selected.SetValue(dock, 0);
                        Require(first.hasFocus && !second.hasFocus, "Controlled dock selection failed");
                        var args = new Dictionary<string, object> { { "window", "__Capture_Second" }, { "path", Path.Combine(DirectoryPath, "tab.png") } };
                        var refused = (Dictionary<string, object>)MCPScreenshotCommands.CaptureEditorWindow(args);
                        Require((string)refused["code"] == "window_not_visible" && first.hasFocus && !second.hasFocus, "Default capture changed the active tab");
                        args["activateTab"] = true;
                        var result = (Dictionary<string, object>)MCPScreenshotCommands.CaptureEditorWindow(args);
                        Require(!result.ContainsKey("code") && !(bool)result["success"], "Opt-in did not reach native-handle resolution");
                        Require(first.hasFocus && !second.hasFocus && (int)selected.GetValue(dock) == 0, "Previous host tab was not restored");
                        Require(GetForegroundWindow() == foreground, "Tab selection requested OS focus"); return result;
                    } finally {
                        panes.Clear(); dockType.GetProperty("actualView", flags).SetValue(dock, null);
                        parent.SetValue(first, null); parent.SetValue(second, null);
                        UnityEngine.Object.DestroyImmediate(dock);
                    }
                });
                // The fixture stays behind existing windows and cannot activate the editor or another application.
                var nativeClass = new WNDCLASS { procedure = Marshal.GetFunctionPointerForDelegate(PaintFixture), className = NativeClassName };
                Require(RegisterClass(ref nativeClass) != 0, "Native fixture class registration failed");
                native = CreateWindowEx(0x08000080, NativeClassName, "__McpCaptureFixture", 0x80000000, 0, 0, 64, 48, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                Require(native != IntPtr.Zero, "Native fixture creation failed");
                SetWindowPos(native, new IntPtr(1), 0, 0, 64, 48, 0x50);
                InvalidateRect(native, IntPtr.Zero, true); UpdateWindow(native);
                Require(DwmFlush() == 0, "Native fixture did not reach the compositor");
                Check("GDI resource counter detects an owned DC", () => {
                    using (var process = Process.GetCurrentProcess()) {
                        uint before = GetGuiResources(process.Handle, 0); IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
                        Require(dc != IntPtr.Zero, "GDI control allocation failed");
                        try { Require(GetGuiResources(process.Handle, 0) > before, "GDI counter failed positive control"); }
                        finally { DeleteDC(dc); }
                        Require(GetGuiResources(process.Handle, 0) == before, "GDI control cleanup failed"); return true;
                    }
                });
                Check("Native full-window PNG has exact opaque pixels", () => { var result = Grab(native, first, "full.png"); Require(result.ContainsKey("success") && (bool)result["success"], MiniJson.Serialize(result)); return Pixels(Path.Combine(DirectoryPath, "full.png"), 64, 48); });
                Check("Native crop PNG has exact dimensions and pixels", () => { var result = Grab(native, first, "crop.png", false); Require((bool)result["success"], MiniJson.Serialize(result)); return Pixels(Path.Combine(DirectoryPath, "crop.png"), 24, 16); });
                Check("Rejected dimensions do not allocate GDI resources", () => {
                    using (var process = Process.GetCurrentProcess()) {
                        uint before = GetGuiResources(process.Handle, 0); var result = Grab(native, first, "limit.png", true, 32);
                        Require(!(bool)result["success"] && !File.Exists(Path.Combine(DirectoryPath, "limit.png")), "Oversized capture written");
                        Require(GetGuiResources(process.Handle, 0) == before, "Rejected capture leaked GDI resources"); return result;
                    }
                });
                Check("Repeated captures release GDI objects and textures", () => {
                    Grab(native, first, "warm.png");
                    using (var process = Process.GetCurrentProcess()) {
                        uint before = GetGuiResources(process.Handle, 0); int textures = Resources.FindObjectsOfTypeAll<Texture2D>().Length;
                        for (int i = 0; i < 20; i++) {
                            bool whole = i % 2 == 0; Require((bool)Grab(native, first, "repeat.png", whole)["success"], "Repeated capture failed");
                            Pixels(Path.Combine(DirectoryPath, "repeat.png"), whole ? 64 : 24, whole ? 48 : 16);
                        }
                        uint after = GetGuiResources(process.Handle, 0); Require(after == before, "GDI objects leaked");
                        Require(Resources.FindObjectsOfTypeAll<Texture2D>().Length == textures, "Capture texture leaked"); return new { iterations = 20, before, after };
                    }
                });
                Check("Filesystem failure also releases capture resources", () => {
                    string blocked = Path.Combine(DirectoryPath, "blocked.png"); Directory.CreateDirectory(blocked);
                    using (var process = Process.GetCurrentProcess()) {
                        uint before = GetGuiResources(process.Handle, 0); int textures = Resources.FindObjectsOfTypeAll<Texture2D>().Length; bool failed = false;
                        try { Grab(native, first, "blocked.png"); } catch (TargetInvocationException) { failed = true; }
                        Require(failed && GetGuiResources(process.Handle, 0) == before && Resources.FindObjectsOfTypeAll<Texture2D>().Length == textures, "Failure cleanup failed"); return true;
                    }
                });
                Check("Owned captures preserve OS foreground and leave Unity windows unshown", () => { Require(GetForegroundWindow() == foreground && !first.hasFocus && !second.hasFocus, "Focus changed"); return true; });
            } finally {
                if (native != IntPtr.Zero) DestroyWindow(native);
                UnregisterClass(NativeClassName, IntPtr.Zero);
                UnityEngine.Object.DestroyImmediate(first); UnityEngine.Object.DestroyImmediate(second);
            }
            bool passed = Checks.All(c => (bool)c.GetType().GetProperty("passed").GetValue(c));
            File.WriteAllText("Library/UnityMcpEditorCaptureValidation.json", MiniJson.Serialize(new { unityVersion = Application.unityVersion, passed, checks = Checks, scope = "Unshown Unity fixtures and a 64x48 native white window behind existing windows without activation; no user-window pixels" }));
            EditorApplication.Exit(passed ? 0 : 1);
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr window);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();
        [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr window, IntPtr rect, bool erase);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr window, out PAINTSTRUCT paint);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr window, ref PAINTSTRUCT paint);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
        [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WNDCLASS value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr dc, IntPtr parameter);
        private delegate IntPtr WindowProc(IntPtr window, uint message, IntPtr dc, IntPtr parameter);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WNDCLASS
        {
            internal uint style; internal IntPtr procedure; internal int classExtra, windowExtra;
            internal IntPtr instance, icon, cursor, background; internal string menuName, className;
        }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { internal int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct PAINTSTRUCT
        {
            internal IntPtr dc; internal int erase; internal RECT rect; internal int restore, incUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] reserved;
        }
    }
}
