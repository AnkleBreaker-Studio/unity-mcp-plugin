using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    public static class MCPRendererCommands
    {
        public static object SetMaterial(Dictionary<string, object> args)
        {
            var go = MCPGameObjectCommands.FindGameObject(args);
            if (go == null) return new { error = "GameObject not found" };

            string materialPath = args.ContainsKey("materialPath") ? args["materialPath"].ToString() : "";
            if (string.IsNullOrEmpty(materialPath))
                return new { error = "materialPath is required" };

            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
                return new { error = $"Material not found at {materialPath}" };

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return new { error = $"No Renderer component on {go.name}" };

            int index = args.ContainsKey("materialIndex") ? Convert.ToInt32(args["materialIndex"]) : 0;

            Undo.RecordObject(renderer, "Set Material");
            var mats = renderer.sharedMaterials;
            if (index >= mats.Length)
                return new { error = $"Material index {index} out of range (has {mats.Length} materials)" };

            mats[index] = material;
            renderer.sharedMaterials = mats;

            return new { success = true, gameObject = go.name, material = material.name, index };
        }
    }
}
