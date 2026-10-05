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

            // IsPartOfPrefabInstance is the authoritative "is this tied to a prefab?" check.
            // GetPrefabInstanceStatus has known false-negative cases (returns NotAPrefab for
            // valid prefab instances with non-root children, missing nested assets, etc.),
            // so we don't gate on it here.
            if (!PrefabUtility.IsPartOfPrefabInstance(go))
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

            // Same confinement and clobber guard as asset/create-prefab, checked before anything is
            // created: saving over an existing prefab keeps its .meta GUID, so every reference to it
            // would silently re-bind to the new variant.
            if (!MCPAssetSafety.TryResolveProjectPath(variantPath, out string variantFullPath, out string variantPathError))
                return new { error = variantPathError };
            if (!string.Equals(Path.GetExtension(variantPath), ".prefab", StringComparison.OrdinalIgnoreCase))
                return new { error = $"variantPath must end with .prefab, got '{variantPath}'" };
            if (MCPAssetSafety.TryResolveProjectPath(basePath, out string baseFullPath, out _)
                && string.Equals(variantFullPath, baseFullPath, StringComparison.OrdinalIgnoreCase))
                return new { error = "variantPath must differ from basePrefabPath" };
            var overwriteError = MCPAssetSafety.OverwriteGuard(variantPath, args);
            if (overwriteError != null) return overwriteError;

            // Ensure directory
            EnsureDirectory(variantPath);

            // Instantiate, then save as variant. The temporary instance sits in the open scene with no
            // Undo record, so it is destroyed even when the save throws.
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            if (instance == null)
                return new { error = $"Failed to instantiate base prefab '{basePath}'" };

            GameObject variant;
            try
            {
                variant = PrefabUtility.SaveAsPrefabAsset(instance, variantPath);
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to save variant at '{variantPath}': {ex.Message}" };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }

            if (variant == null)
                return new { error = $"Unity did not save a variant at '{variantPath}'" };

            return new Dictionary<string, object>
            {
                { "success", true },
                { "variantPath", variantPath },
                { "basePrefabPath", basePath },
                { "name", variant.name },
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
            // AutomatedAction on purpose: the asset write cannot be undone anyway, and UserAction can
            // open a version-control checkout prompt that would block the request pump.
            PrefabUtility.ApplyPrefabInstance(go, InteractionMode.AutomatedAction);

            return new
            {
                success = true,
                gameObject = go.name,
                appliedTo = assetPath,
                note = "The prefab asset was written directly; Undo cannot restore its previous state.",
            };
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

            // UserAction records a full-hierarchy Undo (and shows no dialog here); AutomatedAction
            // records nothing, so the discarded overrides could never be restored.
            PrefabUtility.RevertPrefabInstance(go, InteractionMode.UserAction);

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

            // UserAction records Undo for the unpack (no dialog on this path), so it can be undone.
            if (completely)
                PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.UserAction);
            else
                PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.OutermostRoot, InteractionMode.UserAction);

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
                    var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
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

            // Shared Inspector-like assignment: converts to a fitting component or sub-asset, checks the
            // field type and reads the stored value back.
            if (!MCPComponentCommands.TryAssignObjectReference(prop, targetRef, out var assigned, out string assignError))
                return new { error = assignError };

            return new Dictionary<string, object>
            {
                { "success", true },
                { "gameObject", go.name },
                { "component", component.GetType().Name },
                { "property", propertyName },
                { "reference", assigned.name },
                { "referenceType", assigned.GetType().Name },
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

            // ProBuilder-safe clone: Object.Instantiate makes the copy's MeshFilter share the SAME
            // runtime mesh the source's ProBuilderMesh owns. Deleting either object later
            // (ProBuilderMesh.OnDestroy destroys that mesh) would blank ALL the copies. Give each
            // cloned ProBuilderMesh its own independent mesh via ProBuilder's MakeUnique() so the
            // duplicate is a fully independent, still-editable object (report A1).
            int pbIsolated = MakeProBuilderClonesIndependent(duplicate);

            Undo.RegisterCreatedObjectUndo(duplicate, $"Duplicate {go.name}");

            var result = new Dictionary<string, object>
            {
                { "success", true },
                { "original", go.name },
                { "duplicate", duplicate.name },
                { "instanceId", MCPObjectId.Get(duplicate) },
            };
            if (pbIsolated > 0) result["proBuilderMeshesIsolated"] = pbIsolated;
            return result;
        }

        /// <summary>
        /// After an Object.Instantiate of a GameObject tree, give every cloned ProBuilderMesh an
        /// independent runtime mesh (ProBuilder's MakeUnique) so the clone no longer shares the
        /// source's mesh. Without this the copies share one mesh, and destroying any one of them
        /// (or the source) blanks the rest — the core of the shared-mesh hazard (report A1).
        /// Returns the count of ProBuilder meshes isolated; a harmless 0 when ProBuilder is absent.
        /// </summary>
        private static int MakeProBuilderClonesIndependent(GameObject clone)
        {
#if PROBUILDER_INSTALLED
            int count = 0;
            foreach (var pb in clone.GetComponentsInChildren<UnityEngine.ProBuilder.ProBuilderMesh>(true))
            {
                pb.MakeUnique();
                pb.ToMesh();
                pb.Refresh();
                UnityEditor.ProBuilder.EditorUtility.SynchronizeWithMeshFilter(pb);
                count++;
            }
            return count;
#else
            return 0;
#endif
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

            string parentPath = args.ContainsKey("newParent") ? args["newParent"]?.ToString() : "";
            bool worldPositionStays = !args.ContainsKey("worldPositionStays") || Convert.ToBoolean(args["worldPositionStays"]);

            // Resolve the new parent before moving anything, with the same lookup as the target (it also
            // finds inactive objects). A non-empty path that does not resolve is an error, never a silent
            // move to the scene root; only an empty newParent means the scene root.
            GameObject newParent = null;
            if (!string.IsNullOrEmpty(parentPath))
            {
                newParent = MCPGameObjectCommands.FindGameObject(new Dictionary<string, object> { { "path", parentPath } });
                if (newParent == null)
                    return new { error = $"Parent GameObject '{parentPath}' not found" };
                if (newParent.transform.IsChildOf(go.transform))
                    return new { error = $"Cannot parent '{go.name}' under itself or one of its descendants ('{parentPath}')" };
            }

            Undo.SetTransformParent(go.transform,
                newParent != null ? newParent.transform : null,
                worldPositionStays,
                "Reparent");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "gameObject", go.name },
                { "newParent", newParent != null ? MCPGameObjectCommands.GetHierarchyPath(newParent) : "root" },
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
