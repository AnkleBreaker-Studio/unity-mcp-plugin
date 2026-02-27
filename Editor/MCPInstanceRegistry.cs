using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
        /// Find the first available port in the range [PortRangeStart, PortRangeEnd].
        /// Checks both the registry file (to avoid conflicts with registered instances)
        /// and actual TCP port availability.
        /// </summary>
        public static int FindAvailablePort()
        {
            var occupiedPorts = new HashSet<int>();

            // Read registry to find ports claimed by other instances
            var instances = ReadRegistry();
            foreach (var inst in instances)
            {
                if (inst.ContainsKey("port"))
                {
                    if (inst["port"] is long lp)
                        occupiedPorts.Add((int)lp);
                    else if (inst["port"] is double dp)
                        occupiedPorts.Add((int)dp);
                    else if (int.TryParse(inst["port"].ToString(), out int ip))
                        occupiedPorts.Add(ip);
                }
            }

            // Try each port in range
            for (int port = PortRangeStart; port <= PortRangeEnd; port++)
            {
                if (occupiedPorts.Contains(port))
                    continue;

                if (IsPortAvailable(port))
                    return port;
            }

            // Fallback: try any port in range even if registered (stale entries)
            for (int port = PortRangeStart; port <= PortRangeEnd; port++)
            {
                if (IsPortAvailable(port))
                    return port;
            }

            Debug.LogWarning($"[AB-UMCP] No available port in range {PortRangeStart}-{PortRangeEnd}. Using default {PortRangeStart}.");
            return PortRangeStart;
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

            try
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
                    { "registeredAt", DateTime.UtcNow.ToString("o") }
                };

                instances.Add(entry);
                WriteRegistry(instances);

                Debug.Log($"[AB-UMCP] Registered instance on port {port} in registry.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AB-UMCP] Failed to register in instance registry: {ex.Message}");
            }
        }

        /// <summary>
        /// Unregister this Unity instance from the shared registry.
        /// Call this on server stop, quit, or domain reload.
        /// </summary>
        public static void Unregister()
        {
            if (_registeredPort < 0) return;

            try
            {
                var instances = ReadRegistry();
                string projectPath = GetProjectPath();
                int port = _registeredPort;

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
                Debug.Log($"[AB-UMCP] Unregistered instance (port {_registeredPort}) from registry.");
                _registeredPort = -1;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AB-UMCP] Failed to unregister from instance registry: {ex.Message}");
            }
        }

        /// <summary>
        /// Clean up stale entries from the registry.
        /// Removes entries whose process is no longer running.
        /// </summary>
        public static void CleanupStaleEntries()
        {
            try
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
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AB-UMCP] Failed to clean up stale entries: {ex.Message}");
            }
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
