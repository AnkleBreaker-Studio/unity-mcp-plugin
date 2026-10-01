using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    internal static class MCPMenuCommands
    {
        internal const string WindowRoot = "Window/AB Unity MCP";
        internal const string ToolsRoot = "Tools/AnkleBreaker/Unity MCP";
        internal const string WelcomePath = ToolsRoot + "/Welcome";
        internal const string DocumentationUrl = "https://github.com/AnkleBreaker-Studio/unity-mcp-server/tree/main#get-started";

        [MenuItem(ToolsRoot + "/Dashboard", false, -10)]
        [MenuItem("Tools/AnkleBreaker/MCP For Unity/Dashboard", false, -10)]
        internal static void OpenDashboard() => MCPDashboardWindow.ShowWindow();

        [MenuItem(ToolsRoot + "/Action History", false, 10)]
        internal static void OpenHistory() => MCPActionHistoryWindow.ShowWindow();

        [MenuItem(WindowRoot + "/Welcome", false, 0)]
        internal static void OpenWelcome()
        {
            // The canonical Welcome intentionally lives in an independent assembly.
            if (!EditorApplication.ExecuteMenuItem(WelcomePath))
                Debug.LogWarning("[AB-UMCP] Welcome is unavailable. Check package import and compilation errors.");
        }

        [MenuItem(WindowRoot + "/MCP Menu", false, 30)]
        [MenuItem(ToolsRoot + "/MCP Menu", false, 30)]
        internal static void OpenMenu()
        {
            // UI Toolkit and menu commands do not always have an IMGUI event for ShowAsContext.
            var position = Event.current != null ? Event.current.mousePosition : Vector2.zero;
            MCPToolbarElement.ShowMenu(new Rect(position, Vector2.zero));
        }

        [MenuItem(WindowRoot + "/Settings", false, 40)]
        [MenuItem(ToolsRoot + "/Settings", false, 40)]
        internal static void OpenSettings() => MCPDashboardWindow.ShowSection("Settings");

        [MenuItem(WindowRoot + "/Show Toolbar Status", false, 41)]
        [MenuItem(ToolsRoot + "/Show Toolbar Status", false, 41)]
        internal static void ShowToolbar() => MCPToolbarElement.ShowToolbar();

        [MenuItem(WindowRoot + "/Run Self-Tests", false, 60)]
        [MenuItem(ToolsRoot + "/Run Self-Tests", false, 60)]
        internal static void RunSelfTests() => MCPSelfTest.RunAllAsync();

        [MenuItem(WindowRoot + "/Run Self-Tests", true)]
        [MenuItem(ToolsRoot + "/Run Self-Tests", true)]
        private static bool CanRunSelfTests() => MCPBridgeServer.IsRunning && !MCPSelfTest.IsRunning;

        [MenuItem(WindowRoot + "/Documentation", false, 80)]
        [MenuItem(ToolsRoot + "/Documentation", false, 80)]
        internal static void OpenDocumentation() => Application.OpenURL(DocumentationUrl);
    }
}
