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
    public static partial class MCPActionHistory
    {
        // ═══════════════════════════════════════════════════════
        //  State
        // ═══════════════════════════════════════════════════════

        private static readonly List<MCPActionRecord> _history = new List<MCPActionRecord>();
        private static readonly object _lock = new object();
        private static long _nextId;
        private static long _revision;
        private const int MaxPendingNotifications = 10_000;
        private const int NotificationsPerUpdate = 100;
        private static readonly Queue<MCPActionRecord> _notifications = new Queue<MCPActionRecord>();
        private static bool _notificationScheduled, _dispatchingNotifications;
        private static long _notificationGeneration, _deliveredNotifications, _droppedNotifications;

        internal static long Revision { get { lock (_lock) return _revision; } }

        private const string PersistencePath = "Library/MCPActionHistory.json";

        /// <summary>Deferred editor-thread delivery; pressure drops oldest notifications to bound observer retention.</summary>
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
            MCPRequestQueue.FlushCompletedHistory(10_000);
            ClearNotifications();
            if (MCPSettingsManager.ActionHistoryPersistence)
                SaveToDisk();
        }

        private static void OnQuitting()
        {
            MCPRequestQueue.FlushCompletedHistory(10_000);
            ClearNotifications();
            if (MCPSettingsManager.ActionHistoryPersistence)
                SaveToDisk();
        }

        // ═══════════════════════════════════════════════════════
        //  Public API
        // ═══════════════════════════════════════════════════════

        /// <summary>Call on the main thread because history settings and notifications use editor APIs.</summary>
        public static void RecordAction(MCPActionRecord record)
        {
            int maxEntries = MCPSettingsManager.ActionHistoryMaxEntries;

            lock (_lock)
            {
                record.Id = ++_nextId;
                _history.Add(record);
                _revision++;

                // Trim ring buffer
                if (_history.Count > maxEntries)
                    _history.RemoveRange(0, _history.Count - maxEntries);
            }

            if (OnActionRecorded == null) return;
            lock (_lock)
            {
                if (_notifications.Count >= MaxPendingNotifications)
                {
                    _notifications.Dequeue();
                    _droppedNotifications++;
                }
                _notifications.Enqueue(record);
            }
            ScheduleNotifications();
        }

        private static void ScheduleNotifications()
        {
            if (_notificationScheduled || _dispatchingNotifications) return;
            _notificationScheduled = true;
            // Inspector-dependent delayCall can stall while hidden editors still process MCP updates.
            EditorApplication.update += DispatchNotifications;
        }

        private static void DispatchNotifications()
        {
            EditorApplication.update -= DispatchNotifications;
            _notificationScheduled = false;
            if (_dispatchingNotifications) return;
            _dispatchingNotifications = true;
            long generation;
            int count;
            lock (_lock) { generation = _notificationGeneration; count = Math.Min(NotificationsPerUpdate, _notifications.Count); }
            try
            {
                for (int i = 0; i < count; i++)
                {
                    MCPActionRecord record;
                    lock (_lock)
                    {
                        if (generation != _notificationGeneration || _notifications.Count == 0) break;
                        record = _notifications.Dequeue();
                    }
                    var observers = OnActionRecorded;
                    if (observers == null) { ClearNotifications(); break; }
                    foreach (Action<MCPActionRecord> observer in observers.GetInvocationList())
                    {
                        try { observer(record); }
                        catch (Exception error) { Debug.LogException(error); }
                    }
                    lock (_lock) _deliveredNotifications++;
                }
            }
            finally
            {
                _dispatchingNotifications = false;
                if (OnActionRecorded == null) ClearNotifications();
                bool pending;
                lock (_lock) pending = _notifications.Count > 0;
                if (pending) ScheduleNotifications();
            }
        }

        private static void ClearNotifications()
        {
            EditorApplication.update -= DispatchNotifications;
            _notificationScheduled = false;
            lock (_lock) { _notifications.Clear(); _notificationGeneration++; }
        }

        internal static Dictionary<string, object> GetNotificationInfo()
        {
            lock (_lock) return new Dictionary<string, object>
            {
                { "pendingCount", _notifications.Count }, { "maxPendingCount", MaxPendingNotifications },
                { "maxPerUpdate", NotificationsPerUpdate }, { "delivered", _deliveredNotifications },
                { "dropped", _droppedNotifications },
            };
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
            MCPRequestQueue.ClearPendingHistory();
            ClearNotifications();
            lock (_lock)
            {
                _history.Clear();
                _revision++;
            }

            ResetPersistenceWarning();

            // Delete persistence file
            if (File.Exists(PersistencePath))
            {
                try { File.Delete(PersistencePath); }
                catch (Exception ex) { PersistenceFailure("clear", ex, true); }
            }
        }

    }
}
