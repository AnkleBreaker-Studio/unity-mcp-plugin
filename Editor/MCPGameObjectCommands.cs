using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static class MCPGameObjectCommands
    {
        public static object Create(Dictionary<string, object> args)
        {
            string name = args.ContainsKey("name") ? args["name"].ToString() : "New GameObject";
            string primitiveType = args.ContainsKey("primitiveType") ? args["primitiveType"].ToString() : "Empty";

            GameObject go;
            if (primitiveType == "Empty" || string.IsNullOrEmpty(primitiveType))
            {
                go = new GameObject(name);
            }
            else if (Enum.TryParse<PrimitiveType>(primitiveType, out var pType))
            {
                go = GameObject.CreatePrimitive(pType);
                go.name = name;
            }
            else
            {
                return new { error = $"Unknown primitive type: {primitiveType}" };
            }

            // Set parent
            if (args.ContainsKey("parent"))
            {
                var parent = GameObject.Find(args["parent"].ToString());
                if (parent != null) go.transform.SetParent(parent.transform);
            }

            // Set transform
            if (args.ContainsKey("position"))
                go.transform.position = DictToVector3(args["position"] as Dictionary<string, object>);
            if (args.ContainsKey("rotation"))
                go.transform.eulerAngles = DictToVector3(args["rotation"] as Dictionary<string, object>);
            if (args.ContainsKey("scale"))
                go.transform.localScale = DictToVector3(args["scale"] as Dictionary<string, object>);

            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", go.name },
                { "instanceId", go.GetInstanceID() },
                { "position", Vector3ToDict(go.transform.position) },
            };
        }

        public static object Delete(Dictionary<string, object> args)
        {
            var go = FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            string name = go.name;
            Undo.DestroyObjectImmediate(go);
            return new { success = true, deleted = name };
        }

        public static object GetInfo(Dictionary<string, object> args)
        {
            var go = FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            var components = new List<Dictionary<string, object>>();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                components.Add(new Dictionary<string, object>
                {
                    { "type", comp.GetType().Name },
                    { "fullType", comp.GetType().FullName },
                    { "enabled", comp is Behaviour b ? (object)b.enabled : true },
                });
            }

            var children = new List<string>();
            for (int i = 0; i < go.transform.childCount; i++)
                children.Add(go.transform.GetChild(i).name);

            return new Dictionary<string, object>
            {
                { "name", go.name },
                { "instanceId", go.GetInstanceID() },
                { "active", go.activeSelf },
                { "activeInHierarchy", go.activeInHierarchy },
                { "isStatic", go.isStatic },
                { "tag", go.tag },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "layerIndex", go.layer },
                { "position", Vector3ToDict(go.transform.position) },
                { "localPosition", Vector3ToDict(go.transform.localPosition) },
                { "rotation", Vector3ToDict(go.transform.eulerAngles) },
                { "localRotation", Vector3ToDict(go.transform.localEulerAngles) },
                { "scale", Vector3ToDict(go.transform.localScale) },
                { "lossyScale", Vector3ToDict(go.transform.lossyScale) },
                { "components", components },
                { "children", children },
                { "childCount", go.transform.childCount },
                { "parent", go.transform.parent != null ? go.transform.parent.name : null },
                { "hierarchyPath", GetHierarchyPath(go) },
            };
        }

        public static object SetTransform(Dictionary<string, object> args)
        {
            var go = FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            bool local = args.ContainsKey("local") && (bool)args["local"];
            Undo.RecordObject(go.transform, "Set Transform");

            if (args.ContainsKey("position"))
            {
                var v = DictToVector3(args["position"] as Dictionary<string, object>);
                if (local) go.transform.localPosition = v;
                else go.transform.position = v;
            }

            if (args.ContainsKey("rotation"))
            {
                var v = DictToVector3(args["rotation"] as Dictionary<string, object>);
                if (local) go.transform.localEulerAngles = v;
                else go.transform.eulerAngles = v;
            }

            if (args.ContainsKey("scale"))
            {
                go.transform.localScale = DictToVector3(args["scale"] as Dictionary<string, object>);
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", go.name },
                { "position", Vector3ToDict(go.transform.position) },
                { "rotation", Vector3ToDict(go.transform.eulerAngles) },
                { "scale", Vector3ToDict(go.transform.localScale) },
            };
        }

        // ─── Helpers ───

        public static GameObject FindGameObject(Dictionary<string, object> args)
        {
            if (args.ContainsKey("instanceId"))
            {
                int id = Convert.ToInt32(args["instanceId"]);
                return EditorUtility.InstanceIDToObject(id) as GameObject;
            }

            if (args.ContainsKey("path") || args.ContainsKey("gameObjectPath"))
            {
                string path = args.ContainsKey("path") ? args["path"].ToString() : args["gameObjectPath"].ToString();
                // Try direct find first
                var go = GameObject.Find(path);
                if (go != null) return go;

                // Try searching by name if full path fails
                var allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>();
                foreach (var obj in allObjects)
                {
                    if (obj.name == path || GetHierarchyPath(obj) == path)
                        return obj;
                }
            }

            return null;
        }

        public static string GetHierarchyPath(GameObject go)
        {
            string path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        public static Vector3 DictToVector3(Dictionary<string, object> dict)
        {
            if (dict == null) return Vector3.zero;
            float x = dict.ContainsKey("x") ? Convert.ToSingle(dict["x"]) : 0;
            float y = dict.ContainsKey("y") ? Convert.ToSingle(dict["y"]) : 0;
            float z = dict.ContainsKey("z") ? Convert.ToSingle(dict["z"]) : 0;
            return new Vector3(x, y, z);
        }

        public static Dictionary<string, object> Vector3ToDict(Vector3 v)
        {
            return new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z } };
        }
    }
}
