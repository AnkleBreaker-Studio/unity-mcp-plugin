using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Advanced memory profiling commands.
    /// Provides per-asset-type memory breakdowns, top memory consumers,
    /// and optional Memory Profiler package integration (com.unity.memoryprofiler).
    /// Works without the package using built-in Unity Profiler APIs;
    /// enhanced features available when the package is installed.
    /// </summary>
    public static class MCPMemoryProfilerCommands
    {
        private static bool _packageChecked;
        private static bool _packageInstalled;

        // ─── Package Detection ───

        /// <summary>
        /// Check if com.unity.memoryprofiler package is installed by reading manifest.json.
        /// </summary>
        public static bool IsMemoryProfilerPackageInstalled()
        {
            if (_packageChecked) return _packageInstalled;
            _packageChecked = true;

            try
            {
                string manifestPath = Path.Combine(Application.dataPath, "..", "Packages", "manifest.json");
                if (File.Exists(manifestPath))
                {
                    string content = File.ReadAllText(manifestPath);
                    _packageInstalled = content.Contains("\"com.unity.memoryprofiler\"");
                }
            }
            catch { }

            return _packageInstalled;
        }

        /// <summary>
        /// Check package status and available features.
        /// </summary>
        public static object GetStatus(Dictionary<string, object> args)
        {
            bool hasPkg = IsMemoryProfilerPackageInstalled();

            long totalAllocated = Profiler.GetTotalAllocatedMemoryLong();
            long totalReserved = Profiler.GetTotalReservedMemoryLong();
            long gfxDriver = Profiler.GetAllocatedMemoryForGraphicsDriver();

            return new Dictionary<string, object>
            {
                { "memoryProfilerPackageInstalled", hasPkg },
                { "availableCommands", new string[] {
                    "profiler/memory-status",
                    "profiler/memory-breakdown",
                    "profiler/memory-top-assets",
                    hasPkg ? "profiler/memory-snapshot" : null
                }.Where(s => s != null).ToArray() },
                { "quickSummary", new Dictionary<string, object>
                    {
                        { "totalAllocatedMB", Math.Round(totalAllocated / (1024.0 * 1024.0), 2) },
                        { "totalReservedMB", Math.Round(totalReserved / (1024.0 * 1024.0), 2) },
                        { "gfxDriverMB", Math.Round(gfxDriver / (1024.0 * 1024.0), 2) },
                    }
                },
            };
        }

        // ─── Memory Breakdown by Asset Type ───

        /// <summary>
        /// Get memory breakdown organized by asset type (textures, meshes, audio, etc.).
        /// Uses built-in Profiler.GetRuntimeMemorySizeLong for per-object sizing.
        /// </summary>
        public static object GetMemoryBreakdown(Dictionary<string, object> args)
        {
            bool includeDetails = args.ContainsKey("includeDetails") && GetBool(args, "includeDetails", false);
            int maxPerCategory = args.ContainsKey("maxPerCategory")
                ? Convert.ToInt32(args["maxPerCategory"]) : 5;

            var categories = new Dictionary<string, object>();
            long grandTotal = 0;

            // Textures (Texture2D + RenderTexture)
            var texResult = ProfileAssetType<Texture2D>("Textures", includeDetails, maxPerCategory,
                t => $"{t.width}x{t.height} {t.format}");
            categories["textures"] = texResult;
            grandTotal += (long)((Dictionary<string, object>)texResult)["totalBytes"];

            var rtResult = ProfileAssetType<RenderTexture>("RenderTextures", includeDetails, maxPerCategory,
                rt => $"{rt.width}x{rt.height} {rt.format} depth={rt.depth}");
            categories["renderTextures"] = rtResult;
            grandTotal += (long)((Dictionary<string, object>)rtResult)["totalBytes"];

            // Meshes
            var meshResult = ProfileAssetType<Mesh>("Meshes", includeDetails, maxPerCategory,
                m => $"{m.vertexCount} verts, {m.triangles.Length / 3} tris");
            categories["meshes"] = meshResult;
            grandTotal += (long)((Dictionary<string, object>)meshResult)["totalBytes"];

            // Materials
            var matResult = ProfileAssetType<Material>("Materials", includeDetails, maxPerCategory,
                m => m.shader != null ? m.shader.name : "no shader");
            categories["materials"] = matResult;
            grandTotal += (long)((Dictionary<string, object>)matResult)["totalBytes"];

            // Shaders
            var shaderResult = ProfileAssetType<Shader>("Shaders", includeDetails, maxPerCategory, null);
            categories["shaders"] = shaderResult;
            grandTotal += (long)((Dictionary<string, object>)shaderResult)["totalBytes"];

            // Audio Clips
            var audioResult = ProfileAssetType<AudioClip>("AudioClips", includeDetails, maxPerCategory,
                a => $"{a.length:F1}s {a.frequency}Hz {a.channels}ch");
            categories["audioClips"] = audioResult;
            grandTotal += (long)((Dictionary<string, object>)audioResult)["totalBytes"];

            // Animation Clips
            var animResult = ProfileAssetType<AnimationClip>("AnimationClips", includeDetails, maxPerCategory,
                a => $"{a.length:F1}s {(a.isLooping ? "loop" : "once")}");
            categories["animationClips"] = animResult;
            grandTotal += (long)((Dictionary<string, object>)animResult)["totalBytes"];

            // Fonts
            var fontResult = ProfileAssetType<Font>("Fonts", includeDetails, maxPerCategory, null);
            categories["fonts"] = fontResult;
            grandTotal += (long)((Dictionary<string, object>)fontResult)["totalBytes"];

            // Scriptable Objects
            var soResult = ProfileAssetType<ScriptableObject>("ScriptableObjects", includeDetails, maxPerCategory,
                so => so.GetType().Name);
            categories["scriptableObjects"] = soResult;
            grandTotal += (long)((Dictionary<string, object>)soResult)["totalBytes"];

            // System summary
            long totalAllocated = Profiler.GetTotalAllocatedMemoryLong();
            long gfxDriver = Profiler.GetAllocatedMemoryForGraphicsDriver();
            long monoUsed = Profiler.GetMonoUsedSizeLong();

            return new Dictionary<string, object>
            {
                { "categories", categories },
                { "scannedAssetTotalMB", Math.Round(grandTotal / (1024.0 * 1024.0), 2) },
                { "scannedAssetTotalBytes", grandTotal },
                { "systemMemory", new Dictionary<string, object>
                    {
                        { "totalAllocatedMB", Math.Round(totalAllocated / (1024.0 * 1024.0), 2) },
                        { "gfxDriverMB", Math.Round(gfxDriver / (1024.0 * 1024.0), 2) },
                        { "monoUsedMB", Math.Round(monoUsed / (1024.0 * 1024.0), 2) },
                    }
                },
                { "memoryProfilerPackageInstalled", IsMemoryProfilerPackageInstalled() },
            };
        }

        private static object ProfileAssetType<T>(string categoryName, bool includeDetails, int maxPerCategory,
            Func<T, string> detailFunc) where T : UnityEngine.Object
        {
            var objects = Resources.FindObjectsOfTypeAll<T>();
            long totalBytes = 0;
            var items = new List<AssetMemInfo>();

            foreach (var obj in objects)
            {
                long size = Profiler.GetRuntimeMemorySizeLong(obj);
                totalBytes += size;

                if (includeDetails)
                {
                    items.Add(new AssetMemInfo
                    {
                        name = obj.name,
                        sizeBytes = size,
                        detail = detailFunc != null ? detailFunc(obj) : null,
                        assetPath = AssetDatabase.GetAssetPath(obj),
                    });
                }
            }

            var result = new Dictionary<string, object>
            {
                { "count", objects.Length },
                { "totalMB", Math.Round(totalBytes / (1024.0 * 1024.0), 2) },
                { "totalBytes", totalBytes },
            };

            if (includeDetails && items.Count > 0)
            {
                items.Sort((a, b) => b.sizeBytes.CompareTo(a.sizeBytes));
                var topItems = items.Take(maxPerCategory).Select(item =>
                {
                    var d = new Dictionary<string, object>
                    {
                        { "name", string.IsNullOrEmpty(item.name) ? "(unnamed)" : item.name },
                        { "sizeMB", Math.Round(item.sizeBytes / (1024.0 * 1024.0), 3) },
                        { "sizeBytes", item.sizeBytes },
                    };
                    if (!string.IsNullOrEmpty(item.detail)) d["detail"] = item.detail;
                    if (!string.IsNullOrEmpty(item.assetPath)) d["assetPath"] = item.assetPath;
                    return d;
                }).ToArray();

                result["topAssets"] = topItems;
            }

            return result;
        }

        private struct AssetMemInfo
        {
            public string name;
            public long sizeBytes;
            public string detail;
            public string assetPath;
        }

        // ─── Top Memory Consumers ───

        /// <summary>
        /// Get the top N memory-consuming assets across all types.
        /// </summary>
        public static object GetTopMemoryConsumers(Dictionary<string, object> args)
        {
            int count = args.ContainsKey("count") ? Convert.ToInt32(args["count"]) : 20;
            string filterType = args.ContainsKey("type") ? args["type"].ToString().ToLower() : "";

            var allAssets = new List<Dictionary<string, object>>();

            // Scan all relevant types
            if (filterType == "" || filterType == "texture")
                ScanType<Texture2D>(allAssets, "Texture2D");
            if (filterType == "" || filterType == "rendertexture")
                ScanType<RenderTexture>(allAssets, "RenderTexture");
            if (filterType == "" || filterType == "mesh")
                ScanType<Mesh>(allAssets, "Mesh");
            if (filterType == "" || filterType == "audioclip" || filterType == "audio")
                ScanType<AudioClip>(allAssets, "AudioClip");
            if (filterType == "" || filterType == "material")
                ScanType<Material>(allAssets, "Material");
            if (filterType == "" || filterType == "shader")
                ScanType<Shader>(allAssets, "Shader");
            if (filterType == "" || filterType == "animationclip" || filterType == "animation")
                ScanType<AnimationClip>(allAssets, "AnimationClip");
            if (filterType == "" || filterType == "font")
                ScanType<Font>(allAssets, "Font");

            // Sort by size descending
            allAssets.Sort((a, b) => ((long)b["sizeBytes"]).CompareTo((long)a["sizeBytes"]));

            var topAssets = allAssets.Take(count).ToArray();

            long grandTotal = allAssets.Sum(a => (long)a["sizeBytes"]);

            return new Dictionary<string, object>
            {
                { "totalScannedAssets", allAssets.Count },
                { "totalScannedMB", Math.Round(grandTotal / (1024.0 * 1024.0), 2) },
                { "returnedCount", topAssets.Length },
                { "filterType", string.IsNullOrEmpty(filterType) ? "all" : filterType },
                { "assets", topAssets },
            };
        }

        private static void ScanType<T>(List<Dictionary<string, object>> output, string typeName) where T : UnityEngine.Object
        {
            var objects = Resources.FindObjectsOfTypeAll<T>();
            foreach (var obj in objects)
            {
                long size = Profiler.GetRuntimeMemorySizeLong(obj);
                if (size <= 0) continue;

                string path = AssetDatabase.GetAssetPath(obj);

                output.Add(new Dictionary<string, object>
                {
                    { "name", string.IsNullOrEmpty(obj.name) ? "(unnamed)" : obj.name },
                    { "type", typeName },
                    { "sizeMB", Math.Round(size / (1024.0 * 1024.0), 3) },
                    { "sizeBytes", size },
                    { "assetPath", path ?? "" },
                });
            }
        }

        // ─── Memory Snapshot (requires com.unity.memoryprofiler) ───

        /// <summary>
        /// Take a memory snapshot using the Memory Profiler package API.
        /// Returns error if the package is not installed.
        /// Uses reflection to avoid compile-time dependency.
        /// </summary>
        public static object TakeMemorySnapshot(Dictionary<string, object> args)
        {
            if (!IsMemoryProfilerPackageInstalled())
            {
                return new Dictionary<string, object>
                {
                    { "error", "com.unity.memoryprofiler package is not installed. Install it via Package Manager to use memory snapshots." },
                    { "alternatives", new string[] {
                        "profiler/memory-breakdown - Get per-asset-type memory breakdown (built-in, always available)",
                        "profiler/memory-top-assets - Get top N memory consumers (built-in, always available)",
                        "profiler/memory - Get basic memory stats (built-in, always available)",
                    }},
                };
            }

            try
            {
                // Use reflection to call the experimental MemoryProfiler API
                // UnityEditor.Profiling.Memory.Experimental.MemoryProfiler.TakeSnapshot(path, callback, captureFlags)
                var memProfilerType = Type.GetType(
                    "UnityEditor.Profiling.Memory.Experimental.MemoryProfiler, UnityEditor.CoreModule")
                    ?? Type.GetType(
                    "UnityEditor.Profiling.Memory.Experimental.MemoryProfiler, UnityEditor");

                if (memProfilerType == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "error", "MemoryProfiler experimental API not found in this Unity version." },
                    };
                }

                string snapshotDir = args.ContainsKey("path")
                    ? args["path"].ToString()
                    : Path.Combine(Application.temporaryCachePath, "MemorySnapshots");

                if (!Directory.Exists(snapshotDir))
                    Directory.CreateDirectory(snapshotDir);

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string snapshotPath = Path.Combine(snapshotDir, $"snapshot_{timestamp}.snap");

                // Find the TakeSnapshot method
                var takeSnapshot = memProfilerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "TakeSnapshot" && m.GetParameters().Length >= 2);

                if (takeSnapshot == null)
                {
                    return new Dictionary<string, object>
                    {
                        { "error", "TakeSnapshot method not found on MemoryProfiler type." },
                    };
                }

                // Call TakeSnapshot - the callback is complex, so we do a fire-and-forget approach
                // and report the path where the snapshot will be saved
                var captureFlags = 0x1F; // CaptureFlags.ManagedObjects | NativeObjects | NativeAllocations | NativeAllocationSites | NativeStackTraces
                var captureFlagsType = Type.GetType(
                    "UnityEditor.Profiling.Memory.Experimental.CaptureFlags, UnityEditor.CoreModule")
                    ?? Type.GetType(
                    "UnityEditor.Profiling.Memory.Experimental.CaptureFlags, UnityEditor");

                if (captureFlagsType != null)
                    captureFlags = Convert.ToInt32(Enum.ToObject(captureFlagsType, 0x1F));

                // Use the simple overload: TakeSnapshot(string path, Action<string, bool> finishCallback)
                var simpleOverload = memProfilerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "TakeSnapshot")
                    .OrderBy(m => m.GetParameters().Length)
                    .FirstOrDefault();

                if (simpleOverload != null)
                {
                    Action<string, bool> callback = (path, success) =>
                    {
                        if (success)
                            Debug.Log($"[MCP] Memory snapshot saved to: {path}");
                        else
                            Debug.LogWarning($"[MCP] Memory snapshot failed: {path}");
                    };

                    var parameters = simpleOverload.GetParameters();
                    if (parameters.Length == 2)
                        simpleOverload.Invoke(null, new object[] { snapshotPath, callback });
                    else if (parameters.Length == 3)
                        simpleOverload.Invoke(null, new object[] { snapshotPath, callback, Enum.ToObject(captureFlagsType, 0x1F) });
                }

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "snapshotPath", snapshotPath },
                    { "note", "Snapshot capture initiated. It may take a few seconds to complete. Open the Memory Profiler window to inspect it." },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    { "error", "Failed to take memory snapshot: " + ex.Message },
                };
            }
        }

        // ─── Helpers ───

        private static bool GetBool(Dictionary<string, object> args, string key, bool defaultValue)
        {
            if (!args.ContainsKey(key)) return defaultValue;
            var val = args[key];
            if (val is bool b) return b;
            if (val is string s) return s.ToLowerInvariant() == "true";
            return defaultValue;
        }
    }
}
