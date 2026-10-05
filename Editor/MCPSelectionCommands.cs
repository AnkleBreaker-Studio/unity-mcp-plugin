using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static class MCPSelectionCommands
    {
        public static object GetSelection(Dictionary<string, object> args)
        {
            var selected = new List<Dictionary<string, object>>();
            foreach (var obj in Selection.gameObjects)
            {
                selected.Add(new Dictionary<string, object>
                {
                    { "name", obj.name },
                    { "instanceId", MCPObjectId.Get(obj) },
                    { "path", MCPGameObjectCommands.GetHierarchyPath(obj) },
                });
            }

            return new Dictionary<string, object>
            {
                { "count", selected.Count },
                { "selected", selected },
                { "activeObject", Selection.activeGameObject != null ? Selection.activeGameObject.name : null },
            };
        }

        public static object SetSelection(Dictionary<string, object> args)
        {
            var gameObjects = new List<GameObject>();

            if (args.ContainsKey("paths"))
            {
                var paths = args["paths"] as List<object>;
                if (paths != null)
                {
                    foreach (var p in paths)
                    {
                        var go = GameObject.Find(p.ToString());
                        if (go != null) gameObjects.Add(go);
                    }
                }
            }

            if (args.ContainsKey("path"))
            {
                var go = GameObject.Find(args["path"].ToString());
                if (go != null) gameObjects.Add(go);
            }

            if (args.ContainsKey("instanceId"))
            {
                var go = MCPObjectId.ToObject(args["instanceId"]) as GameObject;
                if (go != null) gameObjects.Add(go);
            }

            Selection.objects = gameObjects.Cast<UnityEngine.Object>().ToArray();
            if (gameObjects.Count > 0)
                Selection.activeGameObject = gameObjects[0];

            return new Dictionary<string, object>
            {
                { "success", true },
                { "selectedCount", gameObjects.Count },
            };
        }

        public static object FocusSceneView(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return new { error = "No active Scene View found" };

            if (go != null)
            {
                Selection.activeGameObject = go;
                sceneView.FrameSelected();
            }

            if (args.ContainsKey("position"))
            {
                sceneView.pivot = MCPGameObjectCommands.DictToVector3(args["position"] as Dictionary<string, object>);
            }

            if (args.ContainsKey("rotation"))
            {
                var euler = MCPGameObjectCommands.DictToVector3(args["rotation"] as Dictionary<string, object>);
                sceneView.rotation = Quaternion.Euler(euler);
            }

            if (args.ContainsKey("size"))
                sceneView.size = Convert.ToSingle(args["size"]);

            if (args.ContainsKey("orthographic"))
                sceneView.orthographic = Convert.ToBoolean(args["orthographic"]);

            sceneView.Repaint();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "pivot", MCPGameObjectCommands.Vector3ToDict(sceneView.pivot) },
                { "rotation", MCPGameObjectCommands.Vector3ToDict(sceneView.rotation.eulerAngles) },
                { "size", sceneView.size },
                { "orthographic", sceneView.orthographic },
            };
        }

        // Default result limit, matching the search/* routes: a common type in a large scene
        // otherwise builds one entry and hierarchy path per component into a multi-MB response.
        private const int DefaultFindLimit = 500;

        public static object FindObjectsByType(Dictionary<string, object> args)
        {
            string typeName = args.ContainsKey("typeName") ? args["typeName"].ToString() : "";
            if (string.IsNullOrEmpty(typeName))
                return new { error = "typeName is required" };

            int limit = DefaultFindLimit;
            if (args.ContainsKey("limit") && args["limit"] != null)
            {
                try { limit = Convert.ToInt32(args["limit"]); }
                catch (Exception) { limit = 0; }
                if (limit < 1)
                    return new { error = $"limit must be a positive integer, got: {args["limit"]}" };
            }

            Type type = Type.GetType($"UnityEngine.{typeName}, UnityEngine") ??
                        Type.GetType($"UnityEngine.{typeName}, UnityEngine.CoreModule") ??
                        Type.GetType($"UnityEngine.{typeName}, UnityEngine.PhysicsModule");

            if (type == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(typeName) ?? assembly.GetType($"UnityEngine.{typeName}");
                    if (type != null) break;
                }
            }

            if (type == null)
                return new { error = $"Type '{typeName}' not found" };

            var objects = UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None);
            var results = new List<Dictionary<string, object>>();
            int totalFound = 0;
            foreach (var obj in objects)
            {
                var comp = obj as Component;
                if (comp == null) continue;
                totalFound++;
                // Past the limit only count: no entry and no hierarchy path is built.
                if (results.Count >= limit) continue;
                results.Add(new Dictionary<string, object>
                {
                    { "gameObject", comp.gameObject.name },
                    { "instanceId", MCPObjectId.Get(comp.gameObject) },
                    { "path", MCPGameObjectCommands.GetHierarchyPath(comp.gameObject) },
                });
            }

            var result = new Dictionary<string, object>
            {
                { "typeName", typeName },
                { "count", results.Count },
                { "totalFound", totalFound },
                { "returned", results.Count },
                { "limit", limit },
                { "objects", results },
            };
            if (totalFound > limit)
                result["truncated"] = true;
            return result;
        }
    }
}
