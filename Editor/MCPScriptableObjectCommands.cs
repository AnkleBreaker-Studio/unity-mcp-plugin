using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for creating and managing ScriptableObjects.
    /// </summary>
    public static class MCPScriptableObjectCommands
    {
        // ─── Create ScriptableObject ───

        public static object CreateScriptableObject(Dictionary<string, object> args)
        {
            string typeName = args.ContainsKey("type") ? args["type"].ToString() : "";
            if (string.IsNullOrEmpty(typeName))
                return new { error = "type is required (e.g. 'MyGameSettings' or 'MyNamespace.MyData')" };

            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                path = $"Assets/{typeName}.asset";

            if (!path.EndsWith(".asset"))
                path += ".asset";

            // Find the type
            Type soType = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                soType = assembly.GetType(typeName, false, true);
                if (soType != null) break;
            }

            if (soType == null)
                return new { error = $"Type '{typeName}' not found. Make sure the script is compiled." };

            if (!typeof(ScriptableObject).IsAssignableFrom(soType))
                return new { error = $"Type '{typeName}' is not a ScriptableObject" };

            // Ensure directory
            string dir = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
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

            var so = ScriptableObject.CreateInstance(soType);
            AssetDatabase.CreateAsset(so, path);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "type", soType.FullName },
            };
        }

        // ─── Get ScriptableObject Info ───

        public static object GetScriptableObjectInfo(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (so == null)
                return new { error = $"ScriptableObject not found at '{path}'" };

            var serialized = new SerializedObject(so);
            var properties = new List<Dictionary<string, object>>();

            var prop = serialized.GetIterator();
            if (prop.NextVisible(true))
            {
                do
                {
                    if (prop.name == "m_Script") continue;

                    properties.Add(new Dictionary<string, object>
                    {
                        { "name", prop.name },
                        { "displayName", prop.displayName },
                        { "type", prop.propertyType.ToString() },
                        { "value", GetPropertyValue(prop) },
                        { "isArray", prop.isArray },
                        { "depth", prop.depth },
                    });
                } while (prop.NextVisible(false));
            }

            return new Dictionary<string, object>
            {
                { "path", path },
                { "type", so.GetType().FullName },
                { "name", so.name },
                { "properties", properties },
            };
        }

        // ─── Set ScriptableObject Field ───

        public static object SetScriptableObjectField(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            string fieldName = args.ContainsKey("field") ? args["field"].ToString() : "";
            if (string.IsNullOrEmpty(fieldName))
                return new { error = "field is required" };

            if (!args.ContainsKey("value"))
                return new { error = "value is required" };

            var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (so == null)
                return new { error = $"ScriptableObject not found at '{path}'" };

            var serialized = new SerializedObject(so);
            var prop = serialized.FindProperty(fieldName);
            if (prop == null)
                return new { error = $"Property '{fieldName}' not found on {so.GetType().Name}" };

            Undo.RecordObject(so, "Set SO Field");

            bool success = SetPropertyValue(prop, args["value"]);
            if (!success)
                return new { error = $"Failed to set property '{fieldName}' of type {prop.propertyType}" };

            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(so);
            AssetDatabase.SaveAssets();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "field", fieldName },
                { "value", GetPropertyValue(prop) },
            };
        }

        // ─── List ScriptableObject Types ───

        public static object ListScriptableObjectTypes(Dictionary<string, object> args)
        {
            string filter = args.ContainsKey("filter") ? args["filter"].ToString() : "";
            bool projectOnly = !args.ContainsKey("includeEngine") || !Convert.ToBoolean(args["includeEngine"]);

            var types = new List<Dictionary<string, object>>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (projectOnly)
                {
                    string asmName = assembly.GetName().Name;
                    if (asmName.StartsWith("Unity") || asmName.StartsWith("System") ||
                        asmName.StartsWith("mscorlib") || asmName.StartsWith("netstandard") ||
                        asmName.StartsWith("Mono") || asmName.StartsWith("nunit"))
                        continue;
                }

                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (!type.IsAbstract && typeof(ScriptableObject).IsAssignableFrom(type) &&
                            !type.IsGenericType && type.IsPublic)
                        {
                            if (!string.IsNullOrEmpty(filter) &&
                                type.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;

                            types.Add(new Dictionary<string, object>
                            {
                                { "name", type.Name },
                                { "fullName", type.FullName },
                                { "assembly", assembly.GetName().Name },
                            });
                        }
                    }
                }
                catch { } // Skip assemblies that can't be reflected
            }

            return new Dictionary<string, object>
            {
                { "count", types.Count },
                { "types", types.OrderBy(t => t["name"].ToString()).ToList() },
            };
        }

        // ─── Helpers ───

        private static object GetPropertyValue(SerializedProperty prop)
        {
            switch (prop.propertyType)
            {
                case SerializedPropertyType.Integer: return prop.intValue;
                case SerializedPropertyType.Boolean: return prop.boolValue;
                case SerializedPropertyType.Float: return prop.floatValue;
                case SerializedPropertyType.String: return prop.stringValue;
                case SerializedPropertyType.Enum: return prop.enumNames[prop.enumValueIndex];
                case SerializedPropertyType.ObjectReference:
                    return prop.objectReferenceValue != null ? prop.objectReferenceValue.name : null;
                case SerializedPropertyType.Vector2:
                    return $"({prop.vector2Value.x}, {prop.vector2Value.y})";
                case SerializedPropertyType.Vector3:
                    return $"({prop.vector3Value.x}, {prop.vector3Value.y}, {prop.vector3Value.z})";
                case SerializedPropertyType.Color:
                    return $"({prop.colorValue.r}, {prop.colorValue.g}, {prop.colorValue.b}, {prop.colorValue.a})";
                default: return prop.propertyType.ToString();
            }
        }

        private static bool SetPropertyValue(SerializedProperty prop, object value)
        {
            try
            {
                switch (prop.propertyType)
                {
                    case SerializedPropertyType.Integer:
                        prop.intValue = Convert.ToInt32(value);
                        return true;
                    case SerializedPropertyType.Boolean:
                        prop.boolValue = Convert.ToBoolean(value);
                        return true;
                    case SerializedPropertyType.Float:
                        prop.floatValue = Convert.ToSingle(value);
                        return true;
                    case SerializedPropertyType.String:
                        prop.stringValue = value.ToString();
                        return true;
                    case SerializedPropertyType.Enum:
                        string enumStr = value.ToString();
                        for (int i = 0; i < prop.enumNames.Length; i++)
                        {
                            if (prop.enumNames[i].Equals(enumStr, StringComparison.OrdinalIgnoreCase))
                            {
                                prop.enumValueIndex = i;
                                return true;
                            }
                        }
                        return false;
                    default:
                        return false;
                }
            }
            catch { return false; }
        }
    }
}
