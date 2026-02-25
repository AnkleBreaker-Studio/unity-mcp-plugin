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

        // ═══════════════════════════════════════════════════════════
        // ─── Node-Level Editing via Reflection ───
        // ═══════════════════════════════════════════════════════════

        private static Assembly _amplifyAssembly;
        private static Type _parentNodeType;
        private static Type _parentGraphType;

        private static Assembly GetAmplifyAssembly()
        {
            if (_amplifyAssembly != null) return _amplifyAssembly;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name == "AmplifyShaderEditor" || asm.GetName().Name.Contains("AmplifyShaderEditor"))
                {
                    _amplifyAssembly = asm;
                    _parentNodeType = asm.GetType("AmplifyShaderEditor.ParentNode");
                    _parentGraphType = asm.GetType("AmplifyShaderEditor.ParentGraph");
                    break;
                }
            }
            return _amplifyAssembly;
        }

        /// <summary>
        /// Get available Amplify Shader Editor node types via reflection.
        /// </summary>
        public static object GetAmplifyNodeTypes(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            var asm = GetAmplifyAssembly();
            if (asm == null)
                return new Dictionary<string, object> { { "error", "Amplify assembly not accessible" } };

            string filter = args.ContainsKey("filter") ? args["filter"].ToString().ToLower() : "";
            int maxResults = args.ContainsKey("maxResults") ? Convert.ToInt32(args["maxResults"]) : 200;

            var nodeTypes = new List<Dictionary<string, object>>();

            try
            {
                Type baseType = _parentNodeType;
                if (baseType == null)
                    return new Dictionary<string, object> { { "error", "ParentNode type not found in Amplify assembly" } };

                foreach (var type in asm.GetTypes())
                {
                    if (type.IsAbstract || type.IsInterface) continue;
                    if (!baseType.IsAssignableFrom(type)) continue;

                    string name = type.Name;
                    if (!string.IsNullOrEmpty(filter) && !name.ToLower().Contains(filter))
                        continue;

                    // Try to get node attributes
                    string category = "";
                    try
                    {
                        var nodeAttr = type.GetCustomAttributes(false)
                            .FirstOrDefault(a => a.GetType().Name.Contains("NodeAttributes"));
                        if (nodeAttr != null)
                        {
                            var catProp = nodeAttr.GetType().GetField("Category") ??
                                          nodeAttr.GetType().GetField("category");
                            if (catProp != null)
                                category = catProp.GetValue(nodeAttr)?.ToString() ?? "";

                            var nameProp = nodeAttr.GetType().GetField("Name") ??
                                           nodeAttr.GetType().GetField("name");
                            if (nameProp != null)
                            {
                                string displayName = nameProp.GetValue(nodeAttr)?.ToString();
                                if (!string.IsNullOrEmpty(displayName))
                                    name = displayName;
                            }
                        }
                    }
                    catch { }

                    nodeTypes.Add(new Dictionary<string, object>
                    {
                        { "name", name },
                        { "typeName", type.Name },
                        { "fullName", type.FullName },
                        { "category", category },
                    });

                    if (nodeTypes.Count >= maxResults) break;
                }
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to enumerate node types: {ex.Message}" } };
            }

            nodeTypes.Sort((a, b) => string.Compare(a["name"].ToString(), b["name"].ToString(), StringComparison.Ordinal));

            return new Dictionary<string, object>
            {
                { "count", nodeTypes.Count },
                { "nodeTypes", nodeTypes.ToArray() },
            };
        }

        /// <summary>
        /// Get nodes from the currently open Amplify Shader Editor graph via reflection.
        /// </summary>
        public static object GetAmplifyGraphNodes(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            try
            {
                var window = GetOpenAmplifyWindow();
                if (window == null)
                    return new Dictionary<string, object>
                    {
                        { "error", "No Amplify Shader Editor window is open. Open a shader first with amplify/open." },
                    };

                // Get the current graph
                var graph = GetCurrentGraph(window);
                if (graph == null)
                    return new Dictionary<string, object> { { "error", "No graph loaded in the Amplify editor" } };

                // Get all nodes
                var allNodesProp = _parentGraphType.GetProperty("AllNodes") ??
                                   _parentGraphType.GetProperty("CurrentNodes");
                if (allNodesProp == null)
                {
                    // Try field
                    var nodesField = _parentGraphType.GetField("m_nodes", BindingFlags.NonPublic | BindingFlags.Instance) ??
                                     _parentGraphType.GetField("m_allNodes", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (nodesField == null)
                        return new Dictionary<string, object> { { "error", "Could not access nodes collection" } };
                }

                var nodes = new List<Dictionary<string, object>>();

                // Use generic approach to iterate nodes
                var nodesObj = allNodesProp != null ? allNodesProp.GetValue(graph) : null;
                if (nodesObj == null) return new Dictionary<string, object> { { "error", "Nodes collection is null" } };

                var enumerator = nodesObj.GetType().GetMethod("GetEnumerator")?.Invoke(nodesObj, null);
                if (enumerator == null) return new Dictionary<string, object> { { "error", "Cannot enumerate nodes" } };

                var moveNext = enumerator.GetType().GetMethod("MoveNext");
                var current = enumerator.GetType().GetProperty("Current");

                while ((bool)moveNext.Invoke(enumerator, null))
                {
                    var node = current.GetValue(enumerator);
                    if (node == null) continue;

                    var nodeType = node.GetType();
                    var nodeInfo = new Dictionary<string, object>
                    {
                        { "typeName", nodeType.Name },
                    };

                    // Get UniqueId
                    var uniqueIdProp = nodeType.GetProperty("UniqueId") ??
                                       _parentNodeType?.GetProperty("UniqueId");
                    if (uniqueIdProp != null)
                        nodeInfo["uniqueId"] = uniqueIdProp.GetValue(node)?.ToString() ?? "-1";

                    // Get position
                    var posProp = nodeType.GetProperty("Position") ?? nodeType.GetProperty("Vec2Position");
                    if (posProp != null)
                    {
                        var pos = posProp.GetValue(node);
                        if (pos is Rect r)
                            nodeInfo["position"] = new Dictionary<string, object> { { "x", r.x }, { "y", r.y } };
                        else if (pos is Vector2 v)
                            nodeInfo["position"] = new Dictionary<string, object> { { "x", v.x }, { "y", v.y } };
                    }

                    // Get input/output port counts
                    var inputPortsProp = nodeType.GetProperty("InputPorts");
                    var outputPortsProp = nodeType.GetProperty("OutputPorts");
                    if (inputPortsProp != null)
                    {
                        var inputs = inputPortsProp.GetValue(node);
                        if (inputs != null)
                        {
                            var countProp = inputs.GetType().GetProperty("Count");
                            if (countProp != null)
                                nodeInfo["inputPortCount"] = countProp.GetValue(inputs);
                        }
                    }
                    if (outputPortsProp != null)
                    {
                        var outputs = outputPortsProp.GetValue(node);
                        if (outputs != null)
                        {
                            var countProp = outputs.GetType().GetProperty("Count");
                            if (countProp != null)
                                nodeInfo["outputPortCount"] = countProp.GetValue(outputs);
                        }
                    }

                    nodes.Add(nodeInfo);
                }

                return new Dictionary<string, object>
                {
                    { "nodeCount", nodes.Count },
                    { "nodes", nodes.ToArray() },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to get nodes: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Get connections between nodes in the currently open Amplify graph.
        /// </summary>
        public static object GetAmplifyGraphConnections(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            try
            {
                var window = GetOpenAmplifyWindow();
                if (window == null)
                    return new Dictionary<string, object> { { "error", "No Amplify editor window is open" } };

                var graph = GetCurrentGraph(window);
                if (graph == null)
                    return new Dictionary<string, object> { { "error", "No graph loaded" } };

                var connections = new List<Dictionary<string, object>>();

                // Iterate all nodes, check their input ports for connections
                var allNodesProp = _parentGraphType.GetProperty("AllNodes") ??
                                   _parentGraphType.GetProperty("CurrentNodes");
                if (allNodesProp == null)
                    return new Dictionary<string, object> { { "error", "Cannot access nodes" } };

                var nodesObj = allNodesProp.GetValue(graph);
                if (nodesObj == null)
                    return new Dictionary<string, object> { { "error", "Nodes collection is null" } };

                var enumerator = nodesObj.GetType().GetMethod("GetEnumerator")?.Invoke(nodesObj, null);
                var moveNext = enumerator.GetType().GetMethod("MoveNext");
                var current = enumerator.GetType().GetProperty("Current");

                while ((bool)moveNext.Invoke(enumerator, null))
                {
                    var node = current.GetValue(enumerator);
                    if (node == null) continue;

                    var nodeType = node.GetType();
                    var uniqueIdProp = nodeType.GetProperty("UniqueId") ?? _parentNodeType?.GetProperty("UniqueId");
                    string nodeId = uniqueIdProp?.GetValue(node)?.ToString() ?? "-1";

                    // Check input ports
                    var inputPortsProp = nodeType.GetProperty("InputPorts");
                    if (inputPortsProp == null) continue;

                    var inputPorts = inputPortsProp.GetValue(node);
                    if (inputPorts == null) continue;

                    var portEnumerator = inputPorts.GetType().GetMethod("GetEnumerator")?.Invoke(inputPorts, null);
                    if (portEnumerator == null) continue;

                    var portMoveNext = portEnumerator.GetType().GetMethod("MoveNext");
                    var portCurrent = portEnumerator.GetType().GetProperty("Current");

                    int portIdx = 0;
                    while ((bool)portMoveNext.Invoke(portEnumerator, null))
                    {
                        var port = portCurrent.GetValue(portEnumerator);
                        if (port == null) { portIdx++; continue; }

                        var isConnectedProp = port.GetType().GetProperty("IsConnected");
                        bool isConnected = isConnectedProp != null && (bool)isConnectedProp.GetValue(port);

                        if (isConnected)
                        {
                            // Get external references
                            var extRefsProp = port.GetType().GetProperty("ExternalReferences");
                            if (extRefsProp != null)
                            {
                                var refs = extRefsProp.GetValue(port);
                                if (refs != null)
                                {
                                    var refsEnumerator = refs.GetType().GetMethod("GetEnumerator")?.Invoke(refs, null);
                                    if (refsEnumerator != null)
                                    {
                                        var refMoveNext = refsEnumerator.GetType().GetMethod("MoveNext");
                                        var refCurrent = refsEnumerator.GetType().GetProperty("Current");

                                        while ((bool)refMoveNext.Invoke(refsEnumerator, null))
                                        {
                                            var extRef = refCurrent.GetValue(refsEnumerator);
                                            if (extRef == null) continue;

                                            var nodeIdField = extRef.GetType().GetField("NodeId") ??
                                                              extRef.GetType().GetProperty("NodeId")?.GetGetMethod() != null
                                                                  ? null : null;
                                            var portIdField = extRef.GetType().GetField("PortId");

                                            string sourceNodeId = "";
                                            string sourcePortId = "";

                                            if (nodeIdField != null)
                                                sourceNodeId = nodeIdField.GetValue(extRef)?.ToString() ?? "";
                                            if (portIdField != null)
                                                sourcePortId = portIdField.GetValue(extRef)?.ToString() ?? "";

                                            connections.Add(new Dictionary<string, object>
                                            {
                                                { "outputNodeId", sourceNodeId },
                                                { "outputPortId", sourcePortId },
                                                { "inputNodeId", nodeId },
                                                { "inputPortIndex", portIdx },
                                            });
                                        }
                                    }
                                }
                            }
                        }
                        portIdx++;
                    }
                }

                return new Dictionary<string, object>
                {
                    { "connectionCount", connections.Count },
                    { "connections", connections.ToArray() },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to get connections: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Create a new Amplify Shader at the specified path.
        /// </summary>
        public static object CreateAmplifyShader(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            if (!args.ContainsKey("path"))
                return new Dictionary<string, object> { { "error", "path is required" } };

            string path = args["path"].ToString();
            string shaderName = args.ContainsKey("shaderName") ? args["shaderName"].ToString() : Path.GetFileNameWithoutExtension(path);

            try
            {
                // Try to use Amplify's create method via reflection
                var asm = GetAmplifyAssembly();
                if (asm == null)
                    return new Dictionary<string, object> { { "error", "Amplify assembly not found" } };

                // Try to find AmplifyShaderEditorWindow.CreateNewGraph or similar
                if (_amplifyShaderType != null)
                {
                    // First try: Create via menu item
                    bool created = false;

                    // Try the CreateNewShader static method
                    var createMethod = _amplifyShaderType.GetMethod("CreateNewShader",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);

                    if (createMethod == null)
                    {
                        // Try CreateConfirmationReceivedFromStandalone or similar
                        createMethod = _amplifyShaderType.GetMethod("CreateNewGraph",
                            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    }

                    // Fallback: Create a minimal shader file with ASE markers
                    if (!created)
                    {
                        string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
                        if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
                        {
                            string[] parts = dir.Split('/');
                            string curr = parts[0];
                            for (int i = 1; i < parts.Length; i++)
                            {
                                string next = curr + "/" + parts[i];
                                if (!AssetDatabase.IsValidFolder(next))
                                    AssetDatabase.CreateFolder(curr, parts[i]);
                                curr = next;
                            }
                        }

                        string shaderContent = GenerateMinimalAmplifyShader(shaderName);
                        string fullPath = Path.Combine(Application.dataPath, "..", path);
                        File.WriteAllText(fullPath, shaderContent);
                        AssetDatabase.ImportAsset(path);
                        created = true;
                    }

                    if (created)
                    {
                        return new Dictionary<string, object>
                        {
                            { "success", true },
                            { "assetPath", path },
                            { "shaderName", shaderName },
                            { "note", "Amplify shader created. Open it in Amplify Shader Editor to add nodes." },
                        };
                    }
                }

                return new Dictionary<string, object> { { "error", "Failed to create Amplify shader" } };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to create shader: {ex.Message}" } };
            }
        }

        // ─── Helpers ───

        private static EditorWindow GetOpenAmplifyWindow()
        {
            if (_amplifyShaderType == null) return null;
            try
            {
                var windows = Resources.FindObjectsOfTypeAll(_amplifyShaderType);
                return windows.Length > 0 ? windows[0] as EditorWindow : null;
            }
            catch { return null; }
        }

        private static object GetCurrentGraph(EditorWindow window)
        {
            try
            {
                var graphProp = window.GetType().GetProperty("CurrentGraph") ??
                                window.GetType().GetProperty("ParentGraph");
                if (graphProp != null) return graphProp.GetValue(window);

                var graphField = window.GetType().GetField("m_currentGraph", BindingFlags.NonPublic | BindingFlags.Instance) ??
                                 window.GetType().GetField("m_graph", BindingFlags.NonPublic | BindingFlags.Instance);
                if (graphField != null) return graphField.GetValue(window);
            }
            catch { }
            return null;
        }

        private static string GenerateMinimalAmplifyShader(string shaderName)
        {
            return $@"Shader ""{shaderName}""
{{
    Properties
    {{
        _Color (""Color"", Color) = (1,1,1,1)
    }}
    SubShader
    {{
        Tags {{ ""RenderType""=""Opaque"" }}
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        fixed4 _Color;

        struct Input
        {{
            float2 uv_MainTex;
        }};

        void surf (Input IN, inout SurfaceOutputStandard o)
        {{
            o.Albedo = _Color.rgb;
            o.Alpha = _Color.a;
        }}
        ENDCG
    }}
    FallBack ""Diffuse""
    //ASEBEGIN
    //ASEEND
    CustomEditor ""AmplifyShaderEditor.MaterialInspector""
}}";
        }
    }
}
