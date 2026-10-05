using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
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
                if (components == null || index < 0 || index >= components.Length)
                    return new { error = $"Component '{componentType}' at index {index} not found on '{go.name}'" };

                var component = components[index];
                UnityEngine.Object.DestroyImmediate(component);

                // Unity refuses to destroy a Transform or a component another one requires: it only logs
                // an error. Check before saving, so an unchanged prefab is not re-saved as a success.
                if (component != null)
                    return new { error = $"Unity refused to remove {component.GetType().Name} from '{go.name}': {MCPComponentCommands.DescribeRemovalBlocker(go, component)}." };

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

                    // Type-checked assignment with read-back; on failure nothing is saved.
                    if (!MCPComponentCommands.TryAssignObjectReference(prop, targetRef, out var assigned, out string assignError))
                        return new { error = assignError };
                    refDescription = $"{assigned.name} ({assigned.GetType().Name})";
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

                    if (!MCPComponentCommands.TryAssignObjectReference(prop, targetRef, out var assigned, out string assignError))
                        return new { error = assignError };
                    refDescription = $"{assigned.name} ({assigned.GetType().Name})";
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

        // ─── Variant Management ───

        /// <summary>
        /// Get variant info for a prefab: is it a variant? what's the base? Also list all known variants of a base prefab.
        /// </summary>
        public static object GetVariantInfo(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null)
                return new { error = $"Prefab not found at '{assetPath}'" };

            var assetType = PrefabUtility.GetPrefabAssetType(asset);
            bool isVariant = assetType == PrefabAssetType.Variant;

            var result = new Dictionary<string, object>
            {
                { "prefab", asset.name },
                { "assetPath", assetPath },
                { "isVariant", isVariant },
                { "assetType", assetType.ToString() },
            };

            if (isVariant)
            {
                var basePrefab = PrefabUtility.GetCorrespondingObjectFromOriginalSource(asset);
                if (basePrefab != null)
                {
                    string basePath = AssetDatabase.GetAssetPath(basePrefab);
                    result["basePrefabPath"] = basePath;
                    result["basePrefabName"] = basePrefab.name;
                }
            }

            // Find all variants of this prefab (or of the base if this is already a variant)
            string searchBasePath = assetPath;
            if (isVariant)
            {
                var basePrefab = PrefabUtility.GetCorrespondingObjectFromOriginalSource(asset);
                if (basePrefab != null)
                    searchBasePath = AssetDatabase.GetAssetPath(basePrefab);
            }

            var variants = new List<Dictionary<string, object>>();
            var allPrefabs = AssetDatabase.FindAssets("t:Prefab");
            foreach (var guid in allPrefabs)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path == searchBasePath) continue;

                // Load-free prefilter on the dependency database: a variant, also a variant of a variant,
                // always has its original base among its recursive dependencies. Prefabs that merely nest
                // the base pass too, so the load-and-confirm below still decides.
                if (Array.IndexOf(AssetDatabase.GetDependencies(path, true), searchBasePath) < 0) continue;

                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;

                if (PrefabUtility.GetPrefabAssetType(go) != PrefabAssetType.Variant)
                    continue;

                var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
                if (source != null && AssetDatabase.GetAssetPath(source) == searchBasePath)
                {
                    variants.Add(new Dictionary<string, object>
                    {
                        { "name", go.name },
                        { "assetPath", path },
                    });
                }
            }

            result["basePrefab"] = searchBasePath;
            result["variants"] = variants;
            result["variantCount"] = variants.Count;

            return result;
        }

        /// <summary>
        /// Compare a variant to its base prefab — list all property overrides, added/removed components, added/removed GameObjects.
        /// </summary>
        public static object CompareVariantToBase(Dictionary<string, object> args)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null)
                return new { error = $"Prefab not found at '{assetPath}'" };

            if (PrefabUtility.GetPrefabAssetType(asset) != PrefabAssetType.Variant)
                return new { error = $"'{assetPath}' is not a variant prefab" };

            // The variant's contents root is the instance of its base inside the variant, so the override
            // APIs on it list the variant's added and removed items. A scene instance of the variant
            // would only show its (empty) overrides against the variant itself.
            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                var basePrefab = PrefabUtility.GetCorrespondingObjectFromOriginalSource(asset);
                string basePath = basePrefab != null ? AssetDatabase.GetAssetPath(basePrefab) : "unknown";

                // Property overrides
                var propertyOverrides = PrefabUtility.GetPropertyModifications(asset);
                var overrideList = new List<Dictionary<string, object>>();
                if (propertyOverrides != null)
                {
                    foreach (var mod in propertyOverrides)
                    {
                        if (mod.target == null) continue;
                        // Skip internal Transform position/rotation on root (always present)
                        overrideList.Add(new Dictionary<string, object>
                        {
                            { "targetType", mod.target.GetType().Name },
                            { "targetName", mod.target.name },
                            { "propertyPath", mod.propertyPath },
                            { "value", mod.value ?? "null" },
                        });
                    }
                }

                // Added components
                var addedComponents = PrefabUtility.GetAddedComponents(root);
                var addedCompList = new List<Dictionary<string, object>>();
                foreach (var added in addedComponents)
                {
                    addedCompList.Add(new Dictionary<string, object>
                    {
                        { "componentType", added.instanceComponent.GetType().Name },
                        { "gameObject", added.instanceComponent.gameObject.name },
                    });
                }

                // Removed components
                var removedComponents = PrefabUtility.GetRemovedComponents(root);
                var removedCompList = new List<Dictionary<string, object>>();
                foreach (var removed in removedComponents)
                {
                    removedCompList.Add(new Dictionary<string, object>
                    {
                        { "componentType", removed.assetComponent.GetType().Name },
                        { "gameObject", removed.assetComponent.gameObject.name },
                    });
                }

                // Added GameObjects
                var addedGOs = PrefabUtility.GetAddedGameObjects(root);
                var addedGOList = new List<Dictionary<string, object>>();
                foreach (var added in addedGOs)
                {
                    addedGOList.Add(new Dictionary<string, object>
                    {
                        { "name", added.instanceGameObject.name },
                        { "childCount", added.instanceGameObject.transform.childCount },
                    });
                }

                // Removed GameObjects
                var removedGOList = new List<Dictionary<string, object>>();
