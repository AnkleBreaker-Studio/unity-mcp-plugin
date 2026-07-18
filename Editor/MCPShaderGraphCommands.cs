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
                int propCount = shader.GetPropertyCount();

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
                    { "propertyCount", shader.GetPropertyCount() },
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

            int propCount = shader.GetPropertyCount();
            var properties = new List<Dictionary<string, object>>();

            for (int i = 0; i < propCount; i++)
            {
                var propType = shader.GetPropertyType(i);
                var prop = new Dictionary<string, object>
                {
                    { "name", shader.GetPropertyName(i) },
                    { "description", shader.GetPropertyDescription(i) },
                    { "type", propType.ToString() },
                };

                // Get range info for Range type properties
                if (propType == UnityEngine.Rendering.ShaderPropertyType.Range)
                {
                    var limits = shader.GetPropertyRangeLimits(i);
                    prop["rangeMin"] = limits.x;
                    prop["rangeMax"] = limits.y;
                    prop["rangeDefault"] = shader.GetPropertyDefaultFloatValue(i);
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

            int propCount = shader.GetPropertyCount();
            var properties = new List<Dictionary<string, object>>();

            for (int i = 0; i < propCount; i++)
            {
                var propType = shader.GetPropertyType(i);
                var prop = new Dictionary<string, object>
                {
                    { "name", shader.GetPropertyName(i) },
                    { "description", shader.GetPropertyDescription(i) },
                    { "type", propType.ToString() },
                    { "isHidden", shader.GetPropertyFlags(i).HasFlag(UnityEngine.Rendering.ShaderPropertyFlags.HideInInspector) },
                };

                if (propType == UnityEngine.Rendering.ShaderPropertyType.Range)
                {
                    var limits = shader.GetPropertyRangeLimits(i);
                    prop["rangeMin"] = limits.x;
                    prop["rangeMax"] = limits.y;
                    prop["rangeDefault"] = shader.GetPropertyDefaultFloatValue(i);
                }

                // Get texture dimension for Texture properties
                if (propType == UnityEngine.Rendering.ShaderPropertyType.Texture)
                {
                    prop["textureDimension"] = shader.GetPropertyTextureDimension(i).ToString();
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

            string template = args.ContainsKey("template") ? args["template"].ToString().ToLower() : "urp_lit";

            if (!MCPShaderGraphApi.Available)
                return new Dictionary<string, object> { { "error", "ShaderGraph real-API access unavailable: " + MCPShaderGraphApi.UnavailableReason } };

            // Don't silently downgrade an explicit URP template to blank when URP isn't installed.
            if (MCPShaderGraphApi.IsUnavailablePipelineTemplate(template))
                return new Dictionary<string, object> { { "error", $"Template '{template}' needs the URP Shader Graph package, which isn't installed. Use template 'blank' for a target-less graph." } };

            // Resolve under the project root; guard against overwriting an existing graph
            // (OverwriteGuard checks the canonical path + disk, so overwrite:true works).
            if (!MCPAssetSafety.TryResolveProjectPath(path, out string fullPath, out string pathError))
                return new Dictionary<string, object> { { "error", pathError } };
            var overwriteError = MCPAssetSafety.OverwriteGuard(path, args);
            if (overwriteError != null)
                return overwriteError;

            try
            {
                string dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                // Build the graph through ShaderGraph's real GraphData model so the output is
                // always a valid, importable asset (the old hand-built JSON produced an
                // invalid graph with a dangling output node — issue #18 bug 1).
                Type subTarget = MCPShaderGraphApi.ResolveTemplateSubTarget(template);
                string createError = MCPShaderGraphApi.CreateGraph(MCPAssetSafety.ToAssetDatabasePath(path), fullPath, subTarget);
                if (createError != null)
                    return new Dictionary<string, object> { { "error", createError } };

                bool blank = subTarget == null;
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "assetPath", path },
                    { "template", template },
                    { "note", blank
                        ? "Blank shader graph created (no active target). Open it and add a target, or use a 'urp_lit'/'urp_unlit' template."
                        : "Shader graph created with a URP target and its default blocks. Add nodes with shadergraph/add-node." },
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
        // (Graph creation and editing now go through MCPShaderGraphApi / the real
        //  ShaderGraph GraphData model; the old hand-built JSON template and node
        //  templates were removed — they produced invalid/corrupt graphs, issue #18.)

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

        // ═══════════════════════════════════════════════════════════
        // ─── Node-Level Graph Editing (JSON-based) ───
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Get all nodes in a shader graph with their types, positions, and slot info.
        /// Parses the .shadergraph JSON file directly.
        /// </summary>
        public static object GetGraphNodes(Dictionary<string, object> args)
        {
            if (!IsShaderGraphInstalled())
                return PackageNotInstalledError("Shader Graph (com.unity.shadergraph)");

            if (!args.ContainsKey("path"))
                return new Dictionary<string, object> { { "error", "Missing required parameter: path" } };

            string path = args["path"].ToString();
            string fullPath = Path.Combine(Application.dataPath, "..", path);

            if (!File.Exists(fullPath))
                return new Dictionary<string, object> { { "error", $"File not found: {path}" } };

            try
            {
                string content = File.ReadAllText(fullPath);
                var jsonBlocks = ParseMultiJson(content);
                var nodes = new List<Dictionary<string, object>>();

                foreach (var block in jsonBlocks)
                {
                    string typeVal = ExtractJsonString(block, "m_Type");
                    string objectId = ExtractJsonString(block, "m_ObjectId") ?? ExtractJsonString(block, "m_Id");

                    if (string.IsNullOrEmpty(typeVal) || string.IsNullOrEmpty(objectId)) continue;

                    // Skip the main GraphData object
                    if (typeVal.Contains("GraphData")) continue;

                    // Extract position from m_DrawState
                    float posX = 0, posY = 0;
                    int drawStateIdx = block.IndexOf("\"m_DrawState\"");
                    if (drawStateIdx >= 0)
                    {
                        string posSection = block.Substring(drawStateIdx, Math.Min(500, block.Length - drawStateIdx));
                        posX = ExtractJsonFloat(posSection, "x");
                        posY = ExtractJsonFloat(posSection, "y");
                    }

                    // Extract node name
                    string name = ExtractJsonString(block, "m_Name") ?? typeVal.Split('.').Last();

                    // Count slots
                    int slotCount = CountOccurrences(block, "\"m_Id\"");

                    var nodeInfo = new Dictionary<string, object>
                    {
                        { "objectId", objectId },
                        { "type", typeVal },
                        { "name", name },
                        { "position", new Dictionary<string, object> { { "x", posX }, { "y", posY } } },
                    };

                    // Extract any m_Value or m_DefaultValue
                    string defaultValue = ExtractJsonString(block, "m_DefaultValue") ?? ExtractJsonString(block, "m_Value");
                    if (!string.IsNullOrEmpty(defaultValue))
                        nodeInfo["defaultValue"] = defaultValue;

                    nodes.Add(nodeInfo);
                }

                return new Dictionary<string, object>
                {
                    { "assetPath", path },
                    { "nodeCount", nodes.Count },
                    { "nodes", nodes.ToArray() },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to parse graph: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Get all edges (connections) in a shader graph.
        /// </summary>
        public static object GetGraphEdges(Dictionary<string, object> args)
        {
            if (!IsShaderGraphInstalled())
                return PackageNotInstalledError("Shader Graph (com.unity.shadergraph)");

            if (!args.ContainsKey("path"))
                return new Dictionary<string, object> { { "error", "Missing required parameter: path" } };

            string path = args["path"].ToString();
            string fullPath = Path.Combine(Application.dataPath, "..", path);

            if (!File.Exists(fullPath))
                return new Dictionary<string, object> { { "error", $"File not found: {path}" } };

            try
            {
                string content = File.ReadAllText(fullPath);
                var edges = ParseEdgesFromJson(content);

                return new Dictionary<string, object>
                {
                    { "assetPath", path },
                    { "edgeCount", edges.Count },
                    { "edges", edges.ToArray() },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to parse edges: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Add a node to a shader graph. Uses reflection to find available node types
        /// from the Shader Graph assembly and generates valid serialized JSON.
        /// </summary>
        public static object AddGraphNode(Dictionary<string, object> args)
        {
            if (!IsShaderGraphInstalled())
                return PackageNotInstalledError("Shader Graph (com.unity.shadergraph)");

            if (!args.ContainsKey("path") || !args.ContainsKey("nodeType"))
                return new Dictionary<string, object> { { "error", "path and nodeType are required" } };

            string path = args["path"].ToString();
            string nodeType = args["nodeType"].ToString();
            // Accept positionX/positionY (canonical) and x/y (common alias).
            float posX = args.ContainsKey("positionX") ? Convert.ToSingle(args["positionX"]) : args.ContainsKey("x") ? Convert.ToSingle(args["x"]) : 0f;
            float posY = args.ContainsKey("positionY") ? Convert.ToSingle(args["positionY"]) : args.ContainsKey("y") ? Convert.ToSingle(args["y"]) : 0f;

            if (!MCPShaderGraphApi.Available)
                return new Dictionary<string, object> { { "error", "ShaderGraph real-API access unavailable: " + MCPShaderGraphApi.UnavailableReason } };
            if (!MCPAssetSafety.TryResolveProjectPath(path, out string fullPath, out string pathError))
                return new Dictionary<string, object> { { "error", pathError } };
            if (!File.Exists(fullPath))
                return new Dictionary<string, object> { { "error", $"File not found: {path}" } };

            Type resolvedType = ResolveShaderGraphNodeType(nodeType);
            if (resolvedType == null)
                return new Dictionary<string, object> { { "error", $"Unknown node type: {nodeType}. Use 'shadergraph/get-node-types' to list available types." } };

            try
            {
                // Load → mutate → save through the real GraphData model. Nodes get their real
                // slots and the position is honored; the graph is guaranteed valid on save
                // (old string-surgery produced empty-slot nodes and dropped the position — #18 bugs 3,4).
                var graph = MCPShaderGraphApi.LoadGraph(fullPath);
                string nodeId = MCPShaderGraphApi.AddNode(graph, resolvedType, posX, posY);
                MCPShaderGraphApi.SaveGraph(MCPAssetSafety.ToAssetDatabasePath(path), graph);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "assetPath", path },
                    { "nodeId", nodeId },
                    { "nodeType", nodeType },
                    { "position", new Dictionary<string, object> { { "x", posX }, { "y", posY } } },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to add node: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Remove a node from a shader graph by its object ID.
        /// Also removes all edges connected to it.
        /// </summary>
        public static object RemoveGraphNode(Dictionary<string, object> args)
        {
            if (!IsShaderGraphInstalled())
                return PackageNotInstalledError("Shader Graph (com.unity.shadergraph)");

            if (!args.ContainsKey("path") || !args.ContainsKey("nodeId"))
                return new Dictionary<string, object> { { "error", "path and nodeId are required" } };

            string path = args["path"].ToString();
            string nodeId = args["nodeId"].ToString();

            if (!MCPShaderGraphApi.Available)
                return new Dictionary<string, object> { { "error", "ShaderGraph real-API access unavailable: " + MCPShaderGraphApi.UnavailableReason } };
            if (!MCPAssetSafety.TryResolveProjectPath(path, out string fullPath, out string pathError))
                return new Dictionary<string, object> { { "error", pathError } };
            if (!File.Exists(fullPath))
                return new Dictionary<string, object> { { "error", $"File not found: {path}" } };

            try
            {
                // Real GraphData.RemoveNode also removes the node's connected edges and
                // leaves every survivor byte-faithful — the old regex path blanked
                // surviving multi-line edges to m_Id:"" and left a dangling node ref.
                var graph = MCPShaderGraphApi.LoadGraph(fullPath);
                var node = MCPShaderGraphApi.GetNodeFromId(graph, nodeId);
                if (node == null)
                    return new Dictionary<string, object> { { "error", $"Node not found: {nodeId}" } };
                MCPShaderGraphApi.RemoveNode(graph, node);
                MCPShaderGraphApi.SaveGraph(MCPAssetSafety.ToAssetDatabasePath(path), graph);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "removedNodeId", nodeId },
                    { "assetPath", path },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to remove node: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Connect two nodes in a shader graph by creating an edge.
        /// </summary>
        public static object ConnectGraphNodes(Dictionary<string, object> args)
        {
            if (!IsShaderGraphInstalled())
                return PackageNotInstalledError("Shader Graph (com.unity.shadergraph)");

            if (!args.ContainsKey("path"))
                return new Dictionary<string, object> { { "error", "path is required" } };
            if (!args.ContainsKey("outputNodeId") || !args.ContainsKey("outputSlotId"))
                return new Dictionary<string, object> { { "error", "outputNodeId and outputSlotId are required" } };
            if (!args.ContainsKey("inputNodeId") || !args.ContainsKey("inputSlotId"))
                return new Dictionary<string, object> { { "error", "inputNodeId and inputSlotId are required" } };

            string path = args["path"].ToString();
            string outputNodeId = args["outputNodeId"].ToString();
            int outputSlotId = Convert.ToInt32(args["outputSlotId"]);
            string inputNodeId = args["inputNodeId"].ToString();
            int inputSlotId = Convert.ToInt32(args["inputSlotId"]);

            if (!MCPShaderGraphApi.Available)
                return new Dictionary<string, object> { { "error", "ShaderGraph real-API access unavailable: " + MCPShaderGraphApi.UnavailableReason } };
            if (!MCPAssetSafety.TryResolveProjectPath(path, out string fullPath, out string pathError))
                return new Dictionary<string, object> { { "error", pathError } };
            if (!File.Exists(fullPath))
                return new Dictionary<string, object> { { "error", $"File not found: {path}" } };

            try
            {
                var graph = MCPShaderGraphApi.LoadGraph(fullPath);
                var outNode = MCPShaderGraphApi.GetNodeFromId(graph, outputNodeId);
                var inNode = MCPShaderGraphApi.GetNodeFromId(graph, inputNodeId);
                if (outNode == null) return new Dictionary<string, object> { { "error", $"Output node not found: {outputNodeId}" } };
                if (inNode == null) return new Dictionary<string, object> { { "error", $"Input node not found: {inputNodeId}" } };

                // graph.Connect validates slot compatibility and refuses cycles, so the saved
                // graph is always valid (unlike the old blind edge-JSON insertion).
                string connectError = MCPShaderGraphApi.Connect(graph, outNode, outputSlotId, inNode, inputSlotId);
                if (connectError != null)
                    return new Dictionary<string, object> { { "error", connectError } };
                MCPShaderGraphApi.SaveGraph(MCPAssetSafety.ToAssetDatabasePath(path), graph);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "assetPath", path },
                    { "outputNodeId", outputNodeId },
                    { "outputSlotId", outputSlotId },
                    { "inputNodeId", inputNodeId },
                    { "inputSlotId", inputSlotId },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to connect nodes: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Disconnect two nodes in a shader graph by removing their edge.
        /// </summary>
        public static object DisconnectGraphNodes(Dictionary<string, object> args)
        {
            if (!IsShaderGraphInstalled())
                return PackageNotInstalledError("Shader Graph (com.unity.shadergraph)");

            if (!args.ContainsKey("path"))
                return new Dictionary<string, object> { { "error", "path is required" } };
            if (!args.ContainsKey("outputNodeId") || !args.ContainsKey("inputNodeId"))
                return new Dictionary<string, object> { { "error", "outputNodeId and inputNodeId are required" } };

            string path = args["path"].ToString();
            string outputNodeId = args["outputNodeId"].ToString();
            string inputNodeId = args["inputNodeId"].ToString();
            int outputSlotId = args.ContainsKey("outputSlotId") ? Convert.ToInt32(args["outputSlotId"]) : -1;
            int inputSlotId = args.ContainsKey("inputSlotId") ? Convert.ToInt32(args["inputSlotId"]) : -1;

            if (!MCPShaderGraphApi.Available)
                return new Dictionary<string, object> { { "error", "ShaderGraph real-API access unavailable: " + MCPShaderGraphApi.UnavailableReason } };
            if (!MCPAssetSafety.TryResolveProjectPath(path, out string fullPath, out string pathError))
                return new Dictionary<string, object> { { "error", pathError } };
            if (!File.Exists(fullPath))
                return new Dictionary<string, object> { { "error", $"File not found: {path}" } };

            try
            {
                // Remove exactly the edges matching the full tuple, via the real edge model —
                // the old parse-and-rebuild matched by output slot only (dropping sibling edges)
                // and blanked surviving multi-line edges to m_Id:"" (issue #18 bug 2).
                var graph = MCPShaderGraphApi.LoadGraph(fullPath);
                int removed = MCPShaderGraphApi.Disconnect(graph, outputNodeId, outputSlotId, inputNodeId, inputSlotId);
                MCPShaderGraphApi.SaveGraph(MCPAssetSafety.ToAssetDatabasePath(path), graph);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "assetPath", path },
                    { "removedEdges", removed },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to disconnect: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Set a property value on a node in the shader graph JSON.
        /// </summary>
        public static object SetGraphNodeProperty(Dictionary<string, object> args)
        {
            if (!IsShaderGraphInstalled())
                return PackageNotInstalledError("Shader Graph (com.unity.shadergraph)");

            if (!args.ContainsKey("path") || !args.ContainsKey("nodeId") || !args.ContainsKey("propertyName"))
                return new Dictionary<string, object> { { "error", "path, nodeId, and propertyName are required" } };

            string path = args["path"].ToString();
            string nodeId = args["nodeId"].ToString();
            string propertyName = args["propertyName"].ToString();
            string value = args.ContainsKey("value") ? args["value"].ToString() : "";

            // Confine the write under the project root (traversal/absolute-escape guard).
            if (!MCPAssetSafety.TryResolveProjectPath(path, out string fullPath, out string pathError))
                return new Dictionary<string, object> { { "error", pathError } };
            if (!File.Exists(fullPath))
                return new Dictionary<string, object> { { "error", $"File not found: {path}" } };

            try
            {
                string content = File.ReadAllText(fullPath);
                var blocks = ParseMultiJson(content);
                var newBlocks = new List<string>();
                bool found = false;

                foreach (var block in blocks)
                {
                    string blockId = ExtractJsonString(block, "m_ObjectId") ?? ExtractJsonString(block, "m_Id");

                    if (blockId == nodeId)
                    {
                        // Replace the property value in this block
                        string modified = SetJsonProperty(block, propertyName, value);
                        newBlocks.Add(modified);
                        found = true;
                    }
                    else
                    {
                        newBlocks.Add(block);
                    }
                }

                if (!found)
                    return new Dictionary<string, object> { { "error", $"Node with ID '{nodeId}' not found" } };

                string newContent = string.Join("\n\n", newBlocks);
                File.WriteAllText(fullPath, newContent);
                AssetDatabase.ImportAsset(MCPAssetSafety.ToAssetDatabasePath(path));

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "nodeId", nodeId },
                    { "propertyName", propertyName },
                    { "value", value },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to set property: {ex.Message}" } };
            }
        }

        /// <summary>
        /// List available Shader Graph node types via reflection on the ShaderGraph assembly.
        /// </summary>
        public static object GetNodeTypes(Dictionary<string, object> args)
        {
            if (!IsShaderGraphInstalled())
                return PackageNotInstalledError("Shader Graph (com.unity.shadergraph)");

            string filter = args.ContainsKey("filter") ? args["filter"].ToString().ToLower() : "";
            int maxResults = args.ContainsKey("maxResults") ? Convert.ToInt32(args["maxResults"]) : 200;

            var nodeTypes = new List<Dictionary<string, object>>();

            try
            {
                Assembly sgAssembly = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "Unity.ShaderGraph.Editor")
                    {
                        sgAssembly = asm;
                        break;
                    }
                }

                if (sgAssembly == null)
                    return new Dictionary<string, object> { { "error", "ShaderGraph assembly not found" } };

                // Find the base node type
                Type baseNodeType = sgAssembly.GetType("UnityEditor.ShaderGraph.AbstractMaterialNode");
                if (baseNodeType == null)
                    return new Dictionary<string, object> { { "error", "AbstractMaterialNode type not found" } };

                foreach (var type in sgAssembly.GetTypes())
                {
                    if (type.IsAbstract || type.IsInterface) continue;
                    if (!baseNodeType.IsAssignableFrom(type)) continue;

                    string typeName = type.Name;
                    string fullName = type.FullName;

                    if (!string.IsNullOrEmpty(filter) &&
                        !typeName.ToLower().Contains(filter) &&
                        !fullName.ToLower().Contains(filter))
                        continue;

                    // Try to get a title attribute
                    string title = typeName;
                    var titleAttr = type.GetCustomAttributes(false)
                        .FirstOrDefault(a => a.GetType().Name.Contains("Title"));
                    if (titleAttr != null)
                    {
                        var titleProp = titleAttr.GetType().GetProperty("title") ??
                                        titleAttr.GetType().GetProperty("Title");
                        if (titleProp != null)
                            title = titleProp.GetValue(titleAttr)?.ToString() ?? typeName;
                    }

                    nodeTypes.Add(new Dictionary<string, object>
                    {
                        { "name", typeName },
                        { "fullName", fullName },
                        { "title", title },
                    });

                    if (nodeTypes.Count >= maxResults) break;
                }
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to enumerate types: {ex.Message}" } };
            }

            nodeTypes.Sort((a, b) => string.Compare(a["name"].ToString(), b["name"].ToString(), StringComparison.Ordinal));

            return new Dictionary<string, object>
            {
                { "count", nodeTypes.Count },
                { "nodeTypes", nodeTypes.ToArray() },
            };
        }

        // ─── JSON Parsing Helpers ───

        private static List<string> ParseMultiJson(string content)
        {
            var blocks = new List<string>();
            int depth = 0;
            int blockStart = -1;

            for (int i = 0; i < content.Length; i++)
            {
                char c = content[i];
                if (c == '{')
                {
                    if (depth == 0) blockStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && blockStart >= 0)
                    {
                        blocks.Add(content.Substring(blockStart, i - blockStart + 1));
                        blockStart = -1;
                    }
                }
            }

            return blocks;
        }

        private static string ExtractJsonString(string json, string key)
        {
            string pattern = $"\"{key}\"\\s*:\\s*\"([^\"]*)\"";
            var match = System.Text.RegularExpressions.Regex.Match(json, pattern);
            return match.Success ? match.Groups[1].Value : null;
        }

        private static float ExtractJsonFloat(string json, string key)
        {
            string pattern = $"\"{key}\"\\s*:\\s*([\\-0-9.eE]+)";
            var match = System.Text.RegularExpressions.Regex.Match(json, pattern);
            if (match.Success && float.TryParse(match.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float val))
                return val;
            return 0f;
        }

        private static int CountOccurrences(string text, string pattern)
        {
            int count = 0;
            int idx = 0;
            while ((idx = text.IndexOf(pattern, idx)) != -1)
            {
                count++;
                idx += pattern.Length;
            }
            return count;
        }

        private static List<Dictionary<string, object>> ParseEdgesFromJson(string content)
        {
            var edges = new List<Dictionary<string, object>>();

            int edgesIdx = content.IndexOf("\"m_Edges\"");
            if (edgesIdx < 0) return edges;

            int arrayStart = content.IndexOf('[', edgesIdx);
            if (arrayStart < 0) return edges;

            int arrayEnd = FindMatchingBracket(content, arrayStart);
            if (arrayEnd < 0) return edges;

            string edgesSection = content.Substring(arrayStart + 1, arrayEnd - arrayStart - 1);

            // Parse individual edge objects
            int depth = 0;
            int objStart = -1;

            for (int i = 0; i < edgesSection.Length; i++)
            {
                if (edgesSection[i] == '{')
                {
                    if (depth == 0) objStart = i;
                    depth++;
                }
                else if (edgesSection[i] == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        string edgeJson = edgesSection.Substring(objStart, i - objStart + 1);

                        // Extract output node ID
                        string outNodePattern = "\"m_OutputSlot\".*?\"m_Id\"\\s*:\\s*\"([^\"]*)\"";
                        var outMatch = System.Text.RegularExpressions.Regex.Match(edgeJson, outNodePattern);
                        string outNodeId = outMatch.Success ? outMatch.Groups[1].Value : "";

                        // Extract output slot ID
                        string outSlotPattern = "\"m_OutputSlot\".*?\"m_SlotId\"\\s*:\\s*(\\d+)";
                        var outSlotMatch = System.Text.RegularExpressions.Regex.Match(edgeJson, outSlotPattern);
                        int outSlotId = outSlotMatch.Success ? int.Parse(outSlotMatch.Groups[1].Value) : 0;

                        // Extract input node ID
                        string inNodePattern = "\"m_InputSlot\".*?\"m_Id\"\\s*:\\s*\"([^\"]*)\"";
                        var inMatch = System.Text.RegularExpressions.Regex.Match(edgeJson, inNodePattern);
                        string inNodeId = inMatch.Success ? inMatch.Groups[1].Value : "";

                        // Extract input slot ID
                        string inSlotPattern = "\"m_InputSlot\".*?\"m_SlotId\"\\s*:\\s*(\\d+)";
                        var inSlotMatch = System.Text.RegularExpressions.Regex.Match(edgeJson, inSlotPattern);
                        int inSlotId = inSlotMatch.Success ? int.Parse(inSlotMatch.Groups[1].Value) : 0;

                        edges.Add(new Dictionary<string, object>
                        {
                            { "outputNodeId", outNodeId },
                            { "outputSlotId", outSlotId },
                            { "inputNodeId", inNodeId },
                            { "inputSlotId", inSlotId },
                        });

                        objStart = -1;
                    }
                }
            }

            return edges;
        }

        private static int FindJsonArrayEnd(string content, string arrayName)
        {
            int idx = content.IndexOf($"\"{arrayName}\"");
            if (idx < 0) return -1;
            int arrayStart = content.IndexOf('[', idx);
            if (arrayStart < 0) return -1;
            return FindMatchingBracket(content, arrayStart);
        }

        private static int FindMatchingBracket(string content, int openPos)
        {
            char open = content[openPos];
            char close = open == '[' ? ']' : '}';
            int depth = 1;
            for (int i = openPos + 1; i < content.Length; i++)
            {
                if (content[i] == open) depth++;
                else if (content[i] == close)
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static string RemoveEdgesForNode(string graphBlock, string nodeId, out int removedCount)
        {
            removedCount = 0;
            int edgesIdx = graphBlock.IndexOf("\"m_Edges\"");
            if (edgesIdx < 0) return graphBlock;

            int arrayStart = graphBlock.IndexOf('[', edgesIdx);
            int arrayEnd = FindMatchingBracket(graphBlock, arrayStart);
            if (arrayEnd < 0) return graphBlock;

            var edges = ParseEdgesFromJson(graphBlock);
            var keepEdges = new List<string>();

            foreach (var edge in edges)
            {
                string outNode = edge["outputNodeId"].ToString();
                string inNode = edge["inputNodeId"].ToString();

                if (outNode == nodeId || inNode == nodeId)
                {
                    removedCount++;
                    continue;
                }

                keepEdges.Add($"{{\"m_OutputSlot\":{{\"m_Node\":{{\"m_Id\":\"{outNode}\"}},\"m_SlotId\":{edge["outputSlotId"]}}},\"m_InputSlot\":{{\"m_Node\":{{\"m_Id\":\"{inNode}\"}},\"m_SlotId\":{edge["inputSlotId"]}}}}}");
            }

            string newArray = "[" + string.Join(",", keepEdges) + "]";
            return graphBlock.Substring(0, arrayStart) + newArray + graphBlock.Substring(arrayEnd + 1);
        }

        private static string SetJsonProperty(string block, string propertyName, string value)
        {
            // Try to find and replace a string property
            string strPattern = $"\"{propertyName}\"\\s*:\\s*\"[^\"]*\"";
            if (System.Text.RegularExpressions.Regex.IsMatch(block, strPattern))
                return System.Text.RegularExpressions.Regex.Replace(block, strPattern, $"\"{propertyName}\": \"{value}\"");

            // Try numeric property
            string numPattern = $"\"{propertyName}\"\\s*:\\s*[\\-0-9.eE]+";
            if (System.Text.RegularExpressions.Regex.IsMatch(block, numPattern))
                return System.Text.RegularExpressions.Regex.Replace(block, numPattern, $"\"{propertyName}\": {value}");

            // Try boolean property
            string boolPattern = $"\"{propertyName}\"\\s*:\\s*(true|false)";
            if (System.Text.RegularExpressions.Regex.IsMatch(block, boolPattern))
                return System.Text.RegularExpressions.Regex.Replace(block, boolPattern, $"\"{propertyName}\": {value.ToLower()}");

            return block; // Property not found
        }

        internal static Type ResolveShaderGraphNodeType(string typeName)
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "Unity.ShaderGraph.Editor") continue;

                    // Try exact match
                    Type t = asm.GetType($"UnityEditor.ShaderGraph.{typeName}");
                    if (t != null) return t;

                    // Try with "Node" suffix
                    t = asm.GetType($"UnityEditor.ShaderGraph.{typeName}Node");
                    if (t != null) return t;

                    // Search by name
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) ||
                            type.Name.Equals(typeName + "Node", StringComparison.OrdinalIgnoreCase))
                            return type;
                    }
                }
            }
            catch { }

            return null;
        }

    }
}
