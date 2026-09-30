using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityMCP.Editor
{
    public static class MCPUMACommands
    {
        private static readonly Dictionary<string, Func<Dictionary<string, object>, object>> Handlers =
            new Dictionary<string, Func<Dictionary<string, object>, object>>();

        private static object Invoke(string method, Dictionary<string, object> args)
        {
            if (!Handlers.TryGetValue(method, out var handler))
            {
                // Optional UMA references belong to a separate assembly so the bridge remains installable without UMA.
                var implementation = Type.GetType("UnityMCP.Editor.MCPUMACommandsImplementation, AnkleBreaker.UnityMCP.UMA.Editor", false);
                if (implementation == null)
                {
                    bool installed = Type.GetType("UMA.UMAData, UMA_Core", false) != null;
                    return new Dictionary<string, object>
                    {
                        { "error", installed
                            ? "UMA is installed but its MCP integration is unavailable. Enable UMA_INSTALLED and resolve compilation errors."
                            : "UMA is not installed. Import UMA and enable UMA_INSTALLED to use these tools." },
                        { "umaInstalled", installed },
                        { "integrationAvailable", false }
                    };
                }
                var entry = implementation.GetMethod(method, BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(Dictionary<string, object>) }, null);
                if (entry == null) throw new MissingMethodException(implementation.FullName, method);
                handler = (Func<Dictionary<string, object>, object>)Delegate.CreateDelegate(
                    typeof(Func<Dictionary<string, object>, object>), entry);
                Handlers.Add(method, handler);
            }
            return handler(args);
        }

        public static object InspectFbx(Dictionary<string, object> args) => Invoke(nameof(InspectFbx), args);
        public static object CreateSlot(Dictionary<string, object> args) => Invoke(nameof(CreateSlot), args);
        public static object CreateOverlay(Dictionary<string, object> args) => Invoke(nameof(CreateOverlay), args);
        public static object CreateWardrobeRecipe(Dictionary<string, object> args) => Invoke(nameof(CreateWardrobeRecipe), args);
        public static object RegisterAssets(Dictionary<string, object> args) => Invoke(nameof(RegisterAssets), args);
        public static object ListGlobalLibrary(Dictionary<string, object> args) => Invoke(nameof(ListGlobalLibrary), args);
        public static object ListWardrobeSlots(Dictionary<string, object> args) => Invoke(nameof(ListWardrobeSlots), args);
        public static object ListUMAMaterials(Dictionary<string, object> args) => Invoke(nameof(ListUMAMaterials), args);
        public static object VerifyRecipe(Dictionary<string, object> args) => Invoke(nameof(VerifyRecipe), args);
        public static object CreateWardrobeFromFbx(Dictionary<string, object> args) => Invoke(nameof(CreateWardrobeFromFbx), args);
        public static object RebuildGlobalLibrary(Dictionary<string, object> args) => Invoke(nameof(RebuildGlobalLibrary), args);
        public static object WardrobeEquip(Dictionary<string, object> args) => Invoke(nameof(WardrobeEquip), args);
        public static object GetProjectConfig(Dictionary<string, object> args) => Invoke(nameof(GetProjectConfig), args);
        public static object EditRace(Dictionary<string, object> args) => Invoke(nameof(EditRace), args);
        public static object CreateRace(Dictionary<string, object> args) => Invoke(nameof(CreateRace), args);
        public static object RenameAsset(Dictionary<string, object> args) => Invoke(nameof(RenameAsset), args);
    }
}
