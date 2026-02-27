using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for visual intelligence: asset previews (base64 PNG), scene/game captures,
    /// and deep graphical metadata (mesh, material, texture, renderer, lighting).
    /// </summary>
    public static class MCPGraphicsCommands
    {
        // ─── Helpers ───

        private static string TextureToBase64(Texture2D tex)
        {
            byte[] bytes = tex.EncodeToPNG();
            return System.Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// AssetPreview.GetAssetPreview may return null on first call (async loading).
        /// Retry with short sleeps, then fall back to mini thumbnail.
        /// </summary>
        private static Texture2D GetPreviewWithRetry(UnityEngine.Object asset, int maxAttempts = 30)
        {
            AssetPreview.SetPreviewTextureCacheSize(256);

            for (int i = 0; i < maxAttempts; i++)
            {
                var preview = AssetPreview.GetAssetPreview(asset);
                if (preview != null) return preview;

                if (!AssetPreview.IsLoadingAssetPreview(asset.GetInstanceID()))
                    break;

                System.Threading.Thread.Sleep(100);
            }

            // Fallback to mini thumbnail (always available, smaller)
            return AssetPreview.GetMiniThumbnail(asset);
        }

        private static Dictionary<string, object> Vec3ToDict(Vector3 v)
        {
            return new Dictionary<string, object>
            {
                { "x", Math.Round(v.x, 4) },
                { "y", Math.Round(v.y, 4) },
                { "z", Math.Round(v.z, 4) },
            };
        }

        private static Dictionary<string, object> BoundsToDict(Bounds b)
        {
            return new Dictionary<string, object>
            {
                { "center", Vec3ToDict(b.center) },
                { "size", Vec3ToDict(b.size) },
                { "extents", Vec3ToDict(b.extents) },
                { "min", Vec3ToDict(b.min) },
                { "max", Vec3ToDict(b.max) },
            };
        }

        private static Dictionary<string, object> ColorToDict(Color c)
        {
            return new Dictionary<string, object>
            {
                { "r", Math.Round(c.r, 4) },
                { "g", Math.Round(c.g, 4) },
                { "b", Math.Round(c.b, 4) },
                { "a", Math.Round(c.a, 4) },
            };
        }

        // ─── 1. Asset Preview (Base64 PNG) ───

        public static object CaptureAssetPreview(Dictionary<string, object> args)
        {
            string assetPath = args.ContainsKey("assetPath") ? args["assetPath"].ToString() : "";
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
                return new { error = $"Asset not found at '{assetPath}'" };

            var preview = GetPreviewWithRetry(asset);
            if (preview == null)
                return new { error = $"Could not generate preview for '{assetPath}'. Asset type may not support previews." };

            // AssetPreview textures are not always readable, so copy to a readable texture
            RenderTexture rt = null;
            Texture2D readable = null;
            try
            {
                rt = RenderTexture.GetTemporary(preview.width, preview.height, 0);
                Graphics.Blit(preview, rt);
                RenderTexture.active = rt;
                readable = new Texture2D(preview.width, preview.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, preview.width, preview.height), 0, 0);
                readable.Apply();
                RenderTexture.active = null;

                string base64 = TextureToBase64(readable);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "base64", base64 },
                    { "width", readable.width },
                    { "height", readable.height },
                    { "assetPath", assetPath },
                    { "assetType", asset.GetType().Name },
                };
            }
            finally
            {
                RenderTexture.active = null;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
            }
        }

        // ─── 2. Scene View Capture (Base64 PNG) ───

        public static object CaptureSceneView(Dictionary<string, object> args)
        {
            int width = args.ContainsKey("width") ? Convert.ToInt32(args["width"]) : 512;
            int height = args.ContainsKey("height") ? Convert.ToInt32(args["height"]) : 512;

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return new { error = "No active Scene View found" };

            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                var camera = sceneView.camera;
                rt = new RenderTexture(width, height, 24);
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                string base64 = TextureToBase64(tex);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "base64", base64 },
                    { "width", width },
                    { "height", height },
                };
            }
            finally
            {
                if (sceneView != null && sceneView.camera != null)
                    sceneView.camera.targetTexture = null;
                RenderTexture.active = null;
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        // ─── 3. Game View Capture (Base64 PNG) ───

        public static object CaptureGameView(Dictionary<string, object> args)
        {
            int width = args.ContainsKey("width") ? Convert.ToInt32(args["width"]) : 512;
            int height = args.ContainsKey("height") ? Convert.ToInt32(args["height"]) : 512;
            string cameraName = args.ContainsKey("cameraName") ? args["cameraName"].ToString() : "";

            Camera camera = null;
            if (!string.IsNullOrEmpty(cameraName))
            {
                var go = GameObject.Find(cameraName);
                if (go != null) camera = go.GetComponent<Camera>();
            }
            if (camera == null) camera = Camera.main;
            if (camera == null)
                return new { error = "No camera found. Ensure a Camera exists with tag 'MainCamera' or specify cameraName." };

            RenderTexture rt = null;
            Texture2D tex = null;
            RenderTexture prevTarget = camera.targetTexture;
            try
            {
                rt = new RenderTexture(width, height, 24);
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();

                string base64 = TextureToBase64(tex);

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "base64", base64 },
                    { "width", width },
                    { "height", height },
                    { "cameraName", camera.name },
                };
            }
            finally
            {
                camera.targetTexture = prevTarget;
                RenderTexture.active = null;
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        // ─── 4. Prefab Render Preview (Base64 PNG) ───

        public static object RenderPrefabPreview(Dictionary<string, object> args)
        {
            // Delegates to CaptureAssetPreview — Unity's built-in AssetPreview system
            // is the safest way to render prefab thumbnails without triggering lifecycle
            // callbacks on complex scripts (NavMeshAgent, NetworkBehaviour, etc.).
            // Custom angle rendering via Instantiate/camera is deferred to a future version.
            return CaptureAssetPreview(args);
        }

        // ─── 5. Mesh Info ───

        public static object GetMeshInfo(Dictionary<string, object> args)
        {
            string assetPath = args.ContainsKey("assetPath") ? args["assetPath"].ToString() : "";
            string gameObjectPath = args.ContainsKey("gameObjectPath") ? args["gameObjectPath"].ToString() : "";

            Mesh mesh = null;
            string source = "";
            bool isSkinned = false;
            int boneCount = 0;

            // Try loading by asset path first
            if (!string.IsNullOrEmpty(assetPath))
            {
                // Could be a mesh asset or a model (FBX) containing meshes
                var loaded = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                if (loaded != null)
                {
                    mesh = loaded;
                    source = assetPath;
                }
                else
                {
                    // Try loading as a model and getting the first mesh
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (go != null)
                    {
                        var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
                        if (smr != null && smr.sharedMesh != null)
                        {
                            mesh = smr.sharedMesh;
                            source = assetPath + " (SkinnedMeshRenderer)";
                            isSkinned = true;
                            boneCount = smr.bones != null ? smr.bones.Length : 0;
                        }
                        else
                        {
                            var mf = go.GetComponentInChildren<MeshFilter>();
                            if (mf != null && mf.sharedMesh != null)
                            {
                                mesh = mf.sharedMesh;
                                source = assetPath + " (MeshFilter)";
                            }
                        }
                    }
                }
            }

            // Try finding in scene by GameObject path
            if (mesh == null && !string.IsNullOrEmpty(gameObjectPath))
            {
                var go = GameObject.Find(gameObjectPath);
                if (go != null)
                {
                    var smr = go.GetComponent<SkinnedMeshRenderer>();
                    if (smr != null && smr.sharedMesh != null)
                    {
                        mesh = smr.sharedMesh;
                        source = gameObjectPath + " (SkinnedMeshRenderer)";
                        isSkinned = true;
                        boneCount = smr.bones != null ? smr.bones.Length : 0;
                    }
                    else
                    {
                        var mf = go.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null)
                        {
                            mesh = mf.sharedMesh;
                            source = gameObjectPath + " (MeshFilter)";
                        }
                    }
                }
            }

            if (mesh == null)
                return new { error = "No mesh found. Provide assetPath to a mesh/model asset or gameObjectPath to a scene object with MeshFilter/SkinnedMeshRenderer." };

            // Count UV channels
            int uvChannels = 0;
            if (mesh.uv != null && mesh.uv.Length > 0) uvChannels++;
            if (mesh.uv2 != null && mesh.uv2.Length > 0) uvChannels++;
            if (mesh.uv3 != null && mesh.uv3.Length > 0) uvChannels++;
            if (mesh.uv4 != null && mesh.uv4.Length > 0) uvChannels++;

            return new Dictionary<string, object>
            {
                { "name", mesh.name },
                { "source", source },
                { "vertexCount", mesh.vertexCount },
                { "triangleCount", mesh.triangles.Length / 3 },
                { "subMeshCount", mesh.subMeshCount },
                { "bounds", BoundsToDict(mesh.bounds) },
                { "uvChannelCount", uvChannels },
                { "hasNormals", mesh.normals != null && mesh.normals.Length > 0 },
                { "hasTangents", mesh.tangents != null && mesh.tangents.Length > 0 },
                { "hasColors", mesh.colors != null && mesh.colors.Length > 0 },
                { "blendShapeCount", mesh.blendShapeCount },
                { "isSkinned", isSkinned },
                { "boneCount", boneCount },
                { "isReadable", mesh.isReadable },
                { "indexFormat", mesh.indexFormat.ToString() },
            };
        }

        // ─── 6. Material Info (with preview) ───

        public static object GetMaterialInfo(Dictionary<string, object> args)
        {
            string assetPath = args.ContainsKey("assetPath") ? args["assetPath"].ToString() : "";
            string gameObjectPath = args.ContainsKey("gameObjectPath") ? args["gameObjectPath"].ToString() : "";
            int materialIndex = args.ContainsKey("materialIndex") ? Convert.ToInt32(args["materialIndex"]) : 0;

            Material mat = null;

            if (!string.IsNullOrEmpty(assetPath))
            {
                mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            }

            if (mat == null && !string.IsNullOrEmpty(gameObjectPath))
            {
                var go = GameObject.Find(gameObjectPath);
                if (go != null)
                {
                    var renderer = go.GetComponent<Renderer>();
                    if (renderer != null && renderer.sharedMaterials.Length > materialIndex)
                        mat = renderer.sharedMaterials[materialIndex];
                }
            }

            if (mat == null)
                return new { error = "Material not found. Provide assetPath to a .mat file or gameObjectPath + materialIndex." };

            var shader = mat.shader;
            var result = new Dictionary<string, object>
            {
                { "name", mat.name },
                { "shaderName", shader.name },
                { "renderQueue", mat.renderQueue },
                { "passCount", mat.passCount },
                { "doubleSidedGI", mat.doubleSidedGI },
                { "enableInstancing", mat.enableInstancing },
                { "globalIlluminationFlags", mat.globalIlluminationFlags.ToString() },
            };

            // Keywords
            var keywords = mat.shaderKeywords;
            result["enabledKeywords"] = keywords != null ? keywords.ToList() : new List<string>();

            // Shader properties
            var properties = new List<Dictionary<string, object>>();
            int propCount = shader.GetPropertyCount();
            for (int i = 0; i < propCount; i++)
            {
                string propName = shader.GetPropertyName(i);
                var propType = shader.GetPropertyType(i);
                var propDict = new Dictionary<string, object>
                {
                    { "name", propName },
                    { "type", propType.ToString() },
                    { "description", shader.GetPropertyDescription(i) },
                };

                try
                {
                    switch (propType)
                    {
                        case ShaderPropertyType.Color:
                            propDict["value"] = ColorToDict(mat.GetColor(propName));
                            break;
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range:
                            propDict["value"] = Math.Round(mat.GetFloat(propName), 4);
                            break;
                        case ShaderPropertyType.Vector:
                            var v = mat.GetVector(propName);
                            propDict["value"] = new Dictionary<string, object>
                            {
                                { "x", Math.Round(v.x, 4) }, { "y", Math.Round(v.y, 4) },
                                { "z", Math.Round(v.z, 4) }, { "w", Math.Round(v.w, 4) },
                            };
                            break;
                        case ShaderPropertyType.Texture:
                            var tex = mat.GetTexture(propName);
                            if (tex != null)
                            {
                                propDict["value"] = new Dictionary<string, object>
                                {
                                    { "name", tex.name },
                                    { "assetPath", AssetDatabase.GetAssetPath(tex) },
                                    { "width", tex.width },
                                    { "height", tex.height },
                                };
                            }
                            else
                            {
                                propDict["value"] = null;
                            }
                            break;
                        case ShaderPropertyType.Int:
                            propDict["value"] = mat.GetInt(propName);
                            break;
                    }
                }
                catch
                {
                    propDict["value"] = "(unreadable)";
                }

                properties.Add(propDict);
            }
            result["properties"] = properties;

            // Material preview thumbnail
            string base64 = null;
            try
            {
                var preview = GetPreviewWithRetry(mat, 20);
                if (preview != null)
                {
                    RenderTexture rt = RenderTexture.GetTemporary(preview.width, preview.height, 0);
                    try
                    {
                        Graphics.Blit(preview, rt);
                        RenderTexture.active = rt;
                        var readable = new Texture2D(preview.width, preview.height, TextureFormat.RGBA32, false);
                        readable.ReadPixels(new Rect(0, 0, preview.width, preview.height), 0, 0);
                        readable.Apply();
                        RenderTexture.active = null;
                        base64 = TextureToBase64(readable);
                        UnityEngine.Object.DestroyImmediate(readable);
                    }
                    finally
                    {
                        RenderTexture.active = null;
                        RenderTexture.ReleaseTemporary(rt);
                    }
                }
            }
            catch { /* preview optional, don't fail */ }

            if (base64 != null) result["base64"] = base64;

            return result;
        }

        // ─── 7. Texture Info (with preview) ───

        public static object GetTextureInfo(Dictionary<string, object> args)
        {
            string assetPath = args.ContainsKey("assetPath") ? args["assetPath"].ToString() : "";
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            var texture = AssetDatabase.LoadAssetAtPath<Texture>(assetPath);
            if (texture == null)
                return new { error = $"Texture not found at '{assetPath}'" };

            var result = new Dictionary<string, object>
            {
                { "name", texture.name },
                { "assetPath", assetPath },
                { "width", texture.width },
                { "height", texture.height },
                { "filterMode", texture.filterMode.ToString() },
                { "wrapMode", texture.wrapMode.ToString() },
                { "anisoLevel", texture.anisoLevel },
                { "texelSize", new Dictionary<string, object>
                    {
                        { "x", texture.texelSize.x },
                        { "y", texture.texelSize.y },
                    }
                },
            };

            // Texture2D-specific info
            if (texture is Texture2D tex2D)
            {
                result["format"] = tex2D.format.ToString();
                result["mipmapCount"] = tex2D.mipmapCount;
                result["isReadable"] = tex2D.isReadable;
            }

            // Import settings
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer != null)
            {
                result["importSettings"] = new Dictionary<string, object>
                {
                    { "textureType", importer.textureType.ToString() },
                    { "spriteMode", importer.spriteImportMode.ToString() },
                    { "sRGB", importer.sRGBTexture },
                    { "alphaSource", importer.alphaSource.ToString() },
                    { "alphaIsTransparency", importer.alphaIsTransparency },
                    { "mipmapEnabled", importer.mipmapEnabled },
                    { "readWriteEnabled", importer.isReadable },
                    { "maxTextureSize", importer.maxTextureSize },
                    { "textureCompression", importer.textureCompression.ToString() },
                    { "npotScale", importer.npotScale.ToString() },
                };
            }

            // Memory estimate (approximate)
            long memBytes = UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(texture);
            result["memoryEstimateKB"] = Math.Round(memBytes / 1024.0, 1);

            // Preview thumbnail
            string base64 = null;
            try
            {
                var preview = GetPreviewWithRetry(texture, 20);
                if (preview != null)
                {
                    RenderTexture rt = RenderTexture.GetTemporary(preview.width, preview.height, 0);
                    try
                    {
                        Graphics.Blit(preview, rt);
                        RenderTexture.active = rt;
                        var readable = new Texture2D(preview.width, preview.height, TextureFormat.RGBA32, false);
                        readable.ReadPixels(new Rect(0, 0, preview.width, preview.height), 0, 0);
                        readable.Apply();
                        RenderTexture.active = null;
                        base64 = TextureToBase64(readable);
                        UnityEngine.Object.DestroyImmediate(readable);
                    }
                    finally
                    {
                        RenderTexture.active = null;
                        RenderTexture.ReleaseTemporary(rt);
                    }
                }
            }
            catch { /* preview optional */ }

            if (base64 != null) result["base64"] = base64;

            return result;
        }

        // ─── 8. Renderer Info ───

        public static object GetRendererInfo(Dictionary<string, object> args)
        {
            string gameObjectPath = args.ContainsKey("gameObjectPath") ? args["gameObjectPath"].ToString() : "";
            if (string.IsNullOrEmpty(gameObjectPath))
                return new { error = "gameObjectPath is required" };

            var go = GameObject.Find(gameObjectPath);
            if (go == null)
                return new { error = $"GameObject '{gameObjectPath}' not found in scene" };

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                return new { error = $"No Renderer component found on '{gameObjectPath}'" };

            var result = new Dictionary<string, object>
            {
                { "gameObjectPath", gameObjectPath },
                { "rendererType", renderer.GetType().Name },
                { "enabled", renderer.enabled },
                { "isVisible", renderer.isVisible },
                { "bounds", BoundsToDict(renderer.bounds) },
                { "shadowCastingMode", renderer.shadowCastingMode.ToString() },
                { "receiveShadows", renderer.receiveShadows },
                { "lightmapIndex", renderer.lightmapIndex },
                { "sortingLayerName", renderer.sortingLayerName },
                { "sortingOrder", renderer.sortingOrder },
                { "lightProbeUsage", renderer.lightProbeUsage.ToString() },
                { "reflectionProbeUsage", renderer.reflectionProbeUsage.ToString() },
            };

            // Materials
            var matList = new List<Dictionary<string, object>>();
            foreach (var mat in renderer.sharedMaterials)
            {
                if (mat != null)
                {
                    matList.Add(new Dictionary<string, object>
                    {
                        { "name", mat.name },
                        { "shaderName", mat.shader != null ? mat.shader.name : "(null)" },
                        { "assetPath", AssetDatabase.GetAssetPath(mat) },
                        { "renderQueue", mat.renderQueue },
                    });
                }
                else
                {
                    matList.Add(new Dictionary<string, object> { { "name", "(null/missing)" } });
                }
            }
            result["materials"] = matList;
            result["materialCount"] = matList.Count;

            // Mesh info
            Mesh mesh = null;
            if (renderer is SkinnedMeshRenderer smr && smr.sharedMesh != null)
            {
                mesh = smr.sharedMesh;
                result["isSkinned"] = true;
                result["boneCount"] = smr.bones != null ? smr.bones.Length : 0;
            }
            else
            {
                var mf = go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                    mesh = mf.sharedMesh;
                result["isSkinned"] = false;
            }

            if (mesh != null)
            {
                result["mesh"] = new Dictionary<string, object>
                {
                    { "name", mesh.name },
                    { "vertexCount", mesh.vertexCount },
                    { "triangleCount", mesh.triangles.Length / 3 },
                    { "assetPath", AssetDatabase.GetAssetPath(mesh) },
                };
            }

            return result;
        }

        // ─── 9. Lighting Summary ───

        public static object GetLightingSummary(Dictionary<string, object> args)
        {
            string lightName = args.ContainsKey("lightName") ? args["lightName"].ToString() : "";

            Light[] allLights;
            if (!string.IsNullOrEmpty(lightName))
            {
                var go = GameObject.Find(lightName);
                if (go == null)
                    return new { error = $"GameObject '{lightName}' not found" };
                var light = go.GetComponent<Light>();
                if (light == null)
                    return new { error = $"No Light component found on '{lightName}'" };
                allLights = new[] { light };
            }
            else
            {
                allLights = UnityEngine.Object.FindObjectsOfType<Light>();
            }

            var lights = new List<Dictionary<string, object>>();
            foreach (var light in allLights)
            {
                var entry = new Dictionary<string, object>
                {
                    { "name", light.gameObject.name },
                    { "type", light.type.ToString() },
                    { "color", ColorToDict(light.color) },
                    { "intensity", Math.Round(light.intensity, 4) },
                    { "range", Math.Round(light.range, 4) },
                    { "enabled", light.enabled },
                    { "gameObjectActive", light.gameObject.activeInHierarchy },
                    { "shadows", light.shadows.ToString() },
                    { "shadowStrength", Math.Round(light.shadowStrength, 4) },
                    { "renderMode", light.renderMode.ToString() },
                    { "cullingMask", light.cullingMask },
                    { "bounceIntensity", Math.Round(light.bounceIntensity, 4) },
                };

                if (light.type == LightType.Spot)
                {
                    entry["spotAngle"] = Math.Round(light.spotAngle, 2);
                    entry["innerSpotAngle"] = Math.Round(light.innerSpotAngle, 2);
                }

                lights.Add(entry);
            }

            return new Dictionary<string, object>
            {
                { "lightCount", lights.Count },
                { "lights", lights },
            };
        }
    }
}
