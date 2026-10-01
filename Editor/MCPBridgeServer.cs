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
    ///
    /// Supports two modes:
    ///   1. Queue mode (async):  POST /api/queue/submit → poll GET /api/queue/status
    ///   2. Legacy mode (sync):  POST /api/{command}    → blocks until done
    ///
    /// Both modes go through MCPRequestQueue for fair round-robin scheduling.
    /// </summary>
    [InitializeOnLoad]
    public static partial class MCPBridgeServer
    {
        private static HttpListener _listener;
        private static Thread _listenerThread;
        private static bool _isRunning;

        private const long MaxRequestBodyBytes = MCPRequestInput.MaxBodyBytes;

        private static string ReadRequestBody(Stream input, Encoding encoding, long limit) => MCPRequestInput.Read(input, encoding, limit);

        /// <summary>
        /// The actual port this server is running on.
        /// Resolved at startup via auto-selection or manual override.
        /// </summary>
        private static int _activePort;

        /// <summary>The port this server is currently bound to (0 if not running).</summary>
        public static int ActivePort => _isRunning ? _activePort : 0;

        // Legacy main-thread queue (kept for direct ExecuteOnMainThread calls)
        private static readonly Queue<Action> _mainThreadQueue = new Queue<Action>();

        // Routes whose Unity APIs use async callbacks (fire on next editor frame).
        // Register here instead of adding per-route if-conditions in HandleRequest/HandleQueueSubmit.
        private static readonly Dictionary<string, Action<Dictionary<string, object>, Action<object>, Func<bool>>>
            _deferredRoutes = new Dictionary<string, Action<Dictionary<string, object>, Action<object>, Func<bool>>>
        {
            { "testing/list-tests", (args, resolve, isActive) => MCPTestRunnerCommands.ListTests(args, resolve) },
            { "packages/list", MCPPackageManagerCommands.ListPackages },
            { "packages/add", MCPPackageManagerCommands.AddPackage },
            { "packages/remove", MCPPackageManagerCommands.RemovePackage },
            { "packages/search", MCPPackageManagerCommands.SearchPackage },
            { "packages/info", MCPPackageManagerCommands.GetPackageInfo },
        };

        // ─── Capability handshake (unity-mcp-server PRs #32/#20) ───
        // One monotonic int, bumped whenever the bridge gains a wire-visible capability.
        // Servers compare it to decide between fast paths and graceful fallbacks.
        // v1: baseline — advertises the handshake itself + unknown-route 404s.
        private const int ProtocolVersion = MCPRequestQueue.ProtocolVersion;

        private static string _pluginVersion;
        private static string PluginVersion
        {
            get
            {
                if (_pluginVersion == null)
                {
                    var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MCPBridgeServer).Assembly);
                    _pluginVersion = info != null ? info.version : "unknown";
                }
                return _pluginVersion;
            }
        }

        // SessionState key to persist running state across domain reloads (Play Mode, recompile)
        private const string WasRunningKey = "UnityMCP_WasRunningBeforeReload";

        // ─── Manual-port restart retry (unity-mcp-server issue #10) ───
        // Right after a domain reload the configured manual port can be briefly
        // unbindable while the previous listener's socket is released. Auto-port
        // mode survives this (it probes and falls back); manual mode had neither
        // probe nor retry and failed permanently. Retry the SAME port instead.
        private const int MaxManualPortRetries = 10;
        private const double ManualPortRetryDelaySeconds = 0.5;
        private static int _manualPortRetryCount;
        private static double _manualPortRetryAt;
        private static bool _manualPortRetryPending;

        /// <summary>
        /// Whether the MCP bridge may auto-start in this Editor. False on MPPM
        /// Virtual Players when StartOnVirtualPlayers is disabled (issue #21) —
        /// manual start is unaffected.
        /// </summary>
        private static bool AutoStartAllowed =>
            MCPSettingsManager.AutoStart &&
            (MCPSettingsManager.StartOnVirtualPlayers || !MCPScenarioCommands.IsVirtualPlayer());

        static MCPBridgeServer()
        {
            // Skip batch-mode Unity subprocesses (AssetImportWorker, CLI builds, etc.).
            // These are short-lived, don't need MCP access, and would otherwise claim
            // ports in the 7890-7899 range and exhaust availability for real editors.
            if (Application.isBatchMode) return;

            // Restart if: auto-start is allowed (respects the Virtual Player setting)
            // OR the server was running before a domain reload.
            bool wasRunning = SessionState.GetBool(WasRunningKey, false);
            if (AutoStartAllowed || wasRunning)
            {
                Start();
                SessionState.SetBool(WasRunningKey, false);
            }
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.quitting += OnQuitting;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
        }

        /// <summary>
        /// Handle Play Mode transitions to ensure the server stays alive.
        /// Unity triggers a domain reload when entering/exiting Play Mode,
        /// which is handled by the assembly reload callbacks and the SessionState flag.
        /// This callback provides additional resilience for edge cases.
        /// </summary>
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode || state == PlayModeStateChange.EnteredEditMode)
            {
                if (!_isRunning && (AutoStartAllowed || SessionState.GetBool(WasRunningKey, false)))
                {
                    Debug.Log("[MCP Bridge] Restarting server after Play Mode transition...");
                    Start();
                    SessionState.SetBool(WasRunningKey, false);
                }
            }
        }

        private static void OnBeforeAssemblyReload()
        {
            if (_isRunning)
            {
                // Persist that we were running, so we restart after reload
                SessionState.SetBool(WasRunningKey, true);
                // Keep the registry entry across the reload — see Stop(bool). The bridge is
                // coming straight back on the same port; removing the entry made a routine
                // recompile look like the project had gone away.
                Stop(false);
            }
        }

        private static void OnQuitting()
        {
            Stop();
            // Final cleanup of registry on quit
            MCPInstanceRegistry.Unregister();
        }

        /// <summary>Whether the server is currently running.</summary>
        public static bool IsRunning => _isRunning;

        public static void Start()
        {
            if (_isRunning) return;

            // Batch-mode subprocesses (AssetImportWorker, etc.) must never start the server.
            if (Application.isBatchMode) return;

            // Ensure console log capture is active before anything else
            MCPConsoleCommands.EnsureListening();

            // Clean up stale entries before selecting a port
            MCPInstanceRegistry.CleanupStaleEntries();

            // Resolve port: use manual override if set, otherwise auto-select
            int port;
            if (MCPSettingsManager.UseManualPort)
            {
                port = MCPSettingsManager.Port;
            }
            else
            {
                port = MCPInstanceRegistry.FindAvailablePort();
                if (port < 0)
                {
                    // No port available in the auto-select range -> give up cleanly.
                    // Without this guard the old retry logic would spin forever.
                    Debug.LogError(
                        $"[AB-UMCP] No available port in range {MCPInstanceRegistry.PortRangeStart}-{MCPInstanceRegistry.PortRangeEnd}. " +
                        "Close other Unity instances or set a manual port in MCP settings.");
                    return;
                }
            }

            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                _isRunning = true;
                _activePort = port;

                // Update the settings so the UI reflects the actual port
                MCPSettingsManager.Port = port;

                _listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "AB Unity MCP Server"
                };
                _listenerThread.Start();

                // Register in the shared instance registry
                MCPInstanceRegistry.Register(port);

                // Successful bind — clear any pending manual-port retry state.
                _manualPortRetryCount = 0;
                _manualPortRetryPending = false;

                Debug.Log($"[AB-UMCP] Server started on port {port}");
            }
            catch (Exception ex)
            {
                if (MCPSettingsManager.UseManualPort)
                {
                    // Manual port: do NOT fall back to another port — the user
                    // explicitly chose this one. The port is usually only briefly
                    // unavailable (socket release after a domain reload), so retry
                    // the SAME port a few times before giving up (issue #10).
                    if (_manualPortRetryCount < MaxManualPortRetries)
                    {
                        _manualPortRetryCount++;
                        Debug.LogWarning(
                            $"[AB-UMCP] Port {port} not yet available ({ex.Message}). " +
                            $"Retry {_manualPortRetryCount}/{MaxManualPortRetries} in {ManualPortRetryDelaySeconds:0.0}s...");
                        _manualPortRetryAt = EditorApplication.timeSinceStartup + ManualPortRetryDelaySeconds;
                        _manualPortRetryPending = true;
                    }
                    else
                    {
                        _manualPortRetryCount = 0;
                        Debug.LogError(
                            $"[AB-UMCP] Failed to start on port {port} after {MaxManualPortRetries} retries: {ex.Message}. " +
                            "Choose a different manual port in MCP settings, or switch to automatic port selection.");
                    }
                }
                else
                {
                    Debug.LogError($"[AB-UMCP] Failed to start on port {port}: {ex.Message}");

                    // Auto-port mode: fall back to another free port.
                    // Retry only if another port is actually free — the previous
                    // implementation retried whenever port < PortRangeEnd which
                    // caused an infinite loop when FindAvailablePort kept returning
                    // the same unavailable default port.
                    int nextPort = MCPInstanceRegistry.FindAvailablePort();
                    if (nextPort < 0 || nextPort == port)
                    {
                        Debug.LogError(
                            "[AB-UMCP] No alternative port available. Giving up to avoid a retry loop.");
                        return;
                    }

                    Debug.Log($"[AB-UMCP] Trying next available port {nextPort}...");
                    EditorApplication.delayCall += Start;
                }
            }
        }

        /// <param name="unregister">
        /// Remove this instance from the shared registry. TRUE for a real shutdown (quit, user
        /// stop). FALSE across a domain reload: the registry entry is exactly what lets the MCP
        /// server ride out a recompile — it treats "unresponsive but present in the registry" as
        /// "compiling, keep the selection", and "absent" as "project gone". Deleting the entry on
        /// every reload made the transient case look permanent, which is what pushed the server
        /// onto the default port and into another project.
        /// </param>
        public static void Stop(bool unregister = true)
        {
            _isRunning = false;

            // Cancel any pending manual-port restart retry.
            _manualPortRetryPending = false;
            _manualPortRetryCount = 0;

            if (unregister)
                MCPInstanceRegistry.Unregister();

            try
            {
                _listener?.Stop();
                _listener?.Close();
                _listenerThread?.Join(1000);
            }
            catch { }
            _activePort = 0;
            Debug.Log("[AB-UMCP] Server stopped");
        }

        // ─── EditorApplication.update — processes both legacy queue AND ticket queue ───

        private static void OnEditorUpdate()
        {
            // 0. Manual-port restart retry (issue #10): the manual port can be
            //    briefly unbindable after a domain reload — retry on a short delay.
            if (_manualPortRetryPending && !_isRunning &&
                EditorApplication.timeSinceStartup >= _manualPortRetryAt)
            {
                _manualPortRetryPending = false;
                Start();
            }

            // 1. Process legacy main-thread actions
            ProcessMainThreadQueue();

            // 2. Process ticket-based queue (fair round-robin)
            MCPRequestQueue.ProcessNextRequests();
        }

        // ─── HTTP Listener ───

        private static void ListenLoop()
        {
            while (_isRunning)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
                }
                catch (HttpListenerException) when (!_isRunning) { break; }
                catch (ThreadAbortException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (_isRunning)
                        Debug.LogError($"[AB-UMCP] Listener error: {ex.Message}");
                }
            }
        }

        // ─── Request Handler ───

        /// <summary>
        /// True when the request comes from a local non-browser client: loopback Host
        /// (or none) and no cross-site Origin. Browser pages always attach their page
        /// Origin on cross-origin fetches — the vehicle CSRF and DNS-rebinding ride on.
        /// A "null" Origin (file:// pages, sandboxed frames) is rejected too.
        /// </summary>
        private static bool IsTrustedLocalRequest(HttpListenerRequest request)
        {
            string host = request.Headers["Host"];
            if (!string.IsNullOrEmpty(host))
            {
                string hostName = host;
                int colon = host.LastIndexOf(':');
                bool bracketed = host.IndexOf(']') >= 0;
                // Bare unbracketed IPv6 ("::1") has multiple colons and no brackets —
                // don't treat its last colon as a port separator.
                bool bareIpv6 = !bracketed && host.IndexOf(':') != colon;
                if (!bareIpv6 && colon >= 0 && host.IndexOf(']') < colon)
                    hostName = host.Substring(0, colon);
                hostName = hostName.Trim('[', ']');
                if (!IsLoopbackHostName(hostName))
                    return false;
            }

            string origin = request.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin))
            {
                // Parse and match the origin host EXACTLY. A StartsWith("http://localhost")
                // prefix check would let http://localhost.evil.com straight through — and
                // with execute-code behind this guard that is a browser-reachable RCE.
                // Uri.TryCreate also rejects the literal "null" Origin (file:// pages,
                // sandboxed frames), which we deliberately do not trust.
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
                    return false;
                if (!IsLoopbackHostName(originUri.Host.Trim('[', ']')))
                    return false;
            }

            // The Origin check above only runs when an Origin is PRESENT — and a no-cors
            // subresource load (<img src>, <iframe>, <script src>, <form> GET) deliberately
            // sends none, so a page the developer merely visits could slip past it. Browsers
            // still attach Sec-Fetch-* on every such request and page script cannot forge them
            // (they are forbidden header names), so treat any non-same-origin fetch metadata as
            // browser-originated and refuse. Non-browser clients send no Sec-Fetch-Site at all.
            string fetchSite = request.Headers["Sec-Fetch-Site"];
            if (!string.IsNullOrEmpty(fetchSite) &&
                !string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fetchSite, "none", StringComparison.OrdinalIgnoreCase))
                return false;

            // A browser navigation/subresource load is never a legitimate bridge call.
            string fetchMode = request.Headers["Sec-Fetch-Mode"];
            if (!string.IsNullOrEmpty(fetchMode) &&
                string.Equals(fetchMode, "navigate", StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }

        /// <summary>
        /// Routes that may be reached with a safe (GET) method. Everything else — i.e. every
        /// route that can mutate the project or run code — requires POST. A no-cors GET from a
        /// page cannot be a POST, so this is the second half of the browser-reachability guard.
        /// </summary>
        private static bool IsReadOnlyRoute(string apiPath)
        {
            return apiPath == "ping"
                || apiPath == "queue/status"
                || apiPath == "queue/status-scoped"
                || apiPath == "queue/info"
                || apiPath == "context"
                || apiPath.StartsWith("context/")
                || apiPath == "_meta/routes";
        }

        private static bool IsLoopbackHostName(string hostName)
        {
            return hostName == "127.0.0.1"
                || hostName == "::1"
                || string.Equals(hostName, "localhost", StringComparison.OrdinalIgnoreCase);
        }

        private static void HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                // ─── Cross-origin / DNS-rebinding guard ───
                // The bridge can execute arbitrary editor code, so only same-machine
                // tools may talk to it. Legit clients (Node MCP server, curl) send a
                // loopback Host and no Origin header; a browser page doing a CSRF/
                // DNS-rebinding fetch always attaches its page Origin (or a non-loopback
                // Host), which we reject before touching any editor state.
                if (!IsTrustedLocalRequest(request))
                {
                    SendJson(response, 403, new { error = "Forbidden: only local, non-browser clients may call the MCP bridge" });
                    return;
                }

                string path = request.Url.AbsolutePath.TrimStart('/');
                if (!path.StartsWith("api/"))
                {
                    SendJson(response, 404, new { error = "Not found" });
                    return;
                }

                string apiPath = path.Substring(4); // Remove "api/"

                // Any route that can mutate the project or run code requires POST. A page can
                // issue a no-cors GET at a loopback URL with no Origin (<img>, <iframe>), but it
                // cannot make that a POST — so this closes the browser-reachable dispatch path
                // that the Origin check alone left open.
                if (!IsReadOnlyRoute(apiPath) &&
                    !string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    SendJson(response, 405, new { error = $"Method {request.HttpMethod} not allowed for '{apiPath}' — use POST." });
                    return;
                }

                string body = "";
                if (request.HasEntityBody)
                {
                    if (request.ContentLength64 > MaxRequestBodyBytes)
                        throw new RequestInputException(413, "request_too_large", $"Request body too large ({request.ContentLength64} bytes; limit {MaxRequestBodyBytes}).");
                    using (var input = request.InputStream)
                        body = ReadRequestBody(input, request.ContentEncoding, MaxRequestBodyBytes);
                }

                string agentId = request.Headers["X-Agent-Id"] ?? "anonymous";

                // ═══ Queue endpoints (async, non-blocking) ═══
                if (apiPath == "queue/submit" || apiPath == "queue/submit-once")
                {
                    HandleQueueSubmit(response, agentId, body, apiPath == "queue/submit-once");
                    return;
                }
                var parsedBody = ParseJson(body);
                if (apiPath == "queue/status" || apiPath == "queue/status-scoped")
                {
                    HandleQueueStatus(response, request, apiPath == "queue/status-scoped");
                    return;
                }
                if (apiPath == "queue/info")
                {
                    SendJson(response, 200, MCPRequestQueue.GetQueueInfo());
                    return;
                }

                // ═══ Project Context endpoints (read-only, no queue needed) ═══
                // Must run on the main thread: GetContextResponse reads EditorPrefs
                // (main-thread-only), which made every context call throw HTTP 500
                // from this ThreadPool thread (community PR #17 by rcasaleiro).
                if (apiPath == "context")
                {
                    SendJson(response, 200, ExecuteOnMainThread(() => MCPContextManager.GetContextResponse()));
                    return;
                }
                if (apiPath.StartsWith("context/"))
                {
                    string category = apiPath.Substring("context/".Length);
                    SendJson(response, 200, ExecuteOnMainThread(() => MCPContextManager.GetContextResponse(category)));
                    return;
                }

                // Preserve legacy responses while Unity completes the request on later editor updates.
                if (_deferredRoutes.ContainsKey(apiPath))
                {
                    var result = MCPRequestQueue.ExecuteDeferredWithTracking(agentId, apiPath,
                        (resolve, isActive) => RouteDeferredRequest(apiPath, parsedBody, resolve, isActive));
                    SendJson(response, 200, result);
                    return;
                }

                // ═══ Unknown routes: 404 before dispatch (capability handshake) ═══
                // Lets servers distinguish "this plugin doesn't have that feature"
                // from a failed call and degrade gracefully. KnownRoutes is generated
                // from the dispatch switch (tools~/generate-routes.mjs, CI-checked).
                if (!KnownRoutes.Contains(apiPath))
                {
                    SendJson(response, 404, new { error = $"Unknown route: {apiPath}" });
                    return;
                }

                // ═══ Legacy synchronous path (blocks until main thread processes) ═══
                {
                    var result = MCPRequestQueue.ExecuteWithTracking(agentId, apiPath,
                        () => ExecuteOnMainThread(() => RouteRequest(apiPath, request.HttpMethod, parsedBody)));
                    SendJson(response, 200, result);
                }
            }
            catch (RequestInputException ex)
            {
                // An unread oversized body must not be drained to reuse this connection.
                if (ex.Code == "request_too_large") response.KeepAlive = false;
                SendJson(response, ex.Status, new { error = ex.Message, code = ex.Code, requestAccepted = false });
            }
            catch (ThreadAbortException)
            {
                // Domain reload aborts waiting HTTP workers; logging an error would fail native tests.
                throw;
            }
            catch (Exception ex)
            {
                // Full stack trace goes to the editor log only — never to the wire.
                Debug.LogError($"[AB-UMCP] Request failed: {ex.Message}\n{ex.StackTrace}");
                SendJson(response, 500, new { error = ex.Message });
            }
        }

        // ─── Queue Submit (async) ───

        private static void HandleQueueSubmit(HttpListenerResponse response, string agentId, string body, bool requireGuard)
        {
            try
            {
                var args = ParseJson(body);
                if (args.TryGetValue("apiPath", out var route) && !(route is string))
                    throw new RequestInputException(400, "invalid_request", "Queue apiPath must be a string.");
                if (args.TryGetValue("body", out var payload) && !(payload is string))
                    throw new RequestInputException(400, "invalid_request", "Queue body must be a JSON string.");
                string apiPath = route as string ?? "";
                string innerBody = payload as string ?? "";

                if (string.IsNullOrEmpty(apiPath))
                {
                    SendJson(response, 400, new { error = "Missing 'apiPath' in request body" });
                    return;
                }

                // Override agentId if provided in the body
                if (args.ContainsKey("agentId") && !string.IsNullOrEmpty(args["agentId"]?.ToString()))
                    agentId = args["agentId"].ToString();

                var parsedBody = ParseJson(innerBody);
                Func<MCPRequestQueue.RequestTicket> submit = () =>
                {
                    if (_deferredRoutes.ContainsKey(apiPath))
                        return MCPRequestQueue.SubmitDeferredRequest(agentId, apiPath, (resolve, isActive) =>
                            RouteDeferredRequest(apiPath, parsedBody, resolve, isActive));
                    return MCPRequestQueue.SubmitRequest(agentId, apiPath, () =>
                        RouteRequest(apiPath, "POST", parsedBody));
                };
                MCPRequestQueue.RequestTicket ticket;
                if (requireGuard || args.ContainsKey("requestId") || args.ContainsKey("queueSessionId") || args.ContainsKey("expiresAtMs"))
                {
                    if (!args.TryGetValue("requestId", out var requestId) || !(requestId is string)
                        || !args.TryGetValue("queueSessionId", out var sessionId) || !(sessionId is string)
                        || !args.TryGetValue("expiresAtMs", out var expires)
                        || !long.TryParse(expires?.ToString(), out long expiresAtMs))
                    {
                        SendJson(response, 400, new { error = "requestId, queueSessionId and integer expiresAtMs are required together", code = "invalid_retry_guard" });
                        return;
                    }
                    var submission = MCPRequestQueue.SubmitOnce(agentId, apiPath, innerBody,
                        (string)requestId, (string)sessionId, expiresAtMs, submit);
                    if (submission.Ticket == null)
                    {
                        SendJson(response, submission.StatusCode, new { error = submission.Error, code = submission.Code });
                        return;
                    }
                    ticket = submission.Ticket;
                }
                else ticket = submit();

                // Return immediately with ticket info
                SendJson(response, 202, new Dictionary<string, object>
                {
                    { "ticketId",      ticket.TicketId },
                    { "status",        ticket.Status.ToString() },
                    { "queuePosition", ticket.QueuePosition },
                    { "agentId",       agentId },
                    { "queueSessionId", MCPRequestQueue.SessionId },
                });
            }
            catch (RequestInputException ex)
            {
                SendJson(response, ex.Status, new { error = ex.Message, code = ex.Code, requestAccepted = false });
            }
            catch (ThreadAbortException) { throw; }
            catch (Exception ex)
            {
                SendJson(response, 500, new { error = $"Queue submit failed: {ex.Message}" });
            }
        }

        // ─── Queue Status (polling) ───

        private static void HandleQueueStatus(HttpListenerResponse response, HttpListenerRequest request, bool requireGuard)
        {
            string sessionId = request.QueryString["queueSessionId"];
            if ((requireGuard || sessionId != null) && !string.Equals(sessionId, MCPRequestQueue.SessionId, StringComparison.Ordinal))
            {
                SendJson(response, 409, new { error = "Queue session changed; the original ticket outcome is unknown", code = "queue_session_changed" });
                return;
            }
            string ticketIdStr = request.QueryString["ticketId"];
            if (string.IsNullOrEmpty(ticketIdStr) || !long.TryParse(ticketIdStr, out long ticketId))
            {
                SendJson(response, 400, new { error = "Missing or invalid 'ticketId' query parameter" });
                return;
            }

            var status = MCPRequestQueue.GetTicketStatus(ticketId);
            if (status == null)
            {
                SendJson(response, 404, new { error = $"Ticket {ticketId} not found or expired" });
                return;
            }

            SendJson(response, 200, status);
        }

        // ─── Route Request (runs on main thread) ───

        private static string ExtractCategory(string path)
        {
            int slash = path.IndexOf('/');
            return slash > 0 ? path.Substring(0, slash) : path;
        }

        /// <summary>
        /// Returns all registered routes for dynamic tool discovery.
        /// Used by the MCP server's lazy loading system to discover tools
        /// added to the plugin without needing a server restart.
        /// </summary>
        private static object GetRegisteredRoutes()
        {
            // The route list is GENERATED from the RouteRequest dispatch switch by
            // tools~/generate-routes.mjs into MCPBridgeServer.Routes.g.cs (CI-checked).
            // The previous hand-maintained list had drifted to ~150 of ~320 routes
            // with several wrong names, silently breaking dynamic tool discovery.
            var grouped = new Dictionary<string, List<string>>();
            foreach (var route in GeneratedRoutes)
            {
                string cat = ExtractCategory(route);
                if (!grouped.ContainsKey(cat)) grouped[cat] = new List<string>();
                grouped[cat].Add(route);
            }

            return new Dictionary<string, object>
            {
                { "routes", GeneratedRoutes },
                { "categories", grouped },
                { "totalRoutes", GeneratedRoutes.Length }
            };
        }

        private static object DisabledCategoryError(string path)
        {
            string category = ExtractCategory(path);
            if (category == "packages") category = "packagemanager";
            if (category != "ping" && category != "agents" && category != "queue"
                && !MCPSettingsManager.IsCategoryEnabled(category))
                return new { error = $"Category '{category}' is currently disabled. Enable it in Window > AB Unity MCP > Dashboard." };
            return null;
        }

        private static void RouteDeferredRequest(string path, Dictionary<string, object> args, Action<object> resolve, Func<bool> isActive)
        {
            var disabled = DisabledCategoryError(path);
            if (disabled != null) { resolve(disabled); return; }
            _deferredRoutes[path](args, resolve, isActive);
        }

        /// <summary>
        /// Route API requests to the appropriate handler.
        /// NOTE: This entire method runs on the main thread (dispatched by HandleRequest
        /// or by MCPRequestQueue.ProcessNextRequests), so all Unity APIs work correctly.
        /// </summary>
        private static object RouteRequest(string path, string method, Dictionary<string, object> args)
        {
            // ─── Meta endpoints (no category check) ───
            if (path == "_meta/routes")
            {
                return GetRegisteredRoutes();
            }

            var disabled = DisabledCategoryError(path);
            if (disabled != null) return disabled;

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
                        platform = Application.platform.ToString(),
                        isClone = MCPInstanceRegistry.IsParrelSyncClone(),
                        cloneIndex = MCPInstanceRegistry.GetParrelSyncCloneIndex(),
                        isVirtualPlayer = MCPScenarioCommands.IsVirtualPlayer(),
                        mainProjectPath = MCPInstanceRegistry.GetMainProjectPath(),
                        virtualPlayerId = MCPInstanceRegistry.GetVirtualPlayerId(),
                        processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                        // Capability handshake: servers gate newer wire features on this
                        // monotonic int so the pair degrades gracefully across version
                        // drift (server and plugin ship on separate release trains).
                        protocolVersion = ProtocolVersion,
                        queueSessionId = MCPRequestQueue.SessionId,
                        pluginVersion = PluginVersion
                    };

                // ─── Editor State ───
                case "editor/state":
                    return MCPEditorCommands.GetEditorState();
                case "editor/play-mode":
                    return MCPEditorCommands.SetPlayMode(args);
                case "editor/execute-menu-item":
                    return MCPEditorCommands.ExecuteMenuItem(args);
                case "editor/execute-code":
                    return MCPEditorCommands.ExecuteCode(args);

                // ─── Scene ───
                case "scene/info":
                    return MCPSceneCommands.GetSceneInfo();
                case "scene/open":
                    return MCPSceneCommands.OpenScene(args);
                case "scene/save":
                    return MCPSceneCommands.SaveScene(args);
                case "scene/new":
                    return MCPSceneCommands.NewScene(args);
                case "scene/hierarchy":
                    return MCPSceneCommands.GetHierarchy(args);

                // ─── GameObject ───
                case "gameobject/create":
                    return MCPGameObjectCommands.Create(args);
                case "gameobject/delete":
                    return MCPGameObjectCommands.Delete(args);
                case "gameobject/info":
                    return MCPGameObjectCommands.GetInfo(args);
                case "gameobject/set-transform":
                    return MCPGameObjectCommands.SetTransform(args);

                // ─── Component ───
                case "component/add":
                    return MCPComponentCommands.Add(args);
                case "component/remove":
                    return MCPComponentCommands.Remove(args);
                case "component/get-properties":
                    return MCPComponentCommands.GetProperties(args);
                case "component/set-property":
                    return MCPComponentCommands.SetProperty(args);
                case "component/set-reference":
                    return MCPComponentCommands.SetReference(args);
                case "component/batch-wire":
                    return MCPComponentCommands.BatchWireReferences(args);
                case "component/get-referenceable":
                    return MCPComponentCommands.GetReferenceableObjects(args);

                // ─── Assets ───
                case "asset/list":
                    return MCPAssetCommands.List(args);
                case "asset/import":
                    return MCPAssetCommands.Import(args);
                case "asset/delete":
                    return MCPAssetCommands.Delete(args);
                case "asset/create-prefab":
                    return MCPAssetCommands.CreatePrefab(args);
                case "asset/instantiate-prefab":
                    return MCPAssetCommands.InstantiatePrefab(args);
                case "asset/create-material":
                    return MCPAssetCommands.CreateMaterial(args);

                // ─── Scripts ───
                case "script/create":
                    return MCPScriptCommands.Create(args);
                case "script/read":
                    return MCPScriptCommands.Read(args);
                case "script/update":
                    return MCPScriptCommands.Update(args);

                // ─── Renderer ───
                case "renderer/set-material":
                    return MCPRendererCommands.SetMaterial(args);

                // ─── Build ───
                case "build/start":
                    return MCPBuildCommands.StartBuild(args);

                // ─── Console ───
                case "console/log":
                    return MCPConsoleCommands.GetLog(args);
                case "console/clear":
                    return MCPConsoleCommands.Clear();

                // ─── Compilation ───
                case "compilation/errors":
                    return MCPConsoleCommands.GetCompilationErrors(args);

                // ─── Project ───
                case "project/info":
                    return MCPProjectCommands.GetInfo();

                // ─── Animation ───
                case "animation/create-controller":
                    return MCPAnimationCommands.CreateController(args);
                case "animation/controller-info":
                    return MCPAnimationCommands.GetControllerInfo(args);
                case "animation/add-parameter":
                    return MCPAnimationCommands.AddParameter(args);
                case "animation/remove-parameter":
                    return MCPAnimationCommands.RemoveParameter(args);
                case "animation/add-state":
                    return MCPAnimationCommands.AddState(args);
                case "animation/remove-state":
                    return MCPAnimationCommands.RemoveState(args);
                case "animation/add-transition":
                    return MCPAnimationCommands.AddTransition(args);
                case "animation/create-clip":
                    return MCPAnimationCommands.CreateClip(args);
                case "animation/clip-info":
                    return MCPAnimationCommands.GetClipInfo(args);
                case "animation/set-clip-curve":
                    return MCPAnimationCommands.SetClipCurve(args);
                case "animation/set-object-reference-curve":
                    return MCPAnimationCommands.SetObjectReferenceCurve(args);
                case "animation/add-layer":
                    return MCPAnimationCommands.AddLayer(args);
                case "animation/assign-controller":
                    return MCPAnimationCommands.AssignController(args);
                case "animation/get-curve-keyframes":
                    return MCPAnimationCommands.GetCurveKeyframes(args);
                case "animation/remove-curve":
                    return MCPAnimationCommands.RemoveCurve(args);
                case "animation/add-keyframe":
                    return MCPAnimationCommands.AddKeyframe(args);
                case "animation/remove-keyframe":
                    return MCPAnimationCommands.RemoveKeyframe(args);
                case "animation/add-event":
                    return MCPAnimationCommands.AddAnimationEvent(args);
                case "animation/remove-event":
                    return MCPAnimationCommands.RemoveAnimationEvent(args);
                case "animation/get-events":
                    return MCPAnimationCommands.GetAnimationEvents(args);
                case "animation/set-clip-settings":
                    return MCPAnimationCommands.SetClipSettings(args);
                case "animation/remove-transition":
                    return MCPAnimationCommands.RemoveTransition(args);
                case "animation/remove-layer":
                    return MCPAnimationCommands.RemoveLayer(args);
                case "animation/create-blend-tree":
                    return MCPAnimationCommands.CreateBlendTree(args);
                case "animation/get-blend-tree":
                    return MCPAnimationCommands.GetBlendTreeInfo(args);

                // ─── Prefab (Advanced) ───
                case "prefab/info":
                    return MCPPrefabCommands.GetPrefabInfo(args);
                case "prefab/create-variant":
                    return MCPPrefabCommands.CreateVariant(args);
                case "prefab/apply-overrides":
                    return MCPPrefabCommands.ApplyOverrides(args);
                case "prefab/revert-overrides":
                    return MCPPrefabCommands.RevertOverrides(args);
                case "prefab/unpack":
                    return MCPPrefabCommands.Unpack(args);
                case "prefab/set-object-reference":
                    return MCPPrefabCommands.SetObjectReference(args);
                case "prefab/duplicate":
                    return MCPPrefabCommands.Duplicate(args);
                case "prefab/set-active":
                    return MCPPrefabCommands.SetActive(args);
                case "prefab/reparent":
                    return MCPPrefabCommands.Reparent(args);

                // ─── Prefab Asset (Direct Editing) ───
                case "prefab-asset/hierarchy":
                    return MCPPrefabAssetCommands.GetHierarchy(args);
                case "prefab-asset/get-properties":
                    return MCPPrefabAssetCommands.GetComponentProperties(args);
                case "prefab-asset/set-property":
                    return MCPPrefabAssetCommands.SetComponentProperty(args);
                case "prefab-asset/add-component":
                    return MCPPrefabAssetCommands.AddComponent(args);
                case "prefab-asset/remove-component":
                    return MCPPrefabAssetCommands.RemoveComponent(args);
                case "prefab-asset/set-reference":
                    return MCPPrefabAssetCommands.SetReference(args);
                case "prefab-asset/add-gameobject":
                    return MCPPrefabAssetCommands.AddGameObject(args);
                case "prefab-asset/remove-gameobject":
                    return MCPPrefabAssetCommands.RemoveGameObject(args);

                // ─── Prefab Variant Management ───
                case "prefab-asset/variant-info":
                    return MCPPrefabAssetCommands.GetVariantInfo(args);
                case "prefab-asset/compare-variant":
                    return MCPPrefabAssetCommands.CompareVariantToBase(args);
                case "prefab-asset/apply-variant-override":
                    return MCPPrefabAssetCommands.ApplyVariantOverride(args);
                case "prefab-asset/revert-variant-override":
                    return MCPPrefabAssetCommands.RevertVariantOverride(args);
                case "prefab-asset/transfer-variant-overrides":
                    return MCPPrefabAssetCommands.TransferVariantOverrides(args);

                // ─── Physics ───
                case "physics/raycast":
                    return MCPPhysicsCommands.Raycast(args);
                case "physics/overlap-sphere":
                    return MCPPhysicsCommands.OverlapSphere(args);
                case "physics/overlap-box":
                    return MCPPhysicsCommands.OverlapBox(args);
                case "physics/collision-matrix":
                    return MCPPhysicsCommands.GetCollisionMatrix(args);
                case "physics/set-collision-layer":
                    return MCPPhysicsCommands.SetCollisionLayer(args);
                case "physics/set-gravity":
                    return MCPPhysicsCommands.SetGravity(args);

                // ─── Lighting ───
                case "lighting/info":
                    return MCPLightingCommands.GetLightingInfo(args);
                case "lighting/create":
                    return MCPLightingCommands.CreateLight(args);
                case "lighting/set-environment":
                    return MCPLightingCommands.SetEnvironment(args);
                case "lighting/create-reflection-probe":
                    return MCPLightingCommands.CreateReflectionProbe(args);
                case "lighting/create-light-probe-group":
                    return MCPLightingCommands.CreateLightProbeGroup(args);

                // ─── Audio ───
                case "audio/info":
                    return MCPAudioCommands.GetAudioInfo(args);
                case "audio/create-source":
                    return MCPAudioCommands.CreateAudioSource(args);
                case "audio/set-global":
                    return MCPAudioCommands.SetGlobalAudio(args);

                // ─── Tags & Layers ───
                case "taglayer/info":
                    return MCPTagLayerCommands.GetTagsAndLayers(args);
                case "taglayer/add-tag":
                    return MCPTagLayerCommands.AddTag(args);
                case "taglayer/set-tag":
                    return MCPTagLayerCommands.SetTag(args);
                case "taglayer/set-layer":
                    return MCPTagLayerCommands.SetLayer(args);
                case "taglayer/set-static":
                    return MCPTagLayerCommands.SetStatic(args);

                // ─── Selection & Scene View ───
                case "selection/get":
                    return MCPSelectionCommands.GetSelection(args);
                case "selection/set":
                    return MCPSelectionCommands.SetSelection(args);
                case "selection/focus-scene-view":
                    return MCPSelectionCommands.FocusSceneView(args);
                case "selection/find-by-type":
                    return MCPSelectionCommands.FindObjectsByType(args);

                // ─── Input Actions ───
                case "input/create":
                    return MCPInputCommands.CreateInputActions(args);
                case "input/info":
                    return MCPInputCommands.GetInputActionsInfo(args);
                case "input/add-map":
                    return MCPInputCommands.AddActionMap(args);
                case "input/remove-map":
                    return MCPInputCommands.RemoveActionMap(args);
                case "input/add-action":
                    return MCPInputCommands.AddAction(args);
                case "input/remove-action":
                    return MCPInputCommands.RemoveAction(args);
                case "input/add-binding":
                    return MCPInputCommands.AddBinding(args);
                case "input/add-composite-binding":
                    return MCPInputCommands.AddCompositeBinding(args);

                // ─── Assembly Definitions ───
                case "asmdef/create":
                    return MCPAssemblyDefCommands.CreateAssemblyDef(args);
                case "asmdef/info":
                    return MCPAssemblyDefCommands.GetAssemblyDefInfo(args);
                case "asmdef/list":
                    return MCPAssemblyDefCommands.ListAssemblyDefs(args);
                case "asmdef/add-references":
                    return MCPAssemblyDefCommands.AddReferences(args);
                case "asmdef/remove-references":
                    return MCPAssemblyDefCommands.RemoveReferences(args);
                case "asmdef/set-platforms":
                    return MCPAssemblyDefCommands.SetPlatforms(args);
                case "asmdef/update-settings":
                    return MCPAssemblyDefCommands.UpdateSettings(args);
                case "asmdef/create-ref":
                    return MCPAssemblyDefCommands.CreateAssemblyRef(args);

                // ─── Profiler ───
                case "profiler/enable":
                    return MCPProfilerCommands.EnableProfiler(args);
                case "profiler/stats":
                    return MCPProfilerCommands.GetRenderingStats(args);
                case "profiler/memory":
                    return MCPProfilerCommands.GetMemoryInfo(args);
                case "profiler/frame-data":
                    return MCPProfilerCommands.GetFrameData(args);
                case "profiler/analyze":
                    return MCPProfilerCommands.AnalyzePerformance(args);

                // ─── Frame Debugger ───
                case "debugger/enable":
                    return MCPProfilerCommands.EnableFrameDebugger(args);
                case "debugger/events":
                    return MCPProfilerCommands.GetFrameEvents(args);
                case "debugger/event-details":
                    return MCPProfilerCommands.GetFrameEventDetails(args);

                // ─── Memory Profiler ───
                case "profiler/memory-status":
                    return MCPMemoryProfilerCommands.GetStatus(args);
                case "profiler/memory-breakdown":
                    return MCPMemoryProfilerCommands.GetMemoryBreakdown(args);
                case "profiler/memory-top-assets":
                    return MCPMemoryProfilerCommands.GetTopMemoryConsumers(args);
                case "profiler/memory-snapshot":
                    return MCPMemoryProfilerCommands.TakeMemorySnapshot(args);

                // ─── Shader Graph ───
                case "shadergraph/status":
                    return MCPShaderGraphCommands.GetStatus(args);
                case "shadergraph/list-shaders":
                    return MCPShaderGraphCommands.ListShaders(args);
                case "shadergraph/list":
                    return MCPShaderGraphCommands.ListShaderGraphs(args);
                case "shadergraph/info":
                    return MCPShaderGraphCommands.GetShaderGraphInfo(args);
                case "shadergraph/get-properties":
                    return MCPShaderGraphCommands.GetShaderProperties(args);
                case "shadergraph/create":
                    return MCPShaderGraphCommands.CreateShaderGraph(args);
                case "shadergraph/open":
                    return MCPShaderGraphCommands.OpenShaderGraph(args);
                case "shadergraph/list-subgraphs":
                    return MCPShaderGraphCommands.ListSubGraphs(args);
                case "shadergraph/list-vfx":
                    return MCPShaderGraphCommands.ListVFXGraphs(args);
                case "shadergraph/open-vfx":
                    return MCPShaderGraphCommands.OpenVFXGraph(args);
                case "shadergraph/get-nodes":
                    return MCPShaderGraphCommands.GetGraphNodes(args);
                case "shadergraph/get-edges":
                    return MCPShaderGraphCommands.GetGraphEdges(args);
                case "shadergraph/add-node":
                    return MCPShaderGraphCommands.AddGraphNode(args);
                case "shadergraph/remove-node":
                    return MCPShaderGraphCommands.RemoveGraphNode(args);
                case "shadergraph/connect":
                    return MCPShaderGraphCommands.ConnectGraphNodes(args);
                case "shadergraph/disconnect":
                    return MCPShaderGraphCommands.DisconnectGraphNodes(args);
                case "shadergraph/set-node-property":
                    return MCPShaderGraphCommands.SetGraphNodeProperty(args);
                case "shadergraph/get-node-types":
                    return MCPShaderGraphCommands.GetNodeTypes(args);

                // ─── Amplify Shader Editor ───
                case "amplify/status":
                    return MCPAmplifyCommands.GetStatus(args);
                case "amplify/list":
                    return MCPAmplifyCommands.ListAmplifyShaders(args);
                case "amplify/info":
                    return MCPAmplifyCommands.GetAmplifyShaderInfo(args);
                case "amplify/open":
                    return MCPAmplifyCommands.OpenAmplifyShader(args);
                case "amplify/list-functions":
                    return MCPAmplifyCommands.ListAmplifyFunctions(args);
                case "amplify/get-node-types":
                    return MCPAmplifyCommands.GetAmplifyNodeTypes(args);
                case "amplify/get-nodes":
                    return MCPAmplifyCommands.GetAmplifyGraphNodes(args);
                case "amplify/get-connections":
                    return MCPAmplifyCommands.GetAmplifyGraphConnections(args);
                case "amplify/create-shader":
                    return MCPAmplifyCommands.CreateAmplifyShader(args);
                case "amplify/add-node":
                    return MCPAmplifyCommands.AddAmplifyNode(args);
                case "amplify/remove-node":
                    return MCPAmplifyCommands.RemoveAmplifyNode(args);
                case "amplify/connect":
                    return MCPAmplifyCommands.ConnectAmplifyNodes(args);
                case "amplify/disconnect":
                    return MCPAmplifyCommands.DisconnectAmplifyNodes(args);
                case "amplify/node-info":
                    return MCPAmplifyCommands.GetAmplifyNodeInfo(args);
                case "amplify/set-node-property":
                    return MCPAmplifyCommands.SetAmplifyNodeProperty(args);
                case "amplify/move-node":
                    return MCPAmplifyCommands.MoveAmplifyNode(args);
                case "amplify/save":
                    return MCPAmplifyCommands.SaveAmplifyGraph(args);
                case "amplify/close":
                    return MCPAmplifyCommands.CloseAmplifyEditor(args);
                case "amplify/create-from-template":
                    return MCPAmplifyCommands.CreateAmplifyFromTemplate(args);
                case "amplify/focus-node":
                    return MCPAmplifyCommands.FocusAmplifyNode(args);
                case "amplify/master-node-info":
                    return MCPAmplifyCommands.GetAmplifyMasterNodeInfo(args);
                case "amplify/disconnect-all":
                    return MCPAmplifyCommands.DisconnectAllAmplifyNode(args);
                case "amplify/duplicate-node":
                    return MCPAmplifyCommands.DuplicateAmplifyNode(args);

                // ─── Agent Management ───
                case "agents/list":
                    return MCPRequestQueue.GetActiveSessions();
                case "agents/log":
                {
                    var agentArgs = args;
                    string id = agentArgs.ContainsKey("agentId") ? agentArgs["agentId"].ToString() : "";
                    return new Dictionary<string, object>
                    {
                        { "agentId", id },
                        { "log", MCPRequestQueue.GetAgentLog(id) },
                    };
                }

                // ─── Search ───
                case "search/by-component":
                    return MCPSearchCommands.FindByComponent(args);
                case "search/by-tag":
                    return MCPSearchCommands.FindByTag(args);
                case "search/by-layer":
                    return MCPSearchCommands.FindByLayer(args);
                case "search/by-name":
                    return MCPSearchCommands.FindByName(args);
                case "search/by-shader":
                    return MCPSearchCommands.FindByShader(args);
                case "search/assets":
                    return MCPSearchCommands.SearchAssets(args);
                case "search/missing-references":
                    return MCPSearchCommands.FindMissingReferences(args);
                case "search/scene-stats":
                    return MCPSearchCommands.GetSceneStats(args);

                // ─── Project Settings ───
                case "settings/quality":
                    return MCPProjectSettingsCommands.GetQualitySettings(args);
                case "settings/quality-level":
                    return MCPProjectSettingsCommands.SetQualityLevel(args);
                case "settings/physics":
                    return MCPProjectSettingsCommands.GetPhysicsSettings(args);
                case "settings/set-physics":
                    return MCPProjectSettingsCommands.SetPhysicsSettings(args);
                case "settings/time":
                    return MCPProjectSettingsCommands.GetTimeSettings(args);
                case "settings/set-time":
                    return MCPProjectSettingsCommands.SetTimeSettings(args);
                case "settings/player":
                    return MCPProjectSettingsCommands.GetPlayerSettings(args);
                case "settings/set-player":
                    return MCPProjectSettingsCommands.SetPlayerSettings(args);
                case "settings/render-pipeline":
                    return MCPProjectSettingsCommands.GetRenderPipelineInfo(args);

                // ─── Undo ───
                case "undo/perform":
                    return MCPUndoCommands.PerformUndo(args);
                case "undo/last":
                    return MCPUndoCommands.UndoLast(args);
                case "undo/redo":
                    return MCPUndoCommands.PerformRedo(args);
                case "undo/history":
                    return MCPUndoCommands.GetUndoHistory(args);
                case "undo/clear":
                    return MCPUndoCommands.ClearUndo(args);

                // ─── ProBuilder ───
                case "probuilder/create-shape":
                    return MCPProBuilderCommands.CreateShape(args);
                case "probuilder/info":
                    return MCPProBuilderCommands.GetInfo(args);
                case "probuilder/extrude-faces":
                    return MCPProBuilderCommands.ExtrudeFaces(args);
                case "probuilder/bevel-edges":
                    return MCPProBuilderCommands.BevelEdges(args);
                case "probuilder/subdivide":
                    return MCPProBuilderCommands.Subdivide(args);
                case "probuilder/delete-faces":
                    return MCPProBuilderCommands.DeleteFaces(args);
                case "probuilder/translate-faces":
                    return MCPProBuilderCommands.TranslateFaces(args);
                case "probuilder/flip-normals":
                    return MCPProBuilderCommands.FlipNormals(args);
                case "probuilder/set-face-material":
                    return MCPProBuilderCommands.SetFaceMaterial(args);
                case "probuilder/boolean":
                    return MCPProBuilderCommands.BooleanOp(args);
                case "probuilder/combine":
                    return MCPProBuilderCommands.Combine(args);
                case "probuilder/probuilderize":
                    return MCPProBuilderCommands.ProBuilderize(args);
                case "probuilder/center-pivot":
                    return MCPProBuilderCommands.CenterPivot(args);
                case "probuilder/export-mesh":
                    return MCPProBuilderCommands.ExportMesh(args);

                // ─── Screenshot / Scene View ───
                case "screenshot/game":
                    return MCPScreenshotCommands.CaptureGameView(args);
                case "screenshot/scene":
                    return MCPScreenshotCommands.CaptureSceneView(args);
                case "screenshot/editor-window":
                    return MCPScreenshotCommands.CaptureEditorWindow(args);
                case "sceneview/info":
                    return MCPScreenshotCommands.GetSceneViewInfo(args);
                case "sceneview/set-camera":
                    return MCPScreenshotCommands.SetSceneViewCamera(args);

                // ─── Graphics & Visuals ───
                case "graphics/asset-preview":
                    return MCPGraphicsCommands.CaptureAssetPreview(args);
                case "graphics/scene-capture":
                    return MCPGraphicsCommands.CaptureSceneView(args);
                case "graphics/game-capture":
                    return MCPGraphicsCommands.CaptureGameView(args);
                case "graphics/prefab-render":
                    return MCPGraphicsCommands.RenderPrefabPreview(args);
                case "graphics/mesh-info":
                    return MCPGraphicsCommands.GetMeshInfo(args);
                case "graphics/material-info":
                    return MCPGraphicsCommands.GetMaterialInfo(args);
                case "graphics/texture-info":
                    return MCPGraphicsCommands.GetTextureInfo(args);
                case "graphics/renderer-info":
                    return MCPGraphicsCommands.GetRendererInfo(args);
                case "graphics/lighting-summary":
                    return MCPGraphicsCommands.GetLightingSummary(args);

                // ─── Terrain ───
                case "terrain/create":
                    return MCPTerrainCommands.CreateTerrain(args);
                case "terrain/info":
                    return MCPTerrainCommands.GetTerrainInfo(args);
                case "terrain/set-height":
                    return MCPTerrainCommands.SetHeight(args);
                case "terrain/flatten":
                    return MCPTerrainCommands.FlattenTerrain(args);
                case "terrain/add-layer":
                    return MCPTerrainCommands.AddTerrainLayer(args);
                case "terrain/get-height":
                    return MCPTerrainCommands.GetHeightAtPosition(args);
                case "terrain/list":
                    return MCPTerrainCommands.ListTerrains(args);
                case "terrain/raise-lower":
                    return MCPTerrainCommands.RaiseLowerHeight(args);
                case "terrain/smooth":
                    return MCPTerrainCommands.SmoothHeight(args);
                case "terrain/noise":
                    return MCPTerrainCommands.SetHeightsFromNoise(args);
                case "terrain/set-heights-region":
                    return MCPTerrainCommands.SetHeightsRegion(args);
                case "terrain/get-heights-region":
                    return MCPTerrainCommands.GetHeightsRegion(args);
                case "terrain/remove-layer":
                    return MCPTerrainCommands.RemoveTerrainLayer(args);
                case "terrain/paint-layer":
                    return MCPTerrainCommands.PaintTerrainLayer(args);
                case "terrain/fill-layer":
                    return MCPTerrainCommands.FillTerrainLayer(args);
                case "terrain/add-tree-prototype":
                    return MCPTerrainCommands.AddTreePrototype(args);
                case "terrain/remove-tree-prototype":
                    return MCPTerrainCommands.RemoveTreePrototype(args);
                case "terrain/place-trees":
                    return MCPTerrainCommands.PlaceTrees(args);
                case "terrain/clear-trees":
                    return MCPTerrainCommands.ClearTrees(args);
                case "terrain/get-tree-instances":
                    return MCPTerrainCommands.GetTreeInstances(args);
                case "terrain/add-detail-prototype":
                    return MCPTerrainCommands.AddDetailPrototype(args);
                case "terrain/paint-detail":
                    return MCPTerrainCommands.PaintDetail(args);
                case "terrain/scatter-detail":
                    return MCPTerrainCommands.ScatterDetail(args);
                case "terrain/clear-detail":
                    return MCPTerrainCommands.ClearDetail(args);
                case "terrain/set-holes":
                    return MCPTerrainCommands.SetHoles(args);
                case "terrain/set-settings":
                    return MCPTerrainCommands.SetTerrainSettings(args);
                case "terrain/resize":
                    return MCPTerrainCommands.ResizeTerrain(args);
                case "terrain/create-grid":
                    return MCPTerrainCommands.CreateTerrainGrid(args);
                case "terrain/set-neighbors":
                    return MCPTerrainCommands.SetTerrainNeighbors(args);
                case "terrain/import-heightmap":
                    return MCPTerrainCommands.ImportHeightmap(args);
                case "terrain/export-heightmap":
                    return MCPTerrainCommands.ExportHeightmap(args);
                case "terrain/get-steepness":
                    return MCPTerrainCommands.GetSteepness(args);

                // ─── Particle System ───
                case "particle/create":
                    return MCPParticleCommands.CreateParticleSystem(args);
                case "particle/info":
                    return MCPParticleCommands.GetParticleSystemInfo(args);
                case "particle/set-main":
                    return MCPParticleCommands.SetMainModule(args);
                case "particle/set-emission":
                    return MCPParticleCommands.SetEmission(args);
                case "particle/set-shape":
                    return MCPParticleCommands.SetShape(args);
                case "particle/playback":
                    return MCPParticleCommands.PlaybackControl(args);

                // ─── ScriptableObject ───
                case "scriptableobject/create":
                    return MCPScriptableObjectCommands.CreateScriptableObject(args);
                case "scriptableobject/info":
                    return MCPScriptableObjectCommands.GetScriptableObjectInfo(args);
                case "scriptableobject/set-field":
                    return MCPScriptableObjectCommands.SetScriptableObjectField(args);
                case "scriptableobject/list-types":
                    return MCPScriptableObjectCommands.ListScriptableObjectTypes(args);

                // ─── Texture ───
                case "texture/info":
                    return MCPTextureCommands.GetTextureInfo(args);
                case "texture/set-import":
                    return MCPTextureCommands.SetTextureImportSettings(args);
                case "texture/reimport":
                    return MCPTextureCommands.ReimportTexture(args);
                case "texture/set-sprite":
                    return MCPTextureCommands.SetAsSprite(args);
                case "texture/set-normalmap":
                    return MCPTextureCommands.SetAsNormalMap(args);

                // ─── Sprite Atlas ───
                case "spriteatlas/create":
                    return MCPSpriteAtlasCommands.CreateSpriteAtlas(args);
                case "spriteatlas/info":
                    return MCPSpriteAtlasCommands.GetSpriteAtlasInfo(args);
                case "spriteatlas/add":
                    return MCPSpriteAtlasCommands.AddToSpriteAtlas(args);
                case "spriteatlas/remove":
                    return MCPSpriteAtlasCommands.RemoveFromSpriteAtlas(args);
                case "spriteatlas/settings":
                    return MCPSpriteAtlasCommands.SetSpriteAtlasSettings(args);
                case "spriteatlas/delete":
                    return MCPSpriteAtlasCommands.DeleteSpriteAtlas(args);
                case "spriteatlas/list":
                    return MCPSpriteAtlasCommands.ListSpriteAtlases(args);

                // ─── Navigation ───
                case "navigation/bake":
                    return MCPNavigationCommands.BakeNavMesh(args);
                case "navigation/clear":
                    return MCPNavigationCommands.ClearNavMesh(args);
                case "navigation/add-agent":
                    return MCPNavigationCommands.AddNavMeshAgent(args);
                case "navigation/add-obstacle":
                    return MCPNavigationCommands.AddNavMeshObstacle(args);
                case "navigation/info":
                    return MCPNavigationCommands.GetNavMeshInfo(args);
                case "navigation/set-destination":
                    return MCPNavigationCommands.SetAgentDestination(args);

                // ─── UI ───
                case "ui/create-canvas":
                    return MCPUICommands.CreateCanvas(args);
                case "ui/create-element":
                    return MCPUICommands.CreateUIElement(args);
                case "ui/info":
                    return MCPUICommands.GetUIInfo(args);
                case "ui/set-text":
                    return MCPUICommands.SetUIText(args);
                case "ui/set-image":
                    return MCPUICommands.SetUIImage(args);

                // ─── Constraints & LOD ───
                case "constraint/add":
                    return MCPConstraintCommands.AddConstraint(args);
                case "constraint/info":
                    return MCPConstraintCommands.GetConstraintInfo(args);
                case "lod/create":
                    return MCPConstraintCommands.CreateLODGroup(args);
                case "lod/info":
                    return MCPConstraintCommands.GetLODGroupInfo(args);

                // ─── Prefs ───
                case "editorprefs/get":
                    return MCPPrefsCommands.GetEditorPref(args);
                case "editorprefs/set":
                    return MCPPrefsCommands.SetEditorPref(args);
                case "editorprefs/delete":
                    return MCPPrefsCommands.DeleteEditorPref(args);
                case "playerprefs/get":
                    return MCPPrefsCommands.GetPlayerPref(args);
                case "playerprefs/set":
                    return MCPPrefsCommands.SetPlayerPref(args);
                case "playerprefs/delete":
                    return MCPPrefsCommands.DeletePlayerPref(args);
                case "playerprefs/delete-all":
                    return MCPPrefsCommands.DeleteAllPlayerPrefs(args);

                // ─── MPPM Scenario Management ───
                case "scenario/list":
                    return MCPScenarioCommands.ListScenarios(args);
                case "scenario/status":
                    return MCPScenarioCommands.GetScenarioStatus(args);
                case "scenario/activate":
                    return MCPScenarioCommands.ActivateScenario(args);
                case "scenario/start":
                    return MCPScenarioCommands.StartScenario(args);
                case "scenario/stop":
                    return MCPScenarioCommands.StopScenario(args);
                case "scenario/info":
                    return MCPScenarioCommands.GetMultiplayerInfo(args);
                case "scenario/create":
                    return MCPScenarioCommands.CreateScenario(args);

                // ─── MPPM Virtual Player management ───
                case "mppm/list-players":
                    return MCPScenarioCommands.MppmListPlayers(args);
                case "mppm/activate-player":
                    return MCPScenarioCommands.MppmActivatePlayer(args);
                case "mppm/deactivate-player":
                    return MCPScenarioCommands.MppmDeactivatePlayer(args);

                // === UMA (Unity Multipurpose Avatar)
                case "uma/inspect-fbx":
                    return MCPUMACommands.InspectFbx(args);
                case "uma/create-slot":
                    return MCPUMACommands.CreateSlot(args);
                case "uma/create-overlay":
                    return MCPUMACommands.CreateOverlay(args);
                case "uma/create-wardrobe-recipe":
                    return MCPUMACommands.CreateWardrobeRecipe(args);
                case "uma/register-assets":
                    return MCPUMACommands.RegisterAssets(args);
                case "uma/list-global-library":
                    return MCPUMACommands.ListGlobalLibrary(args);
                case "uma/list-wardrobe-slots":
                    return MCPUMACommands.ListWardrobeSlots(args);
                case "uma/list-uma-materials":
                    return MCPUMACommands.ListUMAMaterials(args);
                case "uma/get-project-config":
                    return MCPUMACommands.GetProjectConfig(args);
                    case "uma/verify-recipe":
                        return MCPUMACommands.VerifyRecipe(args);
                    case "uma/rebuild-global-library":
                        return MCPUMACommands.RebuildGlobalLibrary(args);
                    case "uma/create-wardrobe-from-fbx":
                        return MCPUMACommands.CreateWardrobeFromFbx(args);
                    case "uma/wardrobe-equip":
                        return MCPUMACommands.WardrobeEquip(args);
                    case "uma/edit-race":
                        return MCPUMACommands.EditRace(args);
                    case "uma/create-race":
                        return MCPUMACommands.CreateRace(args);
                    case "uma/rename-asset":
                        return MCPUMACommands.RenameAsset(args);
                // ─── Testing ───
                case "testing/run-tests":
                    return MCPTestRunnerCommands.RunTests(args);
                case "testing/get-job":
                    return MCPTestRunnerCommands.GetTestJob(args);
                // testing/list-tests is handled via the deferred path in HandleRequest

                default:
                    return new { error = $"Unknown API endpoint: {path}" };
            }
        }

        // ─── Helpers ───

        private static Dictionary<string, object> ParseJson(string json) => MCPRequestInput.ParseObject(json);

        /// <summary>
        /// Execute a function on Unity's main thread and wait for the result.
        /// Used by the legacy synchronous path.
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
                    try { result = action(); }
                    catch (Exception ex) { exception = ex; }
                    finally { resetEvent.Set(); }
                });
            }

            if (!resetEvent.Wait(MCPRequestQueue.SyncTimeoutMs))
                return new { error = $"Timeout waiting for Unity main thread after {MCPRequestQueue.SyncTimeoutMs / 1000}s" };

            if (exception != null)
            {
                // Trace goes to the editor log only — never to the wire.
                Debug.LogError($"[AB-UMCP] Main-thread execution failed: {exception.Message}\n{exception.StackTrace}");
                return new { error = exception.Message };
            }

            return result;
        }

        private static void ProcessMainThreadQueue()
        {
            lock (_mainThreadQueue)
            {
                while (_mainThreadQueue.Count > 0)
                {
                    var action = _mainThreadQueue.Dequeue();
                    try { action?.Invoke(); }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[AB-UMCP] Main thread action error: {ex}");
                    }
                }
            }
        }

        // Response size limits (bytes) — prevents oversized payloads from crashing the MCP stdio pipe
        private const int ResponseSoftLimitBytes = 8 * 1024 * 1024;  // 8 MB — log warning
        private const int ResponseHardLimitBytes = 16 * 1024 * 1024; // 16 MB — replace with error

        private static void SendJson(HttpListenerResponse response, int statusCode, object data)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json";
            string json;
            try { json = MiniJson.Serialize(data, ResponseHardLimitBytes); }
            catch (MiniJson.SerializationException error)
            {
                bool oversized = error.Reason == "byte_limit";
                string message = error.Message.Length > 2048 ? error.Message.Substring(0, 2048) : error.Message;
                var errorData = new Dictionary<string, object>
                {
                    { "error", oversized ? "response_too_large" : "response_serialization_failed" },
                    { "reason", error.Reason },
                    { "outcomeUnknown", true },
                    { "message", oversized ? "Response exceeded size limit. Use pagination parameters (maxNodes, limit, maxResults) to request smaller chunks." : message },
                    { "hint", "The original operation may already have completed. Inspect its effects or original ticket before repeating a write." }
                };
                if (oversized)
                {
                    errorData["size"] = error.BytesRequired;
                    errorData["sizeIsLowerBound"] = true;
                    errorData["limit"] = ResponseHardLimitBytes;
                    Debug.LogWarning("[AB-UMCP] Response exceeded the 16 MiB serialization limit; use pagination parameters.");
                }
                json = MiniJson.Serialize(errorData, ResponseHardLimitBytes);
                response.StatusCode = oversized ? 413 : 500;
            }
            byte[] buffer = Encoding.UTF8.GetBytes(json);
            if (buffer.Length > ResponseSoftLimitBytes)
            {
                Debug.LogWarning($"[AB-UMCP] Large response ({buffer.Length / (1024 * 1024)}MB). Consider using pagination parameters.");
            }

            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        private static string GetProjectPath()
        {
            string dataPath = Application.dataPath;
            return dataPath.Substring(0, dataPath.Length - "/Assets".Length);
        }
    }
}
