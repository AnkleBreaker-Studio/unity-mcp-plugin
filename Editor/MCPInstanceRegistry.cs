using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Manages multi-instance discovery for Unity MCP.
    ///
    /// Each Unity Editor instance with the MCP plugin registers itself in a shared
    /// JSON file at %LOCALAPPDATA%/UnityMCP/instances.json (Windows) or
    /// ~/.local/share/UnityMCP/instances.json (macOS/Linux).
    ///
    /// On startup, the plugin auto-selects the first available port in the range
    /// 7890-7899. The MCP server reads this registry to discover all running instances.
    ///
    /// Supports:
    ///   - Multiple different projects open simultaneously
    ///   - ParrelSync clones (detected via folder naming convention)
    ///   - Any multi-instance Unity workflow
    /// </summary>
    public static class MCPInstanceRegistry
    {
        // Port range for auto-selection
        public const int PortRangeStart = 7890;
        public const int PortRangeEnd = 7899;

        private static readonly string RegistryDir;
        private static readonly string RegistryPath;
        private static int _registeredPort = -1;

        /// <summary>
        /// Heartbeat interval in seconds. The registry entry's lastSeen timestamp
        /// is updated at this interval so the MCP server can detect crashes
        /// (if Unity crashes, OnDisable never fires and the entry goes stale).
        /// </summary>
        private const double HeartbeatIntervalSeconds = 30.0;
        private static double _lastHeartbeatTime = 0;

        /// <summary>
        /// Named mutex to prevent race conditions when multiple Unity instances
        /// (e.g. ParrelSync clones) read/write the registry simultaneously.
        /// </summary>
        private const string MutexName = "Global\\UnityMCP_InstanceRegistry";
        private const int MutexTimeoutMs = 5000;

        static MCPInstanceRegistry()
        {
            // Determine registry directory based on platform
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                RegistryDir = Path.Combine(localAppData, "UnityMCP");
            }
            else
            {
                // macOS / Linux
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                RegistryDir = Path.Combine(home, ".local", "share", "UnityMCP");
            }
            RegistryPath = Path.Combine(RegistryDir, "instances.json");
        }

        /// <summary>
        /// The port this instance registered with, or -1 if not yet registered.
        /// </summary>
        public static int RegisteredPort => _registeredPort;

        /// <summary>
        /// Find an available port in the range [PortRangeStart, PortRangeEnd].
        /// Uses port affinity: tries to reclaim the last-used port for this project first,
        /// which prevents port swapping when multiple projects restart simultaneously.
        /// Falls back to sequential scan if the preferred port is unavailable.
        /// </summary>
        public static int FindAvailablePort()
        {
            int result = PortRangeStart;

            WithRegistryLock(() =>
            {
                var occupiedPorts = new HashSet<int>();
                string myProjectPath = GetProjectPath();

                // Read registry to find ports claimed by other instances
                var instances = ReadRegistry();
                foreach (var inst in instances)
                {
                    if (inst.ContainsKey("port"))
                    {
                        int existingPort = ExtractPort(inst);
                        if (existingPort > 0)
                            occupiedPorts.Add(existingPort);
                    }
                }

                // Port affinity: try to reclaim the last-used port for this project.
                // This prevents port swapping when multiple projects restart simultaneously.
                int preferredPort = GetLastUsedPort();
                if (preferredPort >= PortRangeStart && preferredPort <= PortRangeEnd)
                {
                    // Check the preferred port isn't claimed by a DIFFERENT project in the registry
                    bool claimedByOther = false;
                    foreach (var inst in instances)
                    {
                        int instPort = ExtractPort(inst);
                        if (instPort == preferredPort)
                        {
                            string instPath = inst.ContainsKey("projectPath") ? inst["projectPath"].ToString() : "";
                            if (instPath != myProjectPath)
                            {
                                claimedByOther = true;
                            }
                            break;
                        }
                    }

                    if (!claimedByOther && IsPortAvailable(preferredPort))
                    {
                        Debug.Log($"[AB-UMCP] Reclaimed preferred port {preferredPort} (port affinity).");
                        result = preferredPort;
                        return;
                    }
                }

                // Standard scan: try each port in range, skipping occupied ones
                for (int port = PortRangeStart; port <= PortRangeEnd; port++)
                {
                    if (occupiedPorts.Contains(port))
                        continue;

                    if (IsPortAvailable(port))
                    {
                        result = port;
                        return;
                    }
                }

                // Fallback: try any port in range even if registered (stale entries)
                for (int port = PortRangeStart; port <= PortRangeEnd; port++)
                {
                    if (IsPortAvailable(port))
                    {
                        result = port;
                        return;
                    }
                }

                Debug.LogWarning($"[AB-UMCP] No available port in range {PortRangeStart}-{PortRangeEnd}. Using default {PortRangeStart}.");
            }, "find-port");

            return result;
        }

        /// <summary>
        /// Extract a port number from a registry entry dictionary.
        /// Handles long, double, and string representations.
        /// </summary>
        private static int ExtractPort(Dictionary<string, object> inst)
        {
            if (!inst.ContainsKey("port")) return 0;
            if (inst["port"] is long lp) return (int)lp;
            if (inst["port"] is double dp) return (int)dp;
            if (int.TryParse(inst["port"].ToString(), out int ip)) return ip;
            return 0;
        }

        /// <summary>
        /// Get the last port this project used (stored in EditorPrefs for persistence).
        /// Returns -1 if no previous port is recorded.
        /// </summary>
        private static int GetLastUsedPort()
        {
            return EditorPrefs.GetInt("UnityMCP_LastUsedPort_" + GetProjectPath().GetHashCode(), -1);
        }

        /// <summary>
        /// Save the port this project is using, so it can be reclaimed on restart.
        /// </summary>
        private static void SaveLastUsedPort(int port)
        {
            EditorPrefs.SetInt("UnityMCP_LastUsedPort_" + GetProjectPath().GetHashCode(), port);
        }

        /// <summary>
        /// Check if a TCP port is available for binding.
        /// </summary>
        private static bool IsPortAvailable(int port)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        /// <summary>
        /// Register this Unity instance in the shared registry.
        /// Call this after the HTTP server has successfully started.
        /// </summary>
        public static void Register(int port)
        {
            _registeredPort = port;
            SaveLastUsedPort(port); // Port affinity: remember for next restart

            WithRegistryLock(() =>
            {
                var instances = ReadRegistry();

                // Remove any stale entry for this project path
                string projectPath = GetProjectPath();
                instances.RemoveAll(inst =>
                    inst.ContainsKey("projectPath") &&
                    inst["projectPath"].ToString() == projectPath);

                // Also remove any entry on the same port (stale from crash)
                instances.RemoveAll(inst =>
                {
                    if (!inst.ContainsKey("port")) return false;
                    int existingPort = 0;
                    if (inst["port"] is long lp) existingPort = (int)lp;
                    else if (inst["port"] is double dp) existingPort = (int)dp;
                    else int.TryParse(inst["port"].ToString(), out existingPort);
                    return existingPort == port;
                });

                // Build our entry
                string nowUtc = DateTime.UtcNow.ToString("o");
                var entry = new Dictionary<string, object>
                {
                    { "port", port },
                    { "projectName", Application.productName },
                    { "projectPath", projectPath },
                    { "unityVersion", Application.unityVersion },
                    { "platform", Application.platform.ToString() },
                    { "processId", System.Diagnostics.Process.GetCurrentProcess().Id },
                    { "isClone", IsParrelSyncClone() },
                    { "cloneIndex", GetParrelSyncCloneIndex() },
                    { "registeredAt", nowUtc },
                    { "lastSeen", nowUtc }
                };

                instances.Add(entry);
                WriteRegistry(instances);

                Debug.Log($"[AB-UMCP] Registered instance on port {port} in registry.");
            }, "register");

            // Start the heartbeat: periodically update lastSeen so the MCP server
            // can detect crashes (if Unity crashes, the heartbeat stops and the
            // entry goes stale after ~5-10 minutes).
            _lastHeartbeatTime = EditorApplication.timeSinceStartup;
            EditorApplication.update -= HeartbeatTick;
            EditorApplication.update += HeartbeatTick;
        }

        /// <summary>
        /// EditorApplication.update callback — fires the heartbeat at regular intervals.
        /// This runs on the main thread, so it's safe but lightweight.
        /// During compiles, EditorApplication.update is NOT called (main thread is blocked),
        /// which is fine — the lastSeen timestamp will be slightly stale but the MCP server
        /// uses a generous timeout (5-10 min) that far exceeds any normal compile time.
        /// </summary>
        private static void HeartbeatTick()
        {
            if (_registeredPort < 0) return;

            double now = EditorApplication.timeSinceStartup;
            if (now - _lastHeartbeatTime < HeartbeatIntervalSeconds) return;

            _lastHeartbeatTime = now;
            UpdateLastSeen();
        }

        /// <summary>
        /// Update the lastSeen timestamp for this instance's registry entry.
        /// This is a lightweight write — only modifies the existing entry's timestamp.
        /// </summary>
        private static void UpdateLastSeen()
        {
            WithRegistryLock(() =>
            {
                var instances = ReadRegistry();
                string projectPath = GetProjectPath();
                string nowUtc = DateTime.UtcNow.ToString("o");
                bool updated = false;

                foreach (var inst in instances)
                {
                    if (inst.ContainsKey("projectPath") &&
                        inst["projectPath"].ToString() == projectPath)
                    {
                        inst["lastSeen"] = nowUtc;
                        updated = true;
                        break;
                    }
                }

                if (updated)
                {
                    WriteRegistry(instances);
                }
            }, "heartbeat");
        }

        /// <summary>
        /// Unregister this Unity instance from the shared registry.
        /// Call this on server stop, quit, or domain reload.
        /// </summary>
        public static void Unregister()
        {
            // Stop heartbeat
            EditorApplication.update -= HeartbeatTick;

            if (_registeredPort < 0) return;

            int port = _registeredPort;
            WithRegistryLock(() =>
            {
                var instances = ReadRegistry();
                string projectPath = GetProjectPath();

                instances.RemoveAll(inst =>
                {
                    bool matchPath = inst.ContainsKey("projectPath") &&
                                     inst["projectPath"].ToString() == projectPath;
                    bool matchPort = false;
                    if (inst.ContainsKey("port"))
                    {
                        int existingPort = 0;
                        if (inst["port"] is long lp) existingPort = (int)lp;
                        else if (inst["port"] is double dp) existingPort = (int)dp;
                        else int.TryParse(inst["port"].ToString(), out existingPort);
                        matchPort = existingPort == port;
                    }
                    return matchPath || matchPort;
                });

                WriteRegistry(instances);
                Debug.Log($"[AB-UMCP] Unregistered instance (port {port}) from registry.");
            }, "unregister");
            _registeredPort = -1;
        }

        /// <summary>
        /// Clean up stale entries from the registry.
        /// Removes entries whose process is no longer running.
        /// </summary>
        public static void CleanupStaleEntries()
        {
            WithRegistryLock(() =>
            {
                var instances = ReadRegistry();
                int removed = instances.RemoveAll(inst =>
                {
                    if (!inst.ContainsKey("processId")) return true;

                    int pid = 0;
                    if (inst["processId"] is long lp) pid = (int)lp;
                    else if (inst["processId"] is double dp) pid = (int)dp;
                    else int.TryParse(inst["processId"].ToString(), out pid);

                    if (pid <= 0) return true;

                    try
                    {
                        var proc = System.Diagnostics.Process.GetProcessById(pid);
                        return proc.HasExited;
                    }
                    catch
                    {
                        // Process not found — stale
                        return true;
                    }
                });

                if (removed > 0)
                {
                    WriteRegistry(instances);
                    Debug.Log($"[AB-UMCP] Cleaned up {removed} stale instance(s) from registry.");
                }
            }, "cleanup");
        }

        // ─── ParrelSync Detection ───

        /// <summary>
        /// Detect if this project is a ParrelSync clone based on folder naming convention.
        /// ParrelSync clones have folders named "ProjectName_clone_X".
        /// </summary>
        public static bool IsParrelSyncClone()
        {
            string projectPath = GetProjectPath();
            string folderName = Path.GetFileName(projectPath);
            return folderName != null && System.Text.RegularExpressions.Regex.IsMatch(folderName, @"_clone_\d+$");
        }

        /// <summary>
        /// Get the clone index if this is a ParrelSync clone, or -1 if it's the original.
        /// </summary>
        public static int GetParrelSyncCloneIndex()
        {
            string projectPath = GetProjectPath();
            string folderName = Path.GetFileName(projectPath);
            if (folderName == null) return -1;

            var match = System.Text.RegularExpressions.Regex.Match(folderName, @"_clone_(\d+)$");
            if (match.Success && int.TryParse(match.Groups[1].Value, out int index))
                return index;
            return -1;
        }

        // ─── Mutex-Protected Registry Access ───

        /// <summary>
        /// Execute an action while holding the inter-process registry mutex.
        /// Prevents race conditions when multiple Unity instances (e.g. ParrelSync clones)
        /// simultaneously read/modify/write the shared registry file.
        /// </summary>
        private static void WithRegistryLock(Action action, string operationName)
        {
            Mutex mutex = null;
            bool acquired = false;

            try
            {
                mutex = new Mutex(false, MutexName);

                try
                {
                    acquired = mutex.WaitOne(MutexTimeoutMs);
                }
                catch (AbandonedMutexException)
                {
                    // Previous holder crashed — we still get the mutex
                    acquired = true;
                }

                if (!acquired)
                {
                    Debug.LogWarning($"[AB-UMCP] Could not acquire registry lock for '{operationName}' within {MutexTimeoutMs}ms. Proceeding without lock.");
                }

                action();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AB-UMCP] Failed to {operationName} in instance registry: {ex.Message}");
            }
            finally
            {
                if (acquired && mutex != null)
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
                mutex?.Dispose();
            }
        }

        // ─── Registry File I/O ───

        private static List<Dictionary<string, object>> ReadRegistry()
        {
            var result = new List<Dictionary<string, object>>();

            try
            {
                if (!File.Exists(RegistryPath))
                    return result;

                string json = File.ReadAllText(RegistryPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                    return result;

                var parsed = MiniJson.Deserialize(json);
                if (parsed is List<object> list)
                {
                    foreach (var item in list)
                    {
                        if (item is Dictionary<string, object> dict)
                            result.Add(dict);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AB-UMCP] Error reading instance registry: {ex.Message}");
            }

            return result;
        }

        private static void WriteRegistry(List<Dictionary<string, object>> instances)
        {
            try
            {
                if (!Directory.Exists(RegistryDir))
                    Directory.CreateDirectory(RegistryDir);

                // Convert to list of objects for MiniJson
                var list = new List<object>();
                foreach (var dict in instances)
                    list.Add(dict);

                string json = MiniJson.Serialize(list);
                File.WriteAllText(RegistryPath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AB-UMCP] Error writing instance registry: {ex.Message}");
            }
        }

        private static string GetProjectPath()
        {
            string dataPath = Application.dataPath;
            return dataPath.Substring(0, dataPath.Length - "/Assets".Length);
        }
    }
}
