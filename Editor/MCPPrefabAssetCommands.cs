using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Direct prefab asset editing — browse hierarchy, get/set properties, wire references,
    /// add/remove components and children on prefab assets without needing a scene instance.
    /// Every operation is atomic: load → modify → save → unload.
    /// </summary>
    public static class MCPPrefabAssetCommands
    {
        // ─── Hierarchy ───

        /// <summary>
        /// Get the full hierarchy tree of a prefab asset.
        /// </summary>
        public static object GetHierarchy(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            int maxDepth = args.ContainsKey("maxDepth") ? Convert.ToInt32(args["maxDepth"]) : 10;

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var hierarchy = BuildHierarchyNode(root, 0, maxDepth);
                return new Dictionary<string, object>
                {
                    { "prefab", root.name },
                    { "assetPath", assetPath },
                    { "hierarchy", hierarchy },
                };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ─── Component Properties ───

        /// <summary>
        /// Read all properties from a component on a GameObject inside a prefab asset.
        /// </summary>
        public static object GetComponentProperties(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            string prefabPath = GetString(args, "prefabPath");
            string componentType = GetString(args, "componentType");
            if (string.IsNullOrEmpty(componentType))
                return new { error = "componentType is required" };

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var go = FindInPrefab(root, prefabPath);
                if (go == null)
                    return new { error = $"GameObject '{prefabPath}' not found in prefab" };

                Type type = MCPComponentCommands.FindType(componentType);
                if (type == null)
                    return new { error = $"Type '{componentType}' not found" };

                var component = go.GetComponent(type);
                if (component == null)
                    return new { error = $"Component '{componentType}' not found on '{go.name}'" };

                var serialized = new SerializedObject(component);
                var properties = new List<Dictionary<string, object>>();

                var iterator = serialized.GetIterator();
                if (iterator.NextVisible(true))
                {
                    do
                    {
                        properties.Add(new Dictionary<string, object>
                        {
                            { "name", iterator.name },
                            { "displayName", iterator.displayName },
                            { "type", iterator.propertyType.ToString() },
                            { "value", MCPComponentCommands.GetSerializedValue(iterator) },
                            { "editable", iterator.editable },
                        });
                    } while (iterator.NextVisible(false));
                }

                return new Dictionary<string, object>
                {
                    { "prefab", root.name },
                    { "gameObject", go.name },
                    { "prefabPath", prefabPath ?? "" },
                    { "component", componentType },
                    { "properties", properties },
                };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Set a component property on a GameObject inside a prefab asset.
        /// </summary>
        public static object SetComponentProperty(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            string prefabPath = GetString(args, "prefabPath");
            string componentType = GetString(args, "componentType");
            string propertyName = GetString(args, "propertyName");

            if (string.IsNullOrEmpty(componentType))
                return new { error = "componentType is required" };
            if (string.IsNullOrEmpty(propertyName))
                return new { error = "propertyName is required" };
            if (!args.ContainsKey("value"))
                return new { error = "value is required" };

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var go = FindInPrefab(root, prefabPath);
                if (go == null)
                    return new { error = $"GameObject '{prefabPath}' not found in prefab" };

                Type type = MCPComponentCommands.FindType(componentType);
                if (type == null)
                    return new { error = $"Type '{componentType}' not found" };

                var component = go.GetComponent(type);
                if (component == null)
                    return new { error = $"Component '{componentType}' not found on '{go.name}'" };

                var serialized = new SerializedObject(component);
                var prop = serialized.FindProperty(propertyName);
                if (prop == null)
                    return new { error = $"Property '{propertyName}' not found on '{componentType}'" };

                MCPComponentCommands.SetSerializedValue(prop, args["value"]);
                serialized.ApplyModifiedProperties();

                PrefabUtility.SaveAsPrefabAsset(root, assetPath);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefab", root.name },
                    { "gameObject", go.name },
                    { "component", componentType },
                    { "property", propertyName },
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to set property: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ─── Components ───

        /// <summary>
        /// Add a component to a GameObject inside a prefab asset.
        /// </summary>
        public static object AddComponent(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            string prefabPath = GetString(args, "prefabPath");
            string componentType = GetString(args, "componentType");
            if (string.IsNullOrEmpty(componentType))
                return new { error = "componentType is required" };

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var go = FindInPrefab(root, prefabPath);
                if (go == null)
                    return new { error = $"GameObject '{prefabPath}' not found in prefab" };

                Type type = MCPComponentCommands.FindType(componentType);
                if (type == null)
                    return new { error = $"Type '{componentType}' not found" };

                var component = go.AddComponent(type);
                PrefabUtility.SaveAsPrefabAsset(root, assetPath);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefab", root.name },
                    { "gameObject", go.name },
                    { "component", component.GetType().Name },
                    { "fullType", component.GetType().FullName },
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to add component: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Remove a component from a GameObject inside a prefab asset.
        /// </summary>
        public static object RemoveComponent(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            string prefabPath = GetString(args, "prefabPath");
            string componentType = GetString(args, "componentType");
            if (string.IsNullOrEmpty(componentType))
                return new { error = "componentType is required" };

            int index = args.ContainsKey("index") ? Convert.ToInt32(args["index"]) : 0;

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var go = FindInPrefab(root, prefabPath);
                if (go == null)
                    return new { error = $"GameObject '{prefabPath}' not found in prefab" };

                Type type = MCPComponentCommands.FindType(componentType);
                if (type == null)
                    return new { error = $"Type '{componentType}' not found" };

                var components = go.GetComponents(type);
                if (components == null || index >= components.Length)
                    return new { error = $"Component '{componentType}' at index {index} not found on '{go.name}'" };

                UnityEngine.Object.DestroyImmediate(components[index]);
                PrefabUtility.SaveAsPrefabAsset(root, assetPath);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefab", root.name },
                    { "gameObject", go.name },
                    { "removedComponent", componentType },
                    { "index", index },
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to remove component: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ─── Reference Wiring ───

        /// <summary>
        /// Wire an ObjectReference property on a component inside a prefab asset.
        /// Supports references to assets (by path) and to other GameObjects within the same prefab.
        /// </summary>
        public static object SetReference(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            string prefabPath = GetString(args, "prefabPath");
            string componentType = GetString(args, "componentType");
            string propertyName = GetString(args, "propertyName");
            if (string.IsNullOrEmpty(propertyName))
                return new { error = "propertyName is required" };

            string referenceAssetPath = GetString(args, "referenceAssetPath");
            string referencePrefabPath = GetString(args, "referencePrefabPath");
            string referenceComponentType = GetString(args, "referenceComponentType");
            bool clearRef = args.ContainsKey("clear") && Convert.ToBoolean(args["clear"]);

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var go = FindInPrefab(root, prefabPath);
                if (go == null)
                    return new { error = $"GameObject '{prefabPath}' not found in prefab" };

                // Find component (auto-search if componentType not specified)
                Component component = null;
                if (!string.IsNullOrEmpty(componentType))
                {
                    Type type = MCPComponentCommands.FindType(componentType);
                    if (type != null) component = go.GetComponent(type);
                }
                else
                {
                    foreach (var comp in go.GetComponents<Component>())
                    {
                        if (comp == null) continue;
                        var so = new SerializedObject(comp);
                        if (so.FindProperty(propertyName) != null)
                        {
                            component = comp;
                            break;
                        }
                    }
                }

                if (component == null)
                    return new { error = $"Component '{componentType}' not found on '{go.name}', or no component has property '{propertyName}'" };

                var serialized = new SerializedObject(component);
                var prop = serialized.FindProperty(propertyName);
                if (prop == null)
                    return new { error = $"Property '{propertyName}' not found" };

                if (prop.propertyType != SerializedPropertyType.ObjectReference)
                    return new { error = $"Property '{propertyName}' is not an ObjectReference (type: {prop.propertyType})" };

                // Resolve reference
                UnityEngine.Object targetRef = null;
                string refDescription = "null (cleared)";

                if (clearRef)
                {
                    prop.objectReferenceValue = null;
                }
                else if (!string.IsNullOrEmpty(referenceAssetPath))
                {
                    targetRef = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(referenceAssetPath);
                    if (targetRef == null)
                        return new { error = $"Asset not found at '{referenceAssetPath}'" };

                    prop.objectReferenceValue = targetRef;
                    refDescription = $"{targetRef.name} ({targetRef.GetType().Name})";
                }
                else if (!string.IsNullOrEmpty(referencePrefabPath))
                {
                    var refGo = FindInPrefab(root, referencePrefabPath);
                    if (refGo == null)
                        return new { error = $"GameObject '{referencePrefabPath}' not found in prefab" };

                    if (!string.IsNullOrEmpty(referenceComponentType))
                    {
                        Type refType = MCPComponentCommands.FindType(referenceComponentType);
                        if (refType == null)
                            return new { error = $"Type '{referenceComponentType}' not found" };

                        targetRef = refGo.GetComponent(refType);
                        if (targetRef == null)
                            return new { error = $"Component '{referenceComponentType}' not found on '{refGo.name}'" };
                    }
                    else
                    {
                        targetRef = refGo;
                    }

                    prop.objectReferenceValue = targetRef;
                    refDescription = $"{targetRef.name} ({targetRef.GetType().Name})";
                }
                else
                {
                    return new { error = "Provide referenceAssetPath, referencePrefabPath, or clear=true" };
                }

                serialized.ApplyModifiedProperties();
                PrefabUtility.SaveAsPrefabAsset(root, assetPath);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefab", root.name },
                    { "gameObject", go.name },
                    { "component", component.GetType().Name },
                    { "property", propertyName },
                    { "reference", refDescription },
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to set reference: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ─── Hierarchy Modification ───

        /// <summary>
        /// Create a new child GameObject inside a prefab asset.
        /// </summary>
        public static object AddGameObject(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            string parentPrefabPath = GetString(args, "parentPrefabPath");
            string name = GetString(args, "name");
            if (string.IsNullOrEmpty(name))
                return new { error = "name is required" };

            string primitiveType = GetString(args, "primitiveType");

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var parent = FindInPrefab(root, parentPrefabPath);
                if (parent == null)
                    return new { error = $"Parent '{parentPrefabPath}' not found in prefab" };

                GameObject newGo;
                if (!string.IsNullOrEmpty(primitiveType) && Enum.TryParse<PrimitiveType>(primitiveType, true, out var pt))
                {
                    newGo = GameObject.CreatePrimitive(pt);
                    newGo.name = name;
                }
                else
                {
                    newGo = new GameObject(name);
                }

                newGo.transform.SetParent(parent.transform, false);

                // Set transform if provided
                if (args.ContainsKey("position"))
                    newGo.transform.localPosition = ParseVector3(args["position"]);
                if (args.ContainsKey("rotation"))
                    newGo.transform.localEulerAngles = ParseVector3(args["rotation"]);
                if (args.ContainsKey("scale"))
                    newGo.transform.localScale = ParseVector3(args["scale"]);

                PrefabUtility.SaveAsPrefabAsset(root, assetPath);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefab", root.name },
                    { "createdGameObject", name },
                    { "parent", string.IsNullOrEmpty(parentPrefabPath) ? "root" : parentPrefabPath },
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to add GameObject: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Delete a child GameObject from a prefab asset.
        /// Cannot delete the root GameObject.
        /// </summary>
        public static object RemoveGameObject(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            string prefabPath = GetString(args, "prefabPath");
            if (string.IsNullOrEmpty(prefabPath))
                return new { error = "prefabPath is required (cannot delete root)" };

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var go = FindInPrefab(root, prefabPath);
                if (go == null)
                    return new { error = $"GameObject '{prefabPath}' not found in prefab" };

                if (go == root)
                    return new { error = "Cannot delete the root GameObject of a prefab" };

                string deletedName = go.name;
                UnityEngine.Object.DestroyImmediate(go);
                PrefabUtility.SaveAsPrefabAsset(root, assetPath);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "prefab", root.name },
                    { "deletedGameObject", deletedName },
                    { "prefabPath", prefabPath },
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to remove GameObject: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ─── Helpers ───

        private static GameObject FindInPrefab(GameObject root, string prefabPath)
        {
            if (string.IsNullOrEmpty(prefabPath))
                return root;

            Transform current = root.transform;
            foreach (var part in prefabPath.Split('/'))
            {
                if (string.IsNullOrEmpty(part)) continue;
                current = current.Find(part);
                if (current == null) return null;
            }
            return current.gameObject;
        }

        private static Dictionary<string, object> BuildHierarchyNode(GameObject go, int depth, int maxDepth)
        {
            var components = new List<string>();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp != null)
                    components.Add(comp.GetType().Name);
            }

            var node = new Dictionary<string, object>
            {
                { "name", go.name },
                { "active", go.activeSelf },
                { "tag", go.tag },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "components", components },
                { "localPosition", VectorToDict(go.transform.localPosition) },
                { "localRotation", VectorToDict(go.transform.localEulerAngles) },
                { "localScale", VectorToDict(go.transform.localScale) },
            };

            if (depth < maxDepth && go.transform.childCount > 0)
            {
                var children = new List<object>();
                for (int i = 0; i < go.transform.childCount; i++)
                {
                    children.Add(BuildHierarchyNode(go.transform.GetChild(i).gameObject, depth + 1, maxDepth));
                }
                node["children"] = children;
                node["childCount"] = go.transform.childCount;
            }
            else if (go.transform.childCount > 0)
            {
                node["childCount"] = go.transform.childCount;
                node["childrenTruncated"] = true;
            }

            return node;
        }

        private static string GetString(Dictionary<string, object> args, string key)
        {
            return args != null && args.ContainsKey(key) ? args[key]?.ToString() : "";
        }

        private static Dictionary<string, object> VectorToDict(Vector3 v)
        {
            return new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z } };
        }

        private static Vector3 ParseVector3(object value)
        {
            if (value is Dictionary<string, object> d)
            {
                return new Vector3(
                    d.ContainsKey("x") ? Convert.ToSingle(d["x"]) : 0f,
                    d.ContainsKey("y") ? Convert.ToSingle(d["y"]) : 0f,
                    d.ContainsKey("z") ? Convert.ToSingle(d["z"]) : 0f
                );
            }
            return Vector3.zero;
        }
    }
}
