using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static class MCPTagLayerCommands
    {
        public static object GetTagsAndLayers(Dictionary<string, object> args)
        {
            var tags = new List<string>(UnityEditorInternal.InternalEditorUtility.tags);
            var layers = new List<Dictionary<string, object>>();
            for (int i = 0; i < 32; i++)
            {
                string name = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(name))
                {
                    layers.Add(new Dictionary<string, object>
                    {
                        { "index", i },
                        { "name", name },
                    });
                }
            }

            var sortingLayers = new List<Dictionary<string, object>>();
            foreach (var sl in SortingLayer.layers)
            {
                sortingLayers.Add(new Dictionary<string, object>
                {
                    { "id", sl.id },
                    { "name", sl.name },
                    { "value", sl.value },
                });
            }

            return new Dictionary<string, object>
            {
                { "tags", tags },
                { "layers", layers },
                { "sortingLayers", sortingLayers },
            };
        }

        public static object AddTag(Dictionary<string, object> args)
        {
            string tagName = args.ContainsKey("tag") ? args["tag"].ToString() : "";
            if (string.IsNullOrEmpty(tagName))
                return new { error = "tag is required" };

            // Check if tag already exists
            var existingTags = UnityEditorInternal.InternalEditorUtility.tags;
            foreach (var t in existingTags)
            {
                if (t == tagName)
                    return new { error = $"Tag '{tagName}' already exists" };
            }

            UnityEditorInternal.InternalEditorUtility.AddTag(tagName);
            return new { success = true, tag = tagName };
        }

        public static object SetTag(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            string tag = args.ContainsKey("tag") ? args["tag"].ToString() : "";
            if (string.IsNullOrEmpty(tag))
                return new { error = "tag is required" };

            Undo.RecordObject(go, "Set Tag");
            go.tag = tag;

            return new { success = true, gameObject = go.name, tag };
        }

        public static object SetLayer(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            int layer = -1;
            if (args.ContainsKey("layer"))
            {
                string layerVal = args["layer"].ToString();
                if (!int.TryParse(layerVal, out layer))
                    layer = LayerMask.NameToLayer(layerVal);
            }
            else if (args.ContainsKey("layerName"))
                layer = LayerMask.NameToLayer(args["layerName"].ToString());

            if (layer < 0)
                return new { error = "Valid layer index or layerName required" };

            bool includeChildren = args.ContainsKey("includeChildren") && Convert.ToBoolean(args["includeChildren"]);

            Undo.RecordObject(go, "Set Layer");
            go.layer = layer;

            if (includeChildren)
            {
                foreach (Transform child in go.GetComponentsInChildren<Transform>(true))
                {
                    Undo.RecordObject(child.gameObject, "Set Layer");
                    child.gameObject.layer = layer;
                }
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "gameObject", go.name },
                { "layer", LayerMask.LayerToName(layer) },
                { "layerIndex", layer },
                { "includeChildren", includeChildren },
            };
        }

        public static object SetStatic(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            bool isStatic = args.ContainsKey("isStatic") ? Convert.ToBoolean(args["isStatic"]) : true;
            bool includeChildren = args.ContainsKey("includeChildren") && Convert.ToBoolean(args["includeChildren"]);

            Undo.RecordObject(go, "Set Static");
            go.isStatic = isStatic;

            if (includeChildren)
            {
                foreach (Transform child in go.GetComponentsInChildren<Transform>(true))
                {
                    Undo.RecordObject(child.gameObject, "Set Static");
                    child.gameObject.isStatic = isStatic;
                }
            }

            return new { success = true, gameObject = go.name, isStatic, includeChildren };
        }
    }
}
