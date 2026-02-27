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
                    "amplify/get-node-types",
                    "amplify/get-nodes",
                    "amplify/get-connections",
                    "amplify/create-shader",
                    "amplify/add-node",
                    "amplify/remove-node",
                    "amplify/connect",
                    "amplify/disconnect",
                    "amplify/node-info",
                    "amplify/set-node-property",
                    "amplify/move-node",
                    "amplify/save",
                    "amplify/close",
                    "amplify/create-from-template",
                    "amplify/focus-node",
                    "amplify/master-node-info",
                    "amplify/disconnect-all",
                    "amplify/duplicate-node",
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

        /// <summary>
        /// Convert a string value to the target type, handling Unity types (Color, Vector2, Vector3, Vector4, etc.)
        /// </summary>
        private static object ConvertToType(object value, Type targetType)
        {
            if (value == null) return null;
            string str = value.ToString();

            if (targetType == typeof(Color))
            {
                var parts = str.Split(',');
                if (parts.Length >= 4)
                    return new Color(float.Parse(parts[0].Trim()), float.Parse(parts[1].Trim()), float.Parse(parts[2].Trim()), float.Parse(parts[3].Trim()));
                if (parts.Length == 3)
                    return new Color(float.Parse(parts[0].Trim()), float.Parse(parts[1].Trim()), float.Parse(parts[2].Trim()), 1f);
                if (ColorUtility.TryParseHtmlString(str, out Color c)) return c;
            }
            if (targetType == typeof(Vector2))
            {
                var parts = str.Split(',');
                return new Vector2(float.Parse(parts[0].Trim()), float.Parse(parts[1].Trim()));
            }
            if (targetType == typeof(Vector3))
            {
                var parts = str.Split(',');
                return new Vector3(float.Parse(parts[0].Trim()), float.Parse(parts[1].Trim()), float.Parse(parts[2].Trim()));
            }
            if (targetType == typeof(Vector4))
            {
                var parts = str.Split(',');
                return new Vector4(float.Parse(parts[0].Trim()), float.Parse(parts[1].Trim()), float.Parse(parts[2].Trim()), float.Parse(parts[3].Trim()));
            }
            if (targetType == typeof(bool))
                return str == "1" || str.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (targetType == typeof(float))
                return float.Parse(str);
            if (targetType == typeof(int))
                return int.Parse(str);
            if (targetType.IsEnum)
                return Enum.Parse(targetType, str, true);

            return Convert.ChangeType(value, targetType);
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

                                            string sourceNodeId = "";
                                            string sourcePortId = "";

                                            var nodeIdField = extRef.GetType().GetField("NodeId");
                                            if (nodeIdField != null)
                                                sourceNodeId = nodeIdField.GetValue(extRef)?.ToString() ?? "";
                                            else
                                            {
                                                var nodeIdProp = extRef.GetType().GetProperty("NodeId");
                                                if (nodeIdProp != null)
                                                    sourceNodeId = nodeIdProp.GetValue(extRef)?.ToString() ?? "";
                                            }

                                            var portIdField = extRef.GetType().GetField("PortId");
                                            if (portIdField != null)
                                                sourcePortId = portIdField.GetValue(extRef)?.ToString() ?? "";
                                            else
                                            {
                                                var portIdProp = extRef.GetType().GetProperty("PortId");
                                                if (portIdProp != null)
                                                    sourcePortId = portIdProp.GetValue(extRef)?.ToString() ?? "";
                                            }

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

        // ═══════════════════════════════════════════════════════════
        // ─── Graph Manipulation Methods (NEW) ───
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Add a node to the currently open Amplify graph.
        /// </summary>
        public static object AddAmplifyNode(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            if (!args.ContainsKey("nodeType"))
                return new Dictionary<string, object> { { "error", "nodeType is required (e.g. 'ColorNode', 'SamplerNode', 'SimpleMultiplyOpNode')" } };

            string nodeTypeName = args["nodeType"].ToString();
            float posX = args.ContainsKey("x") ? Convert.ToSingle(args["x"]) :
                         args.ContainsKey("positionX") ? Convert.ToSingle(args["positionX"]) : 0f;
            float posY = args.ContainsKey("y") ? Convert.ToSingle(args["y"]) :
                         args.ContainsKey("positionY") ? Convert.ToSingle(args["positionY"]) : 0f;

            try
            {
                var window = GetOpenAmplifyWindow();
                if (window == null)
                    return new Dictionary<string, object> { { "error", "No Amplify Shader Editor window is open. Open a shader first with amplify/open." } };

                var asm = GetAmplifyAssembly();
                if (asm == null)
                    return new Dictionary<string, object> { { "error", "Amplify assembly not found" } };

                // Find the node type - try exact full name first, then prefixed, then fuzzy
                Type type = asm.GetType(nodeTypeName);
                if (type == null)
                    type = asm.GetType("AmplifyShaderEditor." + nodeTypeName);
                if (type == null)
                {
                    // Try fuzzy search by short name
                    string shortName = nodeTypeName.Contains(".") ? nodeTypeName.Substring(nodeTypeName.LastIndexOf('.') + 1) : nodeTypeName;
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name.Equals(shortName, StringComparison.OrdinalIgnoreCase))
                        {
                            type = t;
                            break;
                        }
                    }
                }

                if (type == null)
                    return new Dictionary<string, object> { { "error", $"Node type '{nodeTypeName}' not found. Use amplify/get-node-types to see available types." } };

                // Use the window's CreateNode method
                var createMethod = window.GetType().GetMethod("CreateNode",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new Type[] { typeof(Type), typeof(Vector2), typeof(object), typeof(bool) },
                    null);

                object newNode = null;
                if (createMethod != null)
                {
                    newNode = createMethod.Invoke(window, new object[] { type, new Vector2(posX, posY), null, true });
                }
                else
                {
                    // Fallback: try via ParentGraph.CreateNode
                    var graph = GetCurrentGraph(window);
                    if (graph == null)
                        return new Dictionary<string, object> { { "error", "No graph loaded in the Amplify editor" } };

                    var graphCreate = graph.GetType().GetMethod("CreateNode",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new Type[] { typeof(Type), typeof(bool), typeof(Vector2), typeof(int), typeof(bool) },
                        null);

                    if (graphCreate != null)
                        newNode = graphCreate.Invoke(graph, new object[] { type, true, new Vector2(posX, posY), -1, true });
                }

                if (newNode == null)
                    return new Dictionary<string, object> { { "error", "Failed to create node" } };

                // Get the node's UniqueId
                var uniqueIdProp = newNode.GetType().GetProperty("UniqueId");
                string nodeId = uniqueIdProp?.GetValue(newNode)?.ToString() ?? "-1";

                // Force repaint
                var repaintMethod = window.GetType().GetMethod("ForceRepaint", BindingFlags.Public | BindingFlags.Instance);
                repaintMethod?.Invoke(window, null);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "nodeId", nodeId },
                    { "nodeType", type.Name },
                    { "position", new Dictionary<string, object> { { "x", posX }, { "y", posY } } },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to add node: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Remove a node from the currently open Amplify graph.
        /// </summary>
        public static object RemoveAmplifyNode(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            if (!args.ContainsKey("nodeId"))
                return new Dictionary<string, object> { { "error", "nodeId is required" } };

            int nodeId = Convert.ToInt32(args["nodeId"]);

            try
            {
                var window = GetOpenAmplifyWindow();
                if (window == null)
                    return new Dictionary<string, object> { { "error", "No Amplify editor window is open" } };

                var graph = GetCurrentGraph(window);
                if (graph == null)
                    return new Dictionary<string, object> { { "error", "No graph loaded" } };

                // Get the node by ID
                var getNodeMethod = graph.GetType().GetMethod("GetNode",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new Type[] { typeof(int) }, null);

                if (getNodeMethod == null)
                    return new Dictionary<string, object> { { "error", "GetNode method not found" } };

                var node = getNodeMethod.Invoke(graph, new object[] { nodeId });
                if (node == null)
                    return new Dictionary<string, object> { { "error", $"Node with id {nodeId} not found" } };

                // Check if it's a master node
                var isMasterMethod = graph.GetType().GetMethod("IsMasterNode",
                    BindingFlags.Public | BindingFlags.Instance);
                if (isMasterMethod != null)
                {
                    bool isMaster = (bool)isMasterMethod.Invoke(graph, new object[] { node });
                    if (isMaster)
                        return new Dictionary<string, object> { { "error", "Cannot remove the master/output node" } };
                }

                // Destroy node via window
                var destroyMethod = window.GetType().GetMethod("DestroyNode",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new Type[] { _parentNodeType, typeof(bool) }, null);

                if (destroyMethod != null)
                {
                    destroyMethod.Invoke(window, new object[] { node, true });
                }
                else
                {
                    // Fallback: graph.DestroyNode(int)
                    var graphDestroyMethod = graph.GetType().GetMethod("DestroyNode",
                        BindingFlags.Public | BindingFlags.Instance,
                        null, new Type[] { typeof(int) }, null);
                    if (graphDestroyMethod != null)
                        graphDestroyMethod.Invoke(graph, new object[] { nodeId });
                    else
                        return new Dictionary<string, object> { { "error", "DestroyNode method not found" } };
                }

                var repaintMethod = window.GetType().GetMethod("ForceRepaint", BindingFlags.Public | BindingFlags.Instance);
                repaintMethod?.Invoke(window, null);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "removedNodeId", nodeId },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to remove node: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Connect two nodes in the currently open Amplify graph.
        /// </summary>
        public static object ConnectAmplifyNodes(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            if (!args.ContainsKey("outputNodeId") || !args.ContainsKey("inputNodeId"))
                return new Dictionary<string, object> { { "error", "outputNodeId and inputNodeId are required" } };

            int outNodeId = Convert.ToInt32(args["outputNodeId"]);
            int outPortId = args.ContainsKey("outputPortId") ? Convert.ToInt32(args["outputPortId"]) : 0;
            int inNodeId = Convert.ToInt32(args["inputNodeId"]);
            int inPortId = args.ContainsKey("inputPortId") ? Convert.ToInt32(args["inputPortId"]) : 0;

            try
            {
                var window = GetOpenAmplifyWindow();
                if (window == null)
                    return new Dictionary<string, object> { { "error", "No Amplify editor window is open" } };

                // Use window's ConnectInputToOutput method
                var connectMethod = window.GetType().GetMethod("ConnectInputToOutput",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);

                if (connectMethod != null)
                {
                    connectMethod.Invoke(window, new object[] { inNodeId, inPortId, outNodeId, outPortId, true });
                }
                else
                {
                    // Fallback: graph.CreateConnection
                    var graph = GetCurrentGraph(window);
                    if (graph == null)
                        return new Dictionary<string, object> { { "error", "No graph loaded" } };

                    var createConn = graph.GetType().GetMethod("CreateConnection",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (createConn != null)
                        createConn.Invoke(graph, new object[] { inNodeId, inPortId, outNodeId, outPortId, true });
                    else
                        return new Dictionary<string, object> { { "error", "Connection method not found" } };
                }

                var repaintMethod = window.GetType().GetMethod("ForceRepaint", BindingFlags.Public | BindingFlags.Instance);
                repaintMethod?.Invoke(window, null);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "outputNodeId", outNodeId },
                    { "outputPortId", outPortId },
                    { "inputNodeId", inNodeId },
                    { "inputPortId", inPortId },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to connect nodes: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Disconnect a connection in the currently open Amplify graph.
        /// </summary>
        public static object DisconnectAmplifyNodes(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            if (!args.ContainsKey("nodeId") || !args.ContainsKey("portId"))
                return new Dictionary<string, object> { { "error", "nodeId and portId are required" } };

            int nodeId = Convert.ToInt32(args["nodeId"]);
            int portId = Convert.ToInt32(args["portId"]);
            bool isInput = args.ContainsKey("isInput") ? Convert.ToBoolean(args["isInput"]) : true;

            try
            {
                var window = GetOpenAmplifyWindow();
                if (window == null)
                    return new Dictionary<string, object> { { "error", "No Amplify editor window is open" } };

                var deleteConnMethod = window.GetType().GetMethod("DeleteConnection",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new Type[] { typeof(bool), typeof(int), typeof(int), typeof(bool), typeof(bool) },
                    null);

                if (deleteConnMethod != null)
                {
                    deleteConnMethod.Invoke(window, new object[] { isInput, nodeId, portId, true, true });
                }
                else
                {
                    var graph = GetCurrentGraph(window);
                    if (graph == null)
                        return new Dictionary<string, object> { { "error", "No graph loaded" } };

                    var graphDelete = graph.GetType().GetMethod("DeleteConnection",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new Type[] { typeof(bool), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool) },
                        null);
                    if (graphDelete != null)
                        graphDelete.Invoke(graph, new object[] { isInput, nodeId, portId, true, true, true });
                    else
                        return new Dictionary<string, object> { { "error", "DeleteConnection method not found" } };
                }

                var repaintMethod = window.GetType().GetMethod("ForceRepaint", BindingFlags.Public | BindingFlags.Instance);
                repaintMethod?.Invoke(window, null);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "nodeId", nodeId },
                    { "portId", portId },
                    { "isInput", isInput },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to disconnect: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Get detailed info about a specific node in the currently open Amplify graph.
        /// </summary>
        public static object GetAmplifyNodeInfo(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            if (!args.ContainsKey("nodeId"))
                return new Dictionary<string, object> { { "error", "nodeId is required" } };

            int nodeId = Convert.ToInt32(args["nodeId"]);

            try
            {
                var window = GetOpenAmplifyWindow();
                if (window == null)
                    return new Dictionary<string, object> { { "error", "No Amplify editor window is open" } };

                var graph = GetCurrentGraph(window);
                if (graph == null)
                    return new Dictionary<string, object> { { "error", "No graph loaded" } };

                var getNodeMethod = graph.GetType().GetMethod("GetNode",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new Type[] { typeof(int) }, null);

                if (getNodeMethod == null)
                    return new Dictionary<string, object> { { "error", "GetNode method not found" } };

                var node = getNodeMethod.Invoke(graph, new object[] { nodeId });
                if (node == null)
                    return new Dictionary<string, object> { { "error", $"Node with id {nodeId} not found" } };

                return ExtractNodeDetails(node);
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to get node info: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Set a property on a node in the currently open Amplify graph.
        /// </summary>
        public static object SetAmplifyNodeProperty(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            if (!args.ContainsKey("nodeId") || !args.ContainsKey("propertyName"))
                return new Dictionary<string, object> { { "error", "nodeId and propertyName are required" } };

            int nodeId = Convert.ToInt32(args["nodeId"]);
            string propName = args["propertyName"].ToString();
            object value = args.ContainsKey("value") ? args["value"] : null;

            try
            {
                var window = GetOpenAmplifyWindow();
                if (window == null)
                    return new Dictionary<string, object> { { "error", "No Amplify editor window is open" } };

                var graph = GetCurrentGraph(window);
                if (graph == null)
                    return new Dictionary<string, object> { { "error", "No graph loaded" } };

                var getNodeMethod = graph.GetType().GetMethod("GetNode",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new Type[] { typeof(int) }, null);

                var node = getNodeMethod?.Invoke(graph, new object[] { nodeId });
                if (node == null)
                    return new Dictionary<string, object> { { "error", $"Node with id {nodeId} not found" } };

                // Try property first, then field
                var prop = node.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop != null && prop.CanWrite)
                {
                    object converted = ConvertToType(value, prop.PropertyType);
                    prop.SetValue(node, converted);
                }
                else
                {
                    var field = node.GetType().GetField(propName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (field != null)
                    {
                        object converted = ConvertToType(value, field.FieldType);
                        field.SetValue(node, converted);
                    }
                    else
                    {
                        // List available properties for help
                        var props = new List<string>();
                        foreach (var p in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (p.CanWrite) props.Add(p.Name);
                        }
                        return new Dictionary<string, object>
                        {
                            { "error", $"Property '{propName}' not found or not writable on {node.GetType().Name}" },
                            { "availableProperties", props.Take(30).ToArray() },
                        };
                    }
                }

                // Mark dirty and repaint
                var setDirty = window.GetType().GetMethod("SetSaveIsDirty", BindingFlags.Public | BindingFlags.Instance);
                setDirty?.Invoke(window, null);
                var repaint = window.GetType().GetMethod("ForceRepaint", BindingFlags.Public | BindingFlags.Instance);
                repaint?.Invoke(window, null);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "nodeId", nodeId },
                    { "propertyName", propName },
                    { "value", value?.ToString() ?? "null" },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to set property: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Move a node to a new position in the Amplify graph.
        /// </summary>
        public static object MoveAmplifyNode(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            if (!args.ContainsKey("nodeId"))
                return new Dictionary<string, object> { { "error", "nodeId is required" } };

            int nodeId = Convert.ToInt32(args["nodeId"]);
            float posX = args.ContainsKey("x") ? Convert.ToSingle(args["x"]) :
                         args.ContainsKey("positionX") ? Convert.ToSingle(args["positionX"]) : 0f;
            float posY = args.ContainsKey("y") ? Convert.ToSingle(args["y"]) :
                         args.ContainsKey("positionY") ? Convert.ToSingle(args["positionY"]) : 0f;

            try
            {
                var window = GetOpenAmplifyWindow();
                if (window == null)
                    return new Dictionary<string, object> { { "error", "No Amplify editor window is open" } };

                var graph = GetCurrentGraph(window);
                if (graph == null)
                    return new Dictionary<string, object> { { "error", "No graph loaded" } };

                var getNodeMethod = graph.GetType().GetMethod("GetNode",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new Type[] { typeof(int) }, null);

                var node = getNodeMethod?.Invoke(graph, new object[] { nodeId });
                if (node == null)
                    return new Dictionary<string, object> { { "error", $"Node with id {nodeId} not found" } };

                // Set position via the Position property (returns a Rect)
                var posProp = node.GetType().GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
                if (posProp != null && posProp.CanWrite)
                {
                    var currentPos = (Rect)posProp.GetValue(node);
                    currentPos.x = posX;
                    currentPos.y = posY;
                    posProp.SetValue(node, currentPos);
                }
                else
                {
                    // Try Vec2Position
                    var vec2Prop = node.GetType().GetProperty("Vec2Position", BindingFlags.Public | BindingFlags.Instance);
                    if (vec2Prop != null && vec2Prop.CanWrite)
                        vec2Prop.SetValue(node, new Vector2(posX, posY));
                    else
                        return new Dictionary<string, object> { { "error", "Cannot set node position" } };
                }

                var repaint = window.GetType().GetMethod("ForceRepaint", BindingFlags.Public | BindingFlags.Instance);
                repaint?.Invoke(window, null);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "nodeId", nodeId },
                    { "position", new Dictionary<string, object> { { "x", posX }, { "y", posY } } },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to move node: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Save the currently open Amplify shader graph to disk.
        /// </summary>
        public static object SaveAmplifyGraph(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            try
            {
                var window = GetOpenAmplifyWindow();
                if (window == null)
                    return new Dictionary<string, object> { { "error", "No Amplify editor window is open" } };

                var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;

                // If the shader has no save path yet, set one from the optional path arg or auto-generate
                var lastPathField = window.GetType().GetField("m_lastpath", flags);
                string currentPath = lastPathField?.GetValue(window) as string;

                if (string.IsNullOrEmpty(currentPath) || !File.Exists(Path.Combine(Application.dataPath, "..", currentPath)))
                {
                    string savePath = args.ContainsKey("path") ? args["path"].ToString() : null;

                    if (string.IsNullOrEmpty(savePath))
                    {
                        // Auto-generate a path based on shader name from the graph
                        var graph = GetCurrentGraph(window);
                        if (graph != null)
                        {
                            var masterNode = graph.GetType().GetProperty("CurrentMasterNode", flags)?.GetValue(graph);
                            if (masterNode != null)
                            {
                                var nameProp = masterNode.GetType().GetProperty("ShaderName", flags);
                                string shaderName = nameProp?.GetValue(masterNode)?.ToString();
                                if (!string.IsNullOrEmpty(shaderName))
                                {
                                    string safeName = shaderName.Replace("/", "_").Replace("\\", "_");
                                    savePath = $"Assets/Shaders/{safeName}.shader";
                                }
                            }
                        }
                        if (string.IsNullOrEmpty(savePath))
                            savePath = $"Assets/Shaders/NewAmplifyShader_{DateTime.Now:yyyyMMdd_HHmmss}.shader";
                    }

                    // Ensure directory exists
                    string dir = Path.GetDirectoryName(savePath)?.Replace('\\', '/');
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

                    // Write minimal file so SaveToDisk can overwrite it
                    string fullPath = Path.Combine(Application.dataPath, "..", savePath);
                    if (!File.Exists(fullPath))
                    {
                        File.WriteAllText(fullPath, "// Amplify Shader placeholder");
                        AssetDatabase.ImportAsset(savePath);
                    }

                    // Set the last path on the window so SaveToDisk knows where to write
                    lastPathField?.SetValue(window, savePath);
                }

                var saveMethod = window.GetType().GetMethod("SaveToDisk", flags);
                if (saveMethod != null)
                {
                    saveMethod.Invoke(window, new object[] { false });
                    string savedPath = lastPathField?.GetValue(window) as string ?? currentPath;
                    return new Dictionary<string, object> { { "success", true }, { "path", savedPath }, { "note", "Graph saved to disk" } };
                }

                // Fallback: RequestSave
                var requestSave = window.GetType().GetMethod("RequestSave", flags);
                if (requestSave != null)
                {
                    requestSave.Invoke(window, null);
                    return new Dictionary<string, object> { { "success", true }, { "note", "Save requested" } };
                }

                return new Dictionary<string, object> { { "error", "Save method not found" } };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to save: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Close the Amplify Shader Editor window.
        /// </summary>
        public static object CloseAmplifyEditor(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            try
            {
                var window = GetOpenAmplifyWindow();
                if (window == null)
                    return new Dictionary<string, object> { { "success", true }, { "note", "No Amplify editor window was open" } };

                // Default to save=true to avoid ASE's unsaved changes dialog
                bool save = args.ContainsKey("save") ? Convert.ToBoolean(args["save"]) : true;
                if (save)
                {
                    var saveMethod = window.GetType().GetMethod("SaveToDisk",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    saveMethod?.Invoke(window, new object[] { false });
                }
                else
                {
                    // Mark graph as not dirty to prevent save dialog
                    var graph = GetCurrentGraph(window);
                    if (graph != null)
                    {
                        var dirtyField = graph.GetType().GetField("m_isDirty", BindingFlags.NonPublic | BindingFlags.Instance);
                        dirtyField?.SetValue(graph, false);
                        var saveIsDirtyField = graph.GetType().GetField("m_saveIsDirty", BindingFlags.NonPublic | BindingFlags.Instance);
                        saveIsDirtyField?.SetValue(graph, false);
                    }
                }

                window.Close();
                return new Dictionary<string, object> { { "success", true }, { "saved", save } };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to close: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Create a new Amplify shader from a template (Standard Surface, URP Lit, etc.).
        /// </summary>
        public static object CreateAmplifyFromTemplate(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            if (!args.ContainsKey("path"))
                return new Dictionary<string, object> { { "error", "path is required" } };

            string path = args["path"].ToString();
            string shaderName = args.ContainsKey("shaderName") ? args["shaderName"].ToString() : Path.GetFileNameWithoutExtension(path);
            string template = args.ContainsKey("template") ? args["template"].ToString().ToLower() : "surface";

            try
            {
                // Ensure directory exists
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

                string shaderContent;
                string templateUsed;

                switch (template)
                {
                    case "unlit":
                        shaderContent = GenerateAmplifyUnlitShader(shaderName);
                        templateUsed = "Unlit";
                        break;
                    case "urp":
                    case "urp_lit":
                        shaderContent = GenerateAmplifyURPLitShader(shaderName);
                        templateUsed = "URP Lit";
                        break;
                    case "transparent":
                        shaderContent = GenerateAmplifyTransparentShader(shaderName);
                        templateUsed = "Transparent";
                        break;
                    case "post_process":
                    case "postprocess":
                        shaderContent = GenerateAmplifyPostProcessShader(shaderName);
                        templateUsed = "Post Process";
                        break;
                    default:
                        shaderContent = GenerateMinimalAmplifyShader(shaderName);
                        templateUsed = "Standard Surface";
                        break;
                }

                string fullPath = Path.Combine(Application.dataPath, "..", path);
                File.WriteAllText(fullPath, shaderContent);
                AssetDatabase.ImportAsset(path);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "assetPath", path },
                    { "shaderName", shaderName },
                    { "template", templateUsed },
                    { "note", "Shader created. Open it with amplify/open to start editing." },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to create shader: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Focus on a specific node in the Amplify editor (centers the view on it).
        /// </summary>
        public static object FocusAmplifyNode(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            if (!args.ContainsKey("nodeId"))
                return new Dictionary<string, object> { { "error", "nodeId is required" } };

            int nodeId = Convert.ToInt32(args["nodeId"]);
            float zoom = args.ContainsKey("zoom") ? Convert.ToSingle(args["zoom"]) : 1.0f;
            bool select = args.ContainsKey("select") ? Convert.ToBoolean(args["select"]) : true;

            try
            {
                var window = GetOpenAmplifyWindow();
                if (window == null)
                    return new Dictionary<string, object> { { "error", "No Amplify editor window is open" } };

                var focusMethod = window.GetType().GetMethod("FocusOnNode",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new Type[] { typeof(int), typeof(float), typeof(bool), typeof(bool) },
                    null);

                if (focusMethod != null)
                {
                    focusMethod.Invoke(window, new object[] { nodeId, zoom, select, false });
                    return new Dictionary<string, object>
                    {
                        { "success", true },
                        { "nodeId", nodeId },
                        { "zoom", zoom },
                    };
                }

                return new Dictionary<string, object> { { "error", "FocusOnNode method not found" } };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to focus node: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Get info about the master/output node of the currently open shader.
        /// </summary>
        public static object GetAmplifyMasterNodeInfo(Dictionary<string, object> args)
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

                var masterProp = graph.GetType().GetProperty("CurrentMasterNode");
                if (masterProp == null)
                    masterProp = graph.GetType().GetProperty("CurrentOutputNode");

                var masterNode = masterProp?.GetValue(graph);
                if (masterNode == null)
                    return new Dictionary<string, object> { { "error", "Master node not found" } };

                return ExtractNodeDetails(masterNode);
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to get master node: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Delete all connections from a specific node.
        /// </summary>
        public static object DisconnectAllAmplifyNode(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            if (!args.ContainsKey("nodeId"))
                return new Dictionary<string, object> { { "error", "nodeId is required" } };

            int nodeId = Convert.ToInt32(args["nodeId"]);

            try
            {
                var window = GetOpenAmplifyWindow();
                if (window == null)
                    return new Dictionary<string, object> { { "error", "No Amplify editor window is open" } };

                var graph = GetCurrentGraph(window);
                if (graph == null)
                    return new Dictionary<string, object> { { "error", "No graph loaded" } };

                var deleteAll = graph.GetType().GetMethod("DeleteAllConnectionFromNode",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(int), typeof(bool), typeof(bool), typeof(bool) },
                    null);

                if (deleteAll != null)
                {
                    deleteAll.Invoke(graph, new object[] { nodeId, true, true, true });
                    var repaint = window.GetType().GetMethod("ForceRepaint", BindingFlags.Public | BindingFlags.Instance);
                    repaint?.Invoke(window, null);

                    return new Dictionary<string, object>
                    {
                        { "success", true },
                        { "nodeId", nodeId },
                        { "note", "All connections removed from node" },
                    };
                }

                return new Dictionary<string, object> { { "error", "DeleteAllConnectionFromNode method not found" } };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to disconnect all: {ex.Message}" } };
            }
        }

        /// <summary>
        /// Duplicate a node in the currently open Amplify graph.
        /// </summary>
        public static object DuplicateAmplifyNode(Dictionary<string, object> args)
        {
            if (!IsAmplifyInstalled())
                return NotInstalledError();

            if (!args.ContainsKey("nodeId"))
                return new Dictionary<string, object> { { "error", "nodeId is required" } };

            int nodeId = Convert.ToInt32(args["nodeId"]);
            float offsetX = args.ContainsKey("offsetX") ? Convert.ToSingle(args["offsetX"]) : 50f;
            float offsetY = args.ContainsKey("offsetY") ? Convert.ToSingle(args["offsetY"]) : 50f;

            try
            {
                var window = GetOpenAmplifyWindow();
                if (window == null)
                    return new Dictionary<string, object> { { "error", "No Amplify editor window is open" } };

                var graph = GetCurrentGraph(window);
                if (graph == null)
                    return new Dictionary<string, object> { { "error", "No graph loaded" } };

                var getNodeMethod = graph.GetType().GetMethod("GetNode",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new Type[] { typeof(int) }, null);

                var sourceNode = getNodeMethod?.Invoke(graph, new object[] { nodeId });
                if (sourceNode == null)
                    return new Dictionary<string, object> { { "error", $"Node with id {nodeId} not found" } };

                // Get the source position and type
                Type nodeT = sourceNode.GetType();
                var posProp = sourceNode.GetType().GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
                Rect sourcePos = posProp != null ? (Rect)posProp.GetValue(sourceNode) : new Rect(0, 0, 100, 100);

                Vector2 newPos = new Vector2(sourcePos.x + offsetX, sourcePos.y + offsetY);

                // Create new node of same type at offset position
                var createMethod = window.GetType().GetMethod("CreateNode",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new Type[] { typeof(Type), typeof(Vector2), typeof(object), typeof(bool) },
                    null);

                object newNode = null;
                if (createMethod != null)
                    newNode = createMethod.Invoke(window, new object[] { nodeT, newPos, null, true });
                else
                {
                    var graphCreate = graph.GetType().GetMethod("CreateNode",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new Type[] { typeof(Type), typeof(bool), typeof(Vector2), typeof(int), typeof(bool) },
                        null);
                    if (graphCreate != null)
                        newNode = graphCreate.Invoke(graph, new object[] { nodeT, true, newPos, -1, true });
                }

                if (newNode == null)
                    return new Dictionary<string, object> { { "error", "Failed to create duplicate node" } };

                var uniqueIdProp = newNode.GetType().GetProperty("UniqueId");
                string newId = uniqueIdProp?.GetValue(newNode)?.ToString() ?? "-1";

                var repaint = window.GetType().GetMethod("ForceRepaint", BindingFlags.Public | BindingFlags.Instance);
                repaint?.Invoke(window, null);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "sourceNodeId", nodeId },
                    { "newNodeId", newId },
                    { "nodeType", nodeT.Name },
                    { "position", new Dictionary<string, object> { { "x", newPos.x }, { "y", newPos.y } } },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", $"Failed to duplicate node: {ex.Message}" } };
            }
        }

        // ═══════════════════════════════════════════════════════════
        // ─── Node Detail Extraction Helper ───
        // ═══════════════════════════════════════════════════════════

        private static Dictionary<string, object> ExtractNodeDetails(object node)
        {
            var info = new Dictionary<string, object>();
            var nodeType = node.GetType();

            info["typeName"] = nodeType.Name;

            // UniqueId
            var uidProp = nodeType.GetProperty("UniqueId");
            if (uidProp != null) info["uniqueId"] = uidProp.GetValue(node)?.ToString() ?? "-1";

            // Position
            var posProp = nodeType.GetProperty("Position");
            if (posProp != null)
            {
                var pos = posProp.GetValue(node);
                if (pos is Rect r)
                    info["position"] = new Dictionary<string, object> { { "x", r.x }, { "y", r.y }, { "width", r.width }, { "height", r.height } };
            }

            // Input ports
            var inputsProp = nodeType.GetProperty("InputPorts");
            if (inputsProp != null)
            {
                var inputs = inputsProp.GetValue(node);
                if (inputs != null)
                {
                    var portList = new List<Dictionary<string, object>>();
                    var enumerator = inputs.GetType().GetMethod("GetEnumerator")?.Invoke(inputs, null);
                    if (enumerator != null)
                    {
                        var moveNext = enumerator.GetType().GetMethod("MoveNext");
                        var current = enumerator.GetType().GetProperty("Current");
                        int idx = 0;
                        while ((bool)moveNext.Invoke(enumerator, null))
                        {
                            var port = current.GetValue(enumerator);
                            if (port == null) { idx++; continue; }

                            var portInfo = new Dictionary<string, object> { { "index", idx } };

                            var nameProp = port.GetType().GetProperty("Name");
                            if (nameProp != null) portInfo["name"] = nameProp.GetValue(port)?.ToString() ?? "";

                            var dataTypeProp = port.GetType().GetProperty("DataType");
                            if (dataTypeProp != null) portInfo["dataType"] = dataTypeProp.GetValue(port)?.ToString() ?? "";

                            var isConnProp = port.GetType().GetProperty("IsConnected");
                            if (isConnProp != null) portInfo["isConnected"] = isConnProp.GetValue(port);

                            var portIdProp = port.GetType().GetProperty("PortId");
                            if (portIdProp != null) portInfo["portId"] = portIdProp.GetValue(port)?.ToString() ?? "";

                            portList.Add(portInfo);
                            idx++;
                        }
                    }
                    info["inputPorts"] = portList.ToArray();
                }
            }

            // Output ports
            var outputsProp = nodeType.GetProperty("OutputPorts");
            if (outputsProp != null)
            {
                var outputs = outputsProp.GetValue(node);
                if (outputs != null)
                {
                    var portList = new List<Dictionary<string, object>>();
                    var enumerator = outputs.GetType().GetMethod("GetEnumerator")?.Invoke(outputs, null);
                    if (enumerator != null)
                    {
                        var moveNext = enumerator.GetType().GetMethod("MoveNext");
                        var current = enumerator.GetType().GetProperty("Current");
                        int idx = 0;
                        while ((bool)moveNext.Invoke(enumerator, null))
                        {
                            var port = current.GetValue(enumerator);
                            if (port == null) { idx++; continue; }

                            var portInfo = new Dictionary<string, object> { { "index", idx } };

                            var nameProp = port.GetType().GetProperty("Name");
                            if (nameProp != null) portInfo["name"] = nameProp.GetValue(port)?.ToString() ?? "";

                            var dataTypeProp = port.GetType().GetProperty("DataType");
                            if (dataTypeProp != null) portInfo["dataType"] = dataTypeProp.GetValue(port)?.ToString() ?? "";

                            var isConnProp = port.GetType().GetProperty("IsConnected");
                            if (isConnProp != null) portInfo["isConnected"] = isConnProp.GetValue(port);

                            var portIdProp = port.GetType().GetProperty("PortId");
                            if (portIdProp != null) portInfo["portId"] = portIdProp.GetValue(port)?.ToString() ?? "";

                            portList.Add(portInfo);
                            idx++;
                        }
                    }
                    info["outputPorts"] = portList.ToArray();
                }
            }

            return info;
        }

        // ═══════════════════════════════════════════════════════════
        // ─── Template Generators ───
        // ═══════════════════════════════════════════════════════════

        private static string GenerateAmplifyUnlitShader(string shaderName)
        {
            return $@"Shader ""{shaderName}""
{{
    Properties
    {{
        _MainTex (""Texture"", 2D) = ""white"" {{}}
        _Color (""Color"", Color) = (1,1,1,1)
    }}
    SubShader
    {{
        Tags {{ ""RenderType""=""Opaque"" }}
        LOD 100
        Pass
        {{
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""UnityCG.cginc""

            struct appdata {{ float4 vertex : POSITION; float2 uv : TEXCOORD0; }};
            struct v2f {{ float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; }};

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            v2f vert (appdata v)
            {{
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }}

            fixed4 frag (v2f i) : SV_Target
            {{
                return tex2D(_MainTex, i.uv) * _Color;
            }}
            ENDCG
        }}
    }}
    FallBack ""Unlit/Texture""
    //ASEBEGIN
    //ASEEND
    CustomEditor ""AmplifyShaderEditor.MaterialInspector""
}}";
        }

        private static string GenerateAmplifyURPLitShader(string shaderName)
        {
            return $@"Shader ""{shaderName}""
{{
    Properties
    {{
        _BaseColor (""Base Color"", Color) = (1,1,1,1)
        _BaseMap (""Base Map"", 2D) = ""white"" {{}}
        _Smoothness (""Smoothness"", Range(0,1)) = 0.5
        _Metallic (""Metallic"", Range(0,1)) = 0.0
        [Normal] _BumpMap (""Normal Map"", 2D) = ""bump"" {{}}
    }}
    SubShader
    {{
        Tags {{ ""RenderType""=""Opaque"" ""RenderPipeline""=""UniversalPipeline"" }}
        LOD 300

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows
        #pragma target 3.0

        sampler2D _BaseMap;
        sampler2D _BumpMap;
        half4 _BaseColor;
        half _Smoothness;
        half _Metallic;

        struct Input
        {{
            float2 uv_BaseMap;
            float2 uv_BumpMap;
        }};

        void surf (Input IN, inout SurfaceOutputStandard o)
        {{
            fixed4 c = tex2D(_BaseMap, IN.uv_BaseMap) * _BaseColor;
            o.Albedo = c.rgb;
            o.Normal = UnpackNormal(tex2D(_BumpMap, IN.uv_BumpMap));
            o.Metallic = _Metallic;
            o.Smoothness = _Smoothness;
            o.Alpha = c.a;
        }}
        ENDCG
    }}
    FallBack ""Universal Render Pipeline/Lit""
    //ASEBEGIN
    //ASEEND
    CustomEditor ""AmplifyShaderEditor.MaterialInspector""
}}";
        }

        private static string GenerateAmplifyTransparentShader(string shaderName)
        {
            return $@"Shader ""{shaderName}""
{{
    Properties
    {{
        _Color (""Color"", Color) = (1,1,1,0.5)
        _MainTex (""Texture"", 2D) = ""white"" {{}}
    }}
    SubShader
    {{
        Tags {{ ""Queue""=""Transparent"" ""RenderType""=""Transparent"" }}
        LOD 200
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off

        CGPROGRAM
        #pragma surface surf Standard alpha:fade fullforwardshadows
        #pragma target 3.0

        sampler2D _MainTex;
        fixed4 _Color;

        struct Input
        {{
            float2 uv_MainTex;
        }};

        void surf (Input IN, inout SurfaceOutputStandard o)
        {{
            fixed4 c = tex2D(_MainTex, IN.uv_MainTex) * _Color;
            o.Albedo = c.rgb;
            o.Alpha = c.a;
        }}
        ENDCG
    }}
    FallBack ""Transparent/Diffuse""
    //ASEBEGIN
    //ASEEND
    CustomEditor ""AmplifyShaderEditor.MaterialInspector""
}}";
        }

        private static string GenerateAmplifyPostProcessShader(string shaderName)
        {
            return $@"Shader ""{shaderName}""
{{
    Properties
    {{
        _MainTex (""Texture"", 2D) = ""white"" {{}}
        _Intensity (""Effect Intensity"", Range(0,1)) = 1.0
    }}
    SubShader
    {{
        Tags {{ ""RenderType""=""Opaque"" }}
        LOD 100
        ZTest Always ZWrite Off Cull Off

        Pass
        {{
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""UnityCG.cginc""

            struct appdata {{ float4 vertex : POSITION; float2 uv : TEXCOORD0; }};
            struct v2f {{ float2 uv : TEXCOORD0; float4 vertex : SV_POSITION; }};

            sampler2D _MainTex;
            float _Intensity;

            v2f vert (appdata v)
            {{
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }}

            fixed4 frag (v2f i) : SV_Target
            {{
                return tex2D(_MainTex, i.uv) * _Intensity;
            }}
            ENDCG
        }}
    }}
    FallBack Off
    //ASEBEGIN
    //ASEEND
    CustomEditor ""AmplifyShaderEditor.MaterialInspector""
}}";
        }

        // ─── Original Helpers ───

        private static EditorWindow GetOpenAmplifyWindow()
        {
            if (_amplifyShaderType == null) return null;
            // Ensure reflection types are initialized
            if (_parentGraphType == null) GetAmplifyAssembly();
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
                var flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

                // Try properties first (most reliable)
                string[] propNames = { "CurrentGraph", "MainGraphInstance", "CustomGraph", "ParentGraph" };
                foreach (var name in propNames)
                {
                    var prop = window.GetType().GetProperty(name, flags);
                    if (prop != null)
                    {
                        var val = prop.GetValue(window);
                        if (val != null) return val;
                    }
                }

                // Fall back to fields
                string[] fieldNames = { "m_mainGraphInstance", "m_customGraph", "m_currentGraph", "m_graph" };
                foreach (var name in fieldNames)
                {
                    var field = window.GetType().GetField(name, flags);
                    if (field != null)
                    {
                        var val = field.GetValue(window);
                        if (val != null) return val;
                    }
                }
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
