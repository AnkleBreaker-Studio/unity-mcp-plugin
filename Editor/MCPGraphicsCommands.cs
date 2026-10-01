using System;
using System.Collections.Generic;
using System.Globalization;
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

        private static bool TryCaptureDimensions(Dictionary<string, object> args, out int width, out int height)
        {
            width = height = 512;
            int deviceLimit = SystemInfo.maxTextureSize;
            int maxSide = deviceLimit > 0 ? Math.Min(8192, deviceLimit) : 8192;
            return TryDimension(args, "width", maxSide, out width) && TryDimension(args, "height", maxSide, out height)
                && (long)width * height <= 33554432;
        }

        private static bool TryDimension(Dictionary<string, object> args, string key, int maxSide, out int result)
        {
            result = 512;
            if (!args.TryGetValue(key, out var value)) return result <= maxSide;
            return TryWholeNumber(value, 1, maxSide, out result);
        }

        private static object InvalidCaptureDimensions() => new {
            error = "Capture dimensions must be integers from 1 to 8192 within the graphics-device limit, with at most 33554432 pixels in total.",
            code = "invalid_capture_dimensions"
        };

        private static Texture2D ReadyPreview(UnityEngine.Object asset)
        {
            if (asset == null) return null;
            var preview = AssetPreview.GetAssetPreview(asset);
            return preview != null ? preview : AssetPreview.GetMiniThumbnail(asset);
        }

        private static void AwaitPreview(UnityEngine.Object asset, double seconds, Action<Texture2D> resolve,
            Action<Exception> fail, Func<bool> isActive)
        {
            MCPAssetPreviewScheduler.Schedule(() => asset == null ? null : AssetPreview.GetAssetPreview(asset),
                () => asset != null && MCPObjectId.IsLoadingPreview(asset),
                () => asset == null ? null : AssetPreview.GetMiniThumbnail(asset), resolve, fail, isActive, seconds);
        }

        private static bool TryWholeNumber(object value, int minimum, int maximum, out int result)
        {
            result = 0;
            if (value == null || value is bool) return false;
            try
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number) || number < minimum || number > maximum || number != Math.Floor(number)) return false;
                result = (int)number; return true;
            }
            catch (Exception error) when (error is FormatException || error is InvalidCastException || error is OverflowException) { return false; }
        }

        private static bool PreviewDimensions(Dictionary<string, object> args, out int width, out int height)
        {
            width = height = 0;
            int maxSide = SystemInfo.maxTextureSize > 0 ? Math.Min(8192, SystemInfo.maxTextureSize) : 8192;
            return (!args.TryGetValue("width", out var w) || TryWholeNumber(w, 1, maxSide, out width))
                && (!args.TryGetValue("height", out var h) || TryWholeNumber(h, 1, maxSide, out height))
                && (long)width * height <= 33554432;
        }

        private sealed class PreviewImage
        {
            public string Base64;
            public int Width, Height;
        }

        private static PreviewImage EncodePreview(Texture2D preview, int width = 0, int height = 0, int maxEdge = 0)
        {
            if (preview == null) return null;
            width = width > 0 ? width : preview.width;
            height = height > 0 ? height : preview.height;
            if (maxEdge > 0 && Math.Max(width, height) > maxEdge)
            {
                double scale = (double)maxEdge / Math.Max(width, height);
                width = Math.Max(1, (int)Math.Round(width * scale)); height = Math.Max(1, (int)Math.Round(height * scale));
            }
            int deviceLimit = SystemInfo.maxTextureSize;
            if (width < 1 || height < 1 || width > 8192 || height > 8192 || (long)width * height > 33554432
                || (deviceLimit > 0 && (width > deviceLimit || height > deviceLimit)))
                throw new ArgumentException("Preview dimensions exceed the graphics-device or 33554432-pixel limit. Request a smaller preview.");
            var previousActive = RenderTexture.active;
            RenderTexture target = null; Texture2D readable = null;
            try
            {
                target = RenderTexture.GetTemporary(width, height, 0);
                Graphics.Blit(preview, target); RenderTexture.active = target;
                readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                return new PreviewImage { Base64 = TextureToBase64(readable), Width = width, Height = height };
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
            }
        }

        private static object AssetPreviewResult(UnityEngine.Object asset, string path, Texture2D preview, int width, int height)
        {
            if (asset == null || preview == null) return new { error = "Could not generate preview for '" + path + "'. Asset may be unavailable or unsupported." };
            var image = EncodePreview(preview, width, height);
            return new Dictionary<string, object> { { "success", true }, { "base64", image.Base64 },
                { "width", image.Width }, { "height", image.Height }, { "assetPath", path }, { "assetType", asset.GetType().Name } };
        }

        private static object WithOptionalPreview(object result, Texture2D preview, int maxEdge = 0)
        {
            if (!(result is Dictionary<string, object> metadata)) return result;
            try
            {
                var image = EncodePreview(preview, maxEdge: maxEdge);
                if (image != null) metadata["base64"] = image.Base64;
            }
            catch { /* Optional thumbnails must not discard valid metadata. */ }
            return result;
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
            if (!PreviewDimensions(args, out int width, out int height)) return InvalidCaptureDimensions();
            string path = args.TryGetValue("assetPath", out var value) ? value?.ToString() : "";
            if (string.IsNullOrEmpty(path)) return new { error = "assetPath is required" };
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null) return new { error = "Asset not found at '" + path + "'" };
            try { return AssetPreviewResult(asset, path, ReadyPreview(asset), width, height); }
            catch (Exception error) { return new { error = error.Message }; }
        }

        public static void CaptureAssetPreview(Dictionary<string, object> args, Action<object> resolve, Func<bool> isActive)
        {
            if (!PreviewDimensions(args, out int width, out int height)) { resolve(InvalidCaptureDimensions()); return; }
            string path = args.TryGetValue("assetPath", out var value) ? value?.ToString() : "";
            if (string.IsNullOrEmpty(path)) { resolve(new { error = "assetPath is required" }); return; }
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null) { resolve(new { error = "Asset not found at '" + path + "'" }); return; }
            AwaitPreview(asset, 3, preview => resolve(AssetPreviewResult(asset, path, preview, width, height)),
                error => resolve(new { error = error.Message }), isActive);
        }

        public static object CaptureSceneView(Dictionary<string, object> args)
        {
            if (!TryCaptureDimensions(args, out int width, out int height)) return InvalidCaptureDimensions();

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null)
                return new { error = "No active Scene View found" };

            var camera = sceneView.camera;
            if (camera == null) return new { error = "The active Scene View has no render camera" };
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D tex = null;
            try
            {
                // Background SceneView cameras can lag behind scripted view changes.
                // Sync the render camera so the captured frame uses the requested view.
                camera.transform.rotation = sceneView.rotation;
                camera.transform.position = sceneView.pivot - sceneView.rotation * Vector3.forward * sceneView.cameraDistance;
                rt = new RenderTexture(width, height, 24);
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);

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
                if (camera != null) camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
                if (rt != null) UnityEngine.Object.DestroyImmediate(rt);
            }
        }

        // ─── 3. Game View Capture (Base64 PNG) ───

        public static object CaptureGameView(Dictionary<string, object> args)
        {
            if (!TryCaptureDimensions(args, out int width, out int height)) return InvalidCaptureDimensions();
            string cameraName = args.ContainsKey("cameraName") ? args["cameraName"].ToString() : "";

            Camera camera = null;
            if (!string.IsNullOrEmpty(cameraName))
            {
                var go = GameObject.Find(cameraName);
                if (go != null) camera = go.GetComponent<Camera>();
                if (camera == null) return new { error = "No Camera found at '" + cameraName + "'. Check the active object's name or hierarchy path.", code = "camera_not_found" };
            }
            else camera = Camera.main;
            if (camera == null)
                return new { error = "No camera found. Ensure a Camera exists with tag 'MainCamera' or specify cameraName." };

            RenderTexture rt = null;
            Texture2D tex = null;
            RenderTexture prevTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                rt = new RenderTexture(width, height, 24);
                camera.targetTexture = rt;
                camera.Render();

                RenderTexture.active = rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);

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
                if (camera != null) camera.targetTexture = prevTarget;
                RenderTexture.active = previousActive;
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
            var result = MaterialData(args, out var material, out bool includePreview);
            try { return includePreview && material != null ? WithOptionalPreview(result, ReadyPreview(material)) : result; }
            catch { return result; }
        }

        public static void GetMaterialInfo(Dictionary<string, object> args, Action<object> resolve, Func<bool> isActive)
        {
            var result = MaterialData(args, out var material, out bool includePreview);
            if (!includePreview || material == null) { resolve(result); return; }
            AwaitPreview(material, 2, preview => resolve(WithOptionalPreview(result, preview)), error => resolve(result), isActive);
        }

        private static object MaterialData(Dictionary<string, object> args, out Material mat, out bool includePreview)
        {
            mat = null; includePreview = true;
            if (args.TryGetValue("includePreview", out var include))
            {
                if (!(include is bool flag)) return new { error = "includePreview must be a boolean" };
                includePreview = flag;
            }
            string assetPath = args.ContainsKey("assetPath") ? args["assetPath"].ToString() : "";
            string gameObjectPath = args.TryGetValue("gameObjectPath", out var legacyPath) ? legacyPath?.ToString() : "";
            if (string.IsNullOrEmpty(gameObjectPath)) gameObjectPath = args.TryGetValue("objectPath", out var objectPath) ? objectPath?.ToString() : "";
            int materialIndex = 0;
            if (args.TryGetValue("materialIndex", out var index) && !TryWholeNumber(index, 0, int.MaxValue, out materialIndex))
                return new { error = "materialIndex must be a nonnegative integer" };

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

            return result;
        }

        public static object GetTextureInfo(Dictionary<string, object> args)
        {
            var result = TextureData(args, out var texture, out int previewSize);
            try { return previewSize != 0 && texture != null ? WithOptionalPreview(result, ReadyPreview(texture), Math.Max(0, previewSize)) : result; }
            catch { return result; }
        }

        public static void GetTextureInfo(Dictionary<string, object> args, Action<object> resolve, Func<bool> isActive)
        {
            var result = TextureData(args, out var texture, out int previewSize);
            if (previewSize == 0 || texture == null) { resolve(result); return; }
            AwaitPreview(texture, 2, preview => resolve(WithOptionalPreview(result, preview, Math.Max(0, previewSize))), error => resolve(result), isActive);
        }

        private static object TextureData(Dictionary<string, object> args, out Texture texture, out int previewSize)
        {
            texture = null; previewSize = -1;
            if (args.TryGetValue("previewSize", out var size) && !TryWholeNumber(size, 0, 8192, out previewSize))
                return new { error = "previewSize must be a whole number from 0 to 8192; 0 omits the preview" };
            string assetPath = args.ContainsKey("assetPath") ? args["assetPath"].ToString() : "";
            if (string.IsNullOrEmpty(assetPath))
                return new { error = "assetPath is required" };

            texture = AssetDatabase.LoadAssetAtPath<Texture>(assetPath);
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

            return result;
        }

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
                allLights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
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
