using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Comprehensive commands for creating and manipulating Unity Terrain.
    /// Covers heightmaps, splatmaps, trees, details/grass, holes, settings, and multi-terrain grids.
    /// </summary>
    public static class MCPTerrainCommands
    {
        // ─────────────────────────────────────────────
        //  CREATE / DELETE
        // ─────────────────────────────────────────────

        public static object CreateTerrain(Dictionary<string, object> args)
        {
            string name = args.ContainsKey("name") ? args["name"].ToString() : "Terrain";
            int width = args.ContainsKey("width") ? Convert.ToInt32(args["width"]) : 1000;
            int length = args.ContainsKey("length") ? Convert.ToInt32(args["length"]) : 1000;
            int height = args.ContainsKey("height") ? Convert.ToInt32(args["height"]) : 600;
            int heightmapRes = args.ContainsKey("heightmapResolution") ? Convert.ToInt32(args["heightmapResolution"]) : 513;

            var terrainData = new TerrainData();
            terrainData.heightmapResolution = heightmapRes;
            terrainData.size = new Vector3(width, height, length);

            string dataPath = args.ContainsKey("dataPath") ? args["dataPath"].ToString()
                : $"Assets/{name}_Data.asset";
            EnsureDirectoryExists(dataPath);
            AssetDatabase.CreateAsset(terrainData, dataPath);

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

        // ─────────────────────────────────────────────
        //  INFO
        // ─────────────────────────────────────────────

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

            // Tree prototypes
            var treeProtos = new List<Dictionary<string, object>>();
            foreach (var tp in data.treePrototypes)
            {
                treeProtos.Add(new Dictionary<string, object>
                {
                    { "prefab", tp.prefab != null ? AssetDatabase.GetAssetPath(tp.prefab) : "" },
                    { "bendFactor", tp.bendFactor },
                });
            }

            // Detail prototypes
            var detailProtos = new List<Dictionary<string, object>>();
            foreach (var dp in data.detailPrototypes)
            {
                detailProtos.Add(new Dictionary<string, object>
                {
                    { "prototype", dp.prototype != null ? AssetDatabase.GetAssetPath(dp.prototype) : "" },
                    { "prototypeTexture", dp.prototypeTexture != null ? AssetDatabase.GetAssetPath(dp.prototypeTexture) : "" },
                    { "minWidth", dp.minWidth },
                    { "maxWidth", dp.maxWidth },
                    { "minHeight", dp.minHeight },
                    { "maxHeight", dp.maxHeight },
                    { "renderMode", dp.renderMode.ToString() },
                });
            }

            return new Dictionary<string, object>
            {
                { "name", terrain.name },
                { "instanceId", terrain.gameObject.GetInstanceID() },
                { "position", Vec3Dict(terrain.transform.position) },
                { "size", Vec3Dict(data.size) },
                { "heightmapResolution", data.heightmapResolution },
                { "alphamapResolution", data.alphamapResolution },
                { "baseMapResolution", data.baseMapResolution },
                { "detailResolution", data.detailResolution },
                { "terrainLayers", layers },
                { "treePrototypes", treeProtos },
                { "treeInstanceCount", data.treeInstanceCount },
                { "detailPrototypes", detailProtos },
                { "drawHeightmap", terrain.drawHeightmap },
                { "drawTreesAndFoliage", terrain.drawTreesAndFoliage },
                { "materialType", terrain.materialTemplate != null ? terrain.materialTemplate.name : "default" },
                { "basemapDistance", terrain.basemapDistance },
                { "drawInstanced", terrain.drawInstanced },
            };
        }

        /// <summary>List all terrains in the scene.</summary>
        public static object ListTerrains(Dictionary<string, object> args)
        {
            var terrains = Terrain.activeTerrains;
            var result = new List<Dictionary<string, object>>();
            foreach (var t in terrains)
            {
                result.Add(new Dictionary<string, object>
                {
                    { "name", t.name },
                    { "instanceId", t.gameObject.GetInstanceID() },
                    { "position", Vec3Dict(t.transform.position) },
                    { "size", Vec3Dict(t.terrainData.size) },
                    { "isActive", t == Terrain.activeTerrain },
                });
            }
            return new Dictionary<string, object>
            {
                { "terrains", result },
                { "count", result.Count },
            };
        }

        // ─────────────────────────────────────────────
        //  HEIGHTMAP OPERATIONS
        // ─────────────────────────────────────────────

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

        /// <summary>Raise or lower terrain in a region by a delta amount.</summary>
        public static object RaiseLowerHeight(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            float normX = args.ContainsKey("x") ? Convert.ToSingle(args["x"]) : 0.5f;
            float normZ = args.ContainsKey("z") ? Convert.ToSingle(args["z"]) : 0.5f;
            float delta = args.ContainsKey("delta") ? Convert.ToSingle(args["delta"]) : 0.01f;
            int radius = args.ContainsKey("radius") ? Convert.ToInt32(args["radius"]) : 10;
            string falloffStr = args.ContainsKey("falloff") ? args["falloff"].ToString() : "smooth";

            var data = terrain.terrainData;
            int res = data.heightmapResolution;
            int centerX = Mathf.Clamp(Mathf.RoundToInt(normX * (res - 1)), 0, res - 1);
            int centerZ = Mathf.Clamp(Mathf.RoundToInt(normZ * (res - 1)), 0, res - 1);

            Undo.RecordObject(data, "Raise/Lower Terrain");

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
                        float f = GetFalloff(dist, radius, falloffStr);
                        heights[z, x] = Mathf.Clamp01(heights[z, x] + delta * f);
                    }
                }
            }

            data.SetHeights(startX, startZ, heights);
            terrain.Flush();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "center", new Dictionary<string, object> { { "x", normX }, { "z", normZ } } },
                { "delta", delta },
                { "radius", radius },
                { "falloff", falloffStr },
            };
        }

        /// <summary>Smooth terrain in a region by averaging neighboring heights.</summary>
        public static object SmoothHeight(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            float normX = args.ContainsKey("x") ? Convert.ToSingle(args["x"]) : 0.5f;
            float normZ = args.ContainsKey("z") ? Convert.ToSingle(args["z"]) : 0.5f;
            int radius = args.ContainsKey("radius") ? Convert.ToInt32(args["radius"]) : 10;
            float strength = args.ContainsKey("strength") ? Convert.ToSingle(args["strength"]) : 0.5f;
            int iterations = args.ContainsKey("iterations") ? Convert.ToInt32(args["iterations"]) : 1;

            var data = terrain.terrainData;
            int res = data.heightmapResolution;
            int centerX = Mathf.Clamp(Mathf.RoundToInt(normX * (res - 1)), 0, res - 1);
            int centerZ = Mathf.Clamp(Mathf.RoundToInt(normZ * (res - 1)), 0, res - 1);

            Undo.RecordObject(data, "Smooth Terrain");

            int startX = Mathf.Max(0, centerX - radius);
            int startZ = Mathf.Max(0, centerZ - radius);
            int endX = Mathf.Min(res - 1, centerX + radius);
            int endZ = Mathf.Min(res - 1, centerZ + radius);
            int sizeX = endX - startX + 1;
            int sizeZ = endZ - startZ + 1;

            // Read slightly expanded for neighbor access
            int padStartX = Mathf.Max(0, startX - 1);
            int padStartZ = Mathf.Max(0, startZ - 1);
            int padEndX = Mathf.Min(res - 1, endX + 1);
            int padEndZ = Mathf.Min(res - 1, endZ + 1);
            int padSizeX = padEndX - padStartX + 1;
            int padSizeZ = padEndZ - padStartZ + 1;

            for (int iter = 0; iter < iterations; iter++)
            {
                float[,] heights = data.GetHeights(padStartX, padStartZ, padSizeX, padSizeZ);
                float[,] result = new float[sizeZ, sizeX];

                for (int z = 0; z < sizeZ; z++)
                {
                    for (int x = 0; x < sizeX; x++)
                    {
                        int hx = (startX - padStartX) + x;
                        int hz = (startZ - padStartZ) + z;

                        float dist = Vector2.Distance(new Vector2(startX + x, startZ + z), new Vector2(centerX, centerZ));
                        if (dist > radius)
                        {
                            result[z, x] = heights[hz, hx];
                            continue;
                        }

                        // Average 3x3 neighborhood
                        float sum = 0; int count = 0;
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = hx + dx, nz = hz + dz;
                                if (nx >= 0 && nx < padSizeX && nz >= 0 && nz < padSizeZ)
                                {
                                    sum += heights[nz, nx];
                                    count++;
                                }
                            }
                        }
                        float avg = sum / count;
                        float falloff = 1f - (dist / radius);
                        result[z, x] = Mathf.Lerp(heights[hz, hx], avg, strength * falloff);
                    }
                }

                data.SetHeights(startX, startZ, result);
            }

            terrain.Flush();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "iterations", iterations },
                { "radius", radius },
                { "strength", strength },
            };
        }

        /// <summary>Apply Perlin noise to the terrain heightmap.</summary>
        public static object SetHeightsFromNoise(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            float scale = args.ContainsKey("scale") ? Convert.ToSingle(args["scale"]) : 0.02f;
            float amplitude = args.ContainsKey("amplitude") ? Convert.ToSingle(args["amplitude"]) : 0.1f;
            float offsetX = args.ContainsKey("offsetX") ? Convert.ToSingle(args["offsetX"]) : 0f;
            float offsetZ = args.ContainsKey("offsetZ") ? Convert.ToSingle(args["offsetZ"]) : 0f;
            int octaves = args.ContainsKey("octaves") ? Convert.ToInt32(args["octaves"]) : 4;
            float persistence = args.ContainsKey("persistence") ? Convert.ToSingle(args["persistence"]) : 0.5f;
            float lacunarity = args.ContainsKey("lacunarity") ? Convert.ToSingle(args["lacunarity"]) : 2f;
            float baseHeight = args.ContainsKey("baseHeight") ? Convert.ToSingle(args["baseHeight"]) : 0f;
            bool additive = args.ContainsKey("additive") && Convert.ToBoolean(args["additive"]);

            var data = terrain.terrainData;
            int res = data.heightmapResolution;
            Undo.RecordObject(data, "Noise Terrain");

            float[,] heights = additive ? data.GetHeights(0, 0, res, res) : new float[res, res];

            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    float noiseVal = 0f;
                    float amp = amplitude;
                    float freq = scale;
                    for (int o = 0; o < octaves; o++)
                    {
                        float px = (x + offsetX) * freq;
                        float pz = (z + offsetZ) * freq;
                        noiseVal += Mathf.PerlinNoise(px, pz) * amp;
                        amp *= persistence;
                        freq *= lacunarity;
                    }
                    if (additive)
                        heights[z, x] = Mathf.Clamp01(heights[z, x] + noiseVal + baseHeight);
                    else
                        heights[z, x] = Mathf.Clamp01(noiseVal + baseHeight);
                }
            }

            data.SetHeights(0, 0, heights);
            terrain.Flush();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "scale", scale },
                { "amplitude", amplitude },
                { "octaves", octaves },
                { "additive", additive },
            };
        }

        /// <summary>Set heights in a rectangular region from a flat array of values.</summary>
        public static object SetHeightsRegion(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            if (!args.ContainsKey("heights"))
                return new { error = "heights array is required" };

            int startX = args.ContainsKey("startX") ? Convert.ToInt32(args["startX"]) : 0;
            int startZ = args.ContainsKey("startZ") ? Convert.ToInt32(args["startZ"]) : 0;
            int width = args.ContainsKey("width") ? Convert.ToInt32(args["width"]) : 0;
            int heightCount = args.ContainsKey("heightSize") ? Convert.ToInt32(args["heightSize"]) : 0;

            // Parse flat array of heights
            var heightList = args["heights"] as List<object>;
            if (heightList == null) return new { error = "heights must be an array of numbers" };
            if (width <= 0 || heightCount <= 0)
            {
                // Infer square
                int total = heightList.Count;
                int side = Mathf.FloorToInt(Mathf.Sqrt(total));
                if (width <= 0) width = side;
                if (heightCount <= 0) heightCount = total / width;
            }

            var data = terrain.terrainData;
            Undo.RecordObject(data, "Set Heights Region");

            float[,] heights = new float[heightCount, width];
            int idx = 0;
            for (int z = 0; z < heightCount && idx < heightList.Count; z++)
                for (int x = 0; x < width && idx < heightList.Count; x++)
                    heights[z, x] = Convert.ToSingle(heightList[idx++]);

            data.SetHeights(startX, startZ, heights);
            terrain.Flush();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "startX", startX },
                { "startZ", startZ },
                { "width", width },
                { "height", heightCount },
                { "pointsSet", idx },
            };
        }

        /// <summary>Get heights in a rectangular region as a flat array (with downsampling).</summary>
        public static object GetHeightsRegion(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            var data = terrain.terrainData;
            int res = data.heightmapResolution;
            int startX = args.ContainsKey("startX") ? Convert.ToInt32(args["startX"]) : 0;
            int startZ = args.ContainsKey("startZ") ? Convert.ToInt32(args["startZ"]) : 0;
            int width = args.ContainsKey("width") ? Convert.ToInt32(args["width"]) : res;
            int height = args.ContainsKey("height") ? Convert.ToInt32(args["height"]) : res;
            int step = args.ContainsKey("step") ? Convert.ToInt32(args["step"]) : 1;

            // Clamp
            width = Mathf.Min(width, res - startX);
            height = Mathf.Min(height, res - startZ);
            step = Mathf.Max(1, step);

            float[,] heights = data.GetHeights(startX, startZ, width, height);

            var result = new List<float>();
            int sampledW = 0, sampledH = 0;
            for (int z = 0; z < height; z += step)
            {
                if (z == 0 || z / step > sampledH - 1) sampledH = z / step + 1;
                sampledW = 0;
                for (int x = 0; x < width; x += step)
                {
                    result.Add((float)Math.Round(heights[z, x], 4));
                    sampledW++;
                }
            }

            // Limit response size
            if (result.Count > 50000)
            {
                return new Dictionary<string, object>
                {
                    { "error", "Region too large. Use 'step' parameter to downsample (e.g. step=4)." },
                    { "requestedPoints", result.Count },
                    { "maxPoints", 50000 },
                    { "suggestedStep", Mathf.CeilToInt(Mathf.Sqrt(result.Count / 10000f)) },
                };
            }

            return new Dictionary<string, object>
            {
                { "startX", startX },
                { "startZ", startZ },
                { "width", sampledW },
                { "height", sampledH },
                { "step", step },
                { "heights", result },
            };
        }

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

        // ─────────────────────────────────────────────
        //  TERRAIN LAYERS (SPLATMAP / PAINTING)
        // ─────────────────────────────────────────────

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

            string maskPath = args.ContainsKey("maskMapPath") ? args["maskMapPath"].ToString() : "";
            if (!string.IsNullOrEmpty(maskPath))
            {
                var maskMap = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
                if (maskMap != null) layer.maskMapTexture = maskMap;
            }

            string layerPath = args.ContainsKey("layerPath") ? args["layerPath"].ToString()
                : $"Assets/TerrainLayers/{System.IO.Path.GetFileNameWithoutExtension(texturePath)}_Layer.terrainlayer";
            EnsureDirectoryExists(layerPath);
            AssetDatabase.CreateAsset(layer, layerPath);

            var data = terrain.terrainData;
            Undo.RecordObject(data, "Add Terrain Layer");

            var existingLayers = data.terrainLayers?.ToList() ?? new List<TerrainLayer>();
            existingLayers.Add(layer);
            data.terrainLayers = existingLayers.ToArray();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "layerIndex", existingLayers.Count - 1 },
                { "layerPath", layerPath },
                { "texture", texturePath },
                { "tileSize", new Dictionary<string, object> { { "x", tileSizeX }, { "y", tileSizeY } } },
                { "totalLayers", data.terrainLayers.Length },
            };
        }

        /// <summary>Remove a terrain layer by index.</summary>
        public static object RemoveTerrainLayer(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            if (!args.ContainsKey("layerIndex"))
                return new { error = "layerIndex is required" };
            int layerIndex = Convert.ToInt32(args["layerIndex"]);

            var data = terrain.terrainData;
            if (layerIndex < 0 || layerIndex >= data.terrainLayers.Length)
                return new { error = $"layerIndex {layerIndex} out of range (0-{data.terrainLayers.Length - 1})" };

            Undo.RecordObject(data, "Remove Terrain Layer");

            var layers = data.terrainLayers.ToList();
            string removedName = layers[layerIndex]?.name ?? "unknown";
            layers.RemoveAt(layerIndex);
            data.terrainLayers = layers.ToArray();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "removedLayer", removedName },
                { "removedIndex", layerIndex },
                { "remainingLayers", data.terrainLayers.Length },
            };
        }

        /// <summary>Paint a terrain layer at a position (set alphamap weights).</summary>
        public static object PaintTerrainLayer(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            if (!args.ContainsKey("layerIndex"))
                return new { error = "layerIndex is required" };
            int layerIndex = Convert.ToInt32(args["layerIndex"]);

            float normX = args.ContainsKey("x") ? Convert.ToSingle(args["x"]) : 0.5f;
            float normZ = args.ContainsKey("z") ? Convert.ToSingle(args["z"]) : 0.5f;
            int radius = args.ContainsKey("radius") ? Convert.ToInt32(args["radius"]) : 10;
            float strength = args.ContainsKey("strength") ? Convert.ToSingle(args["strength"]) : 1f;

            var data = terrain.terrainData;
            int numLayers = data.terrainLayers.Length;
            if (layerIndex < 0 || layerIndex >= numLayers)
                return new { error = $"layerIndex {layerIndex} out of range (0-{numLayers - 1})" };

            int alphaRes = data.alphamapResolution;
            int centerX = Mathf.Clamp(Mathf.RoundToInt(normX * (alphaRes - 1)), 0, alphaRes - 1);
            int centerZ = Mathf.Clamp(Mathf.RoundToInt(normZ * (alphaRes - 1)), 0, alphaRes - 1);

            int startX = Mathf.Max(0, centerX - radius);
            int startZ = Mathf.Max(0, centerZ - radius);
            int endX = Mathf.Min(alphaRes - 1, centerX + radius);
            int endZ = Mathf.Min(alphaRes - 1, centerZ + radius);
            int sizeX = endX - startX + 1;
            int sizeZ = endZ - startZ + 1;

            Undo.RecordObject(data, "Paint Terrain Layer");

            float[,,] alphamaps = data.GetAlphamaps(startX, startZ, sizeX, sizeZ);

            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    float dist = Vector2.Distance(new Vector2(startX + x, startZ + z), new Vector2(centerX, centerZ));
                    if (dist > radius) continue;

                    float falloff = 1f - (dist / radius);
                    float paintStrength = strength * falloff;

                    // Increase target layer, decrease others proportionally
                    float currentWeight = alphamaps[z, x, layerIndex];
                    float newWeight = Mathf.Clamp01(currentWeight + paintStrength);
                    float otherTotal = 0f;
                    for (int l = 0; l < numLayers; l++)
                        if (l != layerIndex) otherTotal += alphamaps[z, x, l];

                    alphamaps[z, x, layerIndex] = newWeight;

                    // Normalize others
                    float remaining = 1f - newWeight;
                    for (int l = 0; l < numLayers; l++)
                    {
                        if (l == layerIndex) continue;
                        if (otherTotal > 0)
                            alphamaps[z, x, l] = alphamaps[z, x, l] / otherTotal * remaining;
                        else
                            alphamaps[z, x, l] = remaining / (numLayers - 1);
                    }
                }
            }

            data.SetAlphamaps(startX, startZ, alphamaps);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "layerIndex", layerIndex },
                { "center", new Dictionary<string, object> { { "x", normX }, { "z", normZ } } },
                { "radius", radius },
                { "strength", strength },
            };
        }

        /// <summary>Fill entire terrain with a single layer (set all alphamaps to one layer).</summary>
        public static object FillTerrainLayer(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            if (!args.ContainsKey("layerIndex"))
                return new { error = "layerIndex is required" };
            int layerIndex = Convert.ToInt32(args["layerIndex"]);

            var data = terrain.terrainData;
            int numLayers = data.terrainLayers.Length;
            if (layerIndex < 0 || layerIndex >= numLayers)
                return new { error = $"layerIndex {layerIndex} out of range (0-{numLayers - 1})" };

            int res = data.alphamapResolution;
            Undo.RecordObject(data, "Fill Terrain Layer");

            float[,,] alphamaps = new float[res, res, numLayers];
            for (int z = 0; z < res; z++)
                for (int x = 0; x < res; x++)
                    alphamaps[z, x, layerIndex] = 1f;

            data.SetAlphamaps(0, 0, alphamaps);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "layerIndex", layerIndex },
                { "resolution", res },
            };
        }

        // ─────────────────────────────────────────────
        //  TREE MANAGEMENT
        // ─────────────────────────────────────────────

        /// <summary>Add a tree prototype (prefab) to the terrain.</summary>
        public static object AddTreePrototype(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            string prefabPath = args.ContainsKey("prefabPath") ? args["prefabPath"].ToString() : "";
            if (string.IsNullOrEmpty(prefabPath))
                return new { error = "prefabPath is required" };

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                return new { error = $"Prefab not found at '{prefabPath}'" };

            float bendFactor = args.ContainsKey("bendFactor") ? Convert.ToSingle(args["bendFactor"]) : 0f;

            var data = terrain.terrainData;
            Undo.RecordObject(data, "Add Tree Prototype");

            var protos = data.treePrototypes.ToList();
            var newProto = new TreePrototype { prefab = prefab, bendFactor = bendFactor };
            protos.Add(newProto);
            data.treePrototypes = protos.ToArray();
            data.RefreshPrototypes();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "prototypeIndex", protos.Count - 1 },
                { "prefabPath", prefabPath },
                { "totalPrototypes", protos.Count },
            };
        }

        /// <summary>Remove a tree prototype by index.</summary>
        public static object RemoveTreePrototype(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            if (!args.ContainsKey("prototypeIndex"))
                return new { error = "prototypeIndex is required" };
            int idx = Convert.ToInt32(args["prototypeIndex"]);

            var data = terrain.terrainData;
            if (idx < 0 || idx >= data.treePrototypes.Length)
                return new { error = $"prototypeIndex {idx} out of range" };

            Undo.RecordObject(data, "Remove Tree Prototype");

            // Remove all instances of this prototype
            var instances = new List<TreeInstance>(data.treeInstances);
            instances.RemoveAll(t => t.prototypeIndex == idx);
            // Adjust indices for remaining instances
            for (int i = 0; i < instances.Count; i++)
            {
                var inst = instances[i];
                if (inst.prototypeIndex > idx) inst.prototypeIndex--;
                instances[i] = inst;
            }

            var protos = data.treePrototypes.ToList();
            protos.RemoveAt(idx);
            data.treePrototypes = protos.ToArray();
            data.treeInstances = instances.ToArray();
            data.RefreshPrototypes();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "removedIndex", idx },
                { "remainingPrototypes", protos.Count },
                { "remainingInstances", instances.Count },
            };
        }

        /// <summary>Place tree instances on the terrain.</summary>
        public static object PlaceTrees(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            int protoIdx = args.ContainsKey("prototypeIndex") ? Convert.ToInt32(args["prototypeIndex"]) : 0;

            var data = terrain.terrainData;
            if (protoIdx < 0 || protoIdx >= data.treePrototypes.Length)
                return new { error = $"prototypeIndex {protoIdx} out of range (0-{data.treePrototypes.Length - 1})" };

            Undo.RecordObject(data, "Place Trees");

            // Check if positions array is provided (manual placement)
            if (args.ContainsKey("positions") && args["positions"] is List<object> posList)
            {
                var instances = new List<TreeInstance>(data.treeInstances);
                float widthScale = args.ContainsKey("widthScale") ? Convert.ToSingle(args["widthScale"]) : 1f;
                float heightScale = args.ContainsKey("heightScale") ? Convert.ToSingle(args["heightScale"]) : 1f;

                foreach (var posObj in posList)
                {
                    if (posObj is Dictionary<string, object> p)
                    {
                        float px = p.ContainsKey("x") ? Convert.ToSingle(p["x"]) : 0f;
                        float pz = p.ContainsKey("z") ? Convert.ToSingle(p["z"]) : 0f;

                        var inst = new TreeInstance
                        {
                            prototypeIndex = protoIdx,
                            position = new Vector3(px, 0, pz), // Y is auto-calculated
                            widthScale = widthScale,
                            heightScale = heightScale,
                            color = Color.white,
                            lightmapColor = Color.white,
                        };
                        instances.Add(inst);
                    }
                }
                data.treeInstances = instances.ToArray();

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "placedCount", posList.Count },
                    { "totalTreeInstances", data.treeInstanceCount },
                };
            }

            // Random scatter mode
            int count = args.ContainsKey("count") ? Convert.ToInt32(args["count"]) : 100;
            float minX = args.ContainsKey("minX") ? Convert.ToSingle(args["minX"]) : 0f;
            float maxX = args.ContainsKey("maxX") ? Convert.ToSingle(args["maxX"]) : 1f;
            float minZ = args.ContainsKey("minZ") ? Convert.ToSingle(args["minZ"]) : 0f;
            float maxZ = args.ContainsKey("maxZ") ? Convert.ToSingle(args["maxZ"]) : 1f;
            float minWidthScale = args.ContainsKey("minWidthScale") ? Convert.ToSingle(args["minWidthScale"]) : 0.8f;
            float maxWidthScale = args.ContainsKey("maxWidthScale") ? Convert.ToSingle(args["maxWidthScale"]) : 1.2f;
            float minHeightScale = args.ContainsKey("minHeightScale") ? Convert.ToSingle(args["minHeightScale"]) : 0.8f;
            float maxHeightScale = args.ContainsKey("maxHeightScale") ? Convert.ToSingle(args["maxHeightScale"]) : 1.2f;
            int seed = args.ContainsKey("seed") ? Convert.ToInt32(args["seed"]) : -1;
            float minSteepness = args.ContainsKey("minSteepness") ? Convert.ToSingle(args["minSteepness"]) : 0f;
            float maxSteepness = args.ContainsKey("maxSteepness") ? Convert.ToSingle(args["maxSteepness"]) : 90f;
            float minHeight = args.ContainsKey("minAltitude") ? Convert.ToSingle(args["minAltitude"]) : 0f;
            float maxHeight = args.ContainsKey("maxAltitude") ? Convert.ToSingle(args["maxAltitude"]) : 1f;

            var rng = seed >= 0 ? new System.Random(seed) : new System.Random();
            var treeInstances = new List<TreeInstance>(data.treeInstances);
            int placed = 0;

            for (int i = 0; i < count * 3 && placed < count; i++) // Oversample in case filtering removes some
            {
                float px = (float)(rng.NextDouble() * (maxX - minX) + minX);
                float pz = (float)(rng.NextDouble() * (maxZ - minZ) + minZ);

                // Check steepness and altitude
                float steepness = data.GetSteepness(px, pz);
                float normHeight = data.GetInterpolatedHeight(px, pz) / data.size.y;

                if (steepness < minSteepness || steepness > maxSteepness) continue;
                if (normHeight < minHeight || normHeight > maxHeight) continue;

                var inst = new TreeInstance
                {
                    prototypeIndex = protoIdx,
                    position = new Vector3(px, 0, pz),
                    widthScale = (float)(rng.NextDouble() * (maxWidthScale - minWidthScale) + minWidthScale),
                    heightScale = (float)(rng.NextDouble() * (maxHeightScale - minHeightScale) + minHeightScale),
                    color = Color.white,
                    lightmapColor = Color.white,
                };
                treeInstances.Add(inst);
                placed++;
            }

            data.treeInstances = treeInstances.ToArray();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "placedCount", placed },
                { "totalTreeInstances", data.treeInstanceCount },
                { "region", new Dictionary<string, object> { { "minX", minX }, { "maxX", maxX }, { "minZ", minZ }, { "maxZ", maxZ } } },
            };
        }

        /// <summary>Clear all tree instances, optionally filtered by prototype index and region.</summary>
        public static object ClearTrees(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            var data = terrain.terrainData;
            Undo.RecordObject(data, "Clear Trees");

            int beforeCount = data.treeInstanceCount;
            int protoIdx = args.ContainsKey("prototypeIndex") ? Convert.ToInt32(args["prototypeIndex"]) : -1;

            if (protoIdx < 0 && !args.ContainsKey("minX"))
            {
                // Clear all
                data.treeInstances = new TreeInstance[0];
            }
            else
            {
                float minX = args.ContainsKey("minX") ? Convert.ToSingle(args["minX"]) : 0f;
                float maxX = args.ContainsKey("maxX") ? Convert.ToSingle(args["maxX"]) : 1f;
                float minZ = args.ContainsKey("minZ") ? Convert.ToSingle(args["minZ"]) : 0f;
                float maxZ = args.ContainsKey("maxZ") ? Convert.ToSingle(args["maxZ"]) : 1f;

                var instances = data.treeInstances.Where(t =>
                {
                    if (protoIdx >= 0 && t.prototypeIndex != protoIdx) return true; // Keep
                    if (t.position.x >= minX && t.position.x <= maxX &&
                        t.position.z >= minZ && t.position.z <= maxZ)
                        return false; // Remove
                    return true; // Keep
                }).ToArray();
                data.treeInstances = instances;
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "removedCount", beforeCount - data.treeInstanceCount },
                { "remainingCount", data.treeInstanceCount },
            };
        }

        /// <summary>Get tree instances with optional region filter and limit.</summary>
        public static object GetTreeInstances(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            var data = terrain.terrainData;
            int limit = args.ContainsKey("limit") ? Convert.ToInt32(args["limit"]) : 200;
            int protoIdx = args.ContainsKey("prototypeIndex") ? Convert.ToInt32(args["prototypeIndex"]) : -1;

            var instances = data.treeInstances.AsEnumerable();
            if (protoIdx >= 0)
                instances = instances.Where(t => t.prototypeIndex == protoIdx);

            int total = instances.Count();
            var result = instances.Take(limit).Select(t => new Dictionary<string, object>
            {
                { "prototypeIndex", t.prototypeIndex },
                { "position", Vec3Dict(t.position) },
                { "widthScale", t.widthScale },
                { "heightScale", t.heightScale },
            }).ToList();

            return new Dictionary<string, object>
            {
                { "trees", result },
                { "returned", result.Count },
                { "total", total },
                { "truncated", total > limit },
            };
        }

        // ─────────────────────────────────────────────
        //  DETAIL / GRASS
        // ─────────────────────────────────────────────

        /// <summary>Add a detail prototype (grass texture or mesh) to the terrain.</summary>
        public static object AddDetailPrototype(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            var data = terrain.terrainData;
            Undo.RecordObject(data, "Add Detail Prototype");

            var proto = new DetailPrototype();

            if (args.ContainsKey("prefabPath"))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(args["prefabPath"].ToString());
                if (prefab == null)
                    return new { error = $"Prefab not found at '{args["prefabPath"]}'" };
                proto.prototype = prefab;
                proto.renderMode = DetailRenderMode.VertexLit;
                proto.usePrototypeMesh = true;
            }
            else if (args.ContainsKey("texturePath"))
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(args["texturePath"].ToString());
                if (tex == null)
                    return new { error = $"Texture not found at '{args["texturePath"]}'" };
                proto.prototypeTexture = tex;
                proto.renderMode = DetailRenderMode.GrassBillboard;
                proto.usePrototypeMesh = false;
            }
            else
            {
                return new { error = "Either prefabPath or texturePath is required" };
            }

            proto.minWidth = args.ContainsKey("minWidth") ? Convert.ToSingle(args["minWidth"]) : 1f;
            proto.maxWidth = args.ContainsKey("maxWidth") ? Convert.ToSingle(args["maxWidth"]) : 2f;
            proto.minHeight = args.ContainsKey("minHeight") ? Convert.ToSingle(args["minHeight"]) : 1f;
            proto.maxHeight = args.ContainsKey("maxHeight") ? Convert.ToSingle(args["maxHeight"]) : 2f;

            if (args.ContainsKey("dryColor") && args["dryColor"] is Dictionary<string, object> dc)
                proto.dryColor = ParseColor(dc);
            if (args.ContainsKey("healthyColor") && args["healthyColor"] is Dictionary<string, object> hc)
                proto.healthyColor = ParseColor(hc);

            var protos = data.detailPrototypes.ToList();
            protos.Add(proto);
            data.detailPrototypes = protos.ToArray();
            data.RefreshPrototypes();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "prototypeIndex", protos.Count - 1 },
                { "totalPrototypes", protos.Count },
            };
        }

        /// <summary>Paint detail/grass density in a region.</summary>
        public static object PaintDetail(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            if (!args.ContainsKey("prototypeIndex"))
                return new { error = "prototypeIndex is required" };
            int protoIdx = Convert.ToInt32(args["prototypeIndex"]);

            var data = terrain.terrainData;
            if (protoIdx < 0 || protoIdx >= data.detailPrototypes.Length)
                return new { error = $"prototypeIndex {protoIdx} out of range" };

            float normX = args.ContainsKey("x") ? Convert.ToSingle(args["x"]) : 0.5f;
            float normZ = args.ContainsKey("z") ? Convert.ToSingle(args["z"]) : 0.5f;
            int radius = args.ContainsKey("radius") ? Convert.ToInt32(args["radius"]) : 10;
            int density = args.ContainsKey("density") ? Convert.ToInt32(args["density"]) : 8;

            int detailRes = data.detailResolution;
            int centerX = Mathf.Clamp(Mathf.RoundToInt(normX * (detailRes - 1)), 0, detailRes - 1);
            int centerZ = Mathf.Clamp(Mathf.RoundToInt(normZ * (detailRes - 1)), 0, detailRes - 1);

            int startX = Mathf.Max(0, centerX - radius);
            int startZ = Mathf.Max(0, centerZ - radius);
            int endX = Mathf.Min(detailRes - 1, centerX + radius);
            int endZ = Mathf.Min(detailRes - 1, centerZ + radius);
            int sizeX = endX - startX + 1;
            int sizeZ = endZ - startZ + 1;

            Undo.RecordObject(data, "Paint Detail");

            int[,] details = data.GetDetailLayer(startX, startZ, sizeX, sizeZ, protoIdx);

            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    float dist = Vector2.Distance(new Vector2(startX + x, startZ + z), new Vector2(centerX, centerZ));
                    if (dist <= radius)
                    {
                        float falloff = 1f - (dist / radius);
                        details[z, x] = Mathf.RoundToInt(density * falloff);
                    }
                }
            }

            data.SetDetailLayer(startX, startZ, protoIdx, details);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "prototypeIndex", protoIdx },
                { "center", new Dictionary<string, object> { { "x", normX }, { "z", normZ } } },
                { "radius", radius },
                { "density", density },
            };
        }

        /// <summary>Scatter detail/grass randomly across the terrain.</summary>
        public static object ScatterDetail(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            if (!args.ContainsKey("prototypeIndex"))
                return new { error = "prototypeIndex is required" };
            int protoIdx = Convert.ToInt32(args["prototypeIndex"]);

            var data = terrain.terrainData;
            if (protoIdx < 0 || protoIdx >= data.detailPrototypes.Length)
                return new { error = $"prototypeIndex {protoIdx} out of range" };

            int density = args.ContainsKey("density") ? Convert.ToInt32(args["density"]) : 4;
            float coverage = args.ContainsKey("coverage") ? Convert.ToSingle(args["coverage"]) : 0.5f;
            int seed = args.ContainsKey("seed") ? Convert.ToInt32(args["seed"]) : -1;
            float maxSteepness = args.ContainsKey("maxSteepness") ? Convert.ToSingle(args["maxSteepness"]) : 45f;

            int detailRes = data.detailResolution;
            Undo.RecordObject(data, "Scatter Detail");

            var rng = seed >= 0 ? new System.Random(seed) : new System.Random();
            int[,] details = new int[detailRes, detailRes];
            int painted = 0;

            for (int z = 0; z < detailRes; z++)
            {
                for (int x = 0; x < detailRes; x++)
                {
                    float nx = (float)x / detailRes;
                    float nz = (float)z / detailRes;
                    float steepness = data.GetSteepness(nx, nz);

                    if (steepness > maxSteepness) continue;
                    if (rng.NextDouble() > coverage) continue;

                    details[z, x] = density;
                    painted++;
                }
            }

            data.SetDetailLayer(0, 0, protoIdx, details);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "prototypeIndex", protoIdx },
                { "density", density },
                { "coverage", coverage },
                { "pixelsPainted", painted },
                { "resolution", detailRes },
            };
        }

        /// <summary>Clear all detail/grass of a specific prototype or all.</summary>
        public static object ClearDetail(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            var data = terrain.terrainData;
            Undo.RecordObject(data, "Clear Detail");

            int detailRes = data.detailResolution;
            int[,] empty = new int[detailRes, detailRes];

            if (args.ContainsKey("prototypeIndex"))
            {
                int idx = Convert.ToInt32(args["prototypeIndex"]);
                if (idx < 0 || idx >= data.detailPrototypes.Length)
                    return new { error = $"prototypeIndex {idx} out of range" };
                data.SetDetailLayer(0, 0, idx, empty);
            }
            else
            {
                // Clear all
                for (int i = 0; i < data.detailPrototypes.Length; i++)
                    data.SetDetailLayer(0, 0, i, empty);
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "resolution", detailRes },
            };
        }

        // ─────────────────────────────────────────────
        //  TERRAIN HOLES
        // ─────────────────────────────────────────────

        /// <summary>Set terrain holes (make areas non-walkable/invisible).</summary>
        public static object SetHoles(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            float normX = args.ContainsKey("x") ? Convert.ToSingle(args["x"]) : 0.5f;
            float normZ = args.ContainsKey("z") ? Convert.ToSingle(args["z"]) : 0.5f;
            int radius = args.ContainsKey("radius") ? Convert.ToInt32(args["radius"]) : 5;
            bool isHole = !args.ContainsKey("fill") || !Convert.ToBoolean(args["fill"]);

            var data = terrain.terrainData;
            int holeRes = data.holesResolution;
            int centerX = Mathf.Clamp(Mathf.RoundToInt(normX * (holeRes - 1)), 0, holeRes - 1);
            int centerZ = Mathf.Clamp(Mathf.RoundToInt(normZ * (holeRes - 1)), 0, holeRes - 1);

            int startX = Mathf.Max(0, centerX - radius);
            int startZ = Mathf.Max(0, centerZ - radius);
            int endX = Mathf.Min(holeRes - 1, centerX + radius);
            int endZ = Mathf.Min(holeRes - 1, centerZ + radius);
            int sizeX = endX - startX + 1;
            int sizeZ = endZ - startZ + 1;

            Undo.RecordObject(data, "Set Terrain Holes");

            bool[,] holes = data.GetHoles(startX, startZ, sizeX, sizeZ);
            for (int z = 0; z < sizeZ; z++)
            {
                for (int x = 0; x < sizeX; x++)
                {
                    float dist = Vector2.Distance(new Vector2(startX + x, startZ + z), new Vector2(centerX, centerZ));
                    if (dist <= radius)
                        holes[z, x] = !isHole; // true = solid, false = hole
                }
            }

            data.SetHoles(startX, startZ, holes);
            terrain.Flush();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "isHole", isHole },
                { "center", new Dictionary<string, object> { { "x", normX }, { "z", normZ } } },
                { "radius", radius },
            };
        }

        // ─────────────────────────────────────────────
        //  TERRAIN SETTINGS
        // ─────────────────────────────────────────────

        /// <summary>Modify terrain rendering and resolution settings.</summary>
        public static object SetTerrainSettings(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            Undo.RecordObject(terrain, "Set Terrain Settings");
            var data = terrain.terrainData;
            Undo.RecordObject(data, "Set Terrain Data Settings");

            // Rendering settings
            if (args.ContainsKey("drawHeightmap")) terrain.drawHeightmap = Convert.ToBoolean(args["drawHeightmap"]);
            if (args.ContainsKey("drawTreesAndFoliage")) terrain.drawTreesAndFoliage = Convert.ToBoolean(args["drawTreesAndFoliage"]);
            if (args.ContainsKey("drawInstanced")) terrain.drawInstanced = Convert.ToBoolean(args["drawInstanced"]);
            if (args.ContainsKey("heightmapPixelError")) terrain.heightmapPixelError = Convert.ToSingle(args["heightmapPixelError"]);
            if (args.ContainsKey("basemapDistance")) terrain.basemapDistance = Convert.ToSingle(args["basemapDistance"]);
            if (args.ContainsKey("detailObjectDistance")) terrain.detailObjectDistance = Convert.ToSingle(args["detailObjectDistance"]);
            if (args.ContainsKey("detailObjectDensity")) terrain.detailObjectDensity = Convert.ToSingle(args["detailObjectDensity"]);
            if (args.ContainsKey("treeDistance")) terrain.treeDistance = Convert.ToSingle(args["treeDistance"]);
            if (args.ContainsKey("treeBillboardDistance")) terrain.treeBillboardDistance = Convert.ToSingle(args["treeBillboardDistance"]);
            if (args.ContainsKey("treeCrossFadeLength")) terrain.treeCrossFadeLength = Convert.ToSingle(args["treeCrossFadeLength"]);
            if (args.ContainsKey("treeMaximumFullLODCount")) terrain.treeMaximumFullLODCount = Convert.ToInt32(args["treeMaximumFullLODCount"]);

            // Resolution settings (requires modifying TerrainData)
            if (args.ContainsKey("alphamapResolution"))
                data.alphamapResolution = Convert.ToInt32(args["alphamapResolution"]);
            if (args.ContainsKey("baseMapResolution"))
                data.baseMapResolution = Convert.ToInt32(args["baseMapResolution"]);
            if (args.ContainsKey("detailResolution"))
            {
                int detailRes = Convert.ToInt32(args["detailResolution"]);
                int detailPatch = args.ContainsKey("detailResolutionPerPatch") ? Convert.ToInt32(args["detailResolutionPerPatch"]) : 16;
                data.SetDetailResolution(detailRes, detailPatch);
            }

            // Material
            if (args.ContainsKey("materialPath"))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(args["materialPath"].ToString());
                if (mat != null) terrain.materialTemplate = mat;
            }

            EditorUtility.SetDirty(terrain);
            EditorUtility.SetDirty(data);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "terrain", terrain.name },
            };
        }

        /// <summary>Resize the terrain dimensions (width, height, length).</summary>
        public static object ResizeTerrain(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            var data = terrain.terrainData;
            Undo.RecordObject(data, "Resize Terrain");

            float w = args.ContainsKey("width") ? Convert.ToSingle(args["width"]) : data.size.x;
            float h = args.ContainsKey("height") ? Convert.ToSingle(args["height"]) : data.size.y;
            float l = args.ContainsKey("length") ? Convert.ToSingle(args["length"]) : data.size.z;

            data.size = new Vector3(w, h, l);

            if (args.ContainsKey("heightmapResolution"))
                data.heightmapResolution = Convert.ToInt32(args["heightmapResolution"]);

            EditorUtility.SetDirty(data);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "size", Vec3Dict(data.size) },
                { "heightmapResolution", data.heightmapResolution },
            };
        }

        // ─────────────────────────────────────────────
        //  MULTI-TERRAIN GRID
        // ─────────────────────────────────────────────

        /// <summary>Create a grid of terrain tiles and connect their neighbors.</summary>
        public static object CreateTerrainGrid(Dictionary<string, object> args)
        {
            int cols = args.ContainsKey("columns") ? Convert.ToInt32(args["columns"]) : 2;
            int rows = args.ContainsKey("rows") ? Convert.ToInt32(args["rows"]) : 2;
            int tileWidth = args.ContainsKey("tileWidth") ? Convert.ToInt32(args["tileWidth"]) : 500;
            int tileLength = args.ContainsKey("tileLength") ? Convert.ToInt32(args["tileLength"]) : 500;
            int tileHeight = args.ContainsKey("tileHeight") ? Convert.ToInt32(args["tileHeight"]) : 600;
            int heightmapRes = args.ContainsKey("heightmapResolution") ? Convert.ToInt32(args["heightmapResolution"]) : 513;
            string baseName = args.ContainsKey("baseName") ? args["baseName"].ToString() : "Terrain";
            string dataFolder = args.ContainsKey("dataFolder") ? args["dataFolder"].ToString() : "Assets/TerrainData";

            float startX = 0, startZ = 0;
            if (args.ContainsKey("position") && args["position"] is Dictionary<string, object> pos)
            {
                startX = pos.ContainsKey("x") ? Convert.ToSingle(pos["x"]) : 0;
                startZ = pos.ContainsKey("z") ? Convert.ToSingle(pos["z"]) : 0;
            }

            EnsureDirectoryExists(dataFolder + "/dummy.asset");

            var terrainGrid = new Terrain[cols, rows];
            var createdTerrains = new List<Dictionary<string, object>>();

            Undo.SetCurrentGroupName("Create Terrain Grid");
            int undoGroup = Undo.GetCurrentGroup();

            for (int z = 0; z < rows; z++)
            {
                for (int x = 0; x < cols; x++)
                {
                    string tileName = $"{baseName}_{x}_{z}";
                    var terrainData = new TerrainData();
                    terrainData.heightmapResolution = heightmapRes;
                    terrainData.size = new Vector3(tileWidth, tileHeight, tileLength);

                    string dataPath = $"{dataFolder}/{tileName}_Data.asset";
                    AssetDatabase.CreateAsset(terrainData, dataPath);

                    var go = Terrain.CreateTerrainGameObject(terrainData);
                    go.name = tileName;
                    go.transform.position = new Vector3(startX + x * tileWidth, 0, startZ + z * tileLength);
                    Undo.RegisterCreatedObjectUndo(go, "Create Terrain Tile");

                    terrainGrid[x, z] = go.GetComponent<Terrain>();
                    createdTerrains.Add(new Dictionary<string, object>
                    {
                        { "name", tileName },
                        { "instanceId", go.GetInstanceID() },
                        { "gridPosition", new Dictionary<string, object> { { "x", x }, { "z", z } } },
                    });
                }
            }

            // Connect neighbors
            for (int z = 0; z < rows; z++)
            {
                for (int x = 0; x < cols; x++)
                {
                    Terrain left = x > 0 ? terrainGrid[x - 1, z] : null;
                    Terrain right = x < cols - 1 ? terrainGrid[x + 1, z] : null;
                    Terrain top = z < rows - 1 ? terrainGrid[x, z + 1] : null;
                    Terrain bottom = z > 0 ? terrainGrid[x, z - 1] : null;
                    terrainGrid[x, z].SetNeighbors(left, top, right, bottom);
                }
            }

            Undo.CollapseUndoOperations(undoGroup);

            return new Dictionary<string, object>
            {
                { "success", true },
                { "columns", cols },
                { "rows", rows },
                { "tileSize", new Dictionary<string, object> { { "x", tileWidth }, { "y", tileHeight }, { "z", tileLength } } },
                { "terrains", createdTerrains },
                { "totalTiles", cols * rows },
            };
        }

        /// <summary>Set terrain neighbors for seamless LOD transitions.</summary>
        public static object SetTerrainNeighbors(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            Terrain left = null, top = null, right = null, bottom = null;

            if (args.ContainsKey("left")) left = FindTerrainByName(args["left"].ToString());
            if (args.ContainsKey("top")) top = FindTerrainByName(args["top"].ToString());
            if (args.ContainsKey("right")) right = FindTerrainByName(args["right"].ToString());
            if (args.ContainsKey("bottom")) bottom = FindTerrainByName(args["bottom"].ToString());

            terrain.SetNeighbors(left, top, right, bottom);
            terrain.Flush();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "terrain", terrain.name },
                { "left", left != null ? left.name : "none" },
                { "top", top != null ? top.name : "none" },
                { "right", right != null ? right.name : "none" },
                { "bottom", bottom != null ? bottom.name : "none" },
            };
        }

        // ─────────────────────────────────────────────
        //  HEIGHTMAP IMPORT/EXPORT
        // ─────────────────────────────────────────────

        /// <summary>Import a heightmap from a RAW file or Texture2D.</summary>
        public static object ImportHeightmap(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required (asset path to a Texture2D or absolute path to .raw file)" };

            var data = terrain.terrainData;
            int res = data.heightmapResolution;
            Undo.RecordObject(data, "Import Heightmap");

            if (path.ToLower().EndsWith(".raw"))
            {
                // Import RAW
                string fullPath = path;
                if (!System.IO.Path.IsPathRooted(fullPath))
                    fullPath = System.IO.Path.Combine(Application.dataPath, "..", fullPath);

                if (!System.IO.File.Exists(fullPath))
                    return new { error = $"File not found: {fullPath}" };

                byte[] rawData = System.IO.File.ReadAllBytes(fullPath);
                bool is16Bit = args.ContainsKey("depth") && args["depth"].ToString() == "16";

                float[,] heights = new float[res, res];
                int idx = 0;

                for (int z = 0; z < res && idx < rawData.Length; z++)
                {
                    for (int x = 0; x < res && idx < rawData.Length; x++)
                    {
                        if (is16Bit && idx + 1 < rawData.Length)
                        {
                            ushort val = (ushort)(rawData[idx] | (rawData[idx + 1] << 8));
                            heights[z, x] = val / 65535f;
                            idx += 2;
                        }
                        else
                        {
                            heights[z, x] = rawData[idx] / 255f;
                            idx++;
                        }
                    }
                }

                data.SetHeights(0, 0, heights);
            }
            else
            {
                // Import from Texture2D
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex == null)
                    return new { error = $"Texture not found at '{path}'. Ensure texture is readable." };

                float[,] heights = new float[res, res];
                for (int z = 0; z < res; z++)
                {
                    for (int x = 0; x < res; x++)
                    {
                        float u = (float)x / (res - 1);
                        float v = (float)z / (res - 1);
                        heights[z, x] = tex.GetPixelBilinear(u, v).grayscale;
                    }
                }
                data.SetHeights(0, 0, heights);
            }

            terrain.Flush();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", path },
                { "resolution", res },
            };
        }

        /// <summary>Export the heightmap to a RAW file.</summary>
        public static object ExportHeightmap(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            string path = args.ContainsKey("path") ? args["path"].ToString() : "";
            if (string.IsNullOrEmpty(path))
                return new { error = "path is required" };

            bool is16Bit = !args.ContainsKey("depth") || args["depth"].ToString() == "16";

            var data = terrain.terrainData;
            int res = data.heightmapResolution;
            float[,] heights = data.GetHeights(0, 0, res, res);

            string fullPath = path;
            if (!System.IO.Path.IsPathRooted(fullPath))
                fullPath = System.IO.Path.Combine(Application.dataPath, "..", fullPath);

            string dir = System.IO.Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            using (var stream = System.IO.File.Create(fullPath))
            {
                for (int z = 0; z < res; z++)
                {
                    for (int x = 0; x < res; x++)
                    {
                        if (is16Bit)
                        {
                            ushort val = (ushort)(heights[z, x] * 65535);
                            stream.WriteByte((byte)(val & 0xFF));
                            stream.WriteByte((byte)((val >> 8) & 0xFF));
                        }
                        else
                        {
                            stream.WriteByte((byte)(heights[z, x] * 255));
                        }
                    }
                }
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "path", fullPath },
                { "resolution", res },
                { "depth", is16Bit ? "16-bit" : "8-bit" },
            };
        }

        // ─────────────────────────────────────────────
        //  STEEPNESS & NORMALS
        // ─────────────────────────────────────────────

        /// <summary>Get terrain steepness at a normalized position.</summary>
        public static object GetSteepness(Dictionary<string, object> args)
        {
            var terrain = FindTerrain(args);
            if (terrain == null) return new { error = "Terrain not found" };

            float normX = args.ContainsKey("x") ? Convert.ToSingle(args["x"]) : 0.5f;
            float normZ = args.ContainsKey("z") ? Convert.ToSingle(args["z"]) : 0.5f;

            var data = terrain.terrainData;
            float steepness = data.GetSteepness(normX, normZ);
            Vector3 normal = data.GetInterpolatedNormal(normX, normZ);

            return new Dictionary<string, object>
            {
                { "steepness", steepness },
                { "normal", Vec3Dict(normal) },
                { "x", normX },
                { "z", normZ },
            };
        }

        // ─────────────────────────────────────────────
        //  HELPERS
        // ─────────────────────────────────────────────

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

            return Terrain.activeTerrain;
        }

        private static Terrain FindTerrainByName(string name)
        {
            var go = GameObject.Find(name);
            return go != null ? go.GetComponent<Terrain>() : null;
        }

        private static float GetFalloff(float distance, float radius, string type)
        {
            float t = distance / radius;
            switch (type)
            {
                case "linear": return 1f - t;
                case "smooth": return Mathf.SmoothStep(1f, 0f, t);
                case "sharp": return 1f - t * t;
                case "flat": return 1f;
                default: return Mathf.SmoothStep(1f, 0f, t);
            }
        }

        private static void EnsureDirectoryExists(string assetPath)
        {
            string dir = System.IO.Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
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
        }

        private static Dictionary<string, object> Vec3Dict(Vector3 v)
        {
            return new Dictionary<string, object> { { "x", v.x }, { "y", v.y }, { "z", v.z } };
        }

        private static Color ParseColor(Dictionary<string, object> c)
        {
            float r = c.ContainsKey("r") ? Convert.ToSingle(c["r"]) : 1f;
            float g = c.ContainsKey("g") ? Convert.ToSingle(c["g"]) : 1f;
            float b = c.ContainsKey("b") ? Convert.ToSingle(c["b"]) : 1f;
            float a = c.ContainsKey("a") ? Convert.ToSingle(c["a"]) : 1f;
            return new Color(r, g, b, a);
        }
    }
}
