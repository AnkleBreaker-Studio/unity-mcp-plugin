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
                { "instanceId", MCPObjectId.Get(go) },
                { "position", Vector3ToDict(go.transform.position) },
            };
        }

        public static object Delete(Dictionary<string, object> args)
        {
            var go = FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            // Shared runtime-mesh guard: a ProBuilderMesh owns a runtime mesh; if the object was
            // cloned with duplicate/Object.Instantiate, its copies' MeshFilters point at that SAME
            // mesh. Destroying this object runs ProBuilderMesh.OnDestroy, which destroys the mesh —
            // and every copy goes invisible at once. Refuse unless force:true, and say how many
            // objects would be hit. Only runtime (non-asset) meshes are at risk, so deleting
            // normal asset-mesh objects stays on the fast path (report A1).
            bool force = args.ContainsKey("force") && Convert.ToBoolean(args["force"]);
            if (!force)
            {
                int sharedWith = CountExternalSharersOfRuntimeMesh(go);
                if (sharedWith > 0)
                    return new
                    {
                        error = $"Refused: this object's runtime mesh is shared by {sharedWith} other object(s) (e.g. ProBuilder clones made with duplicate/Instantiate). Deleting it would blank their mesh too. Give each copy an independent mesh first (duplicate now does this automatically), or pass force:true to delete anyway.",
                        requiresForce = true,
                        sharedWith,
                    };
            }

            string name = go.name;
            Undo.DestroyObjectImmediate(go);
            return new { success = true, deleted = name };
        }

        /// <summary>
        /// Count MeshFilters OUTSIDE the given object's subtree that reference a runtime (non-asset)
        /// mesh used inside the subtree. Non-zero means deleting the object would destroy a mesh
        /// still in use elsewhere (the ProBuilder shared-mesh hazard). Fast-paths to 0 when the
        /// subtree has no runtime meshes (the common case), so normal deletes pay no scan.
        /// </summary>
        private static int CountExternalSharersOfRuntimeMesh(GameObject go)
        {
            var runtimeMeshes = new HashSet<Mesh>();
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null && !AssetDatabase.Contains(mf.sharedMesh))
                    runtimeMeshes.Add(mf.sharedMesh);
            if (runtimeMeshes.Count == 0) return 0;

            var inSubtree = new HashSet<Transform>();
            foreach (var tr in go.GetComponentsInChildren<Transform>(true))
                inSubtree.Add(tr);

            int count = 0;
            // Include INACTIVE objects: FindObjectsByType's default excludes them, which would let
            // an inactive sibling that shares the runtime mesh slip past the guard — the delete
            // would then blank it silently (the exact hazard this guards). Matches the explicit
            // opt-in used by FindGameObject above.
            foreach (var mf in UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mf.sharedMesh == null) continue;
                if (inSubtree.Contains(mf.transform)) continue;
                if (runtimeMeshes.Contains(mf.sharedMesh)) count++;
            }
            return count;
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
                { "instanceId", MCPObjectId.Get(go) },
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
                return MCPObjectId.ToObject(args["instanceId"]) as GameObject;
            }

            if (args.ContainsKey("path") || args.ContainsKey("gameObjectPath"))
            {
                string path = args.ContainsKey("path") ? args["path"].ToString() : args["gameObjectPath"].ToString();
                // Try direct find first (active objects only)
                var go = GameObject.Find(path);
                if (go != null) return go;

                // Fallback: include inactive objects. FindObjectsByType's default
                // (without FindObjectsInactive) excludes inactive, so path-based
                // lookup silently fails for inactive GameObjects. Opt in explicitly.
                var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
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
