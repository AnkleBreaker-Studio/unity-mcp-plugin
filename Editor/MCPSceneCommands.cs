using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityMCP.Editor
{
    public static class MCPSceneCommands
    {
        public static object GetSceneInfo()
        {
            var scenes = new List<object>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                var rootObjects = new List<string>();
                foreach (var go in scene.GetRootGameObjects())
                    rootObjects.Add(go.name);

                scenes.Add(new Dictionary<string, object>
                {
                    { "name", scene.name },
                    { "path", scene.path },
                    { "isDirty", scene.isDirty },
                    { "isLoaded", scene.isLoaded },
                    { "rootObjectCount", scene.rootCount },
                    { "rootObjects", rootObjects },
                    { "buildIndex", scene.buildIndex },
                });
            }

            return new Dictionary<string, object>
            {
                { "activeScene", SceneManager.GetActiveScene().name },
                { "sceneCount", SceneManager.sceneCount },
                { "scenes", scenes },
            };
        }

        public static object OpenScene(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            // Check for unsaved changes
            if (SceneManager.GetActiveScene().isDirty)
            {
                if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                {
                    var scene = EditorSceneManager.OpenScene(path);
                    return new { success = true, name = scene.name, path = scene.path };
                }
                return new { error = "Scene has unsaved changes and user cancelled" };
            }

            var openedScene = EditorSceneManager.OpenScene(path);
            return new { success = true, name = openedScene.name, path = openedScene.path };
        }

        public static object SaveScene()
        {
            var scene = SceneManager.GetActiveScene();
            bool saved = EditorSceneManager.SaveScene(scene);
            return new { success = saved, scene = scene.name, path = scene.path };
        }

        public static object NewScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            return new { success = true, name = scene.name };
        }

        public static object GetHierarchy(Dictionary<string, object> args)
        {
            int maxDepth = 10;
            if (args != null && args.ContainsKey("maxDepth"))
                maxDepth = System.Convert.ToInt32(args["maxDepth"]);

            var scene = SceneManager.GetActiveScene();
            var hierarchy = new List<object>();

            foreach (var root in scene.GetRootGameObjects())
            {
                hierarchy.Add(BuildHierarchyNode(root, 0, maxDepth));
            }

            return new Dictionary<string, object>
            {
                { "scene", scene.name },
                { "hierarchy", hierarchy },
            };
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
                { "instanceId", go.GetInstanceID() },
                { "active", go.activeSelf },
                { "tag", go.tag },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "components", components },
                { "position", VectorToDict(go.transform.position) },
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

        private static Dictionary<string, object> VectorToDict(Vector3 v)
        {
            return new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z } };
        }
    }
}
