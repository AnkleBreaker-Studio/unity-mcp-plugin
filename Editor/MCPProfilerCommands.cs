using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Commands for Unity Profiler and Frame Debugger integration.
    /// Provides performance analysis, memory profiling, rendering stats,
    /// and frame debugger event inspection via MCP.
    ///
    /// Frame Debugger APIs use reflection because Unity 6 moved them to
    /// UnityEditorInternal.FrameDebuggerInternal (internal nested namespace).
    /// </summary>
    public static class MCPProfilerCommands
    {
        // Cached reflection types for Frame Debugger (Unity 6+)
        private static Type _fdUtilType;
        private static Type _fdEventDataType;
        private static bool _fdTypesResolved;

        private static void ResolveFDTypes()
        {
            if (_fdTypesResolved) return;
            _fdTypesResolved = true;

            _fdUtilType = Type.GetType(
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility, UnityEditor.CoreModule")
                ?? Type.GetType(
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility, UnityEditor");

            _fdEventDataType = Type.GetType(
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData, UnityEditor.CoreModule")
                ?? Type.GetType(
                "UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData, UnityEditor");
        }

        // ─── Profiler Control ───

        /// <summary>
        /// Enable/disable the Unity Profiler. Optionally enable deep profiling.
        /// </summary>
        public static object EnableProfiler(Dictionary<string, object> args)
        {
            bool enable = !args.ContainsKey("enabled") || GetBool(args, "enabled", true);

            ProfilerDriver.enabled = enable;

            if (args.ContainsKey("deepProfiling"))
            {
                bool deep = GetBool(args, "deepProfiling", false);
                ProfilerDriver.deepProfiling = deep;
            }

            return new Dictionary<string, object>
            {
                { "success", true },
                { "profilerEnabled", ProfilerDriver.enabled },
                { "deepProfiling", ProfilerDriver.deepProfiling },
                { "firstFrame", ProfilerDriver.firstFrameIndex },
                { "lastFrame", ProfilerDriver.lastFrameIndex },
            };
        }

        // ─── Rendering Stats ───

        /// <summary>
        /// Get current rendering statistics from UnityStats.
        /// Best used while the game is running (Play mode) for meaningful data.
        /// </summary>
        public static object GetRenderingStats(Dictionary<string, object> args)
        {
            try
            {
                var statsType = typeof(UnityEditor.UnityStats);
                var result = new Dictionary<string, object>();

                // Use reflection to read all static properties safely
                string[] intProps = {
                    "batches", "drawCalls", "indirectDrawCalls",
                    "dynamicBatchedDrawCalls", "staticBatchedDrawCalls", "instancedBatchedDrawCalls",
                    "dynamicBatches", "staticBatches", "instancedBatches",
                    "setPassCalls", "triangles", "vertices",
                    "shadowCasters", "renderTextureChanges",
                    "renderTextureCount", "renderTextureBytes",
                    "usedTextureMemorySize", "usedTextureCount",
                    "vboTotal", "vboTotalBytes", "vboUploads", "vboUploadBytes",
                    "ibUploads", "ibUploadBytes",
                    "visibleSkinnedMeshes", "animationComponentsPlaying", "animatorComponentsPlaying"
                };

                foreach (string propName in intProps)
                {
                    var prop = statsType.GetProperty(propName, BindingFlags.Public | BindingFlags.Static);
                    if (prop != null)
                        result[propName] = prop.GetValue(null);
                }

                // Float properties
                string[] floatProps = { "frameTime", "renderTime" };
                foreach (string propName in floatProps)
                {
                    var prop = statsType.GetProperty(propName, BindingFlags.Public | BindingFlags.Static);
                    if (prop != null)
                        result[propName] = prop.GetValue(null);
                }

                // String properties
                var screenRes = statsType.GetProperty("screenRes", BindingFlags.Public | BindingFlags.Static);
                if (screenRes != null)
                    result["screenResolution"] = screenRes.GetValue(null);

                result["isPlaying"] = EditorApplication.isPlaying;
                if (!EditorApplication.isPlaying)
                    result["note"] = "Stats are most meaningful during Play mode. Enter play mode for accurate rendering data.";

                return result;
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", "Failed to read UnityStats: " + ex.Message } };
            }
        }

        // ─── Memory Info ───

        /// <summary>
        /// Get detailed memory usage breakdown.
        /// </summary>
        public static object GetMemoryInfo(Dictionary<string, object> args)
        {
            long totalAllocated = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            long totalReserved = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong();
            long totalUnused = UnityEngine.Profiling.Profiler.GetTotalUnusedReservedMemoryLong();
            long monoUsed = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            long monoHeap = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong();
            long gfxDriver = UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver();
            long tempAlloc = UnityEngine.Profiling.Profiler.GetTempAllocatorSize();

            return new Dictionary<string, object>
            {
                { "totalAllocatedMB", Math.Round(totalAllocated / (1024.0 * 1024.0), 2) },
                { "totalReservedMB", Math.Round(totalReserved / (1024.0 * 1024.0), 2) },
                { "totalUnusedReservedMB", Math.Round(totalUnused / (1024.0 * 1024.0), 2) },
                { "monoUsedMB", Math.Round(monoUsed / (1024.0 * 1024.0), 2) },
                { "monoHeapMB", Math.Round(monoHeap / (1024.0 * 1024.0), 2) },
                { "monoFragmentationPercent", monoHeap > 0 ? Math.Round((1.0 - (double)monoUsed / monoHeap) * 100, 1) : 0 },
                { "gfxDriverMB", Math.Round(gfxDriver / (1024.0 * 1024.0), 2) },
                { "tempAllocatorMB", Math.Round(tempAlloc / (1024.0 * 1024.0), 2) },
                { "totalAllocatedBytes", totalAllocated },
                { "totalReservedBytes", totalReserved },
                { "monoUsedBytes", monoUsed },
                { "monoHeapBytes", monoHeap },
                { "gfxDriverBytes", gfxDriver },
            };
        }

        // ─── Profiler Frame Data ───

        /// <summary>
        /// Get CPU timing hierarchy for a specific profiler frame.
        /// Requires the profiler to be enabled and have recorded frames.
        /// </summary>
        public static object GetFrameData(Dictionary<string, object> args)
        {
            if (!ProfilerDriver.enabled)
                return new Dictionary<string, object> { { "error", "Profiler is not enabled. Call profiler/enable first." } };

            int frameIndex = args.ContainsKey("frameIndex")
                ? Convert.ToInt32(args["frameIndex"])
                : (int)ProfilerDriver.lastFrameIndex;

            int threadIndex = args.ContainsKey("threadIndex")
                ? Convert.ToInt32(args["threadIndex"])
                : 0; // 0 = Main Thread

            int maxItems = args.ContainsKey("maxItems")
                ? Convert.ToInt32(args["maxItems"])
                : 30;

            float minTimeMs = args.ContainsKey("minTimeMs")
                ? Convert.ToSingle(args["minTimeMs"])
                : 0.0f;

            if (frameIndex < ProfilerDriver.firstFrameIndex || frameIndex > ProfilerDriver.lastFrameIndex)
            {
                return new Dictionary<string, object>
                {
                    { "error", $"Frame {frameIndex} out of range [{ProfilerDriver.firstFrameIndex}, {ProfilerDriver.lastFrameIndex}]" },
                    { "firstFrame", ProfilerDriver.firstFrameIndex },
                    { "lastFrame", ProfilerDriver.lastFrameIndex },
                };
            }

            try
            {
                // Sort by total time descending for most useful view
                using (var frameData = ProfilerDriver.GetHierarchyFrameDataView(
                    frameIndex, threadIndex,
                    HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                    HierarchyFrameDataView.columnTotalTime,
                    false))
                {
                    if (!frameData.valid)
                        return new Dictionary<string, object> { { "error", $"No valid data for frame {frameIndex}, thread {threadIndex}" } };

                    var items = new List<Dictionary<string, object>>();
                    int rootId = frameData.GetRootItemID();

                    // Get children of root (top-level timing entries)
                    var children = new List<int>();
                    frameData.GetItemChildren(rootId, children);

                    CollectItems(frameData, children, items, maxItems, minTimeMs, 0, 3);

                    return new Dictionary<string, object>
                    {
                        { "frameIndex", frameIndex },
                        { "threadIndex", threadIndex },
                        { "threadName", frameData.threadName },
                        { "frameTotalMs", Math.Round(frameData.frameTimeMs, 3) },
                        { "frameGpuMs", Math.Round(frameData.frameGpuTimeMs, 3) },
                        { "frameFps", Math.Round(frameData.frameFps, 1) },
                        { "sampleCount", frameData.sampleCount },
                        { "items", items },
                        { "itemCount", items.Count },
                        { "firstFrame", ProfilerDriver.firstFrameIndex },
                        { "lastFrame", ProfilerDriver.lastFrameIndex },
                    };
                }
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", "Failed to read frame data: " + ex.Message } };
            }
        }

        private static void CollectItems(HierarchyFrameDataView frameData, List<int> itemIds,
            List<Dictionary<string, object>> output, int maxItems, float minTimeMs, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;

            foreach (int id in itemIds)
            {
                if (output.Count >= maxItems) break;

                float totalTime = frameData.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime);
                float selfTime = frameData.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnSelfTime);

                if (totalTime < minTimeMs && depth > 0) continue;

                string calls = frameData.GetItemColumnData(id, HierarchyFrameDataView.columnCalls);
                string gcAlloc = frameData.GetItemColumnData(id, HierarchyFrameDataView.columnGcMemory);

                var item = new Dictionary<string, object>
                {
                    { "name", frameData.GetItemName(id) },
                    { "depth", depth },
                    { "totalMs", Math.Round(totalTime, 3) },
                    { "selfMs", Math.Round(selfTime, 3) },
                    { "calls", calls },
                    { "gcAlloc", gcAlloc },
                };

                output.Add(item);

                // Recurse into children
                if (frameData.HasItemChildren(id) && depth < maxDepth)
                {
                    var children = new List<int>();
                    frameData.GetItemChildren(id, children);
                    CollectItems(frameData, children, output, maxItems, minTimeMs, depth + 1, maxDepth);
                }
            }
        }

        // ─── Frame Debugger Control ───

        /// <summary>
        /// Enable/disable the Frame Debugger.
        /// </summary>
        public static object EnableFrameDebugger(Dictionary<string, object> args)
        {
            ResolveFDTypes();
            if (_fdUtilType == null)
                return new Dictionary<string, object> { { "error", "FrameDebuggerUtility type not found. This Unity version may not support Frame Debugger via API." } };

            bool enable = !args.ContainsKey("enabled") || GetBool(args, "enabled", true);

            try
            {
                // SetEnabled(bool enabled, int remotePlayerGUID)
                var setEnabled = _fdUtilType.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Static);
                if (setEnabled == null)
                    return new Dictionary<string, object> { { "error", "SetEnabled method not found on FrameDebuggerUtility" } };

                setEnabled.Invoke(null, new object[] { enable, 0 });

                // Read current state
                int count = GetFDStaticInt("count");
                int limit = GetFDStaticInt("limit");

                // Also open/close the window
                if (enable)
                    EditorWindow.GetWindow(Type.GetType("UnityEditor.FrameDebuggerWindow, UnityEditor.CoreModule")
                        ?? Type.GetType("UnityEditor.FrameDebuggerWindow, UnityEditor"));

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "enabled", enable },
                    { "eventCount", count },
                    { "currentEvent", limit },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", "Failed to control Frame Debugger: " + ex.Message } };
            }
        }

        // ─── Frame Debugger Events ───

        /// <summary>
        /// Get the list of all frame debugger events (draw calls, clears, etc.).
        /// Frame Debugger must be enabled first.
        /// </summary>
        public static object GetFrameEvents(Dictionary<string, object> args)
        {
            ResolveFDTypes();
            if (_fdUtilType == null)
                return new Dictionary<string, object> { { "error", "FrameDebuggerUtility type not found." } };

            int count = GetFDStaticInt("count");
            if (count <= 0)
                return new Dictionary<string, object>
                {
                    { "error", "No frame debugger events. Make sure the Frame Debugger is enabled (call debugger/enable) and the game has rendered at least one frame." },
                    { "eventCount", 0 },
                };

            int maxEvents = args.ContainsKey("maxEvents")
                ? Convert.ToInt32(args["maxEvents"])
                : 100;

            try
            {
                // GetFrameEvents() returns FrameDebuggerEvent[]
                var getEvents = _fdUtilType.GetMethod("GetFrameEvents", BindingFlags.Public | BindingFlags.Static);
                if (getEvents == null)
                    return new Dictionary<string, object> { { "error", "GetFrameEvents method not found." } };

                var eventsArray = getEvents.Invoke(null, null) as Array;
                if (eventsArray == null)
                    return new Dictionary<string, object> { { "error", "GetFrameEvents returned null." } };

                var events = new List<Dictionary<string, object>>();
                int evtCount = Math.Min(eventsArray.Length, maxEvents);

                for (int i = 0; i < evtCount; i++)
                {
                    string eventName = GetFrameEventName(i);
                    var evt = eventsArray.GetValue(i);

                    var eventInfo = new Dictionary<string, object>
                    {
                        { "index", i },
                        { "name", eventName ?? "Unknown" },
                    };

                    // Try to read type field
                    try
                    {
                        var typeField = evt.GetType().GetField("m_Type");
                        if (typeField != null)
                            eventInfo["type"] = typeField.GetValue(evt).ToString();
                    }
                    catch { }

                    events.Add(eventInfo);
                }

                return new Dictionary<string, object>
                {
                    { "totalEvents", count },
                    { "returnedEvents", events.Count },
                    { "currentEvent", GetFDStaticInt("limit") },
                    { "events", events },
                };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", "Failed to get frame events: " + ex.Message } };
            }
        }

        // ─── Frame Debugger Event Details ───

        /// <summary>
        /// Get detailed information about a specific frame debugger event.
        /// Includes shader, mesh, render target, blend state, etc.
        /// </summary>
        public static object GetFrameEventDetails(Dictionary<string, object> args)
        {
            ResolveFDTypes();
            if (_fdUtilType == null || _fdEventDataType == null)
                return new Dictionary<string, object> { { "error", "Frame Debugger types not found." } };

            int index = args.ContainsKey("index") ? Convert.ToInt32(args["index"]) : GetFDStaticInt("limit");
            int count = GetFDStaticInt("count");

            if (index < 0 || index >= count)
                return new Dictionary<string, object> { { "error", $"Event index {index} out of range [0, {count - 1}]" } };

            try
            {
                // Navigate to the event
                SetFDStaticInt("limit", index);

                // Create FrameDebuggerEventData instance
                var eventData = Activator.CreateInstance(_fdEventDataType);

                // GetFrameEventData(int index, FrameDebuggerEventData data) -> bool
                var getEventData = _fdUtilType.GetMethod("GetFrameEventData",
                    BindingFlags.Public | BindingFlags.Static);

                if (getEventData == null)
                    return new Dictionary<string, object> { { "error", "GetFrameEventData method not found." } };

                bool success = (bool)getEventData.Invoke(null, new object[] { index, eventData });
                if (!success)
                    return new Dictionary<string, object> { { "error", $"Failed to get data for event {index}" } };

                // Extract fields via reflection
                var result = new Dictionary<string, object>
                {
                    { "index", index },
                    { "name", GetFrameEventName(index) ?? "Unknown" },
                };

                // Read key fields from the event data
                ReadField(eventData, "m_VertexCount", result, "vertexCount");
                ReadField(eventData, "m_IndexCount", result, "indexCount");
                ReadField(eventData, "m_InstanceCount", result, "instanceCount");
                ReadField(eventData, "m_DrawCallCount", result, "drawCallCount");
                ReadField(eventData, "m_OriginalShaderName", result, "originalShader");
                ReadField(eventData, "m_RealShaderName", result, "realShader");
                ReadField(eventData, "m_PassName", result, "passName");
                ReadField(eventData, "m_PassLightMode", result, "passLightMode");
                ReadField(eventData, "m_ShaderPassIndex", result, "shaderPassIndex");
                ReadField(eventData, "m_SubShaderIndex", result, "subShaderIndex");
                ReadField(eventData, "shaderKeywords", result, "shaderKeywords");
                ReadField(eventData, "m_RenderTargetName", result, "renderTarget");
                ReadField(eventData, "m_RenderTargetWidth", result, "renderTargetWidth");
                ReadField(eventData, "m_RenderTargetHeight", result, "renderTargetHeight");
                ReadField(eventData, "m_RenderTargetFormat", result, "renderTargetFormat");
                ReadField(eventData, "m_RenderTargetCount", result, "renderTargetCount");
                ReadField(eventData, "m_MeshSubset", result, "meshSubset");
                ReadField(eventData, "m_BatchBreakCause", result, "batchBreakCauseIndex");
                ReadField(eventData, "m_ComputeShaderName", result, "computeShader");
                ReadField(eventData, "m_ComputeShaderKernelName", result, "computeKernel");

                // Resolve batch break cause string
                try
                {
                    var getBatchBreak = _fdUtilType.GetMethod("GetBatchBreakCauseStrings",
                        BindingFlags.Public | BindingFlags.Static);
                    if (getBatchBreak != null)
                    {
                        var causes = getBatchBreak.Invoke(null, null) as string[];
                        if (causes != null && result.ContainsKey("batchBreakCauseIndex"))
                        {
                            int causeIdx = Convert.ToInt32(result["batchBreakCauseIndex"]);
                            if (causeIdx >= 0 && causeIdx < causes.Length)
                                result["batchBreakCause"] = causes[causeIdx];
                        }
                    }
                }
                catch { }

                // Mesh info
                try
                {
                    var meshField = _fdEventDataType.GetField("m_Mesh");
                    if (meshField != null)
                    {
                        var mesh = meshField.GetValue(eventData) as Mesh;
                        if (mesh != null)
                            result["meshName"] = mesh.name;
                    }
                }
                catch { }

                return result;
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { { "error", "Failed to get event details: " + ex.Message } };
            }
        }

        // ─── Performance Analysis ───

        /// <summary>
        /// Comprehensive performance snapshot with optimization suggestions.
        /// Combines rendering stats, memory info, and profiler data.
        /// </summary>
        public static object AnalyzePerformance(Dictionary<string, object> args)
        {
            var result = new Dictionary<string, object>();
            var suggestions = new List<string>();

            // 1. Memory
            long totalAllocMB = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024 * 1024);
            long monoUsedMB = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / (1024 * 1024);
            long monoHeapMB = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / (1024 * 1024);
            long gfxMB = UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver() / (1024 * 1024);

            result["memory"] = new Dictionary<string, object>
            {
                { "totalAllocatedMB", totalAllocMB },
                { "monoUsedMB", monoUsedMB },
                { "monoHeapMB", monoHeapMB },
                { "gfxDriverMB", gfxMB },
            };

            if (monoHeapMB > 0 && (double)monoUsedMB / monoHeapMB < 0.5)
                suggestions.Add($"High Mono heap fragmentation: {monoUsedMB}MB used of {monoHeapMB}MB heap ({Math.Round((double)monoUsedMB / monoHeapMB * 100)}% utilization). Consider reducing allocations to allow the heap to shrink.");

            if (gfxMB > 512)
                suggestions.Add($"High GPU memory usage ({gfxMB}MB). Review texture sizes, compression settings, and render texture usage.");

            // 2. Rendering stats (if in play mode)
            if (EditorApplication.isPlaying)
            {
                try
                {
                    var statsType = typeof(UnityEditor.UnityStats);
                    int batches = (int)statsType.GetProperty("batches", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                    int drawCalls = (int)statsType.GetProperty("drawCalls", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                    int setPass = (int)statsType.GetProperty("setPassCalls", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                    int tris = (int)statsType.GetProperty("triangles", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                    int verts = (int)statsType.GetProperty("vertices", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                    int dynBatched = (int)statsType.GetProperty("dynamicBatchedDrawCalls", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                    int staticBatched = (int)statsType.GetProperty("staticBatchedDrawCalls", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                    float frameTime = (float)statsType.GetProperty("frameTime", BindingFlags.Public | BindingFlags.Static).GetValue(null);

                    result["rendering"] = new Dictionary<string, object>
                    {
                        { "batches", batches },
                        { "drawCalls", drawCalls },
                        { "setPassCalls", setPass },
                        { "triangles", tris },
                        { "vertices", verts },
                        { "dynamicBatched", dynBatched },
                        { "staticBatched", staticBatched },
                        { "frameTimeMs", Math.Round(frameTime, 2) },
                        { "estimatedFps", frameTime > 0 ? Math.Round(1000.0 / frameTime, 1) : 0 },
                    };

                    if (setPass > 50)
                        suggestions.Add($"High SetPass call count ({setPass}). Consider using fewer unique materials/shaders, enable GPU instancing, or use SRP Batcher.");
                    if (batches > 200)
                        suggestions.Add($"High batch count ({batches}). Enable static/dynamic batching, GPU instancing, or combine meshes.");
                    if (tris > 500000)
                        suggestions.Add($"High triangle count ({tris}). Consider LOD groups, mesh simplification, or occlusion culling.");
                    if (dynBatched + staticBatched == 0 && drawCalls > 50)
                        suggestions.Add("No batching detected. Enable Static Batching (mark objects as static) and Dynamic Batching in Player Settings.");
                }
                catch { }
            }
            else
            {
                result["rendering"] = new Dictionary<string, object>
                {
                    { "note", "Enter Play mode for rendering stats." }
                };
            }

            // 3. Profiler frame data (if profiler is enabled)
            if (ProfilerDriver.enabled && ProfilerDriver.lastFrameIndex > 0)
            {
                try
                {
                    int frame = (int)ProfilerDriver.lastFrameIndex;
                    using (var frameData = ProfilerDriver.GetHierarchyFrameDataView(
                        frame, 0,
                        HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName,
                        HierarchyFrameDataView.columnSelfTime, false))
                    {
                        if (frameData.valid)
                        {
                            result["profiler"] = new Dictionary<string, object>
                            {
                                { "frameIndex", frame },
                                { "frameTotalMs", Math.Round(frameData.frameTimeMs, 3) },
                                { "frameGpuMs", Math.Round(frameData.frameGpuTimeMs, 3) },
                                { "frameFps", Math.Round(frameData.frameFps, 1) },
                                { "sampleCount", frameData.sampleCount },
                            };

                            // Get top 5 hotspots by self time
                            var hotspots = new List<Dictionary<string, object>>();
                            int rootId = frameData.GetRootItemID();
                            var rootChildren = new List<int>();
                            frameData.GetItemChildren(rootId, rootChildren);
                            CollectHotspots(frameData, rootChildren, hotspots, 5, 0, 4);

                            result["hotspots"] = hotspots;

                            if (frameData.frameTimeMs > 33.3)
                                suggestions.Add($"Frame time {frameData.frameTimeMs:F1}ms exceeds 30fps budget (33.3ms). Check hotspots for optimization opportunities.");
                            if (frameData.frameGpuTimeMs > 16.6)
                                suggestions.Add($"GPU frame time {frameData.frameGpuTimeMs:F1}ms is high. Consider reducing shader complexity, overdraw, or resolution.");
                        }
                    }
                }
                catch { }
            }
            else
            {
                result["profiler"] = new Dictionary<string, object>
                {
                    { "note", "Enable the profiler (profiler/enable) and run the game for CPU timing data." }
                };
            }

            // 4. Scene complexity
            var allRenderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            var allLights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            int totalGameObjects = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Length;

            result["sceneComplexity"] = new Dictionary<string, object>
            {
                { "gameObjectCount", totalGameObjects },
                { "rendererCount", allRenderers.Length },
                { "lightCount", allLights.Length },
                { "realtimeLights", allLights.Count(l => l.lightmapBakeType == LightmapBakeType.Realtime) },
                { "bakedLights", allLights.Count(l => l.lightmapBakeType == LightmapBakeType.Baked) },
                { "shadowCastingLights", allLights.Count(l => l.shadows != LightShadows.None) },
            };

            int realtimeShadowLights = allLights.Count(l => l.shadows != LightShadows.None && l.lightmapBakeType == LightmapBakeType.Realtime);
            if (realtimeShadowLights > 2)
                suggestions.Add($"{realtimeShadowLights} realtime shadow-casting lights detected. Consider baking shadows or limiting realtime shadow lights for better performance.");

            result["suggestions"] = suggestions;
            result["suggestionCount"] = suggestions.Count;

            return result;
        }

        private static void CollectHotspots(HierarchyFrameDataView frameData, List<int> itemIds,
            List<Dictionary<string, object>> output, int maxItems, int depth, int maxDepth)
        {
            if (depth > maxDepth) return;

            foreach (int id in itemIds)
            {
                if (output.Count >= maxItems) break;

                float selfTime = frameData.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnSelfTime);
                float totalTime = frameData.GetItemColumnDataAsFloat(id, HierarchyFrameDataView.columnTotalTime);

                if (selfTime > 0.1f) // Only items with > 0.1ms self time
                {
                    output.Add(new Dictionary<string, object>
                    {
                        { "name", frameData.GetItemName(id) },
                        { "selfMs", Math.Round(selfTime, 3) },
                        { "totalMs", Math.Round(totalTime, 3) },
                        { "calls", frameData.GetItemColumnData(id, HierarchyFrameDataView.columnCalls) },
                        { "gcAlloc", frameData.GetItemColumnData(id, HierarchyFrameDataView.columnGcMemory) },
                    });
                }

                if (frameData.HasItemChildren(id) && depth < maxDepth)
                {
                    var children = new List<int>();
                    frameData.GetItemChildren(id, children);
                    CollectHotspots(frameData, children, output, maxItems, depth + 1, maxDepth);
                }
            }

            // Sort by self time descending
            if (depth == 0)
                output.Sort((a, b) => ((double)b["selfMs"]).CompareTo((double)a["selfMs"]));
        }

        // ─── Helpers ───

        private static int GetFDStaticInt(string propertyName)
        {
            if (_fdUtilType == null) return -1;
            var prop = _fdUtilType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
            if (prop == null) return -1;
            return (int)prop.GetValue(null);
        }

        private static void SetFDStaticInt(string propertyName, int value)
        {
            if (_fdUtilType == null) return;
            var prop = _fdUtilType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
            if (prop != null && prop.CanWrite)
                prop.SetValue(null, value);
        }

        private static string GetFrameEventName(int index)
        {
            if (_fdUtilType == null) return null;
            try
            {
                var method = _fdUtilType.GetMethod("GetFrameEventInfoName", BindingFlags.Public | BindingFlags.Static);
                return method?.Invoke(null, new object[] { index }) as string;
            }
            catch { return null; }
        }

        private static void ReadField(object obj, string fieldName, Dictionary<string, object> output, string outputKey)
        {
            try
            {
                var field = obj.GetType().GetField(fieldName);
                if (field != null)
                {
                    var value = field.GetValue(obj);
                    if (value != null)
                        output[outputKey] = value;
                }
            }
            catch { }
        }

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
