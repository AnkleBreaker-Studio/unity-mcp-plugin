using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Global action history manager. Maintains a ring buffer of structured action records,
    /// supports filtering, and optionally persists to disk (Library/MCPActionHistory.json).
    /// </summary>
    [InitializeOnLoad]
    public static class MCPActionHistory
    {
        // ═══════════════════════════════════════════════════════
        //  State
        // ═══════════════════════════════════════════════════════

        private static readonly List<MCPActionRecord> _history = new List<MCPActionRecord>();
        private static readonly object _lock = new object();
        private static long _nextId;

        private const string PersistencePath = "Library/MCPActionHistory.json";

        /// <summary>Fires on the main thread whenever a new action is recorded.</summary>
        public static event Action<MCPActionRecord> OnActionRecorded;

        // ═══════════════════════════════════════════════════════
        //  Init / Shutdown
        // ═══════════════════════════════════════════════════════

        static MCPActionHistory()
        {
            // Load persisted history if persistence is enabled
            if (MCPSettingsManager.ActionHistoryPersistence)
                LoadFromDisk();

            // Save on domain reload / editor quit
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            EditorApplication.quitting += OnQuitting;
        }

        private static void OnBeforeReload()
        {
            if (MCPSettingsManager.ActionHistoryPersistence)
                SaveToDisk();
        }

        private static void OnQuitting()
        {
            if (MCPSettingsManager.ActionHistoryPersistence)
                SaveToDisk();
        }

        // ═══════════════════════════════════════════════════════
        //  Public API
        // ═══════════════════════════════════════════════════════

        /// <summary>
        /// Record a new action. Called from MCPRequestQueue after ticket completion.
        /// Thread-safe — can be called from any thread.
        /// </summary>
        public static void RecordAction(MCPActionRecord record)
        {
            int maxEntries = MCPSettingsManager.ActionHistoryMaxEntries;

            lock (_lock)
            {
                record.Id = ++_nextId;
                _history.Add(record);

                // Trim ring buffer
                while (_history.Count > maxEntries)
                    _history.RemoveAt(0);
            }

            // Fire event on main thread for UI refresh
            EditorApplication.delayCall += () => OnActionRecorded?.Invoke(record);
        }

        /// <summary>
        /// Get all history records (newest last). Returns a copy.
        /// </summary>
        public static List<MCPActionRecord> GetAll()
        {
            lock (_lock)
            {
                return new List<MCPActionRecord>(_history);
            }
        }

        /// <summary>
        /// Get filtered history. Pass null for any filter to skip it.
        /// </summary>
        public static List<MCPActionRecord> GetFiltered(string agentFilter, string categoryFilter, string searchText)
        {
            lock (_lock)
            {
                var result = new List<MCPActionRecord>();
                foreach (var r in _history)
                {
                    if (!string.IsNullOrEmpty(agentFilter) &&
                        !string.Equals(r.AgentId, agentFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!string.IsNullOrEmpty(categoryFilter) &&
                        !string.Equals(r.Category, categoryFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!string.IsNullOrEmpty(searchText))
                    {
                        bool match = false;
                        if (r.ActionName != null && r.ActionName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                        if (!match && r.TargetPath != null && r.TargetPath.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                        if (!match && r.AgentId != null && r.AgentId.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                        if (!match && r.ErrorMessage != null && r.ErrorMessage.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0) match = true;
                        if (!match) continue;
                    }

                    result.Add(r);
                }
                return result;
            }
        }

        /// <summary>Get the last N records (newest last).</summary>
        public static List<MCPActionRecord> GetRecent(int count)
        {
            lock (_lock)
            {
                int start = Math.Max(0, _history.Count - count);
                return _history.GetRange(start, _history.Count - start);
            }
        }

        /// <summary>Get distinct agent IDs from current history.</summary>
        public static List<string> GetDistinctAgents()
        {
            var agents = new HashSet<string>();
            lock (_lock)
            {
                foreach (var r in _history)
                    if (!string.IsNullOrEmpty(r.AgentId))
                        agents.Add(r.AgentId);
            }
            return new List<string>(agents);
        }

        /// <summary>Get distinct categories from current history.</summary>
        public static List<string> GetDistinctCategories()
        {
            var cats = new HashSet<string>();
            lock (_lock)
            {
                foreach (var r in _history)
                    if (!string.IsNullOrEmpty(r.Category))
                        cats.Add(r.Category);
            }
            return new List<string>(cats);
        }

        public static int Count
        {
            get { lock (_lock) return _history.Count; }
        }

        /// <summary>Clear all history.</summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _history.Clear();
            }

            // Delete persistence file
            if (File.Exists(PersistencePath))
            {
                try { File.Delete(PersistencePath); }
                catch (Exception ex) { Debug.LogWarning($"[MCP History] Failed to delete persistence file: {ex.Message}"); }
            }
        }

        // ═══════════════════════════════════════════════════════
        //  Persistence
        // ═══════════════════════════════════════════════════════

        private static void SaveToDisk()
        {
            try
            {
                List<MCPActionRecord> snapshot;
                lock (_lock) { snapshot = new List<MCPActionRecord>(_history); }

                // Simple JSON array serialization using JsonUtility wrapper
                var wrapper = new HistoryWrapper();
                wrapper.records = new List<HistoryEntry>();

                foreach (var r in snapshot)
                {
                    wrapper.records.Add(new HistoryEntry
                    {
                        id = r.Id,
                        timestamp = r.Timestamp.ToString("O"),
                        agentId = r.AgentId ?? "",
                        actionName = r.ActionName ?? "",
                        category = r.Category ?? "",
                        status = r.Status ?? "",
                        executionTimeMs = r.ExecutionTimeMs,
                        errorMessage = r.ErrorMessage ?? "",
                        targetInstanceId = r.TargetInstanceId,
                        targetPath = r.TargetPath ?? "",
                        targetType = r.TargetType ?? "",
                        undoGroup = r.UndoGroup,
                    });
                }

                string json = JsonUtility.ToJson(wrapper, true);
                File.WriteAllText(PersistencePath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP History] Failed to save: {ex.Message}");
            }
        }

        private static void LoadFromDisk()
        {
            if (!File.Exists(PersistencePath)) return;

            try
            {
                string json = File.ReadAllText(PersistencePath);
                var wrapper = JsonUtility.FromJson<HistoryWrapper>(json);

                if (wrapper?.records == null) return;

                lock (_lock)
                {
                    _history.Clear();
                    foreach (var entry in wrapper.records)
                    {
                        DateTime.TryParse(entry.timestamp, out var ts);
                        _history.Add(new MCPActionRecord
                        {
                            Id              = entry.id,
                            Timestamp       = ts,
                            AgentId         = entry.agentId,
                            ActionName      = entry.actionName,
                            Category        = entry.category,
                            Status          = entry.status,
                            ExecutionTimeMs = entry.executionTimeMs,
                            ErrorMessage    = entry.errorMessage,
                            TargetInstanceId = entry.targetInstanceId,
                            TargetPath      = entry.targetPath,
                            TargetType      = entry.targetType,
                            UndoGroup       = entry.undoGroup,
                        });
                    }

                    // Restore ID counter
                    if (_history.Count > 0)
                        _nextId = _history[_history.Count - 1].Id;
                }

                Debug.Log($"[MCP History] Loaded {wrapper.records.Count} records from disk.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MCP History] Failed to load: {ex.Message}");
            }
        }

        // JsonUtility-compatible wrappers (no Dictionary support)
        [Serializable]
        private class HistoryWrapper
        {
            public List<HistoryEntry> records;
        }

        [Serializable]
        private class HistoryEntry
        {
            public long   id;
            public string timestamp;
            public string agentId;
            public string actionName;
            public string category;
            public string status;
            public long   executionTimeMs;
            public string errorMessage;
            public int    targetInstanceId;
            public string targetPath;
            public string targetType;
            public int    undoGroup;
        }
    }
}