#if UNITY_2022_1_OR_NEWER
                var removedGOs = PrefabUtility.GetRemovedGameObjects(root);
                foreach (var removed in removedGOs)
                {
                    removedGOList.Add(new Dictionary<string, object>
                    {
                        { "name", removed.assetGameObject.name },
                    });
                }
#else
                // Fallback for Unity < 2022.1: compare the base's children vs the variant contents' children
                var assetSource = PrefabUtility.GetCorrespondingObjectFromSource(root);
                if (assetSource != null)
                {
                    foreach (Transform assetChild in assetSource.transform)
                    {
                        var correspondingInInstance = root.transform.Find(assetChild.name);
                        if (correspondingInInstance == null)
                        {
                            removedGOList.Add(new Dictionary<string, object>
                            {
                                { "name", assetChild.name },
                            });
                        }
                    }
                }
#endif

                return new Dictionary<string, object>
                {
                    { "variant", asset.name },
                    { "variantPath", assetPath },
                    { "basePrefab", basePrefab != null ? basePrefab.name : "unknown" },
                    { "basePrefabPath", basePath },
                    { "propertyOverrides", overrideList },
                    { "propertyOverrideCount", overrideList.Count },
                    { "addedComponents", addedCompList },
                    { "removedComponents", removedCompList },
                    { "addedGameObjects", addedGOList },
                    { "removedGameObjects", removedGOList },
                };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Apply a specific override from a variant back to its base prefab, or apply all overrides.
        /// </summary>
        public static object ApplyVariantOverride(Dictionary<string, object> args)
        {
            return ChangeVariantOverrides(args, true);
        }

        /// <summary>
        /// Revert a variant's overrides so it matches the base prefab again.
        /// Can revert all or only specific overrides by component/gameObject filter.
        /// </summary>
        public static object RevertVariantOverride(Dictionary<string, object> args)
        {
            return ChangeVariantOverrides(args, false);
        }

        /// <summary>
        /// Shared body of apply/revert-variant-override. It works on the variant's contents, never on a
        /// scene instance: the contents root is the instance of the immediate base inside the variant, so
        /// its override lists are the variant's own overrides against that base (a scene instance of the
        /// variant has none). Without applyAll/revertAll, componentType and gameObject filter them.
        /// </summary>
        private static object ChangeVariantOverrides(Dictionary<string, object> args, bool apply)
        {
            string assetPath = GetString(args, "assetPath");
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            string allKey = apply ? "applyAll" : "revertAll";
            bool all = args.ContainsKey(allKey) && Convert.ToBoolean(args[allKey]);
            // The server sends componentType/gameObject; targetComponentType/targetGameObject are the older names.
            string componentType = GetString(args, "componentType");
            if (string.IsNullOrEmpty(componentType)) componentType = GetString(args, "targetComponentType");
            string gameObjectFilter = GetString(args, "gameObject");
            if (string.IsNullOrEmpty(gameObjectFilter)) gameObjectFilter = GetString(args, "targetGameObject");
            if (all) componentType = gameObjectFilter = "";

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null)
                return new { error = $"Prefab not found at '{assetPath}'" };

            if (PrefabUtility.GetPrefabAssetType(asset) != PrefabAssetType.Variant)
                return new { error = $"'{assetPath}' is not a variant prefab" };

            var root = PrefabUtility.LoadPrefabContents(assetPath);
            if (root == null)
                return new { error = $"Failed to load prefab at '{assetPath}'" };

            try
            {
                return ChangeVariantContentsOverrides(root, asset, assetPath, apply, componentType, gameObjectFilter);
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to {(apply ? "apply" : "revert")} overrides: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>
        /// Apply each matching override of the loaded variant contents to the immediate base, or revert it,
        /// then save the variant so it keeps no applied or reverted override. Counts are real; filters that
        /// match nothing, or overrides Unity rejects, are reported as errors.
        /// </summary>
        private static object ChangeVariantContentsOverrides(GameObject root, GameObject asset, string assetPath, bool apply,
            string componentType, string gameObjectFilter)
        {
            // Immediate base: the overrides on this root are relative to it (for a variant of a variant,
            // not to the original base), and ApplyPrefabInstance would target it too.
            var basePrefab = PrefabUtility.GetCorrespondingObjectFromSource(root);
            string basePath = basePrefab != null ? AssetDatabase.GetAssetPath(basePrefab) : null;
            if (string.IsNullOrEmpty(basePath))
                return new { error = "Could not determine base prefab path" };
            if (apply && PrefabUtility.IsPartOfImmutablePrefab(basePrefab))
                return new { error = $"Base prefab '{basePath}' is a model or read-only prefab; overrides cannot be applied to it" };

            var overrides = CollectVariantOverrides(root, componentType, gameObjectFilter);
            if (overrides.Count == 0 && (!string.IsNullOrEmpty(componentType) || !string.IsNullOrEmpty(gameObjectFilter)))
                return new { error = $"No override on '{assetPath}' matches componentType '{componentType}' and gameObject '{gameObjectFilter}'" };

            int changedCount = 0;
            var failures = new List<string>();
            foreach (var (prefabOverride, label) in overrides)
            {
                try
                {
                    if (apply)
                        prefabOverride.Apply(basePath, InteractionMode.AutomatedAction);
                    else
                        prefabOverride.Revert(InteractionMode.AutomatedAction);
                    changedCount++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{label}: {ex.Message}");
                }
            }

            // Save the variant: an applied override is now redundant in it (an applied added component
            // would otherwise exist twice once the base has it), and a reverted one must leave its file.
            if (changedCount > 0)
            {
                PrefabUtility.SaveAsPrefabAsset(root, assetPath, out bool saved);
                if (!saved)
                    return new { error = $"{changedCount} override(s) were {(apply ? $"applied to '{basePath}'" : "reverted")}, but saving the variant '{assetPath}' failed" };
            }

            var result = new Dictionary<string, object>
            {
                { "success", failures.Count == 0 },
                { "variant", asset.name },
            };
            if (apply)
            {
                result["basePrefab"] = basePrefab.name;
                result["basePrefabPath"] = basePath;
            }
            result[apply ? "appliedCount" : "revertedCount"] = changedCount;
            if (failures.Count > 0)
            {
                result["error"] = $"{failures.Count} of {overrides.Count} override(s) could not be {(apply ? "applied" : "reverted")}";
                result["failures"] = failures;
            }
            return result;
        }

        /// <summary>
        /// Overrides the variant contents root holds against its immediate base (default root overrides
        /// excluded) that pass the filters, each with a label for error reports. A componentType filter
        /// leaves out added and removed GameObjects. Added items come first, so property overrides applied
        /// after them can still reference them.
        /// </summary>
        private static List<(PrefabOverride Override, string Label)> CollectVariantOverrides(GameObject root,
            string componentType, string gameObjectFilter)
        {
            var matches = new List<(PrefabOverride Override, string Label)>();
            bool anyType = string.IsNullOrEmpty(componentType);

            foreach (var added in PrefabUtility.GetAddedGameObjects(root))
            {
                var go = added.instanceGameObject;
                if (anyType && MatchesGameObjectFilter(gameObjectFilter, root, go))
                    matches.Add((added, $"added GameObject {DescribePathInPrefab(root, go)}"));
            }
            foreach (var added in PrefabUtility.GetAddedComponents(root))
            {
                var component = added.instanceComponent;
                if (MatchesComponentType(component, componentType) && MatchesGameObjectFilter(gameObjectFilter, root, component.gameObject))
                    matches.Add((added, $"added {component.GetType().Name} on {DescribePathInPrefab(root, component.gameObject)}"));
            }
            foreach (var removed in PrefabUtility.GetRemovedComponents(root))
            {
                var holder = removed.containingInstanceGameObject;
                if (MatchesComponentType(removed.assetComponent, componentType) && MatchesGameObjectFilter(gameObjectFilter, root, holder))
                    matches.Add((removed, $"removed {removed.assetComponent.GetType().Name} on {DescribePathInPrefab(root, holder)}"));
            }
#if UNITY_2022_1_OR_NEWER
            foreach (var removed in PrefabUtility.GetRemovedGameObjects(root))
            {
                string parentPath = GetPathInPrefab(root, removed.parentOfRemovedGameObjectInInstance);
                string name = removed.assetGameObject.name;
                string path = parentPath.Length > 0 ? parentPath + "/" + name : name;
                if (anyType && MatchesGameObjectFilter(gameObjectFilter, name, path))
                    matches.Add((removed, $"removed GameObject '{path}'"));
            }
#endif
            foreach (var changed in PrefabUtility.GetObjectOverrides(root, false))
            {
                var component = changed.instanceObject as Component;
                var go = component != null ? component.gameObject : changed.instanceObject as GameObject;
                if (MatchesComponentType(component, componentType) && MatchesGameObjectFilter(gameObjectFilter, root, go))
                    matches.Add((changed, $"property overrides of {(component != null ? component.GetType().Name : "GameObject")} on {DescribePathInPrefab(root, go)}"));
            }
            return matches;
        }

        private static bool MatchesComponentType(UnityEngine.Object obj, string componentType)
        {
            if (string.IsNullOrEmpty(componentType)) return true;
            return obj is Component && (obj.GetType().Name == componentType || obj.GetType().FullName == componentType);
        }

        private static bool MatchesGameObjectFilter(string filter, GameObject root, GameObject go)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            return go != null && MatchesGameObjectFilter(filter, go.name, GetPathInPrefab(root, go));
        }

        /// <summary>
        /// An empty filter matches everything; otherwise the bare name or the path below the root
        /// ('Body/Head', as prefabPath takes it) must equal it.
        /// </summary>
        private static bool MatchesGameObjectFilter(string filter, string name, string pathInPrefab)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            string wanted = filter.Trim('/');
            return name == wanted || pathInPrefab == wanted;
        }

        /// <summary>
        /// Transfer (copy) overrides from one variant to another variant of the same base.
        /// Reads properties from source variant and applies them to target variant.
        /// </summary>
        public static object TransferVariantOverrides(Dictionary<string, object> args)
        {
            string sourceAssetPath = GetString(args, "sourceAssetPath");
            string targetAssetPath = GetString(args, "targetAssetPath");

            if (string.IsNullOrEmpty(sourceAssetPath))
                return new { error = "sourceAssetPath is required" };
            if (string.IsNullOrEmpty(targetAssetPath))
                return new { error = "targetAssetPath is required" };

            var sourceAsset = AssetDatabase.LoadAssetAtPath<GameObject>(sourceAssetPath);
            var targetAsset = AssetDatabase.LoadAssetAtPath<GameObject>(targetAssetPath);

            if (sourceAsset == null) return new { error = $"Source prefab not found at '{sourceAssetPath}'" };
            if (targetAsset == null) return new { error = $"Target prefab not found at '{targetAssetPath}'" };

            // Modification targets are objects of the immediate base, so they only identify the same
            // object in both variants when both are variants of that same base.
            var sourceBase = PrefabUtility.GetPrefabAssetType(sourceAsset) == PrefabAssetType.Variant
                ? PrefabUtility.GetCorrespondingObjectFromSource(sourceAsset) : null;
            var targetBase = PrefabUtility.GetPrefabAssetType(targetAsset) == PrefabAssetType.Variant
                ? PrefabUtility.GetCorrespondingObjectFromSource(targetAsset) : null;
            if (sourceBase == null || targetBase == null || sourceBase != targetBase)
            {
                string sourceBasePath = sourceBase != null ? AssetDatabase.GetAssetPath(sourceBase) : "not a variant";
                string targetBasePath = targetBase != null ? AssetDatabase.GetAssetPath(targetBase) : "not a variant";
                return new { error = $"Source and target must be variants of the same base prefab (source base: {sourceBasePath}, target base: {targetBasePath})" };
            }

            // Get source overrides
            var sourceMods = PrefabUtility.GetPropertyModifications(sourceAsset);
            if (sourceMods == null || sourceMods.Length == 0)
                return new { error = "Source variant has no overrides to transfer" };

            // Filter by component/property if requested
            string filterComponentType = GetString(args, "filterComponentType");
            string filterPropertyPath = GetString(args, "filterPropertyPath");

            // Load target for editing
            var targetRoot = PrefabUtility.LoadPrefabContents(targetAssetPath);
            if (targetRoot == null)
                return new { error = "Failed to load target prefab for editing" };

            try
            {
                int transferred = 0;

                // Get the existing modifications on target
                var targetMods = PrefabUtility.GetPropertyModifications(targetAsset);
                var newMods = new List<PropertyModification>(targetMods ?? new PropertyModification[0]);

                foreach (var mod in sourceMods)
                {
                    if (mod.target == null) continue;

                    // Apply filters
                    if (!string.IsNullOrEmpty(filterComponentType) && mod.target.GetType().Name != filterComponentType)
                        continue;
                    if (!string.IsNullOrEmpty(filterPropertyPath) && !mod.propertyPath.Contains(filterPropertyPath))
                        continue;

                    // Check if this override already exists on target, replace or add. Match the same base
                    // object, not the same type: the root and a child Transform both have m_LocalPosition.x.
                    bool found = false;
                    bool changed = true;
                    for (int i = 0; i < newMods.Count; i++)
                    {
                        if (newMods[i].target == mod.target &&
                            newMods[i].propertyPath == mod.propertyPath)
                        {
                            changed = newMods[i].value != mod.value || newMods[i].objectReference != mod.objectReference;
                            newMods[i] = mod;
                            found = true;
                            break;
                        }
                    }
                    if (!found) newMods.Add(mod);
                    if (changed) transferred++;
                }

                PrefabUtility.SetPropertyModifications(targetRoot, newMods.ToArray());
                PrefabUtility.SaveAsPrefabAsset(targetRoot, targetAssetPath);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "source", sourceAsset.name },
                    { "target", targetAsset.name },
                    { "transferredOverrides", transferred },
                };
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to transfer overrides: {ex.Message}" };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(targetRoot);
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

        /// <summary>Path of <paramref name="go"/> below the prefab root, as FindInPrefab takes it ("" for the root).</summary>
        private static string GetPathInPrefab(GameObject root, GameObject go)
        {
            if (go == null || go == root) return "";
            string path = go.name;
            for (var parent = go.transform.parent; parent != null && parent != root.transform; parent = parent.parent)
                path = parent.name + "/" + path;
            return path;
        }

        private static string DescribePathInPrefab(GameObject root, GameObject go)
        {
            string path = GetPathInPrefab(root, go);
            return path.Length > 0 ? $"'{path}'" : "the root";
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
