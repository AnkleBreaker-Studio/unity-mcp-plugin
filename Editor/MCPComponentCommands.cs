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
            if (index >= components.Length)
                return new { error = $"Component index {index} out of range (found {components.Length})" };

            Undo.DestroyObjectImmediate(components[index]);
            return new { success = true, removed = typeName, fromGameObject = go.name };
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
                    properties.Add(new Dictionary<string, object>
                    {
                        { "name", iterator.name },
                        { "displayName", iterator.displayName },
                        { "type", iterator.propertyType.ToString() },
                        { "value", GetSerializedValue(iterator) },
                        { "editable", iterator.editable },
                    });
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

        // ─── Helpers ───

        private static Type FindType(string name)
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

            return null;
        }

        private static object GetSerializedValue(SerializedProperty prop)
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
                    return prop.enumNames.Length > prop.enumValueIndex ? prop.enumNames[prop.enumValueIndex] : prop.enumValueIndex.ToString();
                case SerializedPropertyType.ObjectReference:
                    return prop.objectReferenceValue != null ? prop.objectReferenceValue.name : null;
                default:
                    return prop.propertyType.ToString();
            }
        }

        private static void SetSerializedValue(SerializedProperty prop, object value)
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
                    if (cd != null)
                        prop.colorValue = new Color(
                            Convert.ToSingle(cd.GetValueOrDefault("r", 0f)),
                            Convert.ToSingle(cd.GetValueOrDefault("g", 0f)),
                            Convert.ToSingle(cd.GetValueOrDefault("b", 0f)),
                            Convert.ToSingle(cd.GetValueOrDefault("a", 1f)));
                    break;
                case SerializedPropertyType.Vector3:
                    var vd = value as Dictionary<string, object>;
                    if (vd != null)
                        prop.vector3Value = new Vector3(
                            Convert.ToSingle(vd.GetValueOrDefault("x", 0f)),
                            Convert.ToSingle(vd.GetValueOrDefault("y", 0f)),
                            Convert.ToSingle(vd.GetValueOrDefault("z", 0f)));
                    break;
                case SerializedPropertyType.Enum:
                    if (value is string enumName)
                    {
                        int index = Array.IndexOf(prop.enumNames, enumName);
                        if (index >= 0) prop.enumValueIndex = index;
                    }
                    else
                    {
                        prop.enumValueIndex = Convert.ToInt32(value);
                    }
                    break;
                default:
                    throw new NotSupportedException($"Cannot set property type: {prop.propertyType}");
            }
        }
    }
}
