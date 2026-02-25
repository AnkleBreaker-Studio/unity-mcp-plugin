using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for managing Input Action Assets (.inputactions files).
    /// Works by manipulating the JSON format directly — no Input System assembly dependency required.
    /// </summary>
    public static class MCPInputCommands
    {
        // ─── Create Input Actions ───

        /// <summary>
        /// Create a new .inputactions file with optional initial action maps.
        /// </summary>
        public static object CreateInputActions(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required (e.g. 'Assets/Settings/MyControls.inputactions')" };

            if (!path.EndsWith(".inputactions"))
                path += ".inputactions";

            string assetName = args.ContainsKey("name") ? args["name"].ToString()
                : Path.GetFileNameWithoutExtension(path);

            // Ensure directory exists
            string dir = Path.GetDirectoryName(path)?.Replace('\\', '/');
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

            // Build initial maps if provided
            var maps = new List<Dictionary<string, object>>();
            if (args.ContainsKey("maps") && args["maps"] is List<object> mapList)
            {
                foreach (var mapObj in mapList)
                {
                    if (mapObj is Dictionary<string, object> mapDef)
                    {
                        string mapName = mapDef.ContainsKey("name") ? mapDef["name"].ToString() : "Default";
                        maps.Add(BuildEmptyMap(mapName));
                    }
                }
            }

            var root = new Dictionary<string, object>
            {
                { "name", assetName },
                { "maps", maps },
                { "controlSchemes", new List<object>() }
            };

            string json = MiniJson.Serialize(root);
            string fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), path).Replace('\\', '/');
            File.WriteAllText(fullPath, json);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "name", assetName },
                { "maps", maps.Count }
            };
        }

        // ─── Get Input Actions Info ───

        /// <summary>
        /// Read and return structured info about an .inputactions file.
        /// </summary>
        public static object GetInputActionsInfo(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            var root = LoadInputActionsJson(path);
            if (root == null)
                return new { error = $"Could not load .inputactions file at '{path}'" };

            var result = new Dictionary<string, object>
            {
                { "name", root.ContainsKey("name") ? root["name"] : "" },
                { "path", path }
            };

            if (root.ContainsKey("maps") && root["maps"] is List<object> maps)
            {
                var mapInfos = new List<Dictionary<string, object>>();
                foreach (var mapObj in maps)
                {
                    if (mapObj is Dictionary<string, object> map)
                    {
                        var mapInfo = new Dictionary<string, object>
                        {
                            { "name", map.ContainsKey("name") ? map["name"] : "" },
                            { "id", map.ContainsKey("id") ? map["id"] : "" }
                        };

                        // Count actions
                        int actionCount = 0;
                        var actionInfos = new List<Dictionary<string, object>>();
                        if (map.ContainsKey("actions") && map["actions"] is List<object> actions)
                        {
                            actionCount = actions.Count;
                            foreach (var actObj in actions)
                            {
                                if (actObj is Dictionary<string, object> act)
                                {
                                    actionInfos.Add(new Dictionary<string, object>
                                    {
                                        { "name", act.ContainsKey("name") ? act["name"] : "" },
                                        { "type", act.ContainsKey("type") ? act["type"] : "" },
                                        { "id", act.ContainsKey("id") ? act["id"] : "" },
                                        { "expectedControlType", act.ContainsKey("expectedControlType") ? act["expectedControlType"] : "" }
                                    });
                                }
                            }
                        }
                        mapInfo["actionCount"] = actionCount;
                        mapInfo["actions"] = actionInfos;

                        // Count bindings
                        int bindingCount = 0;
                        if (map.ContainsKey("bindings") && map["bindings"] is List<object> bindings)
                        {
                            bindingCount = bindings.Count;
                            var bindingInfos = new List<Dictionary<string, object>>();
                            foreach (var bObj in bindings)
                            {
                                if (bObj is Dictionary<string, object> b)
                                {
                                    bindingInfos.Add(new Dictionary<string, object>
                                    {
                                        { "name", b.ContainsKey("name") ? b["name"] : "" },
                                        { "path", b.ContainsKey("path") ? b["path"] : "" },
                                        { "action", b.ContainsKey("action") ? b["action"] : "" },
                                        { "isComposite", b.ContainsKey("isComposite") ? b["isComposite"] : false },
                                        { "isPartOfComposite", b.ContainsKey("isPartOfComposite") ? b["isPartOfComposite"] : false }
                                    });
                                }
                            }
                            mapInfo["bindings"] = bindingInfos;
                        }
                        mapInfo["bindingCount"] = bindingCount;

                        mapInfos.Add(mapInfo);
                    }
                }
                result["maps"] = mapInfos;
                result["mapCount"] = mapInfos.Count;
            }

            if (root.ContainsKey("controlSchemes") && root["controlSchemes"] is List<object> schemes)
                result["controlSchemeCount"] = schemes.Count;

            return result;
        }

        // ─── Add Action Map ───

        public static object AddActionMap(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            string mapName = args.ContainsKey("mapName") ? args["mapName"].ToString() : "";
            if (string.IsNullOrEmpty(mapName))
                return new { error = "mapName is required" };

            var root = LoadInputActionsJson(path);
            if (root == null)
                return new { error = $"Could not load .inputactions file at '{path}'" };

            var maps = root.ContainsKey("maps") ? root["maps"] as List<object> : new List<object>();
            if (maps == null) maps = new List<object>();

            // Check for duplicate
            foreach (var m in maps)
            {
                if (m is Dictionary<string, object> existing &&
                    existing.ContainsKey("name") && existing["name"].ToString() == mapName)
                    return new { error = $"Action map '{mapName}' already exists" };
            }

            maps.Add(BuildEmptyMap(mapName));
            root["maps"] = maps;

            SaveInputActionsJson(path, root);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "mapName", mapName },
                { "totalMaps", maps.Count }
            };
        }

        // ─── Remove Action Map ───

        public static object RemoveActionMap(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            string mapName = args.ContainsKey("mapName") ? args["mapName"].ToString() : "";
            if (string.IsNullOrEmpty(mapName))
                return new { error = "mapName is required" };

            var root = LoadInputActionsJson(path);
            if (root == null)
                return new { error = $"Could not load .inputactions file at '{path}'" };

            if (!root.ContainsKey("maps") || !(root["maps"] is List<object> maps))
                return new { error = "No maps found in file" };

            int removed = maps.RemoveAll(m =>
                m is Dictionary<string, object> d && d.ContainsKey("name") && d["name"].ToString() == mapName);

            if (removed == 0)
                return new { error = $"Action map '{mapName}' not found" };

            SaveInputActionsJson(path, root);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "removedMap", mapName },
                { "totalMaps", maps.Count }
            };
        }

        // ─── Add Action ───

        public static object AddAction(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            string mapName = args.ContainsKey("mapName") ? args["mapName"].ToString() : "";
            string actionName = args.ContainsKey("actionName") ? args["actionName"].ToString() : "";
            string actionType = args.ContainsKey("actionType") ? args["actionType"].ToString() : "Value";
            string controlType = args.ContainsKey("expectedControlType") ? args["expectedControlType"].ToString() : "";

            if (string.IsNullOrEmpty(actionName))
                return new { error = "actionName is required" };

            var root = LoadInputActionsJson(path);
            if (root == null)
                return new { error = $"Could not load .inputactions file at '{path}'" };

            var map = FindMap(root, mapName);
            if (map == null)
                return new { error = $"Action map '{mapName}' not found" };

            var actions = map.ContainsKey("actions") ? map["actions"] as List<object> : new List<object>();
            if (actions == null) actions = new List<object>();

            // Check for duplicate
            foreach (var a in actions)
            {
                if (a is Dictionary<string, object> existing &&
                    existing.ContainsKey("name") && existing["name"].ToString() == actionName)
                    return new { error = $"Action '{actionName}' already exists in map '{mapName}'" };
            }

            var action = new Dictionary<string, object>
            {
                { "name", actionName },
                { "type", actionType },
                { "id", Guid.NewGuid().ToString() },
                { "expectedControlType", controlType },
                { "processors", "" },
                { "interactions", "" },
                { "initialStateCheck", actionType == "Value" }
            };

            actions.Add(action);
            map["actions"] = actions;

            SaveInputActionsJson(path, root);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "mapName", mapName },
                { "actionName", actionName },
                { "actionType", actionType }
            };
        }

        // ─── Remove Action ───

        public static object RemoveAction(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            string mapName = args.ContainsKey("mapName") ? args["mapName"].ToString() : "";
            string actionName = args.ContainsKey("actionName") ? args["actionName"].ToString() : "";

            if (string.IsNullOrEmpty(actionName))
                return new { error = "actionName is required" };

            var root = LoadInputActionsJson(path);
            if (root == null)
                return new { error = $"Could not load .inputactions file at '{path}'" };

            var map = FindMap(root, mapName);
            if (map == null)
                return new { error = $"Action map '{mapName}' not found" };

            if (!map.ContainsKey("actions") || !(map["actions"] is List<object> actions))
                return new { error = "No actions found in map" };

            // Also remove associated bindings
            if (map.ContainsKey("bindings") && map["bindings"] is List<object> bindings)
            {
                bindings.RemoveAll(b =>
                    b is Dictionary<string, object> d && d.ContainsKey("action") && d["action"].ToString() == actionName);
            }

            int removed = actions.RemoveAll(a =>
                a is Dictionary<string, object> d && d.ContainsKey("name") && d["name"].ToString() == actionName);

            if (removed == 0)
                return new { error = $"Action '{actionName}' not found in map '{mapName}'" };

            SaveInputActionsJson(path, root);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "mapName", mapName },
                { "removedAction", actionName }
            };
        }

        // ─── Add Binding ───

        public static object AddBinding(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            string mapName = args.ContainsKey("mapName") ? args["mapName"].ToString() : "";
            string actionName = args.ContainsKey("actionName") ? args["actionName"].ToString() : "";
            string bindingPath = args.ContainsKey("bindingPath") ? args["bindingPath"].ToString() : "";

            if (string.IsNullOrEmpty(actionName))
                return new { error = "actionName is required" };

            var root = LoadInputActionsJson(path);
            if (root == null)
                return new { error = $"Could not load .inputactions file at '{path}'" };

            var map = FindMap(root, mapName);
            if (map == null)
                return new { error = $"Action map '{mapName}' not found" };

            var bindings = map.ContainsKey("bindings") ? map["bindings"] as List<object> : new List<object>();
            if (bindings == null) bindings = new List<object>();

            var binding = new Dictionary<string, object>
            {
                { "name", "" },
                { "id", Guid.NewGuid().ToString() },
                { "path", bindingPath },
                { "interactions", "" },
                { "processors", "" },
                { "groups", "" },
                { "action", actionName },
                { "isComposite", false },
                { "isPartOfComposite", false }
            };

            bindings.Add(binding);
            map["bindings"] = bindings;

            SaveInputActionsJson(path, root);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "mapName", mapName },
                { "actionName", actionName },
                { "bindingPath", bindingPath }
            };
        }

        // ─── Add Composite Binding ───

        public static object AddCompositeBinding(Dictionary<string, object> args)
        {
            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            string mapName = args.ContainsKey("mapName") ? args["mapName"].ToString() : "";
            string actionName = args.ContainsKey("actionName") ? args["actionName"].ToString() : "";
            string compositeName = args.ContainsKey("compositeName") ? args["compositeName"].ToString() : "";
            string compositeType = args.ContainsKey("compositeType") ? args["compositeType"].ToString() : "1DAxis";

            if (string.IsNullOrEmpty(actionName) || string.IsNullOrEmpty(compositeName))
                return new { error = "actionName and compositeName are required" };

            var root = LoadInputActionsJson(path);
            if (root == null)
                return new { error = $"Could not load .inputactions file at '{path}'" };

            var map = FindMap(root, mapName);
            if (map == null)
                return new { error = $"Action map '{mapName}' not found" };

            var bindings = map.ContainsKey("bindings") ? map["bindings"] as List<object> : new List<object>();
            if (bindings == null) bindings = new List<object>();

            // Add composite parent
            var composite = new Dictionary<string, object>
            {
                { "name", compositeName },
                { "id", Guid.NewGuid().ToString() },
                { "path", compositeType },
                { "interactions", "" },
                { "processors", "" },
                { "groups", "" },
                { "action", actionName },
                { "isComposite", true },
                { "isPartOfComposite", false }
            };
            bindings.Add(composite);

            // Add parts if provided
            if (args.ContainsKey("parts") && args["parts"] is List<object> partsList)
            {
                foreach (var partObj in partsList)
                {
                    if (partObj is Dictionary<string, object> partDef)
                    {
                        string partName = partDef.ContainsKey("name") ? partDef["name"].ToString() : "";
                        string partPath = partDef.ContainsKey("path") ? partDef["path"].ToString() : "";

                        var part = new Dictionary<string, object>
                        {
                            { "name", partName },
                            { "id", Guid.NewGuid().ToString() },
                            { "path", partPath },
                            { "interactions", "" },
                            { "processors", "" },
                            { "groups", "" },
                            { "action", actionName },
                            { "isComposite", false },
                            { "isPartOfComposite", true }
                        };
                        bindings.Add(part);
                    }
                }
            }

            map["bindings"] = bindings;

            SaveInputActionsJson(path, root);
            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "mapName", mapName },
                { "actionName", actionName },
                { "compositeName", compositeName }
            };
        }

        // ─── Helpers ───

        private static Dictionary<string, object> BuildEmptyMap(string name)
        {
            return new Dictionary<string, object>
            {
                { "name", name },
                { "id", Guid.NewGuid().ToString() },
                { "actions", new List<object>() },
                { "bindings", new List<object>() }
            };
        }

        private static Dictionary<string, object> FindMap(Dictionary<string, object> root, string mapName)
        {
            if (!root.ContainsKey("maps") || !(root["maps"] is List<object> maps))
                return null;

            foreach (var m in maps)
            {
                if (m is Dictionary<string, object> map &&
                    map.ContainsKey("name") && map["name"].ToString() == mapName)
                    return map;
            }
            return null;
        }

        private static Dictionary<string, object> LoadInputActionsJson(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return null;

            string fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath).Replace('\\', '/');
            if (!File.Exists(fullPath))
                return null;

            string json = File.ReadAllText(fullPath);
            return MiniJson.Deserialize(json) as Dictionary<string, object>;
        }

        private static void SaveInputActionsJson(string assetPath, Dictionary<string, object> root)
        {
            string json = MiniJson.Serialize(root);
            string fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath).Replace('\\', '/');
            File.WriteAllText(fullPath, json);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        }
    }
}
