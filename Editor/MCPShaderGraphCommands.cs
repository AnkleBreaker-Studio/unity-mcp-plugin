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
    /// Commands for Shader Graph and Visual Effect Graph interaction.
    /// Provides listing, inspection, creation, and management of shader graphs.
    /// Requires com.unity.shadergraph to be installed for shader graph features.
    /// Basic shader operations (list, inspect, compile) work without the package.
    /// </summary>
    public static class MCPShaderGraphCommands
    {
        private static bool _sgPackageChecked;
        private static bool _sgPackageInstalled;
        private static bool _vfxPackageChecked;
        private static bool _vfxPackageInstalled;

        // ─── Package Detection ───

        public static bool IsShaderGraphInstalled()
        {
            if (_sgPackageChecked) return _sgPackageInstalled;
            _sgPackageChecked = true;

            try
            {
                string manifestPath = Path.Combine(Application.dataPath, "..", "Packages", "manifest.json");
                if (File.Exists(manifestPath))
                {
                    string content = File.ReadAllText(manifestPath);
                    _sgPackageInstalled = content.Contains("\"com.unity.shadergraph\"");
                }
            }
            catch { }

            // Also check if it's a transitive dependency (URP/HDRP include it)
            if (!_sgPackageInstalled)
            {
                // Check if ShaderGraph types exist in any loaded assembly
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "Unity.ShaderGraph.Editor")
                    {
                        _sgPackageInstalled = true;
                        break;
                    }
                }
            }

            return _sgPackageInstalled;
        }

        public static bool IsVFXGraphInstalled()
        {
            if (_vfxPackageChecked) return _vfxPackageInstalled;
            _vfxPackageChecked = true;

            try
            {
                string manifestPath = Path.Combine(Application.dataPath, "..", "Packages", "manifest.json");
                if (File.Exists(manifestPath))
                {
                    string content = File.ReadAllText(manifestPath);
                    _vfxPackageInstalled = content.Contains("\"com.unity.visualeffectgraph\"");
                }
            }
            catch { }

            return _vfxPackageInstalled;
        }

        // ─── Status ───

        /// <summary>
        /// Get status of graph-related packages and available features.
        /// </summary>
        public static object GetStatus(Dictionary<string, object> args)
        {
            bool hasSG = IsShaderGraphInstalled();
            bool hasVFX = IsVFXGraphInstalled();

            var commands = new List<string>
            {
                "shadergraph/status",
                "shadergraph/list-shaders",
            };

            if (hasSG)
            {
                commands.Add("shadergraph/list");
                commands.Add("shadergraph/info");
                commands.Add("shadergraph/create");
                commands.Add("shadergraph/open");
                commands.Add("shadergraph/get-properties");
                commands.Add("shadergraph/list-subgraphs");
            }

            if (hasVFX)
            {
                commands.Add("shadergraph/list-vfx");
                commands.Add("shadergraph/open-vfx");
            }

            return new Dictionary<string, object>
            {
                { "shaderGraphInstalled", hasSG },
                { "vfxGraphInstalled", hasVFX },
                { "availableCommands", commands.ToArray() },
            };
        }

        // ─── List All Shaders ───

        /// <summary>
        /// List all shaders in the project (built-in, always available).
        /// </summary>
        public static object ListShaders(Dictionary<string, object> args)
        {
            string filter = args.ContainsKey("filter") ? args["filter"].ToString() : "";
            bool includeBuiltin = args.ContainsKey("includeBuiltin") && GetBool(args, "includeBuiltin", false);
            int maxResults = args.ContainsKey("maxResults") ? Convert.ToInt32(args["maxResults"]) : 100;

            var guids = AssetDatabase.FindAssets("t:Shader");
            var shaders = new List<Dictionary<string, object>>();

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!includeBuiltin && !path.StartsWith("Assets/")) continue;

                var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
                if (shader == null) continue;

                if (!string.IsNullOrEmpty(filter) &&
                    !shader.name.ToLower().Contains(filter.ToLower()) &&
                    !path.ToLower().Contains(filter.ToLower()))
                    continue;

                bool isShaderGraph = path.EndsWith(".shadergraph");
                int propCount = ShaderUtil.GetPropertyCount(shader);

                var info = new Dictionary<string, object>
                {
                    { "name", shader.name },
                    { "assetPath", path },
                    { "isShaderGraph", isShaderGraph },
                    { "propertyCount", propCount },
                    { "isSupported", shader.isSupported },
                    { "renderQueue", shader.renderQueue },
                    { "passCount", shader.passCount },
                };

                shaders.Add(info);

                if (shaders.Count >= maxResults) break;
            }

            return new Dictionary<string, object>
            {
                { "totalFound", shaders.Count },
                { "maxResults", maxResults },
                { "filter", string.IsNullOrEmpty(filter) ? "(none)" : filter },
                { "shaders", shaders.ToArray() },
            };
        }

        // ─── List Shader Graphs ───

        /// <summary>
        /// List all .shadergraph assets in the project. Requires Shader Graph package.
        /// </summary>
        public static object ListShaderGraphs(Dictionary<string, object> args)
        {
            if (!IsShaderGraphInstalled())
                return PackageNotInstalledError("Shader Graph (com.unity.shadergraph)");

            string filter = args.ContainsKey("filter") ? args["filter"].ToString() : "";
            int maxResults = args.ContainsKey("maxResults") ? Convert.ToInt32(args["maxResults"]) : 100;

            var guids = AssetDatabase.FindAssets("t:Shader");
            var graphs = new List<Dictionary<string, object>>();

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".shadergraph")) continue;

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

                // Try to get file size for complexity estimate
                try
                {
                    var fi = new FileInfo(Path.Combine(Application.dataPath, "..", path));
                    if (fi.Exists)
                        info["fileSizeKB"] = Math.Round(fi.Length / 1024.0, 1);
                }
                catch { }

                graphs.Add(info);
                if (graphs.Count >= maxResults) break;
            }

            return new Dictionary<string, object>
            {
                { "totalFound", graphs.Count },
                { "graphs", graphs.ToArray() },
            };
        }

        // ─── Get Shader Graph Info ───

        /// <summary>
        /// Get detailed info about a specific shader graph, including exposed properties.
        /// </summary>
        public static object GetShaderGraphInfo(Dictionary<string, object> args)
        {
            if (!IsShaderGraphInstalled())
                return PackageNotInstalledError("Shader Graph (com.unity.shadergraph)");

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

                // Get range info for Range type properties
                if (propType == ShaderUtil.ShaderPropertyType.Range)
                {
                    prop["rangeMin"] = ShaderUtil.GetRangeLimits(shader, i, 1);
                    prop["rangeMax"] = ShaderUtil.GetRangeLimits(shader, i, 2);
                    prop["rangeDefault"] = ShaderUtil.GetRangeLimits(shader, i, 0);
                }

                properties.Add(prop);
            }

            // Parse the .shadergraph JSON for additional metadata
            var graphMeta = new Dictionary<string, object>();
            try
            {
                string fullPath = Path.Combine(Application.dataPath, "..", path);
                if (File.Exists(fullPath))
                {
                    string content = File.ReadAllText(fullPath);
                    // Extract some basic counts from the JSON
                    graphMeta["fileSizeKB"] = Math.Round(new FileInfo(fullPath).Length / 1024.0, 1);

                    // Count nodes (rough estimate by counting "m_ObjectId" occurrences)
                    int nodeCount = content.Split(new[] { "\"m_ObjectId\"" }, StringSplitOptions.None).Length - 1;
                    graphMeta["estimatedNodeCount"] = nodeCount;

                    // Check for common features
                    graphMeta["usesCustomFunction"] = content.Contains("CustomFunctionNode");
                    graphMeta["usesSubGraph"] = content.Contains("SubGraphNode");
                    graphMeta["usesKeywords"] = content.Contains("ShaderKeyword");
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

            if (graphMeta.Count > 0)
                result["graphMetadata"] = graphMeta;

            return result;
        }

        // ─── Get Shader Properties ───

        /// <summary>
        /// Get exposed properties of any shader (works with .shader and .shadergraph).
        /// </summary>
        public static object GetShaderProperties(Dictionary<string, object> args)
        {
            if (!args.ContainsKey("path") && !args.ContainsKey("shaderName"))
                return new Dictionary<string, object> { { "error", "Provide 'path' (asset path) or 'shaderName' (shader name like 'Universal Render Pipeline/Lit')" } };

            Shader shader = null;

            if (args.ContainsKey("path"))
                shader = AssetDatabase.LoadAssetAtPath<Shader>(args["path"].ToString());
            else if (args.ContainsKey("shaderName"))
                shader = Shader.Find(args["shaderName"].ToString());

            if (shader == null)
                return new Dictionary<string, object> { { "error", "Shader not found." } };

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
                    { "isHidden", ShaderUtil.IsShaderPropertyHidden(shader, i) },
                    { "isNonModifiable", ShaderUtil.IsShaderPropertyNonModifiable(shader, i) },
                };

                if (propType == ShaderUtil.ShaderPropertyType.Range)
                {
                    prop["rangeMin"] = ShaderUtil.GetRangeLimits(shader, i, 1);
                    prop["rangeMax"] = ShaderUtil.GetRangeLimits(shader, i, 2);
                    prop["rangeDefault"] = ShaderUtil.GetRangeLimits(shader, i, 0);
                }

                // Get texture dimension for Texture properties
                if (propType == ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    prop["textureDimension"] = ShaderUtil.GetTexDim(shader, i).ToString();
                }

                properties.Add(prop);
            }

            return new Dictionary<string, object>
            {
                { "shaderName", shader.name },
                { "propertyCount", propCount },
                { "properties", properties.ToArray() },
            };
        }

        // ─── Create Shader Graph ───

        /// <summary>
        /// Create a new shader graph from a template type.
        /// </summary>
        public static object CreateShaderGraph(Dictionary<string, object> args)
        {
            if (!IsShaderGraphInstalled())
                return PackageNotInstalledError("Shader Graph (com.unity.shadergraph)");

            if (!args.ContainsKey("path"))
                return new Dictionary<string, object> { { "error", "Missing required parameter: path (e.g. 'Assets/Shaders/MyShader.shadergraph')" } };

            string path = args["path"].ToString();
            if (!path.EndsWith(".shadergraph"))
                path += ".shadergraph";

            if (File.Exists(Path.Combine(Application.dataPath, "..", path)))
                return new Dictionary<string, object> { { "error", $"File already exists at: {path}" } };

            string template = args.ContainsKey("template") ? args["template"].ToString().ToLower() : "urp_lit";

            try
            {
                // Try using ShaderGraph's internal API to create via menu items
                // This is the most reliable approach as the JSON format is complex and version-dependent

                // First ensure directory exists
                string dir = Path.GetDirectoryName(Path.Combine(Application.dataPath, "..", path));
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Use ProjectWindowUtil for reliable creation
                bool created = false;

                // Try menu item approach - create in a temp location then move
                string menuPath = GetMenuPathForTemplate(template);

                if (!string.IsNullOrEmpty(menuPath))
                {
                    // Select the target folder first
                    string folderPath = Path.GetDirectoryName(path);
                    var folder = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(folderPath);
                    if (folder != null)
                        Selection.activeObject = folder;

                    // Create using internal API via reflection
                    try
                    {
                        // Try to find the shader graph creation type
                        Type createActionType = null;
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            if (asm.GetName().Name == "Unity.ShaderGraph.Editor")
                            {
                                createActionType = asm.GetType("UnityEditor.ShaderGraph.CreateShaderGraph");
                                break;
                            }
                        }

                        if (createActionType != null)
                        {
                            // Invoke the creation method
                            var createMethod = createActionType.GetMethod("CreateGraph",
                                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                            if (createMethod != null)
                            {
                                createMethod.Invoke(null, new object[] { path });
                                created = true;
                            }
                        }
                    }
                    catch { }
                }

                // Fallback: create a minimal .shadergraph file
                if (!created)
                {
                    string graphContent = GetMinimalShaderGraphJson(template, Path.GetFileNameWithoutExtension(path));
                    string fullPath = Path.Combine(Application.dataPath, "..", path);
                    File.WriteAllText(fullPath, graphContent);
                    AssetDatabase.ImportAsset(path);
                    created = true;
                }

                if (created)
                {
                    AssetDatabase.Refresh();
                    return new Dictionary<string, object>
                    {
                        { "success", true },
                        { "assetPath", path },
                        { "template", template },
                        { "note", "Shader graph created. Open it in the Shader Graph editor to add nodes." },
                    };
                }

                return new Dictionary<string, object>
                {
                    { "error", "Failed to create shader graph. Try creating it manually via Assets > Create > Shader Graph." },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", "Failed to create shader graph: " + ex.Message } };
            }
        }

        // ─── Open Shader Graph ───

        /// <summary>
        /// Open a shader graph in the Shader Graph editor window.
        /// </summary>
        public static object OpenShaderGraph(Dictionary<string, object> args)
        {
            if (!IsShaderGraphInstalled())
                return PackageNotInstalledError("Shader Graph (com.unity.shadergraph)");

            if (!args.ContainsKey("path"))
                return new Dictionary<string, object> { { "error", "Missing required parameter: path" } };

            string path = args["path"].ToString();
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);

            if (asset == null)
                return new Dictionary<string, object> { { "error", $"Asset not found at: {path}" } };

            AssetDatabase.OpenAsset(asset);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "assetPath", path },
                { "note", "Shader graph opened in editor." },
            };
        }

        // ─── List Sub-Graphs ───

        /// <summary>
        /// List all .shadersubgraph assets in the project.
        /// </summary>
        public static object ListSubGraphs(Dictionary<string, object> args)
        {
            if (!IsShaderGraphInstalled())
                return PackageNotInstalledError("Shader Graph (com.unity.shadergraph)");

            var guids = AssetDatabase.FindAssets("glob:\"*.shadersubgraph\"");
            var subgraphs = new List<Dictionary<string, object>>();

            // Fallback: search by file extension
            if (guids.Length == 0)
            {
                string[] files = Directory.GetFiles(Application.dataPath, "*.shadersubgraph", SearchOption.AllDirectories);
                foreach (string file in files)
                {
                    string relativePath = "Assets" + file.Replace(Application.dataPath, "").Replace('\\', '/');
                    subgraphs.Add(new Dictionary<string, object>
                    {
                        { "assetPath", relativePath },
                        { "name", Path.GetFileNameWithoutExtension(file) },
                    });
                }
            }
            else
            {
                foreach (string guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (path.EndsWith(".shadersubgraph"))
                    {
                        subgraphs.Add(new Dictionary<string, object>
                        {
                            { "assetPath", path },
                            { "name", Path.GetFileNameWithoutExtension(path) },
                        });
                    }
                }
            }

            return new Dictionary<string, object>
            {
                { "count", subgraphs.Count },
                { "subGraphs", subgraphs.ToArray() },
            };
        }

        // ─── List VFX Graphs ───

        /// <summary>
        /// List all .vfx assets (Visual Effect Graphs) in the project.
        /// </summary>
        public static object ListVFXGraphs(Dictionary<string, object> args)
        {
            if (!IsVFXGraphInstalled())
                return PackageNotInstalledError("Visual Effect Graph (com.unity.visualeffectgraph)");

            var guids = AssetDatabase.FindAssets("t:VisualEffectAsset");
            var vfxGraphs = new List<Dictionary<string, object>>();

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);

                vfxGraphs.Add(new Dictionary<string, object>
                {
                    { "assetPath", path },
                    { "name", asset != null ? asset.name : Path.GetFileNameWithoutExtension(path) },
                });
            }

            return new Dictionary<string, object>
            {
                { "count", vfxGraphs.Count },
                { "vfxGraphs", vfxGraphs.ToArray() },
            };
        }

        // ─── Open VFX Graph ───

        public static object OpenVFXGraph(Dictionary<string, object> args)
        {
            if (!IsVFXGraphInstalled())
                return PackageNotInstalledError("Visual Effect Graph (com.unity.visualeffectgraph)");

            if (!args.ContainsKey("path"))
                return new Dictionary<string, object> { { "error", "Missing required parameter: path" } };

            string path = args["path"].ToString();
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);

            if (asset == null)
                return new Dictionary<string, object> { { "error", $"VFX Graph not found at: {path}" } };

            AssetDatabase.OpenAsset(asset);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "assetPath", path },
            };
        }

        // ─── Helpers ───

        private static string GetMenuPathForTemplate(string template)
        {
            switch (template)
            {
                case "urp_lit": return "Assets/Create/Shader Graph/URP/Lit Shader Graph";
                case "urp_unlit": return "Assets/Create/Shader Graph/URP/Unlit Shader Graph";
                case "urp_sprite_lit": return "Assets/Create/Shader Graph/URP/Sprite Lit Shader Graph";
                case "urp_sprite_unlit": return "Assets/Create/Shader Graph/URP/Sprite Unlit Shader Graph";
                case "urp_decal": return "Assets/Create/Shader Graph/URP/Decal Shader Graph";
                case "hdrp_lit": return "Assets/Create/Shader Graph/HDRP/Lit Shader Graph";
                case "hdrp_unlit": return "Assets/Create/Shader Graph/HDRP/Unlit Shader Graph";
                case "blank": return "Assets/Create/Shader Graph/Blank Shader Graph";
                default: return null;
            }
        }

        private static string GetMinimalShaderGraphJson(string template, string name)
        {
            // Minimal valid .shadergraph file structure
            // This creates a basic graph that Unity can parse and open in the editor
            return $@"{{
    ""m_SGVersion"": 3,
    ""m_Type"": ""UnityEditor.ShaderGraph.GraphData"",
    ""m_ObjectId"": ""{Guid.NewGuid():N}"",
    ""m_Properties"": [],
    ""m_Keywords"": [],
    ""m_Dropdowns"": [],
    ""m_CategoryData"": [],
    ""m_Nodes"": [],
    ""m_GroupDatas"": [],
    ""m_StickyNoteDatas"": [],
    ""m_Edges"": [],
    ""m_VertexContext"": {{
        ""m_Position"": {{ ""x"": 0.0, ""y"": 0.0 }},
        ""m_Blocks"": []
    }},
    ""m_FragmentContext"": {{
        ""m_Position"": {{ ""x"": 200.0, ""y"": 0.0 }},
        ""m_Blocks"": []
    }},
    ""m_PreviewData"": {{
        ""serializedMesh"": {{ ""m_SerializedMesh"": """", ""m_Guid"": """" }}
    }},
    ""m_Path"": ""Shader Graphs"",
    ""m_GraphPrecision"": 1,
    ""m_PreviewMode"": 2,
    ""m_OutputNode"": {{
        ""m_Id"": ""{Guid.NewGuid():N}""
    }}
}}";
        }

        private static object PackageNotInstalledError(string packageName)
        {
            return new Dictionary<string, object>
            {
                { "error", $"{packageName} is not installed. Install it via Package Manager to use this feature." },
                { "hint", "Use 'shadergraph/status' to check which graph packages are available." },
            };
        }

        private static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (!args.ContainsKey(key)) return defaultValue;
            var val = args[key];
            if (val is bool b) return b;
            if (val is string s) return s.ToLowerInvariant() == "true";
            return defaultValue;
        }
    }
}
