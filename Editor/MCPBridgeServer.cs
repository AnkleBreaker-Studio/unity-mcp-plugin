using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// HTTP server that runs inside the Unity Editor, enabling external MCP tools
    /// to control the editor via REST API calls.
    /// </summary>
    [InitializeOnLoad]
    public static class MCPBridgeServer
    {
        private static HttpListener _listener;
        private static Thread _listenerThread;
        private static bool _isRunning;
        private static int Port => MCPSettingsManager.Port;
        private static readonly Queue<Action> _mainThreadQueue = new Queue<Action>();

        static MCPBridgeServer()
        {
            if (MCPSettingsManager.AutoStart)
                Start();
            EditorApplication.update += ProcessMainThreadQueue;
            EditorApplication.quitting += Stop;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        /// <summary>
        /// Gracefully stop the server before Unity reloads assemblies (script recompile).
        /// This prevents ThreadAbortExceptions during domain reload.
        /// </summary>
        private static void OnBeforeAssemblyReload()
        {
            if (_isRunning)
                Stop();
        }

        /// <summary>Whether the server is currently running.</summary>
        public static bool IsRunning => _isRunning;

        public static void Start()
        {
            if (_isRunning) return;

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
                _isRunning = true;

                _listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "MCP Bridge Server"
                };
                _listenerThread.Start();

                Debug.Log($"[MCP Bridge] Server started on port {Port}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MCP Bridge] Failed to start: {ex.Message}");
            }
        }

        public static void Stop()
        {
            _isRunning = false;
            try
            {
                _listener?.Stop();
                _listener?.Close();
                _listenerThread?.Join(1000);
            }
            catch { }
            Debug.Log("[MCP Bridge] Server stopped");
        }

        private static void ListenLoop()
        {
            while (_isRunning)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
                }
                catch (HttpListenerException) when (!_isRunning)
                {
                    break;
                }
                catch (ThreadAbortException)
                {
                    // Expected during domain reload or editor shutdown — exit silently
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // Listener was disposed during shutdown — exit silently
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                        Debug.LogError($"[MCP Bridge] Listener error: {ex.Message}");
                }
            }
        }

        private static void HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                // Parse the API path: /api/{category}/{action}
                string path = request.Url.AbsolutePath.TrimStart('/');
                if (!path.StartsWith("api/"))
                {
                    SendJson(response, 404, new { error = "Not found" });
                    return;
                }

                string apiPath = path.Substring(4); // Remove "api/"
                string body = "";
                if (request.HasEntityBody)
                {
                    using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                    {
                        body = reader.ReadToEnd();
                    }
                }

                // Extract agent identity from header
                string agentId = request.Headers["X-Agent-Id"] ?? "anonymous";

                // Route the request with agent tracking.
                // The entire RouteRequest runs on the main thread so all Unity APIs
                // (EditorPrefs, Application, etc.) work correctly.
                var result = MCPRequestQueue.ExecuteWithTracking(agentId, apiPath,
                    () => ExecuteOnMainThread(() => RouteRequest(apiPath, request.HttpMethod, body)));
                SendJson(response, 200, result);
            }
            catch (Exception ex)
            {
                SendJson(response, 500, new { error = ex.Message, stackTrace = ex.StackTrace });
            }
        }

        /// <summary>
        /// Route API requests to the appropriate handler.
        /// Many operations must run on the main thread, so we use ExecuteOnMainThread.
        /// </summary>
        private static string ExtractCategory(string path)
        {
            int slash = path.IndexOf('/');
            return slash > 0 ? path.Substring(0, slash) : path;
        }

        /// <summary>
        /// Route API requests to the appropriate handler.
        /// NOTE: This entire method runs on the main thread (dispatched by HandleRequest),
        /// so all Unity APIs (EditorPrefs, Application, etc.) work correctly here.
        /// No individual ExecuteOnMainThread wrappers needed.
        /// </summary>
        private static object RouteRequest(string path, string method, string body)
        {
            // Check if category is enabled (skip for ping and agents)
            string category = ExtractCategory(path);
            if (category != "ping" && category != "agents"
                && !MCPSettingsManager.IsCategoryEnabled(category))
            {
                return new { error = $"Category '{category}' is currently disabled. Enable it in Window > MCP Dashboard." };
            }

            switch (path)
            {
                // ─── Ping ───
                case "ping":
                    return new
                    {
                        status = "ok",
                        unityVersion = Application.unityVersion,
                        projectName = Application.productName,
                        projectPath = GetProjectPath(),
                        platform = Application.platform.ToString()
                    };

                // ─── Editor State ───
                case "editor/state":
                    return MCPEditorCommands.GetEditorState();

                case "editor/play-mode":
                    return MCPEditorCommands.SetPlayMode(ParseJson(body));

                case "editor/execute-menu-item":
                    return MCPEditorCommands.ExecuteMenuItem(ParseJson(body));

                case "editor/execute-code":
                    return MCPEditorCommands.ExecuteCode(ParseJson(body));

                // ─── Scene ───
                case "scene/info":
                    return MCPSceneCommands.GetSceneInfo();

                case "scene/open":
                    return MCPSceneCommands.OpenScene(ParseJson(body));

                case "scene/save":
                    return MCPSceneCommands.SaveScene();

                case "scene/new":
                    return MCPSceneCommands.NewScene();

                case "scene/hierarchy":
                    return MCPSceneCommands.GetHierarchy(ParseJson(body));

                // ─── GameObject ───
                case "gameobject/create":
                    return MCPGameObjectCommands.Create(ParseJson(body));

                case "gameobject/delete":
                    return MCPGameObjectCommands.Delete(ParseJson(body));

                case "gameobject/info":
                    return MCPGameObjectCommands.GetInfo(ParseJson(body));

                case "gameobject/set-transform":
                    return MCPGameObjectCommands.SetTransform(ParseJson(body));

                // ─── Component ───
                case "component/add":
                    return MCPComponentCommands.Add(ParseJson(body));

                case "component/remove":
                    return MCPComponentCommands.Remove(ParseJson(body));

                case "component/get-properties":
                    return MCPComponentCommands.GetProperties(ParseJson(body));

                case "component/set-property":
                    return MCPComponentCommands.SetProperty(ParseJson(body));

                // ─── Assets ───
                case "asset/list":
                    return MCPAssetCommands.List(ParseJson(body));

                case "asset/import":
                    return MCPAssetCommands.Import(ParseJson(body));

                case "asset/delete":
                    return MCPAssetCommands.Delete(ParseJson(body));

                case "asset/create-prefab":
                    return MCPAssetCommands.CreatePrefab(ParseJson(body));

                case "asset/instantiate-prefab":
                    return MCPAssetCommands.InstantiatePrefab(ParseJson(body));

                case "asset/create-material":
                    return MCPAssetCommands.CreateMaterial(ParseJson(body));

                // ─── Scripts ───
                case "script/create":
                    return MCPScriptCommands.Create(ParseJson(body));

                case "script/read":
                    return MCPScriptCommands.Read(ParseJson(body));

                case "script/update":
                    return MCPScriptCommands.Update(ParseJson(body));

                // ─── Renderer ───
                case "renderer/set-material":
                    return MCPRendererCommands.SetMaterial(ParseJson(body));

                // ─── Build ───
                case "build/start":
                    return MCPBuildCommands.StartBuild(ParseJson(body));

                // ─── Console ───
                case "console/log":
                    return MCPConsoleCommands.GetLog(ParseJson(body));

                case "console/clear":
                    return MCPConsoleCommands.Clear();

                // ─── Project ───
                case "project/info":
                    return MCPProjectCommands.GetInfo();

                // ─── Animation ───
                case "animation/create-controller":
                    return MCPAnimationCommands.CreateController(ParseJson(body));

                case "animation/controller-info":
                    return MCPAnimationCommands.GetControllerInfo(ParseJson(body));

                case "animation/add-parameter":
                    return MCPAnimationCommands.AddParameter(ParseJson(body));

                case "animation/remove-parameter":
                    return MCPAnimationCommands.RemoveParameter(ParseJson(body));

                case "animation/add-state":
                    return MCPAnimationCommands.AddState(ParseJson(body));

                case "animation/remove-state":
                    return MCPAnimationCommands.RemoveState(ParseJson(body));

                case "animation/add-transition":
                    return MCPAnimationCommands.AddTransition(ParseJson(body));

                case "animation/create-clip":
                    return MCPAnimationCommands.CreateClip(ParseJson(body));

                case "animation/clip-info":
                    return MCPAnimationCommands.GetClipInfo(ParseJson(body));

                case "animation/set-clip-curve":
                    return MCPAnimationCommands.SetClipCurve(ParseJson(body));

                case "animation/add-layer":
                    return MCPAnimationCommands.AddLayer(ParseJson(body));

                case "animation/assign-controller":
                    return MCPAnimationCommands.AssignController(ParseJson(body));

                case "animation/get-curve-keyframes":
                    return MCPAnimationCommands.GetCurveKeyframes(ParseJson(body));

                case "animation/remove-curve":
                    return MCPAnimationCommands.RemoveCurve(ParseJson(body));

                case "animation/add-keyframe":
                    return MCPAnimationCommands.AddKeyframe(ParseJson(body));

                case "animation/remove-keyframe":
                    return MCPAnimationCommands.RemoveKeyframe(ParseJson(body));

                case "animation/add-event":
                    return MCPAnimationCommands.AddAnimationEvent(ParseJson(body));

                case "animation/remove-event":
                    return MCPAnimationCommands.RemoveAnimationEvent(ParseJson(body));

                case "animation/get-events":
                    return MCPAnimationCommands.GetAnimationEvents(ParseJson(body));

                case "animation/set-clip-settings":
                    return MCPAnimationCommands.SetClipSettings(ParseJson(body));

                case "animation/remove-transition":
                    return MCPAnimationCommands.RemoveTransition(ParseJson(body));

                case "animation/remove-layer":
                    return MCPAnimationCommands.RemoveLayer(ParseJson(body));

                case "animation/create-blend-tree":
                    return MCPAnimationCommands.CreateBlendTree(ParseJson(body));

                case "animation/get-blend-tree":
                    return MCPAnimationCommands.GetBlendTreeInfo(ParseJson(body));

                // ─── Prefab (Advanced) ───
                case "prefab/info":
                    return MCPPrefabCommands.GetPrefabInfo(ParseJson(body));

                case "prefab/create-variant":
                    return MCPPrefabCommands.CreateVariant(ParseJson(body));

                case "prefab/apply-overrides":
                    return MCPPrefabCommands.ApplyOverrides(ParseJson(body));

                case "prefab/revert-overrides":
                    return MCPPrefabCommands.RevertOverrides(ParseJson(body));

                case "prefab/unpack":
                    return MCPPrefabCommands.Unpack(ParseJson(body));

                case "prefab/set-object-reference":
                    return MCPPrefabCommands.SetObjectReference(ParseJson(body));

                case "prefab/duplicate":
                    return MCPPrefabCommands.Duplicate(ParseJson(body));

                case "prefab/set-active":
                    return MCPPrefabCommands.SetActive(ParseJson(body));

                case "prefab/reparent":
                    return MCPPrefabCommands.Reparent(ParseJson(body));

                // ─── Physics ───
                case "physics/raycast":
                    return MCPPhysicsCommands.Raycast(ParseJson(body));

                case "physics/overlap-sphere":
                    return MCPPhysicsCommands.OverlapSphere(ParseJson(body));

                case "physics/overlap-box":
                    return MCPPhysicsCommands.OverlapBox(ParseJson(body));

                case "physics/collision-matrix":
                    return MCPPhysicsCommands.GetCollisionMatrix(ParseJson(body));

                case "physics/set-collision-layer":
                    return MCPPhysicsCommands.SetCollisionLayer(ParseJson(body));

                case "physics/set-gravity":
                    return MCPPhysicsCommands.SetGravity(ParseJson(body));

                // ─── Lighting ───
                case "lighting/info":
                    return MCPLightingCommands.GetLightingInfo(ParseJson(body));

                case "lighting/create":
                    return MCPLightingCommands.CreateLight(ParseJson(body));

                case "lighting/set-environment":
                    return MCPLightingCommands.SetEnvironment(ParseJson(body));

                case "lighting/create-reflection-probe":
                    return MCPLightingCommands.CreateReflectionProbe(ParseJson(body));

                case "lighting/create-light-probe-group":
                    return MCPLightingCommands.CreateLightProbeGroup(ParseJson(body));

                // ─── Audio ───
                case "audio/info":
                    return MCPAudioCommands.GetAudioInfo(ParseJson(body));

                case "audio/create-source":
                    return MCPAudioCommands.CreateAudioSource(ParseJson(body));

                case "audio/set-global":
                    return MCPAudioCommands.SetGlobalAudio(ParseJson(body));

                // ─── Tags & Layers ───
                case "taglayer/info":
                    return MCPTagLayerCommands.GetTagsAndLayers(ParseJson(body));

                case "taglayer/add-tag":
                    return MCPTagLayerCommands.AddTag(ParseJson(body));

                case "taglayer/set-tag":
                    return MCPTagLayerCommands.SetTag(ParseJson(body));

                case "taglayer/set-layer":
                    return MCPTagLayerCommands.SetLayer(ParseJson(body));

                case "taglayer/set-static":
                    return MCPTagLayerCommands.SetStatic(ParseJson(body));

                // ─── Selection & Scene View ───
                case "selection/get":
                    return MCPSelectionCommands.GetSelection(ParseJson(body));

                case "selection/set":
                    return MCPSelectionCommands.SetSelection(ParseJson(body));

                case "selection/focus-scene-view":
                    return MCPSelectionCommands.FocusSceneView(ParseJson(body));

                case "selection/find-by-type":
                    return MCPSelectionCommands.FindObjectsByType(ParseJson(body));

                // ─── Input Actions ───
                case "input/create":
                    return MCPInputCommands.CreateInputActions(ParseJson(body));

                case "input/info":
                    return MCPInputCommands.GetInputActionsInfo(ParseJson(body));

                case "input/add-map":
                    return MCPInputCommands.AddActionMap(ParseJson(body));

                case "input/remove-map":
                    return MCPInputCommands.RemoveActionMap(ParseJson(body));

                case "input/add-action":
                    return MCPInputCommands.AddAction(ParseJson(body));

                case "input/remove-action":
                    return MCPInputCommands.RemoveAction(ParseJson(body));

                case "input/add-binding":
                    return MCPInputCommands.AddBinding(ParseJson(body));

                case "input/add-composite-binding":
                    return MCPInputCommands.AddCompositeBinding(ParseJson(body));

                // ─── Assembly Definitions ───
                case "asmdef/create":
                    return MCPAssemblyDefCommands.CreateAssemblyDef(ParseJson(body));

                case "asmdef/info":
                    return MCPAssemblyDefCommands.GetAssemblyDefInfo(ParseJson(body));

                case "asmdef/list":
                    return MCPAssemblyDefCommands.ListAssemblyDefs(ParseJson(body));

                case "asmdef/add-references":
                    return MCPAssemblyDefCommands.AddReferences(ParseJson(body));

                case "asmdef/remove-references":
                    return MCPAssemblyDefCommands.RemoveReferences(ParseJson(body));

                case "asmdef/set-platforms":
                    return MCPAssemblyDefCommands.SetPlatforms(ParseJson(body));

                case "asmdef/update-settings":
                    return MCPAssemblyDefCommands.UpdateSettings(ParseJson(body));

                case "asmdef/create-ref":
                    return MCPAssemblyDefCommands.CreateAssemblyRef(ParseJson(body));

                // ─── Profiler ───
                case "profiler/enable":
                    return MCPProfilerCommands.EnableProfiler(ParseJson(body));

                case "profiler/stats":
                    return MCPProfilerCommands.GetRenderingStats(ParseJson(body));

                case "profiler/memory":
                    return MCPProfilerCommands.GetMemoryInfo(ParseJson(body));

                case "profiler/frame-data":
                    return MCPProfilerCommands.GetFrameData(ParseJson(body));

                case "profiler/analyze":
                    return MCPProfilerCommands.AnalyzePerformance(ParseJson(body));

                // ─── Frame Debugger ───
                case "debugger/enable":
                    return MCPProfilerCommands.EnableFrameDebugger(ParseJson(body));

                case "debugger/events":
                    return MCPProfilerCommands.GetFrameEvents(ParseJson(body));

                case "debugger/event-details":
                    return MCPProfilerCommands.GetFrameEventDetails(ParseJson(body));

                // ─── Memory Profiler ───
                case "profiler/memory-status":
                    return MCPMemoryProfilerCommands.GetStatus(ParseJson(body));

                case "profiler/memory-breakdown":
                    return MCPMemoryProfilerCommands.GetMemoryBreakdown(ParseJson(body));

                case "profiler/memory-top-assets":
                    return MCPMemoryProfilerCommands.GetTopMemoryConsumers(ParseJson(body));

                case "profiler/memory-snapshot":
                    return MCPMemoryProfilerCommands.TakeMemorySnapshot(ParseJson(body));

                // ─── Shader Graph ───
                case "shadergraph/status":
                    return MCPShaderGraphCommands.GetStatus(ParseJson(body));

                case "shadergraph/list-shaders":
                    return MCPShaderGraphCommands.ListShaders(ParseJson(body));

                case "shadergraph/list":
                    return MCPShaderGraphCommands.ListShaderGraphs(ParseJson(body));

                case "shadergraph/info":
                    return MCPShaderGraphCommands.GetShaderGraphInfo(ParseJson(body));

                case "shadergraph/get-properties":
                    return MCPShaderGraphCommands.GetShaderProperties(ParseJson(body));

                case "shadergraph/create":
                    return MCPShaderGraphCommands.CreateShaderGraph(ParseJson(body));

                case "shadergraph/open":
                    return MCPShaderGraphCommands.OpenShaderGraph(ParseJson(body));

                case "shadergraph/list-subgraphs":
                    return MCPShaderGraphCommands.ListSubGraphs(ParseJson(body));

                case "shadergraph/list-vfx":
                    return MCPShaderGraphCommands.ListVFXGraphs(ParseJson(body));

                case "shadergraph/open-vfx":
                    return MCPShaderGraphCommands.OpenVFXGraph(ParseJson(body));

                case "shadergraph/get-nodes":
                    return MCPShaderGraphCommands.GetGraphNodes(ParseJson(body));

                case "shadergraph/get-edges":
                    return MCPShaderGraphCommands.GetGraphEdges(ParseJson(body));

                case "shadergraph/add-node":
                    return MCPShaderGraphCommands.AddGraphNode(ParseJson(body));

                case "shadergraph/remove-node":
                    return MCPShaderGraphCommands.RemoveGraphNode(ParseJson(body));

                case "shadergraph/connect":
                    return MCPShaderGraphCommands.ConnectGraphNodes(ParseJson(body));

                case "shadergraph/disconnect":
                    return MCPShaderGraphCommands.DisconnectGraphNodes(ParseJson(body));

                case "shadergraph/set-node-property":
                    return MCPShaderGraphCommands.SetGraphNodeProperty(ParseJson(body));

                case "shadergraph/get-node-types":
                    return MCPShaderGraphCommands.GetNodeTypes(ParseJson(body));

                // ─── Amplify Shader Editor ───
                case "amplify/status":
                    return MCPAmplifyCommands.GetStatus(ParseJson(body));

                case "amplify/list":
                    return MCPAmplifyCommands.ListAmplifyShaders(ParseJson(body));

                case "amplify/info":
                    return MCPAmplifyCommands.GetAmplifyShaderInfo(ParseJson(body));

                case "amplify/open":
                    return MCPAmplifyCommands.OpenAmplifyShader(ParseJson(body));

                case "amplify/list-functions":
                    return MCPAmplifyCommands.ListAmplifyFunctions(ParseJson(body));

                case "amplify/get-node-types":
                    return MCPAmplifyCommands.GetAmplifyNodeTypes(ParseJson(body));

                case "amplify/get-nodes":
                    return MCPAmplifyCommands.GetAmplifyGraphNodes(ParseJson(body));

                case "amplify/get-connections":
                    return MCPAmplifyCommands.GetAmplifyGraphConnections(ParseJson(body));

                case "amplify/create-shader":
                    return MCPAmplifyCommands.CreateAmplifyShader(ParseJson(body));

                // ─── Agent Management ───
                case "agents/list":
                    return MCPRequestQueue.GetActiveSessions();

                case "agents/log":
                {
                    var agentArgs = ParseJson(body);
                    string id = agentArgs.ContainsKey("agentId") ? agentArgs["agentId"].ToString() : "";
                    return new Dictionary<string, object>
                    {
                        { "agentId", id },
                        { "log", MCPRequestQueue.GetAgentLog(id) },
                    };
                }

                default:
                    return new { error = $"Unknown API endpoint: {path}" };
            }
        }

        // ─── Helpers ───

        private static Dictionary<string, object> ParseJson(string json)
        {
            if (string.IsNullOrEmpty(json))
                return new Dictionary<string, object>();

            return MiniJson.Deserialize(json) as Dictionary<string, object>
                ?? new Dictionary<string, object>();
        }

        /// <summary>
        /// Execute a function on Unity's main thread and wait for the result.
        /// Required because most Unity APIs can only be called from the main thread.
        /// </summary>
        private static object ExecuteOnMainThread(Func<object> action)
        {
            if (Thread.CurrentThread.ManagedThreadId == 1)
                return action();

            object result = null;
            Exception exception = null;
            var resetEvent = new ManualResetEventSlim(false);

            lock (_mainThreadQueue)
            {
                _mainThreadQueue.Enqueue(() =>
                {
                    try
                    {
                        result = action();
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                    }
                    finally
                    {
                        resetEvent.Set();
                    }
                });
            }

            // Wait up to 25 seconds for main thread to process
            if (!resetEvent.Wait(25000))
                return new { error = "Timeout waiting for Unity main thread" };

            if (exception != null)
                return new { error = exception.Message, stackTrace = exception.StackTrace };

            return result;
        }

        private static void ProcessMainThreadQueue()
        {
            lock (_mainThreadQueue)
            {
                while (_mainThreadQueue.Count > 0)
                {
                    var action = _mainThreadQueue.Dequeue();
                    try
                    {
                        action?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[MCP Bridge] Main thread action error: {ex}");
                    }
                }
            }
        }

        private static void SendJson(HttpListenerResponse response, int statusCode, object data)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            string json = MiniJson.Serialize(data);
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        private static string GetProjectPath()
        {
            // Application.dataPath ends with /Assets
            string dataPath = Application.dataPath;
            return dataPath.Substring(0, dataPath.Length - "/Assets".Length);
        }
    }
}
