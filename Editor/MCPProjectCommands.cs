using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityMCP.Editor
{
    public static class MCPProjectCommands
    {
        public static object GetInfo()
        {
            string projectPath = Application.dataPath.Replace("/Assets", "");

            // Get scenes in build settings
            var buildScenes = EditorBuildSettings.scenes
                .Select(s => new Dictionary<string, object>
                {
                    { "path", s.path },
                    { "enabled", s.enabled },
                })
                .ToList();

            // Detect render pipeline
            string renderPipeline = "Built-in";
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                string rpName = GraphicsSettings.currentRenderPipeline.GetType().Name;
                if (rpName.Contains("Universal") || rpName.Contains("URP"))
                    renderPipeline = "URP";
                else if (rpName.Contains("HighDefinition") || rpName.Contains("HDRP"))
                    renderPipeline = "HDRP";
                else
                    renderPipeline = rpName;
            }

            // Count assets
            var allAssets = AssetDatabase.FindAssets("", new[] { "Assets" });

            // Get packages
            string packagesPath = Path.Combine(projectPath, "Packages", "manifest.json");
            string packages = "[]";
            if (File.Exists(packagesPath))
                packages = File.ReadAllText(packagesPath);

            return new Dictionary<string, object>
            {
                { "productName", Application.productName },
                { "companyName", Application.companyName },
                { "version", Application.version },
                { "unityVersion", Application.unityVersion },
                { "projectPath", projectPath },
                { "dataPath", Application.dataPath },
                { "platform", EditorUserBuildSettings.activeBuildTarget.ToString() },
                { "renderPipeline", renderPipeline },
                { "colorSpace", PlayerSettings.colorSpace.ToString() },
                { "scriptingBackend", PlayerSettings.GetScriptingBackend(EditorUserBuildSettings.selectedBuildTargetGroup).ToString() },
                { "apiCompatibility", PlayerSettings.GetApiCompatibilityLevel(EditorUserBuildSettings.selectedBuildTargetGroup).ToString() },
                { "buildScenes", buildScenes },
                { "totalAssetCount", allAssets.Length },
                { "packagesManifest", packages },
            };
        }
    }
}
