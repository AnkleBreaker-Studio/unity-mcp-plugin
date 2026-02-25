using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for Amplify Shader Editor integration.
    /// Detects whether Amplify Shader Editor is installed via type reflection,
    /// and provides listing, inspection, and opening of Amplify shaders.
    /// Only available when the Amplify Shader Editor asset is imported into the project.
    /// </summary>
    public static class MCPAmplifyCommands
    {
        private static bool _checked;
        private static bool _installed;
        private static Type _amplifyShaderType;
        private static Type _amplifyFunctionType;

        // ─── Detection ───

        /// <summary>
        /// Check if Amplify Shader Editor is installed by looking for its types.
        /// </summary>
        public static bool IsAmplifyInstalled()
        {
            if (_checked) return _installed;
            _checked = true;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string asmName = asm.GetName().Name;
                    if (asmName == "AmplifyShaderEditor" || asmName.Contains("AmplifyShaderEditor"))
                    {
                        _amplifyShaderType = asm.GetType("AmplifyShaderEditor.AmplifyShaderEditorWindow");
                        _amplifyFunctionType = asm.GetType("AmplifyShaderEditor.AmplifyShaderFunction");
                        _installed = _amplifyShaderType != null;
                        break;
                    }
                }
            }
            catch { }

            return _installed;
        }

        // ─── Status ───

        /// <summary>
        /// Check Amplify Shader Editor status and available features.
        /// </summary>
        public static object GetStatus(Dictionary<string, object> args)
        {
            bool installed = IsAmplifyInstalled();

            var result = new Dictionary<string, object>
            {
                { "amplifyShaderEditorInstalled", installed },
            };

            if (installed)
            {
                result["availableCommands"] = new string[]
                {
                    "amplify/status",
                    "amplify/list",
                    "amplify/info",
                    "amplify/open",
                    "amplify/list-functions",
                };

                // Count amplify shaders
                int shaderCount = CountAmplifyShaders();
                int funcCount = CountAmplifyFunctions();
                result["amplifyShaderCount"] = shaderCount;
                result["amplifyFunctionCount"] = funcCount;
            }
            else
            {
                result["note"] = "Amplify Shader Editor is not installed. Import it from the Unity Asset Store to enable these features.";
                result["availableCommands"] = new string[] { "amplify/status" };
            }

            return result;
        }

        // ─── List Amplify Shaders ───

        /// <summary>
        /// List all shaders created with Amplify Shader Editor.
        /// Detects Amplify shaders by looking for the "Amplify" tag in .shader files
        /// and by checking for companion .asset files.
        /// </summary>
        public static object ListAmplifyShaders(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            string filter = args.ContainsKey("filter") ? args["filter"].ToString() : "";
            int maxResults = args.ContainsKey("maxResults") ? Convert.ToInt32(args["maxResults"]) : 100;

            var amplifyShaders = new List<Dictionary<string, object>>();

            // Search for .shader files that have companion Amplify .asset data,
            // or that contain the "/*ASEBEGIN" marker (Amplify's serialization block)
            var shaderGuids = AssetDatabase.FindAssets("t:Shader", new[] { "Assets" });

            foreach (string guid in shaderGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".shader")) continue;

                bool isAmplify = false;
                string shaderContent = null;

                try
                {
                    string fullPath = Path.Combine(Application.dataPath, "..", path);
                    if (File.Exists(fullPath))
                    {
                        // Read first 200 lines to check for Amplify markers
                        using (var reader = new StreamReader(fullPath))
                        {
                            var lines = new List<string>();
                            string line;
                            int lineCount = 0;
                            while ((line = reader.ReadLine()) != null && lineCount < 200)
                            {
                                lines.Add(line);
                                lineCount++;
                            }
                            shaderContent = string.Join("\n", lines);
                        }

                        // Check for Amplify markers
                        isAmplify = shaderContent.Contains("/*ASEBEGIN") ||
                                    shaderContent.Contains("AmplifyShaderEditor") ||
                                    shaderContent.Contains("Amplify Shader Editor");
                    }
                }
                catch { continue; }

                if (!isAmplify) continue;

                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null) continue;

                if (!string.IsNullOrEmpty(filter) &&
                    !shader.name.ToLower().Contains(filter.ToLower()) &&
                    !path.ToLower().Contains(filter.ToLower()))
                    continue;

                var info = new Dictionary<string, object>
                {
                    { "name", shader.name },
                    { "assetPath", path },
                    { "propertyCount", ShaderUtil.GetPropertyCount(shader) },
                    { "isSupported", shader.isSupported },
                    { "renderQueue", shader.renderQueue },
                    { "passCount", shader.passCount },
                };

                amplifyShaders.Add(info);
                if (amplifyShaders.Count >= maxResults) break;
            }

            return new Dictionary<string, object>
            {
                { "count", amplifyShaders.Count },
                { "filter", string.IsNullOrEmpty(filter) ? "(none)" : filter },
                { "shaders", amplifyShaders.ToArray() },
            };
        }

        // ─── Get Amplify Shader Info ───

        /// <summary>
        /// Get detailed info about an Amplify shader, including properties and Amplify metadata.
        /// </summary>
        public static object GetAmplifyShaderInfo(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            if (!args.ContainsKey("path"))
                return new Dictionary<string, object> { { "error", "Missing required parameter: path" } };

            string path = args["path"].ToString();
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);

            if (shader == null)
                return new Dictionary<string, object> { { "error", $"Shader not found at: {path}" } };

            int propCount = ShaderUtil.GetPropertyCount(shader);
            var properties = new List<Dictionary<string, object>>();

            for (int i = 0; i < propCount; i++)
            {
                var propType = ShaderUtil.GetPropertyType(shader, i);
                var prop = new Dictionary<string, object>
                {
                    { "name", ShaderUtil.GetPropertyName(shader, i) },
                    { "description", ShaderUtil.GetPropertyDescription(shader, i) },
                    { "type", propType.ToString() },
                };

                if (propType == ShaderUtil.ShaderPropertyType.Range)
                {
                    prop["rangeMin"] = ShaderUtil.GetRangeLimits(shader, i, 1);
                    prop["rangeMax"] = ShaderUtil.GetRangeLimits(shader, i, 2);
                }

                properties.Add(prop);
            }

            // Extract Amplify-specific metadata from the shader source
            var amplifyMeta = new Dictionary<string, object>();
            try
            {
                string fullPath = Path.Combine(Application.dataPath, "..", path);
                if (File.Exists(fullPath))
                {
                    string content = File.ReadAllText(fullPath);

                    // Extract ASE version
                    int aseStart = content.IndexOf("/*ASEBEGIN");
                    int aseEnd = content.IndexOf("ASEEND*/");

                    if (aseStart >= 0 && aseEnd > aseStart)
                    {
                        string aseBlock = content.Substring(aseStart, aseEnd - aseStart + "ASEEND*/".Length);

                        // Count nodes
                        int nodeCount = aseBlock.Split(new[] { ";n;" }, StringSplitOptions.None).Length - 1;
                        amplifyMeta["estimatedNodeCount"] = nodeCount;

                        // Check for common node types
                        amplifyMeta["usesCustomExpression"] = aseBlock.Contains("CustomExpression");
                        amplifyMeta["usesFunction"] = aseBlock.Contains("Function");
                        amplifyMeta["usesTextureSample"] = aseBlock.Contains("SamplerNode") || aseBlock.Contains("TextureProperty");

                        // Extract version if present
                        int versionIdx = aseBlock.IndexOf("Version=");
                        if (versionIdx >= 0)
                        {
                            int versionEnd = aseBlock.IndexOf(';', versionIdx);
                            if (versionEnd > versionIdx)
                                amplifyMeta["amplifyVersion"] = aseBlock.Substring(versionIdx + 8, versionEnd - versionIdx - 8);
                        }
                    }
                }
            }
            catch { }

            var result = new Dictionary<string, object>
            {
                { "name", shader.name },
                { "assetPath", path },
                { "isSupported", shader.isSupported },
                { "renderQueue", shader.renderQueue },
                { "passCount", shader.passCount },
                { "propertyCount", propCount },
                { "properties", properties.ToArray() },
            };

            if (amplifyMeta.Count > 0)
                result["amplifyMetadata"] = amplifyMeta;

            return result;
        }

        // ─── Open in Amplify Editor ───

        /// <summary>
        /// Open a shader in the Amplify Shader Editor window.
        /// </summary>
        public static object OpenAmplifyShader(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            if (!args.ContainsKey("path"))
                return new Dictionary<string, object> { { "error", "Missing required parameter: path" } };

            string path = args["path"].ToString();
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);

            if (asset == null)
                return new Dictionary<string, object> { { "error", $"Asset not found at: {path}" } };

            // Try to open via Amplify's API
            try
            {
                if (_amplifyShaderType != null)
                {
                    var openMethod = _amplifyShaderType.GetMethod("LoadShaderFromDisk",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);

                    if (openMethod != null)
                    {
                        openMethod.Invoke(null, new object[] { asset });
                        return new Dictionary<string, object>
                        {
                            { "success", true },
                            { "assetPath", path },
                            { "note", "Shader opened in Amplify Shader Editor." },
                        };
                    }
                }
            }
            catch { }

            // Fallback: use AssetDatabase.OpenAsset which works for most cases
            AssetDatabase.OpenAsset(asset);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "assetPath", path },
                { "note", "Shader opened (via AssetDatabase.OpenAsset fallback)." },
            };
        }

        // ─── List Amplify Functions ───

        /// <summary>
        /// List all Amplify Shader Functions in the project.
        /// Amplify Functions are reusable node groups (similar to Sub Graphs).
        /// </summary>
        public static object ListAmplifyFunctions(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            var functions = new List<Dictionary<string, object>>();

            if (_amplifyFunctionType != null)
            {
                // Find all AmplifyShaderFunction ScriptableObjects
                var guids = AssetDatabase.FindAssets($"t:{_amplifyFunctionType.Name}");

                foreach (string guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);

                    functions.Add(new Dictionary<string, object>
                    {
                        { "name", asset != null ? asset.name : Path.GetFileNameWithoutExtension(path) },
                        { "assetPath", path },
                    });
                }
            }

            // Also search by file pattern as fallback
            if (functions.Count == 0)
            {
                try
                {
                    string[] files = Directory.GetFiles(Application.dataPath, "*.asset", SearchOption.AllDirectories);
                    foreach (string file in files)
                    {
                        // Quick check: read first few lines for Amplify function marker
                        try
                        {
                            using (var reader = new StreamReader(file))
                            {
                                string line;
                                int lineCount = 0;
                                while ((line = reader.ReadLine()) != null && lineCount < 20)
                                {
                                    if (line.Contains("AmplifyShaderFunction"))
                                    {
                                        string relativePath = "Assets" + file.Replace(Application.dataPath, "").Replace('\\', '/');
                                        functions.Add(new Dictionary<string, object>
                                        {
                                            { "name", Path.GetFileNameWithoutExtension(file) },
                                            { "assetPath", relativePath },
                                        });
                                        break;
                                    }
                                    lineCount++;
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return new Dictionary<string, object>
            {
                { "count", functions.Count },
                { "functions", functions.ToArray() },
            };
        }

        // ─── Helpers ───

        private static int CountAmplifyShaders()
        {
            int count = 0;
            var guids = AssetDatabase.FindAssets("t:Shader", new[] { "Assets" });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".shader")) continue;

                try
                {
                    string fullPath = Path.Combine(Application.dataPath, "..", path);
                    if (File.Exists(fullPath))
                    {
                        // Quick check first 5KB for Amplify marker
                        using (var reader = new StreamReader(fullPath))
                        {
                            char[] buf = new char[5120];
                            int read = reader.Read(buf, 0, buf.Length);
                            string content = new string(buf, 0, read);
                            if (content.Contains("/*ASEBEGIN") || content.Contains("AmplifyShaderEditor"))
                                count++;
                        }
                    }
                }
                catch { }
            }
            return count;
        }

        private static int CountAmplifyFunctions()
        {
            if (_amplifyFunctionType == null) return 0;
            return AssetDatabase.FindAssets($"t:{_amplifyFunctionType.Name}").Length;
        }

        private static object NotInstalledError()
        {
            return new Dictionary<string, object>
            {
                { "error", "Amplify Shader Editor is not installed in this project." },
                { "hint", "Import Amplify Shader Editor from the Unity Asset Store, then these commands will become available." },
            };
        }
    }
}
