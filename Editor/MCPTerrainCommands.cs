using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for creating and manipulating Unity Terrain.
    /// </summary>
    public static class MCPTerrainCommands
    {
        // ─── Create Terrain ───

        public static object CreateTerrain(Dictionary<string, object> args)
        {
            string name = args.ContainsKey("name") ? args["name"].ToString() : "Terrain";
            int width = args.ContainsKey("width") ? Convert.ToInt32(args["width"]) : 1000;
            int length = args.ContainsKey("length") ? Convert.ToInt32(args["length"]) : 1000;
            int height = args.ContainsKey("height") ? Convert.ToInt32(args["height"]) : 600;
            int heightmapRes = args.ContainsKey("heightmapResolution") ? Convert.ToInt32(args["heightmapResolution"]) : 513;

            // Create terrain data
            var terrainData = new TerrainData();
            terrainData.heightmapResolution = heightmapRes;
            terrainData.size = new Vector3(width, height, length);

            // Save terrain data as asset
            string dataPath = args.ContainsKey("dataPath") ? args["dataPath"].ToString()
                : $"Assets/{name}_Data.asset";
            string dir = System.IO.Path.GetDirectoryName(dataPath)?.Replace('\\', '/');
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
            AssetDatabase.CreateAsset(terrainData, dataPath);

            // Create terrain GameObject
            var terrainGO = Terrain.CreateTerrainGameObject(terrainData);
            terrainGO.name = name;

            if (args.ContainsKey("position") && args["position"] is Dictionary<string, object> pos)
            {
                float x = pos.ContainsKey("x") ? Convert.ToSingle(pos["x"]) : 0;
                float y = pos.ContainsKey("y") ? Convert.ToSingle(pos["y"]) : 0;
                float z = pos.ContainsKey("z") ? Convert.ToSingle(pos["z"]) : 0;
                terrainGO.transform.position = new Vector3(x, y, z);
            }

            Undo.RegisterCreatedObjectUndo(terrainGO, "Create Terrain");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "name", name },
                { "instanceId", terrainGO.GetInstanceID() },
                { "dataPath", dataPath },
                { "size", new Dictionary<string, object> { { "x", width }, { "y", height }, { "z", length } } },
                { "heightmapResolution", heightmapRes },
            };
        }

        // ─── Get Terrain Info ───

        public static object GetTerrainInfo(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null)
                return new { error = "Terrain not found. Specify 'name' or select a terrain." };

            var data = terrain.terrainData;
            var layers = new List<Dictionary<string, object>>();
            if (data.terrainLayers != null)
            {
                foreach (var layer in data.terrainLayers)
                {
                    if (layer == null) continue;
                    layers.Add(new Dictionary<string, object>
                    {
                        { "name", layer.name },
                        { "diffuseTexture", layer.diffuseTexture != null ? AssetDatabase.GetAssetPath(layer.diffuseTexture) : "" },
                        { "tileSize", new Dictionary<string, object> { { "x", layer.tileSize.x }, { "y", layer.tileSize.y } } },
                        { "tileOffset", new Dictionary<string, object> { { "x", layer.tileOffset.x }, { "y", layer.tileOffset.y } } },
                    });
                }
            }

            return new Dictionary<string, object>
            {
                { "name", terrain.name },
                { "instanceId", terrain.gameObject.GetInstanceID() },
                { "position", new Dictionary<string, object>
                    {
                        { "x", terrain.transform.position.x },
                        { "y", terrain.transform.position.y },
                        { "z", terrain.transform.position.z },
                    }
                },
                { "size", new Dictionary<string, object>
                    {
                        { "x", data.size.x },
                        { "y", data.size.y },
                        { "z", data.size.z },
                    }
                },
                { "heightmapResolution", data.heightmapResolution },
                { "alphamapResolution", data.alphamapResolution },
                { "baseMapResolution", data.baseMapResolution },
                { "detailResolution", data.detailResolution },
                { "terrainLayers", layers },
                { "treeInstanceCount", data.treeInstanceCount },
                { "treePrototypeCount", data.treePrototypes.Length },
                { "detailPrototypeCount", data.detailPrototypes.Length },
                { "drawHeightmap", terrain.drawHeightmap },
                { "drawTreesAndFoliage", terrain.drawTreesAndFoliage },
            };
        }

        // ─── Set Height ───

        public static object SetHeight(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            float normX = args.ContainsKey("x") ? Convert.ToSingle(args["x"]) : 0.5f;
            float normZ = args.ContainsKey("z") ? Convert.ToSingle(args["z"]) : 0.5f;
            float heightValue = args.ContainsKey("height") ? Convert.ToSingle(args["height"]) : 0f;
            int radius = args.ContainsKey("radius") ? Convert.ToInt32(args["radius"]) : 1;

            var data = terrain.terrainData;
            int res = data.heightmapResolution;
            int centerX = Mathf.Clamp(Mathf.RoundToInt(normX * (res - 1)), 0, res - 1);
            int centerZ = Mathf.Clamp(Mathf.RoundToInt(normZ * (res - 1)), 0, res - 1);

            Undo.RecordObject(data, "Set Terrain Height");

            int startX = Mathf.Max(0, centerX - radius);
            int startZ = Mathf.Max(0, centerZ - radius);
            int endX = Mathf.Min(res - 1, centerX + radius);
            int endZ = Mathf.Min(res - 1, centerZ + radius);

            int sizeX = endX - startX + 1;
            int sizeZ = endZ - startZ + 1;
            float[,] heights = data.GetHeights(startX, startZ, sizeX, sizeZ);

            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    float dist = Vector2.Distance(new Vector2(startX + x, startZ + z), new Vector2(centerX, centerZ));
                    if (dist <= radius)
                    {
                        float falloff = 1f - (dist / radius);
                        heights[z, x] = Mathf.Lerp(heights[z, x], heightValue, falloff);
                    }
                }
            }

            data.SetHeights(startX, startZ, heights);
            terrain.Flush();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "center", new Dictionary<string, object> { { "x", normX }, { "z", normZ } } },
                { "height", heightValue },
                { "radius", radius },
            };
        }

        // ─── Flatten Terrain ───

        public static object FlattenTerrain(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            float heightValue = args.ContainsKey("height") ? Convert.ToSingle(args["height"]) : 0f;

            var data = terrain.terrainData;
            Undo.RecordObject(data, "Flatten Terrain");

            int res = data.heightmapResolution;
            float[,] heights = new float[res, res];
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    heights[z, x] = heightValue;

            data.SetHeights(0, 0, heights);
            terrain.Flush();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "height", heightValue },
                { "resolution", res },
            };
        }

        // ─── Add Terrain Layer ───

        public static object AddTerrainLayer(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            string texturePath = args.ContainsKey("texturePath") ? args["texturePath"].ToString() : "";
            if (string.IsNullOrEmpty(texturePath))
                return new { error = "texturePath is required" };

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (texture == null)
                return new { error = $"Texture not found at '{texturePath}'" };

            float tileSizeX = args.ContainsKey("tileSizeX") ? Convert.ToSingle(args["tileSizeX"]) : 10f;
            float tileSizeY = args.ContainsKey("tileSizeY") ? Convert.ToSingle(args["tileSizeY"]) : 10f;

            var layer = new TerrainLayer();
            layer.diffuseTexture = texture;
            layer.tileSize = new Vector2(tileSizeX, tileSizeY);

            string normalPath = args.ContainsKey("normalMapPath") ? args["normalMapPath"].ToString() : "";
            if (!string.IsNullOrEmpty(normalPath))
            {
                var normalMap = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
                if (normalMap != null) layer.normalMapTexture = normalMap;
            }

            // Save the layer as an asset
            string layerPath = args.ContainsKey("layerPath") ? args["layerPath"].ToString()
                : $"Assets/TerrainLayers/{System.IO.Path.GetFileNameWithoutExtension(texturePath)}_Layer.terrainlayer";
            string layerDir = System.IO.Path.GetDirectoryName(layerPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(layerDir) && !AssetDatabase.IsValidFolder(layerDir))
            {
                string[] parts = layerDir.Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }
            AssetDatabase.CreateAsset(layer, layerPath);

            var data = terrain.terrainData;
            Undo.RecordObject(data, "Add Terrain Layer");

            var existingLayers = data.terrainLayers?.ToList() ?? new List<TerrainLayer>();
            existingLayers.Add(layer);
            data.terrainLayers = existingLayers.ToArray();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "layerPath", layerPath },
                { "texture", texturePath },
                { "tileSize", new Dictionary<string, object> { { "x", tileSizeX }, { "y", tileSizeY } } },
                { "totalLayers", data.terrainLayers.Length },
            };
        }

        // ─── Get Height At Position ───

        public static object GetHeightAtPosition(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            float worldX = args.ContainsKey("worldX") ? Convert.ToSingle(args["worldX"]) : 0;
            float worldZ = args.ContainsKey("worldZ") ? Convert.ToSingle(args["worldZ"]) : 0;

            float height = terrain.SampleHeight(new Vector3(worldX, 0, worldZ));

            return new Dictionary<string, object>
            {
                { "worldX", worldX },
                { "worldZ", worldZ },
                { "height", height },
                { "worldY", terrain.transform.position.y + height },
            };
        }

        // ─── Helper ───

        private static Terrain FindTerrain(Dictionary<string, object> args)
        {
            if (args.ContainsKey("name"))
            {
                var go = GameObject.Find(args["name"].ToString());
                return go != null ? go.GetComponent<Terrain>() : null;
            }

            if (args.ContainsKey("instanceId"))
            {
                var go = EditorUtility.InstanceIDToObject(Convert.ToInt32(args["instanceId"])) as GameObject;
                return go != null ? go.GetComponent<Terrain>() : null;
            }

            // Return first terrain in scene
            return Terrain.activeTerrain;
        }
    }
}
