using UnityEditor;
using UnityEngine.UIElements;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Applies the AnkleBreaker editor theme (deep warm-brown + molten-orange, the
    /// studio website palette) to a window root. Loads the shared brand sheet the
    /// welcome window ships (resolved by asset name, so it works across assemblies)
    /// plus the dashboard-specific styles. Call once from <c>CreateGUI</c>; idempotent.
    /// </summary>
    internal static class MCPTheme
    {
        public const string ClassWindow = "ab-window";

        private const string BaseSheetFilter = "UnityMcpWelcomeTheme t:StyleSheet";
        private const string DashboardSheetFilter = "MCPDashboardStyles t:StyleSheet";

        private static StyleSheet _baseSheet;
        private static StyleSheet _dashboardSheet;

        public static void Apply(VisualElement root)
        {
            if (root == null) return;

            if (!root.ClassListContains(ClassWindow))
                root.AddToClassList(ClassWindow);

            StyleSheet baseSheet = Load(ref _baseSheet, BaseSheetFilter);
            if (baseSheet != null && !root.styleSheets.Contains(baseSheet))
                root.styleSheets.Add(baseSheet);

            StyleSheet dashSheet = Load(ref _dashboardSheet, DashboardSheetFilter);
            if (dashSheet != null && !root.styleSheets.Contains(dashSheet))
                root.styleSheets.Add(dashSheet);
        }

        private static StyleSheet Load(ref StyleSheet cache, string filter)
        {
            if (cache != null) return cache;
            string[] guids = AssetDatabase.FindAssets(filter);
            if (guids.Length > 0)
                cache = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guids[0]));
            return cache;
        }
    }
}
