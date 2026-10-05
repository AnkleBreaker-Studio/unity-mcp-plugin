using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static class MCPComponentCommands
    {
        public static object Add(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            string typeName = args.ContainsKey("componentType") ? args["componentType"].ToString() : "";
            if (string.IsNullOrEmpty(typeName))
                return new { error = "componentType is required" };

            Type type = FindType(typeName);
            if (type == null)
                return new { error = $"Component type '{typeName}' not found" };

            var component = Undo.AddComponent(go, type);
            if (component == null)
                return new { error = $"Failed to add component '{typeName}'" };

            return new Dictionary<string, object>
            {
                { "success", true },
                { "gameObject", go.name },
                { "component", component.GetType().Name },
                { "fullType", component.GetType().FullName },
            };
        }

        public static object Remove(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            string typeName = args.ContainsKey("componentType") ? args["componentType"].ToString() : "";
            Type type = FindType(typeName);
            if (type == null) return new { error = $"Component type '{typeName}' not found" };

            int index = args.ContainsKey("index") ? Convert.ToInt32(args["index"]) : 0;

            var components = go.GetComponents(type);
            if (index < 0 || index >= components.Length)
                return new { error = $"Component index {index} out of range (found {components.Length})" };

            var component = components[index];
            // Undo.DestroyObjectImmediate does not refuse a Transform: Unity swaps in a new one, which
            // breaks every reference to the old one (a RectTransform becomes a plain Transform).
            if (component is Transform)
                return new { error = $"Cannot remove the {component.GetType().Name} of '{go.name}': {DescribeRemovalBlocker(go, component)}; delete the GameObject instead." };

            Undo.DestroyObjectImmediate(component);

            // Unity refuses to destroy a component another one requires: it only logs an error and
            // throws nothing, so check that the component is really gone.
            if (component != null)
                return new { error = $"Unity refused to remove {component.GetType().Name} from '{go.name}': {DescribeRemovalBlocker(go, component)}." };

            return new { success = true, removed = typeName, fromGameObject = go.name };
        }

        /// <summary>Why Unity kept a component it was asked to destroy, for the error message.</summary>
        internal static string DescribeRemovalBlocker(GameObject go, Component component)
        {
            if (component is Transform)
                return "a GameObject's Transform cannot be removed";

            Type type = component.GetType();
            var dependents = new List<string>();
            foreach (var sibling in go.GetComponents<Component>())
            {
                if (sibling == null || sibling == component) continue;
                foreach (RequireComponent require in sibling.GetType().GetCustomAttributes(typeof(RequireComponent), true))
                {
                    if (IsRequiredType(require.m_Type0, type) || IsRequiredType(require.m_Type1, type) || IsRequiredType(require.m_Type2, type))
                    {
                        dependents.Add(sibling.GetType().Name);
                        break;
                    }
                }
            }

            if (dependents.Count > 0)
                return $"{string.Join(", ", dependents)} {(dependents.Count == 1 ? "requires" : "require")} it; remove {(dependents.Count == 1 ? "that component" : "those components")} first";
            return "another component probably depends on it (see the Console)";
        }

        private static bool IsRequiredType(Type required, Type componentType)
        {
            return required != null && required.IsAssignableFrom(componentType);
        }

        public static object GetProperties(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            string typeName = args.ContainsKey("componentType") ? args["componentType"].ToString() : "";
            Type type = FindType(typeName);
            if (type == null) return new { error = $"Component type '{typeName}' not found" };

            var component = go.GetComponent(type);
            if (component == null) return new { error = $"Component '{typeName}' not found on {go.name}" };

            // Use SerializedObject to read properties
            var serialized = new SerializedObject(component);
            var properties = new List<Dictionary<string, object>>();

            var iterator = serialized.GetIterator();
            if (iterator.NextVisible(true))
            {
                do
                {
                    var info = new Dictionary<string, object>
                    {
                        { "name", iterator.name },
                        { "displayName", iterator.displayName },
                        { "type", iterator.propertyType.ToString() },
                        { "value", GetSerializedValue(iterator) },
                        { "editable", iterator.editable },
                    };
                    if (iterator.propertyType == SerializedPropertyType.Enum)
                    {
                        info["enumValue"] = iterator.intValue;
                        info["enumIndex"] = iterator.enumValueIndex;
                        info["enumNames"] = iterator.enumNames;
                    }
                    properties.Add(info);
                } while (iterator.NextVisible(false));
            }

            return new Dictionary<string, object>
            {
                { "gameObject", go.name },
                { "component", typeName },
                { "properties", properties },
            };
        }

        public static object SetProperty(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            string typeName = args.ContainsKey("componentType") ? args["componentType"].ToString() : "";
            string propName = args.ContainsKey("propertyName") ? args["propertyName"].ToString() : "";
            object value = args.ContainsKey("value") ? args["value"] : null;

            Type type = FindType(typeName);
            if (type == null) return new { error = $"Component type '{typeName}' not found" };

            var component = go.GetComponent(type);
            if (component == null) return new { error = $"Component '{typeName}' not found on {go.name}" };

            var serialized = new SerializedObject(component);
            var prop = serialized.FindProperty(propName);
            if (prop == null) return new { error = $"Property '{propName}' not found on {typeName}" };

            try
            {
                SetSerializedValue(prop, value);
                serialized.ApplyModifiedProperties();
                return new { success = true, gameObject = go.name, component = typeName, property = propName };
            }
            catch (Exception ex)
            {
                return new { error = $"Failed to set property: {ex.Message}" };
            }
        }

        // ─── New: Set Object Reference ───

        /// <summary>
        /// Set an object reference property on a component.
        /// Resolves references by: asset path, scene GameObject name/path,
        /// component type on a GameObject, or instanceId.
        /// This is the critical wiring feature for connecting objects together.
        /// </summary>
        public static object SetReference(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found. Provide 'path' or 'instanceId' for the target GameObject." };

            string componentType = args.ContainsKey("componentType") ? args["componentType"].ToString() : "";
            string propertyName = args.ContainsKey("propertyName") ? args["propertyName"].ToString() : "";

            if (string.IsNullOrEmpty(propertyName))
                return new { error = "propertyName is required" };

            // Find the component that has this property
            Component component = null;
            if (!string.IsNullOrEmpty(componentType))
            {
                Type type = FindType(componentType);
                if (type != null) component = go.GetComponent(type);
            }
            else
            {
                // Auto-search all components for this property
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
                return new { error = $"No component on '{go.name}' has property '{propertyName}'" };

            var serialized = new SerializedObject(component);
            var prop = serialized.FindProperty(propertyName);
            if (prop == null)
                return new { error = $"Property '{propertyName}' not found" };

            if (prop.propertyType != SerializedPropertyType.ObjectReference)
                return new { error = $"Property '{propertyName}' is not an ObjectReference (type: {prop.propertyType}). Use component/set-property instead." };

            // Resolve the reference from the various input options
            string assetPath = args.ContainsKey("assetPath") ? args["assetPath"].ToString() : "";
            string gameObjectRef = args.ContainsKey("referenceGameObject") ? args["referenceGameObject"].ToString() : "";
            string componentRef = args.ContainsKey("referenceComponentType") ? args["referenceComponentType"].ToString() : "";
            object refInstanceId = args.ContainsKey("referenceInstanceId") ? args["referenceInstanceId"] : null;
            bool clearRef = args.ContainsKey("clear") && Convert.ToBoolean(args["clear"]);

            UnityEngine.Object targetRef = null;

            if (clearRef)
            {
                // Clear the reference
                prop.objectReferenceValue = null;
                serialized.ApplyModifiedProperties();
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "gameObject", go.name },
                    { "property", propertyName },
                    { "reference", "null (cleared)" },
                };
            }
            else if (!string.IsNullOrEmpty(assetPath))
            {
                // Load from asset path (for prefabs, materials, textures, scriptable objects, etc.)
                targetRef = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                if (targetRef == null)
                    return new { error = $"Asset not found at '{assetPath}'" };
            }
            else if (refInstanceId != null)
            {
                // Find by instance ID
                targetRef = MCPObjectId.ToObject(refInstanceId);
                if (targetRef == null)
                    return new { error = $"No object found with instanceId {refInstanceId}" };
            }
            else if (!string.IsNullOrEmpty(gameObjectRef))
            {
                // Find a scene GameObject
                GameObject refGo = GameObject.Find(gameObjectRef);
                if (refGo == null)
                {
                    // Fallback: search by name in all objects
                    var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
                    foreach (var obj in allObjects)
                    {
                        if (obj.name == gameObjectRef)
                        {
                            refGo = obj;
                            break;
                        }
                    }
                }
                if (refGo == null)
                    return new { error = $"GameObject '{gameObjectRef}' not found in scene" };

                if (!string.IsNullOrEmpty(componentRef))
                {
                    // Get a specific component on the referenced GameObject
                    Type refType = FindType(componentRef);
                    if (refType == null)
                        return new { error = $"Component type '{componentRef}' not found" };

                    var refComp = refGo.GetComponent(refType);
                    if (refComp == null)
                        return new { error = $"Component '{componentRef}' not found on '{refGo.name}'" };

                    targetRef = refComp;
                }
                else
                {
                    targetRef = refGo;
                }
            }
            else
            {
                return new { error = "Provide one of: assetPath, referenceGameObject, referenceInstanceId, or clear=true" };
            }

            if (!TryAssignObjectReference(prop, targetRef, out var assigned, out string assignError))
                return new { error = assignError };

            return new Dictionary<string, object>
            {
                { "success", true },
                { "gameObject", go.name },
                { "component", component.GetType().Name },
                { "property", propertyName },
                { "referenceName", assigned.name },
                { "referenceType", assigned.GetType().Name },
            };
        }

        // ─── New: Batch Wire References ───

        /// <summary>
        /// Wire multiple references at once. Each entry specifies a target GO,
        /// property, and what to wire to it. Ideal for scene setup automation.
        /// </summary>
        public static object BatchWireReferences(Dictionary<string, object> args)
        {
            if (!args.ContainsKey("references"))
                return new { error = "references array is required" };

            var refList = args["references"] as List<object>;
            if (refList == null || refList.Count == 0)
                return new { error = "references must be a non-empty array" };

            var results = new List<Dictionary<string, object>>();
            int successCount = 0;
            int errorCount = 0;

            // Inherit parent-level path/instanceId/componentType into each ref entry
            string[] inheritKeys = { "path", "instanceId", "componentType" };

            foreach (var item in refList)
            {
                var refArgs = item as Dictionary<string, object>;
                if (refArgs == null)
                {
                    results.Add(new Dictionary<string, object> { { "error", "Invalid reference entry" } });
                    errorCount++;
                    continue;
                }

                // Merge parent keys into each ref if not already specified
                foreach (var key in inheritKeys)
                {
                    if (!refArgs.ContainsKey(key) && args.ContainsKey(key))
                        refArgs[key] = args[key];
                }

                var result = SetReference(refArgs);
                if (result is Dictionary<string, object> dict && dict.ContainsKey("success"))
                {
                    results.Add(dict);
                    successCount++;
                }
                else
                {
                    // Convert anonymous type to dict for error entries
                    var errDict = new Dictionary<string, object>();
                    foreach (var prop in result.GetType().GetProperties())
                        errDict[prop.Name] = prop.GetValue(result);
                    results.Add(errDict);
                    errorCount++;
                }
            }

            return new Dictionary<string, object>
            {
                { "success", errorCount == 0 },
                { "total", refList.Count },
                { "succeeded", successCount },
                { "failed", errorCount },
                { "results", results },
            };
        }

        // ─── New: Get Referenceable Objects ───

        /// <summary>
        /// Get all objects that can be assigned to a specific ObjectReference property.
        /// Helps agents know what's available to wire up.
        /// </summary>
        public static object GetReferenceableObjects(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            string componentType = args.ContainsKey("componentType") ? args["componentType"].ToString() : "";
            string propertyName = args.ContainsKey("propertyName") ? args["propertyName"].ToString() : "";

            if (string.IsNullOrEmpty(propertyName))
                return new { error = "propertyName is required" };

            // Find the property
            Component component = null;
            if (!string.IsNullOrEmpty(componentType))
            {
                Type type = FindType(componentType);
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
                return new { error = $"No component on '{go.name}' has property '{propertyName}'" };

            var serialized = new SerializedObject(component);
            var prop = serialized.FindProperty(propertyName);
            if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference)
                return new { error = $"Property '{propertyName}' is not an ObjectReference" };

            // Get the expected type from reflection
            var fieldInfo = component.GetType().GetField(propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Type expectedType = typeof(UnityEngine.Object);
            if (fieldInfo != null) expectedType = fieldInfo.FieldType;

            // Gather scene objects of matching type
            var sceneObjects = new List<Dictionary<string, object>>();
            if (typeof(Component).IsAssignableFrom(expectedType))
            {
                var found = UnityEngine.Object.FindObjectsByType(expectedType, FindObjectsSortMode.None);
                foreach (var obj in found)
                {
                    var comp = obj as Component;
                    if (comp == null) continue;
                    sceneObjects.Add(new Dictionary<string, object>
                    {
                        { "name", comp.gameObject.name },
                        { "type", comp.GetType().Name },
                        { "instanceId", MCPObjectId.Get(comp) },
                        { "path", GetGameObjectPath(comp.gameObject) },
                    });
                    if (sceneObjects.Count >= 50) break; // Limit results
                }
            }
            else if (typeof(GameObject).IsAssignableFrom(expectedType))
            {
                var found = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
                foreach (var obj in found)
                {
                    sceneObjects.Add(new Dictionary<string, object>
                    {
                        { "name", obj.name },
                        { "type", "GameObject" },
                        { "instanceId", MCPObjectId.Get(obj) },
                        { "path", GetGameObjectPath(obj) },
                    });
                    if (sceneObjects.Count >= 50) break;
                }
            }

            // Gather assets of matching type
            var assetResults = new List<Dictionary<string, object>>();
            if (!typeof(Component).IsAssignableFrom(expectedType) || typeof(MonoBehaviour).IsAssignableFrom(expectedType))
            {
                string[] guids = AssetDatabase.FindAssets($"t:{expectedType.Name}");
                foreach (var guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    assetResults.Add(new Dictionary<string, object>
                    {
                        { "assetPath", path },
                        { "name", System.IO.Path.GetFileNameWithoutExtension(path) },
                    });
                    if (assetResults.Count >= 50) break;
                }
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "gameObject", go.name },
                { "property", propertyName },
                { "expectedType", expectedType.Name },
                { "currentValue", prop.objectReferenceValue != null ? prop.objectReferenceValue.name : null },
                { "sceneObjects", sceneObjects },
                { "assets", assetResults },
            };
        }

        // ─── Helpers ───

        // FindType results by exact input name, null (not found) included. Static state resets on
        // domain reload; a newly loaded assembly (e.g. an execute-code compilation) can change what
        // a name resolves to, so AssemblyLoad clears the cache and bumps the generation.
        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();
        private static readonly object _typeCacheLock = new object();
        private static int _typeCacheGeneration;

        static MCPComponentCommands()
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            lock (_typeCacheLock)
            {
                _typeCache.Clear();
                _typeCacheGeneration++;
            }
        }

        internal static Type FindType(string name)
        {
            if (name == null) return ResolveType(name);

            int generation;
            lock (_typeCacheLock)
            {
                if (_typeCache.TryGetValue(name, out var cached)) return cached;
                generation = _typeCacheGeneration;
            }

            Type resolved = ResolveType(name);
            lock (_typeCacheLock)
            {
                // An assembly loaded during the scan may make this result stale: do not keep it.
                if (generation == _typeCacheGeneration) _typeCache[name] = resolved;
            }
            return resolved;
        }

        private static Type ResolveType(string name)
        {
            // Try common Unity types
            Type t = Type.GetType($"UnityEngine.{name}, UnityEngine");
            if (t != null) return t;

            t = Type.GetType($"UnityEngine.{name}, UnityEngine.CoreModule");
            if (t != null) return t;

            t = Type.GetType($"UnityEngine.{name}, UnityEngine.PhysicsModule");
            if (t != null) return t;

            t = Type.GetType($"UnityEngine.{name}, UnityEngine.AudioModule");
            if (t != null) return t;

            t = Type.GetType($"UnityEngine.UI.{name}, UnityEngine.UI");
            if (t != null) return t;

            // Search all loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = assembly.GetType(name);
                if (t != null) return t;

                // Try with UnityEngine prefix
                t = assembly.GetType($"UnityEngine.{name}");
                if (t != null) return t;
            }

            // Fallback: search by short class name across all assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.Name == name)
                            return type;
                    }
                }
                catch { }
            }

            return null;
        }

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

        internal static object GetSerializedValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: return prop.intValue;
                case SerializedPropertyType.Boolean: return prop.boolValue;
                case SerializedPropertyType.Float: return prop.floatValue;
                case SerializedPropertyType.String: return prop.stringValue;
                case SerializedPropertyType.Color:
                    var c = prop.colorValue;
                    return new Dictionary<string, object> { { "r", c.r }, { "g", c.g }, { "b", c.b }, { "a", c.a } };
                case SerializedPropertyType.Vector2:
                    var v2 = prop.vector2Value;
                    return new Dictionary<string, object> { { "x", v2.x }, { "y", v2.y } };
                case SerializedPropertyType.Vector3:
                    var v3 = prop.vector3Value;
                    return new Dictionary<string, object> { { "x", v3.x }, { "y", v3.y }, { "z", v3.z } };
                case SerializedPropertyType.Vector4:
                    var v4 = prop.vector4Value;
                    return new Dictionary<string, object> { { "x", v4.x }, { "y", v4.y }, { "z", v4.z }, { "w", v4.w } };
                case SerializedPropertyType.Enum:
                    // Combined flags and unknown serialized values have no named index, but remain valid stored values.
                    int enumIndex = prop.enumValueIndex;
                    return enumIndex >= 0 && enumIndex < prop.enumNames.Length
                        ? (object)prop.enumNames[enumIndex] : prop.intValue;
                case SerializedPropertyType.ObjectReference:
                    if (prop.objectReferenceValue != null)
                    {
                        var refObj = prop.objectReferenceValue;
                        var info = new Dictionary<string, object>
                        {
                            { "name", refObj.name },
                            { "type", refObj.GetType().Name },
                            { "instanceId", MCPObjectId.Get(refObj) },
                        };
                        // Add asset path for project assets
                        string assetPath = AssetDatabase.GetAssetPath(refObj);
                        if (!string.IsNullOrEmpty(assetPath))
                            info["assetPath"] = assetPath;
                        // Add GameObject path for scene objects
                        if (refObj is GameObject refGo)
                            info["path"] = GetGameObjectPath(refGo);
                        else if (refObj is Component refComp)
                            info["path"] = GetGameObjectPath(refComp.gameObject);
                        return info;
                    }
                    return null;
                case SerializedPropertyType.LayerMask:
                    return prop.intValue;
                case SerializedPropertyType.Quaternion:
                    var q = prop.quaternionValue;
                    return new Dictionary<string, object> { { "x", q.x }, { "y", q.y }, { "z", q.z }, { "w", q.w } };
                case SerializedPropertyType.Rect:
                    var r = prop.rectValue;
                    return new Dictionary<string, object> { { "x", r.x }, { "y", r.y }, { "width", r.width }, { "height", r.height } };
                case SerializedPropertyType.Bounds:
                    var b = prop.boundsValue;
                    return new Dictionary<string, object>
                    {
                        { "center", new Dictionary<string, object> { { "x", b.center.x }, { "y", b.center.y }, { "z", b.center.z } } },
                        { "size", new Dictionary<string, object> { { "x", b.size.x }, { "y", b.size.y }, { "z", b.size.z } } },
                    };
                default:
                    return prop.propertyType.ToString();
            }
        }

        /// <summary>Short human description of a value's shape for error messages.</summary>
        private static string DescribeValue(object value)
        {
            if (value == null) return "null";
            if (value is string s) return $"string \"{s}\"";
            if (value is System.Collections.IList) return "an array";
            return $"{value.GetType().Name} ({value})";
        }

        internal static void SetSerializedValue(SerializedProperty prop, object value)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer:
                    prop.intValue = Convert.ToInt32(value);
                    break;
                case SerializedPropertyType.Boolean:
                    prop.boolValue = Convert.ToBoolean(value);
                    break;
                case SerializedPropertyType.Float:
                    prop.floatValue = Convert.ToSingle(value);
                    break;
                case SerializedPropertyType.String:
                    prop.stringValue = value?.ToString() ?? "";
                    break;
                case SerializedPropertyType.Color:
                    var cd = value as Dictionary<string, object>;
                    if (cd == null)
                        throw new ArgumentException($"Color property '{prop.name}' expects an object {{r,g,b,a}}, got {DescribeValue(value)}.");
                    prop.colorValue = new Color(
                        Convert.ToSingle(cd.GetValueOrDefault("r", 0f)),
                        Convert.ToSingle(cd.GetValueOrDefault("g", 0f)),
                        Convert.ToSingle(cd.GetValueOrDefault("b", 0f)),
                        Convert.ToSingle(cd.GetValueOrDefault("a", 1f)));
                    break;
                case SerializedPropertyType.Vector2:
                    var v2d = value as Dictionary<string, object>;
                    if (v2d == null)
                        throw new ArgumentException($"Vector2 property '{prop.name}' expects an object {{x,y}}, got {DescribeValue(value)}.");
                    prop.vector2Value = new Vector2(
                        Convert.ToSingle(v2d.GetValueOrDefault("x", 0f)),
                        Convert.ToSingle(v2d.GetValueOrDefault("y", 0f)));
                    break;
                case SerializedPropertyType.Vector3:
                    var vd = value as Dictionary<string, object>;
                    if (vd == null)
                        throw new ArgumentException($"Vector3 property '{prop.name}' expects an object {{x,y,z}}, got {DescribeValue(value)}.");
                    prop.vector3Value = new Vector3(
                        Convert.ToSingle(vd.GetValueOrDefault("x", 0f)),
                        Convert.ToSingle(vd.GetValueOrDefault("y", 0f)),
                        Convert.ToSingle(vd.GetValueOrDefault("z", 0f)));
                    break;
                case SerializedPropertyType.Vector4:
                    var v4d = value as Dictionary<string, object>;
                    if (v4d == null)
                        throw new ArgumentException($"Vector4 property '{prop.name}' expects an object {{x,y,z,w}}, got {DescribeValue(value)}.");
                    prop.vector4Value = new Vector4(
                        Convert.ToSingle(v4d.GetValueOrDefault("x", 0f)),
                        Convert.ToSingle(v4d.GetValueOrDefault("y", 0f)),
                        Convert.ToSingle(v4d.GetValueOrDefault("z", 0f)),
                        Convert.ToSingle(v4d.GetValueOrDefault("w", 0f)));
                    break;
                case SerializedPropertyType.Enum:
                    if (value is Dictionary<string, object> enumValue && enumValue.TryGetValue("enumValue", out var rawValue))
                    {
                        prop.intValue = ReadEnumInteger(rawValue, prop.name);
                    }
                    else if (value is string enumName)
                    {
                        int index = Array.IndexOf(prop.enumNames, enumName);
                        if (index < 0) throw new ArgumentException($"Unknown enum name '{enumName}' for '{prop.name}'. Expected: {string.Join(", ", prop.enumNames)}.");
                        prop.enumValueIndex = index;
                    }
                    else
                    {
                        int index = ReadEnumInteger(value, prop.name);
                        if (index < 0 || index >= prop.enumNames.Length)
                            throw new ArgumentOutOfRangeException(prop.name, $"Enum index must be between 0 and {prop.enumNames.Length - 1}. Use {{enumValue:...}} for a stored value or combined flags.");
                        prop.enumValueIndex = index;
                    }
                    break;
                case SerializedPropertyType.LayerMask:
                    prop.intValue = Convert.ToInt32(value);
                    break;
                case SerializedPropertyType.Rect:
                    var rd = value as Dictionary<string, object>;
                    if (rd == null)
                        throw new ArgumentException($"Rect property '{prop.name}' expects an object {{x,y,width,height}}, got {DescribeValue(value)}.");
                    prop.rectValue = new Rect(
                        Convert.ToSingle(rd.GetValueOrDefault("x", 0f)),
                        Convert.ToSingle(rd.GetValueOrDefault("y", 0f)),
                        Convert.ToSingle(rd.GetValueOrDefault("width", 0f)),
                        Convert.ToSingle(rd.GetValueOrDefault("height", 0f)));
                    break;
                case SerializedPropertyType.ObjectReference:
                    var reference = ResolveObjectReference(value);
                    if (reference == null)
                        prop.objectReferenceValue = null;
                    else if (!TryAssignObjectReference(prop, reference, out _, out string referenceError))
                        throw new InvalidOperationException(referenceError);
                    break;
                default:
                    throw new NotSupportedException($"Cannot set property type: {prop.propertyType}");
            }
        }

        private static int ReadEnumInteger(object value, string property)
        {
            if (value == null || value is bool || value is string)
                throw new ArgumentException($"Enum property '{property}' expects an integer value.");
            double number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            if (double.IsNaN(number) || double.IsInfinity(number) || number != Math.Truncate(number)
                || number < int.MinValue || number > int.MaxValue)
                throw new ArgumentException($"Enum property '{property}' expects a 32-bit integer value.");
            return (int)number;
        }

        /// <summary>
        /// Resolve an ObjectReference value from various input formats:
        /// - null or empty string → null (clear reference)
        /// - Dictionary with assetPath, instanceId, or gameObject keys
        /// - JSON string that parses to a dictionary (e.g. from MCP tool params)
        /// - Plain string → try as asset path, then scene hierarchy path, then GameObject.Find
        /// </summary>
        internal static UnityEngine.Object ResolveObjectReference(object value)
        {
            // Null / empty → clear
            if (value == null) return null;
            if (value is string s && string.IsNullOrEmpty(s)) return null;

            // Already a dictionary
            var dict = value as Dictionary<string, object>;

            // String that looks like JSON → try to parse as dictionary
            if (dict == null && value is string jsonStr && jsonStr.TrimStart().StartsWith("{"))
            {
                try
                {
                    dict = MiniJson.Deserialize(jsonStr) as Dictionary<string, object>;
                }
                catch { /* not valid JSON, fall through to string handling */ }
            }

            // Dictionary-based resolution
            if (dict != null)
            {
                UnityEngine.Object resolved = null;

                if (dict.ContainsKey("assetPath"))
                {
                    resolved = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(dict["assetPath"].ToString());
                }
                else if (dict.ContainsKey("instanceId"))
                {
                    resolved = MCPObjectId.ToObject(dict["instanceId"]);
                }
                else if (dict.ContainsKey("gameObject") || dict.ContainsKey("path"))
                {
                    string goPath = dict.ContainsKey("path") ? dict["path"].ToString() : dict["gameObject"].ToString();
                    var refGo = GameObject.Find(goPath);
                    if (refGo != null && dict.ContainsKey("componentType"))
                    {
                        Type ct = FindType(dict["componentType"].ToString());
                        if (ct != null) resolved = refGo.GetComponent(ct);
                    }
                    else
                    {
                        resolved = refGo;
                    }
                }

                if (resolved == null)
                    throw new InvalidOperationException("Could not resolve object reference from dict. Provide assetPath, instanceId, path, or gameObject.");
                return resolved;
            }

            // Plain string — try asset path, then hierarchy path, then name search
            if (value is string strVal)
            {
                // Asset path (starts with Assets/)
                if (strVal.StartsWith("Assets/"))
                {
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(strVal);
                    if (asset != null) return asset;
                }

                // Scene hierarchy path or name via GameObject.Find
                var foundGo = GameObject.Find(strVal);
                if (foundGo != null) return foundGo;

                // Last resort: search all root objects for partial match
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                foreach (var root in scene.GetRootGameObjects())
                {
                    var found = root.transform.Find(strVal);
                    if (found != null) return found.gameObject;
                }

                throw new InvalidOperationException($"Could not resolve '{strVal}' as asset path or scene object.");
            }

            throw new NotSupportedException($"ObjectReference value must be a string (path/name) or dict with assetPath/instanceId/gameObject/path.");
        }

        // ─── Object reference assignment ───

        /// <summary>
        /// Assign <paramref name="candidate"/> to an ObjectReference property the way the Inspector's
        /// object field does. SerializedProperty.objectReferenceValue stores whatever it is given, so
        /// the field's expected type is checked first: a GameObject going into a Component-typed field
        /// becomes its first matching component, and a main asset that does not fit is replaced by its
        /// first matching sub-asset (a .png gives its Sprite). The change is applied, then read back.
        /// Returns false with an error, without assigning, when nothing compatible exists, or when
        /// Unity did not store the object. When the expected type cannot be determined the candidate
        /// is assigned as given, still with the read-back check.
        /// </summary>
        internal static bool TryAssignObjectReference(SerializedProperty prop, UnityEngine.Object candidate,
            out UnityEngine.Object assigned, out string error)
        {
            assigned = null;
            error = null;
            if (candidate == null)
            {
                error = $"No object to assign to '{prop.propertyPath}'.";
                return false;
            }

            var target = FindCompatibleReference(prop, candidate, out string mismatch);
            if (target == null)
            {
                error = $"Cannot assign '{candidate.name}' ({candidate.GetType().Name}) to '{prop.propertyPath}': {mismatch}.";
                return false;
            }

            var serialized = prop.serializedObject;
            prop.objectReferenceValue = target;
            serialized.ApplyModifiedProperties();
            serialized.Update();
            var stored = serialized.FindProperty(prop.propertyPath)?.objectReferenceValue;
            if (stored != target)
            {
                string holds = stored != null ? $"'{stored.name}' ({stored.GetType().Name})" : "null";
                error = $"Unity did not store '{target.name}' ({target.GetType().Name}) in '{prop.propertyPath}' ({prop.type}); it holds {holds}.";
                return false;
            }

            assigned = target;
            return true;
        }

        /// <summary>
        /// The candidate itself, one of its components, or one of its sub-assets that fits the
        /// property's expected type; the candidate unchanged when that type is unknown; null (with
        /// <paramref name="mismatch"/> set) when nothing fits.
        /// </summary>
        private static UnityEngine.Object FindCompatibleReference(SerializedProperty prop, UnityEngine.Object candidate, out string mismatch)
        {
            mismatch = null;
            string expectedName = GetReferenceTypeName(prop);
            Type declaredType = expectedName != null ? GetDeclaredReferenceType(prop, expectedName) : null;
            if (declaredType == null && !IsKnownObjectTypeName(expectedName))
                return candidate;

            bool Fits(UnityEngine.Object obj) => obj != null && (declaredType != null
                ? declaredType.IsInstanceOfType(obj)
                : InheritsFromTypeName(obj.GetType(), expectedName));

            if (Fits(candidate)) return candidate;

            var searched = new List<string> { "the object itself" };
            if (candidate is GameObject candidateGo)
            {
                searched.Add("its components");
                foreach (var comp in candidateGo.GetComponents<Component>())
                    if (Fits(comp)) return comp;
            }

            // Sub-assets proper only: a prefab's child objects and components are not substitutes.
            string assetPath = AssetDatabase.IsMainAsset(candidate) ? AssetDatabase.GetAssetPath(candidate) : null;
            if (!string.IsNullOrEmpty(assetPath))
            {
                searched.Add($"the sub-assets of '{assetPath}'");
                foreach (var subAsset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                    if (!(subAsset is GameObject) && !(subAsset is Component) && Fits(subAsset)) return subAsset;
            }

            mismatch = $"it expects {expectedName} ({prop.type}); checked {string.Join(", ", searched)}";
            return null;
        }

        /// <summary>
        /// Class name inside SerializedProperty.type: "PPtr&lt;$Enemy&gt;" (script field) gives
        /// "Enemy", "PPtr&lt;Sprite&gt;" (native field) gives "Sprite". Null when it cannot be read,
        /// and for native "PPtr&lt;MonoBehaviour&gt;", which also holds ScriptableObjects.
        /// </summary>
        private static string GetReferenceTypeName(SerializedProperty prop)
        {
            string type = prop.type;
            if (type == null || !type.StartsWith("PPtr<", StringComparison.Ordinal) || !type.EndsWith(">", StringComparison.Ordinal))
                return null;
            string inner = type.Substring(5, type.Length - 6);
            if (inner == "MonoBehaviour") return null;
            string name = inner.TrimStart('$');
            return name.Length > 0 ? name : null;
        }

        /// <summary>
        /// Declared type of a top-level script field (exact, namespace-aware). Null for nested fields,
        /// array elements and native properties, or when it disagrees with the PPtr class name.
        /// </summary>
        private static Type GetDeclaredReferenceType(SerializedProperty prop, string typeName)
        {
            string path = prop.propertyPath;
            var target = prop.serializedObject.targetObject;
            if (target == null || path.IndexOf('.') >= 0) return null;

            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(path, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field == null) continue;
                var fieldType = field.FieldType;
                return fieldType.Name == typeName && typeof(UnityEngine.Object).IsAssignableFrom(fieldType) ? fieldType : null;
            }
            return null;
        }

        /// <summary>
        /// True when a loaded UnityEngine.Object class has this name, so the name can be checked
        /// against the candidate's class chain. A native-only name skips the check.
        /// </summary>
        private static bool IsKnownObjectTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            var type = FindType(typeName);
            return type != null && typeof(UnityEngine.Object).IsAssignableFrom(type);
        }

        private static bool InheritsFromTypeName(Type type, string typeName)
        {
            for (var t = type; t != null; t = t.BaseType)
                if (t.Name == typeName) return true;
            return false;
        }
    }
}
