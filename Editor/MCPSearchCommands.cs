using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for searching and finding GameObjects and assets in the project.
    /// </summary>
    public static class MCPSearchCommands
    {
        // ─── Find By Component ───

        public static object FindByComponent(Dictionary<string, object> args)
        {
            string typeName = args.ContainsKey("componentType") ? args["componentType"].ToString() : "";
            if (string.IsNullOrEmpty(typeName))
                return new { error = "componentType is required" };

            bool includeInactive = args.ContainsKey("includeInactive") && Convert.ToBoolean(args["includeInactive"]);

            // Try to find the type
            Type componentType = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                componentType = assembly.GetType(typeName, false, true);
                if (componentType != null) break;

                // Try with UnityEngine prefix
                componentType = assembly.GetType("UnityEngine." + typeName, false, true);
                if (componentType != null) break;
            }

            if (componentType == null)
                return new { error = $"Component type '{typeName}' not found" };

            var results = new List<Dictionary<string, object>>();
            var objects = UnityEngine.Object.FindObjectsByType(componentType, includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            foreach (var obj in objects)
            {
                var comp = obj as Component;
                if (comp == null) continue;
                results.Add(new Dictionary<string, object>
                {
                    { "name", comp.gameObject.name },
                    { "path", GetGameObjectPath(comp.gameObject) },
                    { "instanceId", comp.gameObject.GetInstanceID() },
                    { "active", comp.gameObject.activeInHierarchy },
                    { "scene", comp.gameObject.scene.name },
                });
            }

            return new Dictionary<string, object>
            {
                { "componentType", typeName },
                { "count", results.Count },
                { "results", results },
            };
        }

        // ─── Find By Tag ───

        public static object FindByTag(Dictionary<string, object> args)
        {
            string tag = args.ContainsKey("tag") ? args["tag"].ToString() : "";
            if (string.IsNullOrEmpty(tag))
                return new { error = "tag is required" };

            GameObject[] objects;
            try { objects = GameObject.FindGameObjectsWithTag(tag); }
            catch (Exception e) { return new { error = e.Message }; }

            var results = new List<Dictionary<string, object>>();
            foreach (var go in objects)
            {
                results.Add(new Dictionary<string, object>
                {
                    { "name", go.name },
                    { "path", GetGameObjectPath(go) },
                    { "instanceId", go.GetInstanceID() },
                    { "active", go.activeInHierarchy },
                    { "layer", LayerMask.LayerToName(go.layer) },
                });
            }

            return new Dictionary<string, object>
            {
                { "tag", tag },
                { "count", results.Count },
                { "results", results },
            };
        }

        // ─── Find By Layer ───

        public static object FindByLayer(Dictionary<string, object> args)
        {
            int layer = -1;
            if (args.ContainsKey("layer"))
            {
                string val = args["layer"].ToString();
                if (!int.TryParse(val, out layer))
                    layer = LayerMask.NameToLayer(val);
            }
            if (layer < 0)
                return new { error = "Valid layer index or name is required" };

            var results = new List<Dictionary<string, object>>();
            var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var go in allObjects)
            {
                if (go.layer == layer)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "name", go.name },
                        { "path", GetGameObjectPath(go) },
                        { "instanceId", go.GetInstanceID() },
                        { "active", go.activeInHierarchy },
                        { "tag", go.tag },
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "layer", LayerMask.LayerToName(layer) },
                { "layerIndex", layer },
                { "count", results.Count },
                { "results", results },
            };
        }

        // ─── Find By Name ───

        public static object FindByName(Dictionary<string, object> args)
        {
            string pattern = args.ContainsKey("name") ? args["name"].ToString() : "";
            if (string.IsNullOrEmpty(pattern))
                return new { error = "name is required" };

            bool useRegex = args.ContainsKey("regex") && Convert.ToBoolean(args["regex"]);
            bool includeInactive = args.ContainsKey("includeInactive") && Convert.ToBoolean(args["includeInactive"]);

            var results = new List<Dictionary<string, object>>();
            var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(
                includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            foreach (var go in allObjects)
            {
                bool match = false;
                if (useRegex)
                {
                    try { match = Regex.IsMatch(go.name, pattern, RegexOptions.IgnoreCase); }
                    catch { continue; }
                }
                else
                {
                    match = go.name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
                }

                if (match)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "name", go.name },
                        { "path", GetGameObjectPath(go) },
                        { "instanceId", go.GetInstanceID() },
                        { "active", go.activeInHierarchy },
                        { "tag", go.tag },
                        { "layer", LayerMask.LayerToName(go.layer) },
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "pattern", pattern },
                { "regex", useRegex },
                { "count", results.Count },
                { "results", results },
            };
        }

        // ─── Find By Shader ───

        public static object FindByShader(Dictionary<string, object> args)
        {
            string shaderName = args.ContainsKey("shader") ? args["shader"].ToString() : "";
            if (string.IsNullOrEmpty(shaderName))
                return new { error = "shader is required" };

            var results = new List<Dictionary<string, object>>();
            var renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var renderer in renderers)
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat != null && mat.shader != null &&
                        mat.shader.name.IndexOf(shaderName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        results.Add(new Dictionary<string, object>
                        {
                            { "name", renderer.gameObject.name },
                            { "path", GetGameObjectPath(renderer.gameObject) },
                            { "instanceId", renderer.gameObject.GetInstanceID() },
                            { "material", mat.name },
                            { "shader", mat.shader.name },
                        });
                        break; // One entry per object
                    }
                }
            }

            return new Dictionary<string, object>
            {
                { "shader", shaderName },
                { "count", results.Count },
                { "results", results },
            };
        }

        // ─── Search Assets ───

        public static object SearchAssets(Dictionary<string, object> args)
        {
            string query = args.ContainsKey("query") ? args["query"].ToString() : "";
            string type = args.ContainsKey("type") ? args["type"].ToString() : "";
            string folder = args.ContainsKey("folder") ? args["folder"].ToString() : "";
            int maxResults = args.ContainsKey("maxResults") ? Convert.ToInt32(args["maxResults"]) : 100;

            string searchFilter = "";
            if (!string.IsNullOrEmpty(query)) searchFilter += query;
            if (!string.IsNullOrEmpty(type)) searchFilter += " t:" + type;

            string[] searchFolders = string.IsNullOrEmpty(folder) ? null : new[] { folder };
            string[] guids = searchFolders != null
                ? AssetDatabase.FindAssets(searchFilter, searchFolders)
                : AssetDatabase.FindAssets(searchFilter);

            var results = new List<Dictionary<string, object>>();
            int count = Math.Min(guids.Length, maxResults);
            for (int i = 0; i < count; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
                var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                results.Add(new Dictionary<string, object>
                {
                    { "path", assetPath },
                    { "guid", guids[i] },
                    { "type", assetType != null ? assetType.Name : "Unknown" },
                    { "name", System.IO.Path.GetFileNameWithoutExtension(assetPath) },
                });
            }

            return new Dictionary<string, object>
            {
                { "totalFound", guids.Length },
                { "returned", results.Count },
                { "results", results },
            };
        }

        // ─── Find Missing References ───

        public static object FindMissingReferences(Dictionary<string, object> args)
        {
            bool searchScene = !args.ContainsKey("scope") || args["scope"].ToString() != "assets";

            var results = new List<Dictionary<string, object>>();

            if (searchScene)
            {
                var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var go in allObjects)
                {
                    var components = go.GetComponents<Component>();
                    for (int i = 0; i < components.Length; i++)
                    {
                        if (components[i] == null)
                        {
                            results.Add(new Dictionary<string, object>
                            {
                                { "gameObject", go.name },
                                { "path", GetGameObjectPath(go) },
                                { "issue", "Missing script (component is null)" },
                                { "componentIndex", i },
                            });
                            continue;
                        }

                        var so = new SerializedObject(components[i]);
                        var sp = so.GetIterator();
                        while (sp.NextVisible(true))
                        {
                            if (sp.propertyType == SerializedPropertyType.ObjectReference &&
                                sp.objectReferenceValue == null &&
                                sp.objectReferenceInstanceIDValue != 0)
                            {
                                results.Add(new Dictionary<string, object>
                                {
                                    { "gameObject", go.name },
                                    { "path", GetGameObjectPath(go) },
                                    { "component", components[i].GetType().Name },
                                    { "property", sp.displayName },
                                    { "issue", "Missing object reference" },
                                });
                            }
                        }
                    }
                }
            }

            return new Dictionary<string, object>
            {
                { "scope", searchScene ? "scene" : "assets" },
                { "count", results.Count },
                { "results", results },
            };
        }

        // ─── Scene Stats ───

        public static object GetSceneStats(Dictionary<string, object> args)
        {
            var scene = SceneManager.GetActiveScene();
            var rootObjects = scene.GetRootGameObjects();

            int totalObjects = 0;
            int totalComponents = 0;
            int totalMeshes = 0;
            int totalLights = 0;
            int totalCameras = 0;
            int totalColliders = 0;
            int totalRigidbodies = 0;
            int totalRenderers = 0;
            long totalVertices = 0;
            long totalTriangles = 0;
            var componentCounts = new Dictionary<string, int>();

            void CountRecursive(GameObject go)
            {
                totalObjects++;
                var components = go.GetComponents<Component>();
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    totalComponents++;
                    string typeName = comp.GetType().Name;
                    if (!componentCounts.ContainsKey(typeName))
                        componentCounts[typeName] = 0;
                    componentCounts[typeName]++;

                    if (comp is MeshFilter mf && mf.sharedMesh != null)
                    {
                        totalMeshes++;
                        totalVertices += mf.sharedMesh.vertexCount;
                        totalTriangles += mf.sharedMesh.triangles.Length / 3;
                    }
                    if (comp is Light) totalLights++;
                    if (comp is Camera) totalCameras++;
                    if (comp is Collider) totalColliders++;
                    if (comp is Rigidbody) totalRigidbodies++;
                    if (comp is Renderer) totalRenderers++;
                }

                foreach (Transform child in go.transform)
                    CountRecursive(child.gameObject);
            }

            foreach (var root in rootObjects)
                CountRecursive(root);

            // Top 10 most common components
            var topComponents = componentCounts.OrderByDescending(kv => kv.Value).Take(10)
                .Select(kv => new Dictionary<string, object> { { "type", kv.Key }, { "count", kv.Value } })
                .ToList();

            return new Dictionary<string, object>
            {
                { "sceneName", scene.name },
                { "totalGameObjects", totalObjects },
                { "totalComponents", totalComponents },
                { "totalMeshes", totalMeshes },
                { "totalVertices", totalVertices },
                { "totalTriangles", totalTriangles },
                { "totalLights", totalLights },
                { "totalCameras", totalCameras },
                { "totalColliders", totalColliders },
                { "totalRigidbodies", totalRigidbodies },
                { "totalRenderers", totalRenderers },
                { "topComponents", topComponents },
            };
        }

        // ─── Helpers ───

        private static string GetGameObjectPath(GameObject go)
        {
            string path = go.name;
            Transform parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }
    }
}
