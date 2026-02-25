using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Advanced prefab operations: editing, variants, overrides, nested prefabs, and object references.
    /// Basic create/instantiate are in MCPAssetCommands. This handles the advanced workflow.
    /// </summary>
    public static class MCPPrefabCommands
    {
        /// <summary>
        /// Get detailed prefab info: overrides, variant status, nested prefabs.
        /// </summary>
        public static object GetPrefabInfo(Dictionary<string, object> args)
        {
            // Can work on scene instance or asset
            string assetPath = args.ContainsKey("assetPath") ? args["assetPath"].ToString() : "";

            if (!string.IsNullOrEmpty(assetPath))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (prefab == null)
                    return new { error = $"Prefab not found at '{assetPath}'" };

                return BuildPrefabInfo(prefab, assetPath, false);
            }

            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null)
                return new { error = "GameObject not found. Provide assetPath or path/instanceId." };

            var status = PrefabUtility.GetPrefabInstanceStatus(go);
            if (status == PrefabInstanceStatus.NotAPrefab)
                return new { error = "GameObject is not a prefab instance" };

            string sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            return BuildPrefabInfo(go, sourcePath, true);
        }

        private static object BuildPrefabInfo(GameObject go, string assetPath, bool isInstance)
        {
            var result = new Dictionary<string, object>
            {
                { "name", go.name },
                { "assetPath", assetPath },
                { "isInstance", isInstance },
                { "prefabType", PrefabUtility.GetPrefabAssetType(go).ToString() },
            };

            if (isInstance)
            {
                result["instanceStatus"] = PrefabUtility.GetPrefabInstanceStatus(go).ToString();
                result["hasOverrides"] = PrefabUtility.HasPrefabInstanceAnyOverrides(go, false);

                // List property overrides
                var modifications = PrefabUtility.GetPropertyModifications(go);
                if (modifications != null)
                {
                    var overrides = new List<Dictionary<string, object>>();
                    foreach (var mod in modifications)
                    {
                        overrides.Add(new Dictionary<string, object>
                        {
                            { "target", mod.target != null ? mod.target.name : "null" },
                            { "propertyPath", mod.propertyPath },
                            { "value", mod.value },
                        });
                    }
                    result["overrides"] = overrides;
                    result["overrideCount"] = overrides.Count;
                }

                // Added components
                var addedComponents = PrefabUtility.GetAddedComponents(go);
                if (addedComponents != null)
                {
                    var added = new List<string>();
                    foreach (var ac in addedComponents)
                        added.Add(ac.instanceComponent.GetType().Name);
                    result["addedComponents"] = added;
                }

                // Removed components
                var removedComponents = PrefabUtility.GetRemovedComponents(go);
                if (removedComponents != null)
                    result["removedComponentCount"] = removedComponents.Count;
            }

            // Check if variant
            if (!string.IsNullOrEmpty(assetPath))
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (asset != null)
                {
                    bool isVariant = PrefabUtility.GetPrefabAssetType(asset) == PrefabAssetType.Variant;
                    result["isVariant"] = isVariant;
                    if (isVariant)
                    {
                        var basePrefab = PrefabUtility.GetCorrespondingObjectFromSource(asset);
                        if (basePrefab != null)
                            result["basePrefabPath"] = AssetDatabase.GetAssetPath(basePrefab);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Create a prefab variant from an existing prefab.
        /// </summary>
        public static object CreateVariant(Dictionary<string, object> args)
        {
            string basePath = args.ContainsKey("basePrefabPath") ? args["basePrefabPath"].ToString() : "";
            string variantPath = args.ContainsKey("variantPath") ? args["variantPath"].ToString() : "";

            if (string.IsNullOrEmpty(basePath))
                return new { error = "basePrefabPath is required" };
            if (string.IsNullOrEmpty(variantPath))
                return new { error = "variantPath is required" };

            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(basePath);
            if (basePrefab == null)
                return new { error = $"Base prefab not found at '{basePath}'" };

            // Ensure directory
            EnsureDirectory(variantPath);

            // Instantiate, then save as variant
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            var variant = PrefabUtility.SaveAsPrefabAsset(instance, variantPath);
            UnityEngine.Object.DestroyImmediate(instance);

            return new Dictionary<string, object>
            {
                { "success", variant != null },
                { "variantPath", variantPath },
                { "basePrefabPath", basePath },
                { "name", variant != null ? variant.name : null },
            };
        }

        /// <summary>
        /// Apply all overrides from a prefab instance back to the source prefab asset.
        /// </summary>
        public static object ApplyOverrides(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null)
                return new { error = "GameObject not found" };

            var status = PrefabUtility.GetPrefabInstanceStatus(go);
            if (status != PrefabInstanceStatus.Connected)
                return new { error = "GameObject is not a connected prefab instance" };

            string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            PrefabUtility.ApplyPrefabInstance(go, InteractionMode.AutomatedAction);

            return new { success = true, gameObject = go.name, appliedTo = assetPath };
        }

        /// <summary>
        /// Revert all overrides on a prefab instance.
        /// </summary>
        public static object RevertOverrides(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null)
                return new { error = "GameObject not found" };

            var status = PrefabUtility.GetPrefabInstanceStatus(go);
            if (status != PrefabInstanceStatus.Connected)
                return new { error = "GameObject is not a connected prefab instance" };

            PrefabUtility.RevertPrefabInstance(go, InteractionMode.AutomatedAction);

            return new { success = true, gameObject = go.name, message = "All overrides reverted" };
        }

        /// <summary>
        /// Unpack a prefab instance (completely or just the outermost).
        /// </summary>
        public static object Unpack(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null)
                return new { error = "GameObject not found" };

            bool completely = args.ContainsKey("completely") && Convert.ToBoolean(args["completely"]);

            if (completely)
                PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            else
                PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);

            return new { success = true, gameObject = go.name, mode = completely ? "Completely" : "OutermostRoot" };
        }

        /// <summary>
        /// Set an object reference on a component (e.g., assign a prefab, material, sprite to a field).
        /// This is the critical feature for wiring up references between objects.
        /// </summary>
        public static object SetObjectReference(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            string componentType = args.ContainsKey("componentType") ? args["componentType"].ToString() : "";
            string propertyName = args.ContainsKey("propertyName") ? args["propertyName"].ToString() : "";
            string referencePath = args.ContainsKey("referencePath") ? args["referencePath"].ToString() : "";
            string referenceGameObject = args.ContainsKey("referenceGameObject") ? args["referenceGameObject"].ToString() : "";

            if (string.IsNullOrEmpty(propertyName))
                return new { error = "propertyName is required" };

            // Find the component
            Type type = null;
            Component component = null;

            if (!string.IsNullOrEmpty(componentType))
            {
                type = FindType(componentType);
                if (type != null) component = go.GetComponent(type);
            }
            else
            {
                // Search all components for this property
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
                return new { error = $"Component '{componentType}' not found on {go.name}, or no component has property '{propertyName}'" };

            var serialized = new SerializedObject(component);
            var prop = serialized.FindProperty(propertyName);
            if (prop == null)
                return new { error = $"Property '{propertyName}' not found" };

            if (prop.propertyType != SerializedPropertyType.ObjectReference)
                return new { error = $"Property '{propertyName}' is not an ObjectReference (type: {prop.propertyType})" };

            // Resolve the reference
            UnityEngine.Object targetRef = null;

            if (!string.IsNullOrEmpty(referencePath))
            {
                // Load from asset path
                targetRef = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(referencePath);
                if (targetRef == null)
                    return new { error = $"Asset not found at '{referencePath}'" };
            }
            else if (!string.IsNullOrEmpty(referenceGameObject))
            {
                // Find in scene
                targetRef = GameObject.Find(referenceGameObject);
                if (targetRef == null)
                {
                    // Try finding by component
                    var allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();
                    foreach (var obj in allObjects)
                    {
                        if (obj.name == referenceGameObject)
                        {
                            targetRef = obj;
                            break;
                        }
                    }
                }
                if (targetRef == null)
                    return new { error = $"GameObject '{referenceGameObject}' not found in scene" };
            }
            else
            {
                // Set to null (clear reference)
                prop.objectReferenceValue = null;
                serialized.ApplyModifiedProperties();
                return new { success = true, gameObject = go.name, property = propertyName, reference = "null (cleared)" };
            }

            prop.objectReferenceValue = targetRef;
            serialized.ApplyModifiedProperties();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "gameObject", go.name },
                { "component", component.GetType().Name },
                { "property", propertyName },
                { "reference", targetRef.name },
                { "referenceType", targetRef.GetType().Name },
            };
        }

        /// <summary>
        /// Duplicate a GameObject (with all children and components).
        /// </summary>
        public static object Duplicate(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null)
                return new { error = "GameObject not found" };

            string newName = args.ContainsKey("newName") ? args["newName"].ToString() : go.name + " (Copy)";

            var duplicate = UnityEngine.Object.Instantiate(go);
            duplicate.name = newName;

            if (go.transform.parent != null)
                duplicate.transform.SetParent(go.transform.parent);

            Undo.RegisterCreatedObjectUndo(duplicate, $"Duplicate {go.name}");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "original", go.name },
                { "duplicate", duplicate.name },
                { "instanceId", duplicate.GetInstanceID() },
            };
        }

        /// <summary>
        /// Set a GameObject active/inactive.
        /// </summary>
        public static object SetActive(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            bool active = args.ContainsKey("active") ? Convert.ToBoolean(args["active"]) : true;
            Undo.RecordObject(go, "Set Active");
            go.SetActive(active);

            return new { success = true, gameObject = go.name, active };
        }

        /// <summary>
        /// Reparent a GameObject under a new parent.
        /// </summary>
        public static object Reparent(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            string parentPath = args.ContainsKey("newParent") ? args["newParent"].ToString() : "";
            bool worldPositionStays = !args.ContainsKey("worldPositionStays") || Convert.ToBoolean(args["worldPositionStays"]);

            Undo.SetTransformParent(go.transform,
                string.IsNullOrEmpty(parentPath) ? null : GameObject.Find(parentPath)?.transform,
                worldPositionStays,
                "Reparent");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "gameObject", go.name },
                { "newParent", string.IsNullOrEmpty(parentPath) ? "root" : parentPath },
                { "worldPositionStays", worldPositionStays },
            };
        }

        // ─── Helpers ───

        private static void EnsureDirectory(string assetPath)
        {
            string dir = Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                string[] parts = dir.Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }
        }

        private static Type FindType(string name)
        {
            Type t = Type.GetType($"UnityEngine.{name}, UnityEngine");
            if (t != null) return t;
            t = Type.GetType($"UnityEngine.{name}, UnityEngine.CoreModule");
            if (t != null) return t;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = assembly.GetType(name);
                if (t != null) return t;
                t = assembly.GetType($"UnityEngine.{name}");
                if (t != null) return t;
            }
            return null;
        }
    }
}
