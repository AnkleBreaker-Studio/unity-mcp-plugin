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

                // Route the request with agent tracking
                var result = MCPRequestQueue.ExecuteWithTracking(agentId, apiPath,
                    () => RouteRequest(apiPath, request.HttpMethod, body));
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
                    return ExecuteOnMainThread(() => MCPEditorCommands.GetEditorState());

                case "editor/play-mode":
                    return ExecuteOnMainThread(() => MCPEditorCommands.SetPlayMode(ParseJson(body)));

                case "editor/execute-menu-item":
                    return ExecuteOnMainThread(() => MCPEditorCommands.ExecuteMenuItem(ParseJson(body)));

                case "editor/execute-code":
                    return ExecuteOnMainThread(() => MCPEditorCommands.ExecuteCode(ParseJson(body)));

                // ─── Scene ───
                case "scene/info":
                    return ExecuteOnMainThread(() => MCPSceneCommands.GetSceneInfo());

                case "scene/open":
                    return ExecuteOnMainThread(() => MCPSceneCommands.OpenScene(ParseJson(body)));

                case "scene/save":
                    return ExecuteOnMainThread(() => MCPSceneCommands.SaveScene());

                case "scene/new":
                    return ExecuteOnMainThread(() => MCPSceneCommands.NewScene());

                case "scene/hierarchy":
                    return ExecuteOnMainThread(() => MCPSceneCommands.GetHierarchy(ParseJson(body)));

                // ─── GameObject ───
                case "gameobject/create":
                    return ExecuteOnMainThread(() => MCPGameObjectCommands.Create(ParseJson(body)));

                case "gameobject/delete":
                    return ExecuteOnMainThread(() => MCPGameObjectCommands.Delete(ParseJson(body)));

                case "gameobject/info":
                    return ExecuteOnMainThread(() => MCPGameObjectCommands.GetInfo(ParseJson(body)));

                case "gameobject/set-transform":
                    return ExecuteOnMainThread(() => MCPGameObjectCommands.SetTransform(ParseJson(body)));

                // ─── Component ───
                case "component/add":
                    return ExecuteOnMainThread(() => MCPComponentCommands.Add(ParseJson(body)));

                case "component/remove":
                    return ExecuteOnMainThread(() => MCPComponentCommands.Remove(ParseJson(body)));

                case "component/get-properties":
                    return ExecuteOnMainThread(() => MCPComponentCommands.GetProperties(ParseJson(body)));

                case "component/set-property":
                    return ExecuteOnMainThread(() => MCPComponentCommands.SetProperty(ParseJson(body)));

                // ─── Assets ───
                case "asset/list":
                    return ExecuteOnMainThread(() => MCPAssetCommands.List(ParseJson(body)));

                case "asset/import":
                    return ExecuteOnMainThread(() => MCPAssetCommands.Import(ParseJson(body)));

                case "asset/delete":
                    return ExecuteOnMainThread(() => MCPAssetCommands.Delete(ParseJson(body)));

                case "asset/create-prefab":
                    return ExecuteOnMainThread(() => MCPAssetCommands.CreatePrefab(ParseJson(body)));

                case "asset/instantiate-prefab":
                    return ExecuteOnMainThread(() => MCPAssetCommands.InstantiatePrefab(ParseJson(body)));

                case "asset/create-material":
                    return ExecuteOnMainThread(() => MCPAssetCommands.CreateMaterial(ParseJson(body)));

                // ─── Scripts ───
                case "script/create":
                    return ExecuteOnMainThread(() => MCPScriptCommands.Create(ParseJson(body)));

                case "script/read":
                    return ExecuteOnMainThread(() => MCPScriptCommands.Read(ParseJson(body)));

                case "script/update":
                    return ExecuteOnMainThread(() => MCPScriptCommands.Update(ParseJson(body)));

                // ─── Renderer ───
                case "renderer/set-material":
                    return ExecuteOnMainThread(() => MCPRendererCommands.SetMaterial(ParseJson(body)));

                // ─── Build ───
                case "build/start":
                    return ExecuteOnMainThread(() => MCPBuildCommands.StartBuild(ParseJson(body)));

                // ─── Console ───
                case "console/log":
                    return ExecuteOnMainThread(() => MCPConsoleCommands.GetLog(ParseJson(body)));

                case "console/clear":
                    return ExecuteOnMainThread(() => MCPConsoleCommands.Clear());

                // ─── Project ───
                case "project/info":
                    return ExecuteOnMainThread(() => MCPProjectCommands.GetInfo());

                // ─── Animation ───
                case "animation/create-controller":
                    return ExecuteOnMainThread(() => MCPAnimationCommands.CreateController(ParseJson(body)));

                case "animation/controller-info":
                    return ExecuteOnMainThread(() => MCPAnimationCommands.GetControllerInfo(ParseJson(body)));

                case "animation/add-parameter":
                    return ExecuteOnMainThread(() => MCPAnimationCommands.AddParameter(ParseJson(body)));

                case "animation/remove-parameter":
                    return ExecuteOnMainThread(() => MCPAnimationCommands.RemoveParameter(ParseJson(body)));

                case "animation/add-state":
                    return ExecuteOnMainThread(() => MCPAnimationCommands.AddState(ParseJson(body)));

                case "animation/remove-state":
                    return ExecuteOnMainThread(() => MCPAnimationCommands.RemoveState(ParseJson(body)));

                case "animation/add-transition":
                    return ExecuteOnMainThread(() => MCPAnimationCommands.AddTransition(ParseJson(body)));

                case "animation/create-clip":
                    return ExecuteOnMainThread(() => MCPAnimationCommands.CreateClip(ParseJson(body)));

                case "animation/clip-info":
                    return ExecuteOnMainThread(() => MCPAnimationCommands.GetClipInfo(ParseJson(body)));

                case "animation/set-clip-curve":
                    return ExecuteOnMainThread(() => MCPAnimationCommands.SetClipCurve(ParseJson(body)));

                case "animation/add-layer":
                    return ExecuteOnMainThread(() => MCPAnimationCommands.AddLayer(ParseJson(body)));

                case "animation/assign-controller":
                    return ExecuteOnMainThread(() => MCPAnimationCommands.AssignController(ParseJson(body)));

                // ─── Prefab (Advanced) ───
                case "prefab/info":
                    return ExecuteOnMainThread(() => MCPPrefabCommands.GetPrefabInfo(ParseJson(body)));

                case "prefab/create-variant":
                    return ExecuteOnMainThread(() => MCPPrefabCommands.CreateVariant(ParseJson(body)));

                case "prefab/apply-overrides":
                    return ExecuteOnMainThread(() => MCPPrefabCommands.ApplyOverrides(ParseJson(body)));

                case "prefab/revert-overrides":
                    return ExecuteOnMainThread(() => MCPPrefabCommands.RevertOverrides(ParseJson(body)));

                case "prefab/unpack":
                    return ExecuteOnMainThread(() => MCPPrefabCommands.Unpack(ParseJson(body)));

                case "prefab/set-object-reference":
                    return ExecuteOnMainThread(() => MCPPrefabCommands.SetObjectReference(ParseJson(body)));

                case "prefab/duplicate":
                    return ExecuteOnMainThread(() => MCPPrefabCommands.Duplicate(ParseJson(body)));

                case "prefab/set-active":
                    return ExecuteOnMainThread(() => MCPPrefabCommands.SetActive(ParseJson(body)));

                case "prefab/reparent":
                    return ExecuteOnMainThread(() => MCPPrefabCommands.Reparent(ParseJson(body)));

                // ─── Physics ───
                case "physics/raycast":
                    return ExecuteOnMainThread(() => MCPPhysicsCommands.Raycast(ParseJson(body)));

                case "physics/overlap-sphere":
                    return ExecuteOnMainThread(() => MCPPhysicsCommands.OverlapSphere(ParseJson(body)));

                case "physics/overlap-box":
                    return ExecuteOnMainThread(() => MCPPhysicsCommands.OverlapBox(ParseJson(body)));

                case "physics/collision-matrix":
                    return ExecuteOnMainThread(() => MCPPhysicsCommands.GetCollisionMatrix(ParseJson(body)));

                case "physics/set-collision-layer":
                    return ExecuteOnMainThread(() => MCPPhysicsCommands.SetCollisionLayer(ParseJson(body)));

                case "physics/set-gravity":
                    return ExecuteOnMainThread(() => MCPPhysicsCommands.SetGravity(ParseJson(body)));

                // ─── Lighting ───
                case "lighting/info":
                    return ExecuteOnMainThread(() => MCPLightingCommands.GetLightingInfo(ParseJson(body)));

                case "lighting/create":
                    return ExecuteOnMainThread(() => MCPLightingCommands.CreateLight(ParseJson(body)));

                case "lighting/set-environment":
                    return ExecuteOnMainThread(() => MCPLightingCommands.SetEnvironment(ParseJson(body)));

                case "lighting/create-reflection-probe":
                    return ExecuteOnMainThread(() => MCPLightingCommands.CreateReflectionProbe(ParseJson(body)));

                case "lighting/create-light-probe-group":
                    return ExecuteOnMainThread(() => MCPLightingCommands.CreateLightProbeGroup(ParseJson(body)));

                // ─── Audio ───
                case "audio/info":
                    return ExecuteOnMainThread(() => MCPAudioCommands.GetAudioInfo(ParseJson(body)));

                case "audio/create-source":
                    return ExecuteOnMainThread(() => MCPAudioCommands.CreateAudioSource(ParseJson(body)));

                case "audio/set-global":
                    return ExecuteOnMainThread(() => MCPAudioCommands.SetGlobalAudio(ParseJson(body)));

                // ─── Tags & Layers ───
                case "taglayer/info":
                    return ExecuteOnMainThread(() => MCPTagLayerCommands.GetTagsAndLayers(ParseJson(body)));

                case "taglayer/add-tag":
                    return ExecuteOnMainThread(() => MCPTagLayerCommands.AddTag(ParseJson(body)));

                case "taglayer/set-tag":
                    return ExecuteOnMainThread(() => MCPTagLayerCommands.SetTag(ParseJson(body)));

                case "taglayer/set-layer":
                    return ExecuteOnMainThread(() => MCPTagLayerCommands.SetLayer(ParseJson(body)));

                case "taglayer/set-static":
                    return ExecuteOnMainThread(() => MCPTagLayerCommands.SetStatic(ParseJson(body)));

                // ─── Selection & Scene View ───
                case "selection/get":
                    return ExecuteOnMainThread(() => MCPSelectionCommands.GetSelection(ParseJson(body)));

                case "selection/set":
                    return ExecuteOnMainThread(() => MCPSelectionCommands.SetSelection(ParseJson(body)));

                case "selection/focus-scene-view":
                    return ExecuteOnMainThread(() => MCPSelectionCommands.FocusSceneView(ParseJson(body)));

                case "selection/find-by-type":
                    return ExecuteOnMainThread(() => MCPSelectionCommands.FindObjectsByType(ParseJson(body)));

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
